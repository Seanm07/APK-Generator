namespace ApkGenerator.Core;

/// <summary>
/// Well-known folders.
///   Windows: everything lives next to the exe ("Dependencies", "Keystores", "Generated APKs").
///   macOS:   the .app bundle is read-only once signed, so keystores, settings and built APKs live in
///            ~/Library/Application Support/APK Generator, and the bundled bundletool jar sits inside the app.
/// </summary>
public sealed class AppPaths
{
    /// <summary>Where Keystores, Generated APKs and the settings file live (must be writable).</summary>
    public string BaseDir { get; }

    /// <summary>Files shipped with the program (the bundletool jar).</summary>
    public string DependenciesDir { get; }

    /// <summary>Extra files the user may drop in (e.g. a newer bundletool jar, or a platform-tools folder).</summary>
    public string UserDependenciesDir => Path.Combine(BaseDir, "Dependencies");

    public string KeystoresDir => Path.Combine(BaseDir, "Keystores");
    public string OutputDir => Path.Combine(BaseDir, "Generated APKs");

    public AppPaths(string baseDir, string? dependenciesDir = null)
    {
        BaseDir = baseDir;
        DependenciesDir = dependenciesDir ?? Path.Combine(baseDir, "Dependencies");
    }

    public const string AppName = "APK Generator";

    /// <summary>The exe's folder on Windows, the per-user data folder on macOS/Linux, or APKGENERATOR_HOME if set (used for testing).</summary>
    public static AppPaths Detect()
    {
        var overrideDir = Environment.GetEnvironmentVariable("APKGENERATOR_HOME");
        if (!string.IsNullOrWhiteSpace(overrideDir) && Directory.Exists(overrideDir))
            return new AppPaths(overrideDir);

        var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

        if (OperatingSystem.IsMacOS())
        {
            var data = Path.Combine(Home(), "Library", "Application Support", AppName);
            // <App>.app/Contents/MacOS/<exe>  ->  <App>.app/Contents/Resources/Dependencies
            var bundled = Path.GetFullPath(Path.Combine(exeDir, "..", "Resources", "Dependencies"));
            return new AppPaths(data, Directory.Exists(bundled) ? bundled : Path.Combine(exeDir, "Dependencies"));
        }

        if (OperatingSystem.IsLinux())
        {
            var share = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var data = Path.Combine(string.IsNullOrWhiteSpace(share) ? Path.Combine(Home(), ".local", "share") : share, AppName);
            return new AppPaths(data, Path.Combine(exeDir, "Dependencies"));
        }

        return new AppPaths(exeDir);
    }

    public static string Home() => Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } h
        ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Per-run scratch folder under the temp folder (build-apks output can be hundreds of MB).</summary>
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
