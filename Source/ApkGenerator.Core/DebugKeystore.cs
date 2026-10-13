namespace ApkGenerator.Core;

/// <summary>
/// Android tools sign test builds with ~/.android/debug.keystore, which Android Studio / Gradle create on first use.
/// On a machine that never ran those (e.g. a Mac with only Unity) it doesn't exist, so create the standard one with keytool.
/// </summary>
public static class DebugKeystore
{
    public static async Task<OperationError?> EnsureAsync(ToolSet tools, Action<string> log, CancellationToken ct)
    {
        var path = BuildService.DebugKeystorePath;
        if (File.Exists(path)) return null;

        if (tools.KeytoolPath is null)
            return ErrorInterpreter.Custom("There's no key to sign the APK with",
                "No Android debug keystore exists and keytool (part of Java) wasn't found to create one. Install a JDK, or tick Sign APK and choose a keystore.");

        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); }
        catch (Exception ex)
        {
            return ErrorInterpreter.Custom("Couldn't create the Android debug keystore", technical: ex.Message);
        }

        log("Creating the Android debug keystore (first time only) …");
        var r = await ProcessRunner.RunAsync(tools.KeytoolPath, new[]
        {
            "-genkeypair", "-keystore", path, "-storetype", "JKS", "-storepass", "android",
            "-alias", "androiddebugkey", "-keypass", "android", "-keyalg", "RSA", "-keysize", "2048", "-validity", "10000",
            "-dname", "CN=Android Debug,O=Android,C=US",
        }, ct, timeout: TimeSpan.FromSeconds(60));

        if (r.Cancelled) return ErrorInterpreter.Custom("Cancelled");
        if (!r.Success || !File.Exists(path))
            return ErrorInterpreter.Custom("Couldn't create the Android debug keystore",
                "Tick Sign APK and choose a keystore instead.", OutputFilter.KeyLines(OutputFilter.Clean(r.Combined)));
        return null;
    }
}
