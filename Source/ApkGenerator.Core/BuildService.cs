using System.IO.Compression;

namespace ApkGenerator.Core;

public sealed record SigningConfig(string KeystorePath, string StorePassword, string Alias, string KeyPassword);

public sealed class BuildOutcome
{
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    public string? OutputPath { get; init; }
    public OperationError? Error { get; init; }

    public static BuildOutcome Ok(string path) => new() { Success = true, OutputPath = path };
    public static BuildOutcome Fail(OperationError e) => new() { Error = e };
    public static BuildOutcome Cancel() => new() { Cancelled = true };
}

public static class BuildService
{
    public static string DebugKeystorePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android", "debug.keystore");

    /// <summary>
    /// The default name for a built APK: "&lt;name&gt;_universal.apk" for an AAB ("_universal_signed.apk" when a keystore is used),
    /// or "&lt;name&gt;_signed.apk" when re-signing an APK.
    /// </summary>
    public static string DefaultFileName(string inputPath, bool inputIsAab, bool signedWithKeystore)
    {
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        return inputIsAab ? stem + (signedWithKeystore ? "_universal_signed.apk" : "_universal.apk") : stem + "_signed.apk";
    }

    /// <summary>
    /// AAB → universal APK written to <paramref name="outputPath"/>.
    /// Without a keystore bundletool signs with the Android debug key.
    /// </summary>
    public static async Task<BuildOutcome> BuildUniversalApkAsync(
        string aabPath, SigningConfig? signing, string outputPath, ToolSet tools, Action<string> log, CancellationToken ct)
    {
        if (tools.JavaPath is null) return BuildOutcome.Fail(ToolErrors.JavaMissing);
        if (tools.BundletoolPath is null) return BuildOutcome.Fail(ToolErrors.BundletoolMissing);

        if (signing is null && !File.Exists(DebugKeystorePath))
            return BuildOutcome.Fail(ErrorInterpreter.Custom(
                "There's no key to sign the APK with",
                "Tick \"Sign APK\" and choose a keystore. (Without one, bundletool uses the Android debug keystore, which doesn't exist at " + DebugKeystorePath + ".)"));

        var temp = AppPaths.NewTempDir();
        try
        {
            var apks = Path.Combine(temp, "universal.apks");
            var args = new List<string>
            {
                "-jar", tools.BundletoolPath, "build-apks",
                $"--bundle={aabPath}", $"--output={apks}", "--mode=universal", "--overwrite",
            };
            if (signing != null)
            {
                args.Add($"--ks={signing.KeystorePath}");
                args.Add($"--ks-pass=pass:{signing.StorePassword}");
                args.Add($"--ks-key-alias={signing.Alias}");
                args.Add($"--key-pass=pass:{signing.KeyPassword}");
            }

            log("> " + CommandLine.Format(tools.JavaPath, args));
            var r = await ProcessRunner.RunAsync(tools.JavaPath, args, ct, onLine: l => { if (!OutputFilter.IsNoise(l)) log(l); },
                timeout: TimeSpan.FromMinutes(20));
            if (r.Cancelled) return BuildOutcome.Cancel();
            if (!r.Success)
                return BuildOutcome.Fail(r.StartError != null
                    ? ErrorInterpreter.Custom("Couldn't start Java", technical: r.StartError)
                    : ErrorInterpreter.Build("Building the APK", r.Combined));

            var final = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);

            log("Extracting universal.apk …");
            using (var zip = ZipFile.OpenRead(apks))
            {
                var entry = zip.GetEntry("universal.apk")
                    ?? throw new InvalidDataException("bundletool's output doesn't contain universal.apk.");
                var part = final + ".part";
                entry.ExtractToFile(part, overwrite: true);
                File.Move(part, final, overwrite: true);
            }
            log("Saved " + final);
            return BuildOutcome.Ok(final);
        }
        catch (OperationCanceledException)
        {
            return BuildOutcome.Cancel();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return BuildOutcome.Fail(ErrorInterpreter.Custom("Couldn't save the APK", "Check that the Generated APKs folder is writable and the file isn't in use.", ex.Message));
        }
        finally
        {
            AppPaths.TryDeleteDir(temp);
        }
    }

    /// <summary>Re-signs an existing APK with the chosen keystore (zipalign, then apksigner), writing it to <paramref name="outputPath"/>.</summary>
    public static async Task<BuildOutcome> SignApkAsync(
        string apkPath, SigningConfig signing, string outputPath, ToolSet tools, Action<string> log, CancellationToken ct)
    {
        if (tools.JavaPath is null) return BuildOutcome.Fail(ToolErrors.JavaMissing);
        if (tools.ApkSignerJar is null) return BuildOutcome.Fail(ToolErrors.ApkSignerMissing);

        var temp = AppPaths.NewTempDir();
        try
        {
            var input = apkPath;
            if (tools.ZipAlignPath != null)
            {
                var aligned = Path.Combine(temp, "aligned.apk");
                var zargs = new[] { "-f", "-p", "4", apkPath, aligned };
                log("> " + CommandLine.Format(tools.ZipAlignPath, zargs));
                var z = await ProcessRunner.RunAsync(tools.ZipAlignPath, zargs, ct, onLine: log, timeout: TimeSpan.FromMinutes(5));
                if (z.Cancelled) return BuildOutcome.Cancel();
                if (!z.Success)
                    return BuildOutcome.Fail(ErrorInterpreter.Custom("Aligning the APK failed", technical: OutputFilter.KeyLines(OutputFilter.Clean(z.Combined))));
                input = aligned;
            }
            else log("zipalign not found – signing without re-aligning.");

            var final = Path.GetFullPath(outputPath);
            if (string.Equals(final, Path.GetFullPath(apkPath), StringComparison.OrdinalIgnoreCase))
                return BuildOutcome.Fail(ErrorInterpreter.Custom("Choose a different file name",
                    "The signed copy can't overwrite the APK it is made from."));
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);

            var args = new List<string>
            {
                "-jar", tools.ApkSignerJar, "sign",
                "--ks", signing.KeystorePath,
                "--ks-key-alias", signing.Alias,
                "--ks-pass", "env:APKGENERATOR_KS_PASS",
                "--key-pass", "env:APKGENERATOR_KEY_PASS",
            };
            if (BuildToolsMajor(tools) >= 30) { args.Add("--v4-signing-enabled"); args.Add("false"); }
            args.Add("--out"); args.Add(final);
            args.Add(input);

            var env = new Dictionary<string, string>
            {
                ["APKGENERATOR_KS_PASS"] = signing.StorePassword,
                ["APKGENERATOR_KEY_PASS"] = signing.KeyPassword,
            };

            log("> " + CommandLine.Format(tools.JavaPath, args));
            var r = await ProcessRunner.RunAsync(tools.JavaPath, args, ct, onLine: l => { if (!OutputFilter.IsNoise(l)) log(l); },
                timeout: TimeSpan.FromMinutes(10), env: env);
            if (r.Cancelled) return BuildOutcome.Cancel();
            if (!r.Success)
                return BuildOutcome.Fail(r.StartError != null
                    ? ErrorInterpreter.Custom("Couldn't start Java", technical: r.StartError)
                    : ErrorInterpreter.Sign(r.Combined));

            log("Saved " + final);
            return BuildOutcome.Ok(final);
        }
        catch (OperationCanceledException)
        {
            return BuildOutcome.Cancel();
        }
        finally
        {
            AppPaths.TryDeleteDir(temp);
        }
    }

    private static int BuildToolsMajor(ToolSet tools) =>
        int.TryParse(tools.BuildToolsVersion?.Split('.', '-')[0], out var m) ? m : 0;
}
