using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ApkGenerator.Core;

public sealed class AliasListResult
{
    public List<string> Aliases { get; } = new();
    public string? Error { get; init; }
    /// <summary>The keystore format needs the password before aliases can be listed.</summary>
    public bool NeedsPassword { get; init; }
    /// <summary>True/false when the store password was checked, null if it couldn't be.</summary>
    public bool? PasswordVerified { get; init; }
    public bool Success => Error is null && !NeedsPassword;
}

/// <summary>
/// Lists the key aliases in a keystore. JKS files (the Unity / Android Studio default) are parsed directly –
/// no Java and no password required, and the store password can be verified instantly.
/// Anything else (PKCS12 …) is read with keytool.
/// </summary>
public static class KeystoreReader
{
    public static readonly string[] KeystoreExtensions = { ".keystore", ".jks", ".ks", ".p12", ".pfx", ".bks" };

    public static bool LooksLikeKeystore(string path) =>
        KeystoreExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static async Task<AliasListResult> ListAliasesAsync(string path, string? storePassword, ToolSet tools, CancellationToken ct)
    {
        byte[] head;
        try
        {
            using var fs = File.OpenRead(path);
            head = new byte[4];
            if (fs.Read(head, 0, 4) < 4) return new AliasListResult { Error = "The keystore file is empty or damaged." };
        }
        catch (Exception ex)
        {
            return new AliasListResult { Error = "Couldn't read the keystore: " + ex.Message };
        }

        if (head[0] == 0xFE && head[1] == 0xED && head[2] == 0xFE && head[3] == 0xED)
            return await Task.Run(() => ReadJks(path, storePassword), ct);

        return await ListWithKeytoolAsync(path, storePassword, tools, ct);
    }

    // ------------------------------------------------------------------ JKS

    private static AliasListResult ReadJks(string path, string? password)
    {
        try
        {
            var d = File.ReadAllBytes(path);
            var p = 4;
            var version = BE32(d, ref p);
            if (version != 1 && version != 2) return Damaged();
            var count = BE32(d, ref p);
            if (count < 0 || count > 100_000) return Damaged();

            var aliases = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var tag = BE32(d, ref p);
                var alias = ReadUtf(d, ref p);
                p += 8;                                                   // creation date
                if (tag == 1)                                             // private key entry
                {
                    var keyLen = BE32(d, ref p);                          // encrypted key blob
                    p += keyLen;
                    var chain = BE32(d, ref p);
                    for (var c = 0; c < chain; c++) SkipCert(d, ref p, version);
                    aliases.Add(alias);
                }
                else if (tag == 2) SkipCert(d, ref p, version);           // trusted certificate
                else return Damaged();
            }
            if (p != d.Length - 20) return Damaged();                     // 20-byte digest must follow the last entry

            bool? verified = password is null ? null : VerifyJksPassword(d, password);
            var result = new AliasListResult
            {
                PasswordVerified = verified,
                Error = verified == false ? "Incorrect keystore password." : null,
            };
            result.Aliases.AddRange(aliases);
            return result;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException)
        {
            return Damaged();
        }
        catch (Exception ex)
        {
            return new AliasListResult { Error = "Couldn't read the keystore: " + ex.Message };
        }
    }

    private static AliasListResult Damaged() => new()
    {
        Error = "This keystore file is damaged (it can't be read as a JKS keystore). " +
                "If it was sent by email or chat it may have been corrupted – ask for a fresh copy.",
    };

    private static void SkipCert(byte[] d, ref int p, int version)
    {
        if (version == 2) ReadUtf(d, ref p);                              // certificate type, e.g. "X.509"
        var len = BE32(d, ref p);
        if (len < 0) throw new InvalidDataException();
        p += len;
    }

    /// <summary>JKS integrity digest: SHA-1(password as UTF-16BE ‖ "Mighty Aphrodite" ‖ store bytes).</summary>
    private static bool VerifyJksPassword(byte[] d, string password)
    {
        if (d.Length < 20) return false;
        var pw = new byte[password.Length * 2];
        for (var i = 0; i < password.Length; i++)
        {
            pw[2 * i] = (byte)(password[i] >> 8);
            pw[2 * i + 1] = (byte)password[i];
        }
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        h.AppendData(pw);
        h.AppendData(Encoding.ASCII.GetBytes("Mighty Aphrodite"));
        h.AppendData(d, 0, d.Length - 20);
        return h.GetHashAndReset().AsSpan().SequenceEqual(d.AsSpan(d.Length - 20));
    }

    private static int BE32(byte[] d, ref int p)
    {
        var v = (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];
        p += 4;
        return v;
    }

    private static string ReadUtf(byte[] d, ref int p)
    {
        var len = (d[p] << 8) | d[p + 1];
        p += 2;
        var s = Encoding.UTF8.GetString(d, p, len);
        p += len;
        return s;
    }

    // ------------------------------------------------------------------ keytool (PKCS12 etc.)

    private static async Task<AliasListResult> ListWithKeytoolAsync(string path, string? storePassword, ToolSet tools, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(storePassword))
            return new AliasListResult { NeedsPassword = true };

        if (tools.KeytoolPath is null)
            return new AliasListResult { Error = "keytool wasn't found, so aliases can't be listed for this keystore type. Type the alias in manually." };

        var env = new Dictionary<string, string> { ["APKGENERATOR_STOREPASS"] = storePassword };
        ProcessResult r = null!;
        string text = "";

        // Newest JDK first; an old keytool reports "Invalid keystore format" for PKCS12 files made by new JDKs, so try the next one.
        foreach (var keytool in tools.KeytoolPaths)
        {
            r = await ProcessRunner.RunAsync(
                keytool,
                new[] { "-list", "-v", "-keystore", path, "-storepass:env", "APKGENERATOR_STOREPASS", "-J-Duser.language=en", "-J-Duser.country=US" },
                ct, timeout: TimeSpan.FromSeconds(30), env: env);
            if (r.Cancelled) return new AliasListResult { Error = "Cancelled." };
            text = OutputFilter.Clean(r.Combined);
            if (!Regex.IsMatch(text, "Invalid keystore format", RegexOptions.IgnoreCase)) break;
        }

        if (Regex.IsMatch(text, "password was incorrect|Keystore was tampered with", RegexOptions.IgnoreCase))
            return new AliasListResult { Error = "Incorrect keystore password.", PasswordVerified = false };
        if (!r.Success)
            return new AliasListResult { Error = FirstLine(text) ?? "keytool couldn't read this keystore." };

        var result = new AliasListResult { PasswordVerified = true };
        string? alias = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Alias name:", StringComparison.OrdinalIgnoreCase))
                alias = line["Alias name:".Length..].Trim();
            else if (line.StartsWith("Entry type:", StringComparison.OrdinalIgnoreCase) && alias != null)
            {
                if (line.Contains("PrivateKeyEntry", StringComparison.OrdinalIgnoreCase)) result.Aliases.Add(alias);
                alias = null;
            }
        }
        return result;
    }

    private static string? FirstLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
}
