using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApkGenerator.Core;

public sealed class KeystoreRecord
{
    /// <summary>DPAPI-protected.</summary>
    public string? StorePassword { get; set; }
    public string? LastAlias { get; set; }
    /// <summary>alias → DPAPI-protected alias password.</summary>
    public Dictionary<string, string?> AliasPasswords { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class FtpRecord
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 21;
    public string User { get; set; } = "";
    /// <summary>DPAPI-protected.</summary>
    public string? Password { get; set; }
    public string RemoteFolder { get; set; } = "";
    public bool UseTls { get; set; }
}

public sealed class SizeCacheEntry
{
    public long MinBytes { get; set; }
    public long MaxBytes { get; set; }
    public DateTime SavedUtc { get; set; }
}

public sealed class AppSettings
{
    public string? LastDevice { get; set; }

    public bool AllowDowngrade { get; set; } = true;
    public bool UninstallConflicting { get; set; }
    public bool ClearData { get; set; }
    public bool GrantPermissions { get; set; }
    public bool LaunchAfterInstall { get; set; } = true;

    public bool SignApk { get; set; }
    /// <summary>File name inside the Keystores folder.</summary>
    public string? SelectedKeystore { get; set; }

    /// <summary>Package names that don't start with this get a warning. Empty disables the check.</summary>
    public string ExpectedPackagePrefix { get; set; } = "com.pickle.";

    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }

    /// <summary>Advanced: point at specific copies of the tools instead of auto-detecting them.</summary>
    public ToolOverrides ToolOverrides { get; set; } = new();

    /// <summary>Where "Save APK to FTP" uploads to.</summary>
    public FtpRecord Ftp { get; set; } = new();

    public Dictionary<string, KeystoreRecord> Keystores { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remembered store-size estimates, so re-opening the same AAB is instant.</summary>
    public Dictionary<string, SizeCacheEntry> SizeCache { get; set; } = new();
}

public sealed record KeystoreDetails(string StorePassword, string Alias, string AliasPassword);

public sealed class SettingsStore
{
    private const string FileName = "ApkGenerator.settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly object _gate = new();
    private readonly string _path;

    public AppSettings Data { get; private set; }
    public string FilePath => _path;

    private SettingsStore(string path, AppSettings data)
    {
        _path = path;
        Data = data;
    }

    public static SettingsStore Load(AppPaths paths)
    {
        var path = ResolvePath(paths);
        var readFrom = path;
        var data = new AppSettings();
        try
        {
            if (File.Exists(readFrom))
                data = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(readFrom), JsonOptions) ?? new AppSettings();
        }
        catch
        {
            // A corrupt settings file shouldn't stop the app – keep it aside and start fresh.
            try { File.Move(readFrom, readFrom + ".bad", overwrite: true); } catch { }
        }

        // JSON round-tripping loses the case-insensitive comparers.
        data.Keystores = new Dictionary<string, KeystoreRecord>(data.Keystores ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var rec in data.Keystores.Values)
            rec.AliasPasswords = new Dictionary<string, string?>(rec.AliasPasswords ?? new(), StringComparer.OrdinalIgnoreCase);
        data.SizeCache ??= new();
        data.ToolOverrides ??= new();
        data.Ftp ??= new();
        return new SettingsStore(path, data);
    }

    /// <summary>Next to the exe if that folder is writable, otherwise %APPDATA%.</summary>
    private static string ResolvePath(AppPaths paths)
    {
        var primary = Path.Combine(paths.BaseDir, FileName);
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ApkGenerator", FileName);
        if (File.Exists(primary)) return primary;
        if (File.Exists(fallback)) return fallback;
        try
        {
            var probe = Path.Combine(paths.BaseDir, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return primary;
        }
        catch
        {
            return fallback;
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Data, JsonOptions));
                File.Move(tmp, _path, overwrite: true);
            }
            catch
            {
                // Losing a preference is not worth interrupting the user.
            }
        }
    }

    // ------------------------------------------------------------------ keystore details

    public KeystoreDetails? GetKeystoreDetails(string keystoreFileName)
    {
        if (!Data.Keystores.TryGetValue(keystoreFileName, out var rec)) return null;
        var alias = rec.LastAlias ?? "";
        var aliasPw = alias.Length > 0 && rec.AliasPasswords.TryGetValue(alias, out var p) ? SecretProtector.Unprotect(p) : null;
        return new KeystoreDetails(SecretProtector.Unprotect(rec.StorePassword) ?? "", alias, aliasPw ?? "");
    }

    /// <summary>The remembered password for one specific alias of a keystore, if there is one.</summary>
    public string? GetAliasPassword(string keystoreFileName, string alias)
    {
        if (!Data.Keystores.TryGetValue(keystoreFileName, out var rec)) return null;
        return rec.AliasPasswords.TryGetValue(alias, out var p) ? SecretProtector.Unprotect(p) : null;
    }

    public void SaveKeystoreDetails(string keystoreFileName, string storePassword, string alias, string aliasPassword)
    {
        if (!Data.Keystores.TryGetValue(keystoreFileName, out var rec))
            Data.Keystores[keystoreFileName] = rec = new KeystoreRecord();

        rec.StorePassword = SecretProtector.Protect(storePassword);
        rec.LastAlias = alias;
        if (alias.Length > 0) rec.AliasPasswords[alias] = SecretProtector.Protect(aliasPassword);
        Save();
    }

    // ------------------------------------------------------------------ FTP

    public FtpSettings GetFtp()
    {
        var f = Data.Ftp;
        return new FtpSettings(f.Host ?? "", f.Port is > 0 and <= 65535 ? f.Port : 21, f.User ?? "",
            SecretProtector.Unprotect(f.Password) ?? "", f.RemoteFolder ?? "", f.UseTls);
    }

    public void SaveFtp(FtpSettings s)
    {
        Data.Ftp = new FtpRecord
        {
            Host = s.Host.Trim(),
            Port = s.Port,
            User = s.User.Trim(),
            Password = SecretProtector.Protect(s.Password),
            RemoteFolder = s.RemoteFolder.Trim(),
            UseTls = s.UseTls,
        };
        Save();
    }

    // ------------------------------------------------------------------ size cache

    private static string SizeKey(PackageInfo p) =>
        $"{p.FilePath}|{p.FileSize}|{File.GetLastWriteTimeUtc(p.FilePath).Ticks}";

    public SizeRange? GetCachedSize(PackageInfo p) =>
        Data.SizeCache.TryGetValue(SizeKey(p), out var e) ? new SizeRange(e.MinBytes, e.MaxBytes) : null;

    public void CacheSize(PackageInfo p, SizeRange range)
    {
        Data.SizeCache[SizeKey(p)] = new SizeCacheEntry { MinBytes = range.MinBytes, MaxBytes = range.MaxBytes, SavedUtc = DateTime.UtcNow };
        foreach (var stale in Data.SizeCache.OrderByDescending(kv => kv.Value.SavedUtc).Skip(40).Select(kv => kv.Key).ToList())
            Data.SizeCache.Remove(stale);
        Save();
    }
}
