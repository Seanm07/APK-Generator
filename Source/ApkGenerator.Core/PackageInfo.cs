namespace ApkGenerator.Core;

public enum PackageKind { Aab, Apk }

public sealed record PermissionInfo(string Name, bool IsDangerous)
{
    private const string Prefix = "android.permission.";
    public string ShortName => Name.StartsWith(Prefix, StringComparison.Ordinal) ? Name[Prefix.Length..] : Name;
}

public enum WarningLevel { Info, Warning }

public sealed record PackageWarning(WarningLevel Level, string Text);

/// <summary>Everything we can learn from the manifest and zip listing without running Java.</summary>
public sealed class PackageInfo
{
    public PackageKind Kind { get; init; }
    public string FilePath { get; init; } = "";
    public string FileName => Path.GetFileName(FilePath);
    public long FileSize { get; init; }

    public string? PackageName { get; set; }
    public long? VersionCode { get; set; }
    public string? VersionName { get; set; }
    public int? MinSdk { get; set; }
    public int? TargetSdk { get; set; }
    public bool Debuggable { get; set; }
    public bool TestOnly { get; set; }

    /// <summary>Component to start after install, e.g. "com.unity3d.player.UnityPlayerActivity".</summary>
    public string? LauncherActivity { get; set; }

    public List<PermissionInfo> Permissions { get; } = new();
    public List<string> Abis { get; } = new();

    /// <summary>AAB only: base plus any dynamic feature / asset pack modules.</summary>
    public List<string> Modules { get; } = new();

    public bool IsAab => Kind == PackageKind.Aab;
}
