using System.Text.RegularExpressions;

namespace ApkGenerator.Core;

/// <summary>Where each external tool was found. Null means "not found".</summary>
public sealed class ToolSet
{
    public string? JavaPath { get; set; }
    public string? JavaVersion { get; set; }
    /// <summary>Every keytool found, newest JDK first (old keytools can't read PKCS12 files made by new JDKs).</summary>
    public List<string> KeytoolPaths { get; } = new();
    public string? KeytoolPath => KeytoolPaths.FirstOrDefault();
    public string? AdbPath { get; set; }
    public string? AdbVersion { get; set; }
    public string? BundletoolPath { get; set; }
    public string? BundletoolVersion { get; set; }
    public string? ApkSignerJar { get; set; }
    public string? ZipAlignPath { get; set; }
    public string? BuildToolsVersion { get; set; }

    public bool HasBundletool => JavaPath != null && BundletoolPath != null;
}

public sealed class ToolOverrides
{
    public string? JavaPath { get; set; }
    public string? AdbPath { get; set; }
    public string? BundletoolPath { get; set; }
}

public static class ToolLocator
{
    public static ToolSet Locate(AppPaths paths, ToolOverrides? overrides = null)
    {
        var set = new ToolSet();

        set.JavaPath = ExistingFile(overrides?.JavaPath) ?? FindJava();
        set.KeytoolPaths.AddRange(FindKeytools(set.JavaPath));
        set.AdbPath = ExistingFile(overrides?.AdbPath) ?? FindAdb(paths);
        set.BundletoolPath = ExistingFile(overrides?.BundletoolPath) ?? FindBundletool(paths);
        if (set.BundletoolPath != null)
        {
            var m = Regex.Match(Path.GetFileName(set.BundletoolPath), @"(\d+(?:\.\d+)+)");
            if (m.Success) set.BundletoolVersion = m.Groups[1].Value;
        }

        var bt = FindBuildTools();
        if (bt != null)
        {
            var jar = Path.Combine(bt, "lib", "apksigner.jar");
            if (File.Exists(jar)) set.ApkSignerJar = jar;
            var za = Path.Combine(bt, "zipalign.exe");
            if (File.Exists(za)) set.ZipAlignPath = za;
            set.BuildToolsVersion = Path.GetFileName(bt);
        }
        return set;
    }

    /// <summary>Fills in the version strings (spawns a couple of quick processes).</summary>
    public static async Task ProbeVersionsAsync(ToolSet set)
    {
        var tasks = new List<Task>();
        if (set.JavaPath != null)
            tasks.Add(Task.Run(async () =>
            {
                var r = await ProcessRunner.RunAsync(set.JavaPath, new[] { "-version" }, timeout: TimeSpan.FromSeconds(10));
                var m = Regex.Match(r.Combined, "version \"([^\"]+)\"");
                if (m.Success) set.JavaVersion = m.Groups[1].Value;
            }));
        if (set.AdbPath != null)
            tasks.Add(Task.Run(async () =>
            {
                var r = await ProcessRunner.RunAsync(set.AdbPath, new[] { "version" }, timeout: TimeSpan.FromSeconds(15));
                var m = Regex.Match(r.Combined, @"Android Debug Bridge version ([\d.]+)");
                if (m.Success) set.AdbVersion = m.Groups[1].Value;
            }));
        await Task.WhenAll(tasks);
    }

    private static string? ExistingFile(string? p) => !string.IsNullOrWhiteSpace(p) && File.Exists(p) ? p : null;

    // ---------------------------------------------------------------- Java

    private static string? FindJava() => FindInJdkDirs("java.exe");

    private static IEnumerable<string> FindKeytools(string? javaPath)
    {
        var found = new List<string>();
        void Add(string? p)
        {
            if (p != null && File.Exists(p) && !found.Contains(p, StringComparer.OrdinalIgnoreCase)) found.Add(p);
        }

        if (javaPath != null) Add(Path.Combine(Path.GetDirectoryName(javaPath)!, "keytool.exe"));

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome)) Add(Path.Combine(javaHome, "bin", "keytool.exe"));

        foreach (var bin in CandidateJdkBinDirs()) Add(Path.Combine(bin, "keytool.exe"));
        Add(FindOnPath("keytool.exe"));
        return found;
    }

    /// <summary>JAVA_HOME, then PATH, then the JDKs that Android Studio / Unity / installers drop on disk.</summary>
    private static string? FindInJdkDirs(string exe)
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var p = Path.Combine(javaHome, "bin", exe);
            if (File.Exists(p)) return p;
        }

        var onPath = FindOnPath(exe);
        if (onPath != null) return onPath;

        foreach (var bin in CandidateJdkBinDirs())
        {
            var p = Path.Combine(bin, exe);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static IEnumerable<string> CandidateJdkBinDirs()
    {
        var pf = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";

        yield return Path.Combine(pf, "Android", "Android Studio", "jbr", "bin");

        // Unity ships an OpenJDK inside every Android-enabled editor install.
        foreach (var editors in new[] { Path.Combine(pf, "Unity", "Hub", "Editor") })
            foreach (var ed in SafeDirs(editors).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                yield return Path.Combine(ed, "Editor", "Data", "PlaybackEngines", "AndroidPlayer", "OpenJDK", "bin");

        foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Microsoft", "Amazon Corretto", "Zulu" })
            foreach (var jdk in SafeDirs(Path.Combine(pf, vendor)).OrderByDescending(d => JdkMajor(Path.GetFileName(d))))
                yield return Path.Combine(jdk, "bin");
    }

    /// <summary>"jdk1.8.0_221" → 8, "jdk-25" → 25, "jdk-17.0.9.9-hotspot" → 17.</summary>
    private static int JdkMajor(string dirName)
    {
        var m = Regex.Match(dirName, @"(?:1\.)?(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    // ---------------------------------------------------------------- adb

    private static string? FindAdb(AppPaths paths)
    {
        var bundled = Path.Combine(paths.DependenciesDir, "platform-tools", "adb.exe");
        if (File.Exists(bundled)) return bundled;

        var onPath = FindOnPath("adb.exe");
        if (onPath != null) return onPath;

        foreach (var sdk in SdkRoots())
        {
            var p = Path.Combine(sdk, "platform-tools", "adb.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // ---------------------------------------------------------------- bundletool

    private static string? FindBundletool(AppPaths paths)
    {
        var dirs = new[] { paths.BundletoolDir, paths.DependenciesDir };
        var jars = dirs.SelectMany(d => SafeFiles(d, "bundletool*.jar")).ToList();
        if (jars.Count == 0) return null;
        return jars.OrderByDescending(j => VersionKey(Path.GetFileName(j))).First();
    }

    // ---------------------------------------------------------------- build-tools (apksigner / zipalign)

    private static string? FindBuildTools()
    {
        foreach (var sdk in SdkRoots())
        {
            var root = Path.Combine(sdk, "build-tools");
            var best = SafeDirs(root)
                .Where(d => File.Exists(Path.Combine(d, "lib", "apksigner.jar")))
                .OrderByDescending(d => IsPreRelease(Path.GetFileName(d)) ? 0 : 1)
                .ThenByDescending(d => VersionKey(Path.GetFileName(d)))
                .FirstOrDefault();
            if (best != null) return best;
        }
        return null;
    }

    private static bool IsPreRelease(string name) => name.Contains('-');

    private static IEnumerable<string> SdkRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            var p = Environment.GetEnvironmentVariable(v);
            if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p) && seen.Add(p)) yield return p;
        }
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (local != null)
        {
            var p = Path.Combine(local, "Android", "Sdk");
            if (Directory.Exists(p) && seen.Add(p)) yield return p;
        }
    }

    // ---------------------------------------------------------------- helpers

    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path == null) return null;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim().Trim('"'), exe);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Sortable key for names like "bundletool-all-1.18.2.jar" or "34.0.0-rc4".</summary>
    private static long VersionKey(string name)
    {
        var m = Regex.Match(name, @"(\d+)(?:\.(\d+))?(?:\.(\d+))?");
        if (!m.Success) return 0;
        long Part(int i) => m.Groups[i].Success ? long.Parse(m.Groups[i].Value) : 0;
        return Part(1) * 1_000_000_000L + Part(2) * 1_000_000L + Part(3);
    }
}
