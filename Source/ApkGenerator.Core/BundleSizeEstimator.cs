namespace ApkGenerator.Core;

public sealed record SizeRange(long MinBytes, long MaxBytes);

public sealed class SizeEstimateResult
{
    public SizeRange? Range { get; init; }
    public OperationError? Error { get; init; }
    public bool Cancelled { get; init; }
}

/// <summary>
/// Estimates the Google Play download size the same way the old batch file did:
/// build the full split-APK set, then ask bundletool for its total size across all devices.
/// This takes tens of seconds for a big game, so callers run it in the background.
/// </summary>
public static class BundleSizeEstimator
{
    public static async Task<SizeEstimateResult> EstimateAsync(string aabPath, ToolSet tools, CancellationToken ct)
    {
        if (tools.JavaPath is null)
            return Fail(ToolErrors.JavaMissing);
        if (tools.BundletoolPath is null)
            return Fail(ToolErrors.BundletoolMissing);

        var temp = AppPaths.NewTempDir();
        try
        {
            var apks = Path.Combine(temp, "estimate.apks");

            var build = await ProcessRunner.RunAsync(tools.JavaPath,
                new[] { "-jar", tools.BundletoolPath, "build-apks", $"--bundle={aabPath}", $"--output={apks}", "--mode=default", "--overwrite" },
                ct, timeout: TimeSpan.FromMinutes(15));
            if (build.Cancelled) return new SizeEstimateResult { Cancelled = true };
            if (!build.Success) return Fail(ErrorInterpreter.Build("Estimating the download size", build.Combined));

            var size = await ProcessRunner.RunAsync(tools.JavaPath,
                new[] { "-jar", tools.BundletoolPath, "get-size", "total", $"--apks={apks}" },
                ct, timeout: TimeSpan.FromMinutes(5));
            if (size.Cancelled) return new SizeEstimateResult { Cancelled = true };
            if (!size.Success) return Fail(ErrorInterpreter.Build("Estimating the download size", size.Combined));

            // Output is "MIN,MAX" followed by "<bytes>,<bytes>".
            foreach (var raw in size.StdOut.Split('\n'))
            {
                var parts = raw.Trim().Split(',');
                if (parts.Length == 2 && long.TryParse(parts[0], out var min) && long.TryParse(parts[1], out var max))
                    return new SizeEstimateResult { Range = new SizeRange(min, max) };
            }
            return Fail(ErrorInterpreter.Custom("Couldn't read the size from bundletool's output", technical: OutputFilter.KeyLines(OutputFilter.Clean(size.Combined))));
        }
        catch (OperationCanceledException)
        {
            return new SizeEstimateResult { Cancelled = true };
        }
        finally
        {
            AppPaths.TryDeleteDir(temp);
        }
    }

    private static SizeEstimateResult Fail(OperationError e) => new() { Error = e };
}

public static class ToolErrors
{
    public static OperationError JavaMissing { get; } = ErrorInterpreter.Custom(
        "Java wasn't found",
        "Install a JDK (17 or later), or set the JAVA_HOME environment variable. Android Studio and Unity both include one.");

    public static OperationError BundletoolMissing { get; } = ErrorInterpreter.Custom(
        "bundletool wasn't found",
        "Put bundletool-all-<version>.jar in the \"Dependencies\" folder next to this program.");

    public static OperationError AdbMissing { get; } = ErrorInterpreter.Custom(
        "adb wasn't found",
        "Install the Android SDK Platform-Tools (or Android Studio) and make sure adb is on your PATH, or copy platform-tools into \"Dependencies\".");

    public static OperationError ApkSignerMissing { get; } = ErrorInterpreter.Custom(
        "apksigner wasn't found",
        "Install the Android SDK Build-Tools (Android Studio → SDK Manager) so APK files can be signed.");
}
