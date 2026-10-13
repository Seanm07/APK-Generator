using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ApkGenerator.Core;

/// <summary>
/// Protects saved passwords so the settings file never contains them in plain text.
///   Windows: DPAPI (current user) - can't be read by another Windows account or machine.
///   macOS / Linux: AES-256 with a random key. On macOS the key lives in the login Keychain; if the
///   Keychain can't be used it falls back to a key file (mode 600) in the app's data folder.
/// </summary>
public static class SecretProtector
{
    private const string DpapiPrefix = "dpapi:";
    private const string KeyPrefix = "key:";
    private const int CryptProtectUiForbidden = 0x1;

    private const string KeychainService = "APK Generator";
    private const string KeychainAccount = "settings-encryption-key";

    private static byte[]? _key;
    private static bool _keyTried;

    // ------------------------------------------------------------------ public API

    /// <summary>Returns null for an empty value.</summary>
    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;

        if (OperatingSystem.IsWindows())
        {
            var result = Dpapi(Encoding.UTF8.GetBytes(plain), protect: true);
            return result is null ? null : DpapiPrefix + Convert.ToBase64String(result);
        }

        var key = GetKey();
        return key is null ? null : KeyPrefix + Convert.ToBase64String(Encrypt(key, Encoding.UTF8.GetBytes(plain)));
    }

    /// <summary>Returns null if the value is empty, or can't be decrypted here (e.g. saved by another user).</summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try
        {
            if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows()) return null;
                var r = Dpapi(Convert.FromBase64String(stored[DpapiPrefix.Length..]), protect: false);
                return r is null ? null : Encoding.UTF8.GetString(r);
            }

            if (stored.StartsWith(KeyPrefix, StringComparison.Ordinal))
            {
                var key = GetKey();
                if (key is null) return null;
                var r = Decrypt(key, Convert.FromBase64String(stored[KeyPrefix.Length..]));
                return r is null ? null : Encoding.UTF8.GetString(r);
            }
        }
        catch (FormatException)
        {
        }
        return null;
    }

    // ------------------------------------------------------------------ AES-256-CBC + HMAC-SHA256 (works on every platform)

    private static byte[] Encrypt(byte[] key, byte[] plain)
    {
        using var aes = Aes.Create();
        aes.Key = HMACSHA256.HashData(key, "enc"u8);
        aes.GenerateIV();
        var cipher = aes.EncryptCbc(plain, aes.IV);
        var body = aes.IV.Concat(cipher).ToArray();
        var mac = HMACSHA256.HashData(HMACSHA256.HashData(key, "mac"u8), body);
        return body.Concat(mac).ToArray();
    }

    private static byte[]? Decrypt(byte[] key, byte[] data)
    {
        if (data.Length < 16 + 16 + 32) return null;
        var body = data[..^32];
        var mac = data[^32..];
        var expected = HMACSHA256.HashData(HMACSHA256.HashData(key, "mac"u8), body);
        if (!CryptographicOperations.FixedTimeEquals(mac, expected)) return null;

        using var aes = Aes.Create();
        aes.Key = HMACSHA256.HashData(key, "enc"u8);
        try { return aes.DecryptCbc(body[16..], body[..16]); }
        catch (CryptographicException) { return null; }
    }

    // ------------------------------------------------------------------ key storage (macOS / Linux)

    private static byte[]? GetKey()
    {
        lock (typeof(SecretProtector))
        {
            if (_keyTried) return _key;
            _keyTried = true;
            _key = OperatingSystem.IsMacOS() ? KeyFromKeychain() ?? KeyFromFile() : KeyFromFile();
            return _key;
        }
    }

    private static byte[]? KeyFromKeychain()
    {
        try
        {
            var found = Run("/usr/bin/security", "find-generic-password", "-s", KeychainService, "-a", KeychainAccount, "-w");
            if (found.code == 0 && TryDecode(found.output.Trim()) is { } existing) return existing;

            var fresh = RandomNumberGenerator.GetBytes(32);
            var add = Run("/usr/bin/security", "add-generic-password", "-s", KeychainService, "-a", KeychainAccount,
                "-w", Convert.ToBase64String(fresh), "-U");
            return add.code == 0 ? fresh : null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? KeyFromFile()
    {
        try
        {
            var dir = AppPaths.Detect().BaseDir;
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, ".secret-key");
            if (File.Exists(file) && TryDecode(File.ReadAllText(file).Trim()) is { } existing) return existing;

            var fresh = RandomNumberGenerator.GetBytes(32);
            File.WriteAllText(file, Convert.ToBase64String(fresh));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return fresh;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryDecode(string base64)
    {
        try
        {
            var b = Convert.FromBase64String(base64);
            return b.Length == 32 ? b : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static (int code, string output) Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return (-1, ""); }
        return (p.ExitCode, output);
    }

    // ------------------------------------------------------------------ Windows DPAPI

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private static byte[]? Dpapi(byte[] input, bool protect)
    {
        var inBlob = new DataBlob { cbData = input.Length, pbData = Marshal.AllocHGlobal(input.Length) };
        try
        {
            Marshal.Copy(input, 0, inBlob.pbData, input.Length);
            var ok = protect
                ? CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob);
            if (!ok) return null;
            try
            {
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
        }
    }
}
