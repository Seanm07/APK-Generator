namespace ApkGenerator.Core;

/// <summary>
/// Well-known folders. Everything lives next to the exe, mirroring the layout the old
/// AABToAPK.bat used: "Dependencies" (bundletool jar, optionally platform-tools), "Keystores" and "Generated APKs".
/// </summary>
public sealed class AppPaths
{
    public string BaseDir { get; }
    public string DependenciesDir => Path.Combine(BaseDir, "Dependencies");
    public string BundletoolDir => DependenciesDir;   // the jar sits directly in Dependencies
    public string KeystoresDir => Path.Combine(BaseDir, "Keystores");
    public string OutputDir => Path.Combine(BaseDir, "Generated APKs");

    public AppPaths(string baseDir) => BaseDir = baseDir;

    /// <summary>The exe's folder, or the APKGENERATOR_HOME environment variable if set (used for testing).</summary>
    public static AppPaths Detect()
    {
        var overrideDir = Environment.GetEnvironmentVariable("APKGENERATOR_HOME");
        if (!string.IsNullOrWhiteSpace(overrideDir) && Directory.Exists(overrideDir))
            return new AppPaths(overrideDir);

        var exeDir = Path.GetDirectoryName(Environment.ProcessPath);
        return new AppPaths(exeDir ?? AppContext.BaseDirectory);
    }

    /// <summary>Per-run scratch folder under %TEMP% (build-apks output can be hundreds of MB).</summary>
    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ApkGenerator", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* locked file – cleaned up on next start */ }
    }

    /// <summary>Removes scratch folders left behind by crashed or cancelled runs.</summary>
    public static void PurgeOldTempDirs()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "ApkGenerator");
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.GetDirectories(root))
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d) > TimeSpan.FromHours(2))
                    TryDeleteDir(d);
            }
        }
        catch { }
    }
}
