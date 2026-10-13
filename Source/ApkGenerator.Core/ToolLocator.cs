using System.Diagnostics;
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
    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    public static ToolSet Locate(AppPaths paths, ToolOverrides? overrides = null)
    {
        _macPath = null;      // a retry after installing something must search again
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
            var za = Path.Combine(bt, Exe("zipalign"));
            if (File.Exists(za)) set.ZipAlignPath = za;
            set.BuildToolsVersion = Path.GetFileName(bt);
        }

        HideSimulatedMissing(set);
        return set;
    }

/// <summary>
    /// Testing aid: while the file named by APKGENERATOR_SIMULATE_MISSING exists, the tools listed in it
    /// (java, bundletool, adb, build-tools - one per line or comma separated) are treated as not installed.
    /// </summary>
    private static void HideSimulatedMissing(ToolSet set)
    {
        var path = Environment.GetEnvironmentVariable("APKGENERATOR_SIMULATE_MISSING");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var names = File.ReadAllText(path).Split(new[] { ',', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(n => n.Trim().ToLowerInvariant()).ToHashSet();
        if (names.Contains("java")) { set.JavaPath = null; set.KeytoolPaths.Clear(); }
        if (names.Contains("bundletool")) set.BundletoolPath = null;
        if (names.Contains("adb")) set.AdbPath = null;
        if (names.Contains("build-tools")) { set.ApkSignerJar = null; set.ZipAlignPath = null; set.BuildToolsVersion = null; }
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

    private static string? FindJava() => FindInJdkDirs(Exe("java"));

    private static IEnumerable<string> FindKeytools(string? javaPath)
    {
        var found = new List<string>();
        void Add(string? p)
        {
            if (p != null && File.Exists(p) && !found.Contains(p, StringComparer.OrdinalIgnoreCase)) found.Add(p);
        }

        if (javaPath != null) Add(Path.Combine(Path.GetDirectoryName(javaPath)!, Exe("keytool")));

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome)) Add(Path.Combine(javaHome, "bin", Exe("keytool")));

        foreach (var bin in CandidateJdkBinDirs()) Add(Path.Combine(bin, Exe("keytool")));
        Add(FindOnPath(Exe("keytool")));
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

        // macOS ships a /usr/bin/java stub that pops up an "install Java" dialog when no JDK exists - never run that.
        var onPath = FindOnPath(exe, skip: OperatingSystem.IsMacOS() ? "/usr/bin/java" : null);
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
        if (OperatingSystem.IsMacOS())
        {
            foreach (var d in MacJdkBinDirs()) yield return d;
            yield break;
        }
        if (!OperatingSystem.IsWindows()) yield break;

        var pf = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";

        yield return Path.Combine(pf, "Android", "Android Studio", "jbr", "bin");

        // Unity ships an OpenJDK inside every Android-enabled editor install.
        foreach (var ed in SafeDirs(Path.Combine(pf, "Unity", "Hub", "Editor")).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            yield return Path.Combine(ed, "Editor", "Data", "PlaybackEngines", "AndroidPlayer", "OpenJDK", "bin");

        foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Microsoft", "Amazon Corretto", "Zulu" })
            foreach (var jdk in SafeDirs(Path.Combine(pf, vendor)).OrderByDescending(d => JdkMajor(Path.GetFileName(d))))
                yield return Path.Combine(jdk, "bin");
    }

    private static IEnumerable<string> MacJdkBinDirs()
    {
        var home = AppPaths.Home();

        // The system's chosen JDK (does nothing and exits non-zero if none is installed).
        var javaHome = RunQuick("/usr/libexec/java_home");
        if (!string.IsNullOrWhiteSpace(javaHome)) yield return Path.Combine(javaHome.Trim(), "bin");

        yield return "/Applications/Android Studio.app/Contents/jbr/Contents/Home/bin";
        yield return "/Applications/Android Studio Preview.app/Contents/jbr/Contents/Home/bin";

        // Unity ships an OpenJDK with its Android support module.
        foreach (var ed in SafeDirs("/Applications/Unity/Hub/Editor").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(ed, "PlaybackEngines", "AndroidPlayer", "OpenJDK", "bin");
            yield return Path.Combine(ed, "Unity.app", "Contents", "PlaybackEngines", "AndroidPlayer", "OpenJDK", "bin");
        }

        foreach (var root in new[] { "/Library/Java/JavaVirtualMachines", Path.Combine(home, "Library", "Java", "JavaVirtualMachines") })
            foreach (var jdk in SafeDirs(root).OrderByDescending(d => JdkMajor(Path.GetFileName(d))))
                yield return Path.Combine(jdk, "Contents", "Home", "bin");

        // Homebrew (Apple Silicon and Intel)
        foreach (var brew in new[] { "/opt/homebrew/opt", "/usr/local/opt" })
            foreach (var jdk in SafeDirs(brew).Where(d => Path.GetFileName(d).StartsWith("openjdk", StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(d => JdkMajor(Path.GetFileName(d))))
            {
                yield return Path.Combine(jdk, "bin");
                yield return Path.Combine(jdk, "libexec", "openjdk.jdk", "Contents", "Home", "bin");
            }
    }

    /// <summary>"jdk1.8.0_221" → 8, "jdk-25" → 25, "temurin-17.jdk" → 17.</summary>
    private static int JdkMajor(string dirName)
    {
        var m = Regex.Match(dirName, @"(?:1\.)?(\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : 0;
    }

    // ---------------------------------------------------------------- adb

    private static string? FindAdb(AppPaths paths)
    {
        foreach (var deps in new[] { paths.DependenciesDir, paths.UserDependenciesDir })
        {
            var bundled = Path.Combine(deps, "platform-tools", Exe("adb"));
            if (File.Exists(bundled)) return bundled;
        }

        var onPath = FindOnPath(Exe("adb"));
        if (onPath != null) return onPath;

        foreach (var sdk in SdkRoots())
        {
            var p = Path.Combine(sdk, "platform-tools", Exe("adb"));
            if (File.Exists(p)) return p;
        }
        return null;
    }

    // ---------------------------------------------------------------- bundletool

    private static string? FindBundletool(AppPaths paths)
    {
        var dirs = new[] { paths.DependenciesDir, paths.UserDependenciesDir, Path.Combine(paths.UserDependenciesDir, "Bundletool") }
            .Distinct(StringComparer.OrdinalIgnoreCase);
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

        var defaults = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (local != null) defaults.Add(Path.Combine(local, "Android", "Sdk"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            defaults.Add(Path.Combine(AppPaths.Home(), "Library", "Android", "sdk"));
            defaults.Add(Path.Combine(AppPaths.Home(), "Library", "Android", "Sdk"));
            defaults.Add("/opt/homebrew/share/android-commandlinetools");
            defaults.Add("/usr/local/share/android-commandlinetools");
        }
        else
        {
            defaults.Add(Path.Combine(AppPaths.Home(), "Android", "Sdk"));
        }
        foreach (var p in defaults)
            if (Directory.Exists(p) && seen.Add(p)) yield return p;

        // Unity bundles a full Android SDK (adb, build-tools) with its Android build support module.
        foreach (var p in UnitySdkRoots())
            if (seen.Add(p)) yield return p;
    }

    /// <summary>The SDK inside each installed Unity editor that has Android support, newest editor first.</summary>
    private static IEnumerable<string> UnitySdkRoots()
    {
        var editors = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var pf = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
            editors.AddRange(SafeDirs(Path.Combine(pf, "Unity", "Hub", "Editor")).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d, "Editor", "Data")));
        }
        else if (OperatingSystem.IsMacOS())
        {
            foreach (var ed in SafeDirs("/Applications/Unity/Hub/Editor").OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                editors.Add(ed);                                          // Hub layout: PlaybackEngines sits beside Unity.app
                editors.Add(Path.Combine(ed, "Unity.app", "Contents"));
            }
            editors.Add("/Applications/Unity/Unity.app/Contents");        // standalone install
        }
        foreach (var e in editors)
        {
            var sdk = Path.Combine(e, "PlaybackEngines", "AndroidPlayer", "SDK");
            if (Directory.Exists(sdk)) yield return sdk;
        }
    }
    // ---------------------------------------------------------------- helpers

    private static string[]? _macPath;

    /// <summary>
    /// PATH entries to search. Apps launched from Finder get a bare PATH (no Homebrew), so on macOS this adds the
    /// login shell's PATH plus the usual Homebrew folders.
    /// </summary>
    private static IEnumerable<string> PathDirs()
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (OperatingSystem.IsMacOS())
        {
            _macPath ??= (RunQuick("/bin/zsh", "-lc", "printf %s \"$PATH\"") ?? "")
                .Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Concat(new[] { "/opt/homebrew/bin", "/usr/local/bin" }).ToArray();
            dirs.AddRange(_macPath);
        }
        return dirs;
    }

    private static string? FindOnPath(string exe, string? skip = null)
    {
        foreach (var dir in PathDirs())
        {
            try
            {
                var p = Path.Combine(dir.Trim().Trim('"'), exe);
                if (File.Exists(p) && !string.Equals(p, skip, StringComparison.Ordinal)) return p;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Runs a tool and returns stdout, or null if it fails or takes more than a few seconds.</summary>
    private static string? RunQuick(string file, params string[] args)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(4000)) { try { p.Kill(true); } catch { } return null; }
            return p.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch
        {
            return null;
        }
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
