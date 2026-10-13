using System.Text.RegularExpressions;

namespace ApkGenerator.Core;

public sealed class InstallOptions
{
    /// <summary>adb install -d (Android only honours it for debuggable apps).</summary>
    public bool AllowDowngrade { get; set; } = true;
    /// <summary>If the install is refused because of a signature clash or a newer version, uninstall the old app and retry.</summary>
    public bool UninstallConflicting { get; set; }
    /// <summary>pm clear after installing.</summary>
    public bool ClearData { get; set; }
    /// <summary>adb install -g</summary>
    public bool GrantPermissions { get; set; }
    public bool Launch { get; set; } = true;
}

public sealed class DeviceInstallResult
{
    public required AdbDevice Device { get; init; }
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    /// <summary>One line for the results list: "Installed and launched", "Installed (replaced conflicting version)", …</summary>
    public string Summary { get; init; } = "";
    public OperationError? Error { get; init; }
}

public static class DeviceInstaller
{
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan ShellTimeout = TimeSpan.FromSeconds(60);

    public static async Task<DeviceInstallResult> InstallAsync(
        AdbDevice device, string apkPath, PackageInfo pkg, InstallOptions opt, ToolSet tools,
        Action<string> status, Action<string> log, CancellationToken ct)
    {
        if (tools.AdbPath is null)
            return Failed(device, ToolErrors.AdbMissing);
        if (string.IsNullOrEmpty(pkg.PackageName))
            return Failed(device, ErrorInterpreter.Custom("The package name couldn't be read from this file"));
        var package = pkg.PackageName!;
        var tag = $"[{device.Serial}] ";

        async Task<ProcessResult> RunAdb(TimeSpan timeout, params string[] args)
        {
            var full = new List<string> { "-s", device.Serial };
            full.AddRange(args);
            log(tag + "> adb " + string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
            return await ProcessRunner.RunAsync(tools.AdbPath, full, ct, onLine: l => log(tag + l), timeout: timeout);
        }

        Task<ProcessResult> RunInstall()
        {
            var args = new List<string> { "install", "-r", "-t" };
            if (opt.AllowDowngrade) args.Add("-d");
            if (opt.GrantPermissions) args.Add("-g");
            args.Add(apkPath);
            return RunAdb(InstallTimeout, args.ToArray());
        }

        try
        {
            if (!device.IsUsable)
                return Failed(device, ErrorInterpreter.Adb("The device isn't ready", device.State));

            status("Installing…");
            var r = await RunInstall();
            if (r.Cancelled) return Cancelled(device);

            var replacedConflict = false;
            if (!InstallSucceeded(r) && opt.UninstallConflicting && IsConflict(r.Combined))
            {
                status("Removing the conflicting version…");
                var u = await RunAdb(ShellTimeout, "uninstall", package);
                if (u.Cancelled) return Cancelled(device);
                if (!Regex.IsMatch(u.Combined, @"^\s*Success\s*$", RegexOptions.Multiline))
                    return Failed(device, ErrorInterpreter.Custom(
                        "Couldn't uninstall the conflicting version",
                        "Try uninstalling the app on the device by hand.",
                        OutputFilter.KeyLines(OutputFilter.Clean(u.Combined))));

                replacedConflict = true;
                status("Installing…");
                r = await RunInstall();
                if (r.Cancelled) return Cancelled(device);
            }

            if (!InstallSucceeded(r))
                return Failed(device, r.StartError != null
                    ? ErrorInterpreter.Custom("Couldn't start adb", technical: r.StartError)
                    : r.TimedOut
                        ? ErrorInterpreter.Custom("The install timed out",
                            "Check the phone for an install prompt (some phones ask for confirmation), then try again.")
                        : ErrorInterpreter.Install(r.Combined, package));

            var parts = new List<string> { replacedConflict ? "Installed (replaced the conflicting version)" : "Installed" };

            if (opt.ClearData)
            {
                status("Clearing app data…");
                var c = await RunAdb(ShellTimeout, "shell", "pm", "clear", package);
                if (c.Cancelled) return Cancelled(device);
                if (!Regex.IsMatch(c.Combined, @"^\s*Success\s*$", RegexOptions.Multiline))
                    return Failed(device, ErrorInterpreter.Custom(
                        "Installed, but clearing the app data failed",
                        technical: OutputFilter.KeyLines(OutputFilter.Clean(c.Combined))));
                parts.Add("data cleared");
            }

            if (opt.Launch)
            {
                status("Launching…");
                var launchError = await LaunchAsync();
                if (ct.IsCancellationRequested) return Cancelled(device);
                if (launchError != null)
                    return Failed(device, ErrorInterpreter.Custom("Installed, but the app couldn't be launched", technical: launchError));
                parts.Add("launched");
            }

            return new DeviceInstallResult { Device = device, Success = true, Summary = string.Join(", ", parts) };
        }
        catch (OperationCanceledException)
        {
            return Cancelled(device);
        }

        async Task<string?> LaunchAsync()
        {
            if (!string.IsNullOrEmpty(pkg.LauncherActivity))
            {
                var activity = pkg.LauncherActivity!;
                if (!activity.Contains('.')) activity = "." + activity;
                var a = await RunAdb(ShellTimeout, "shell", $"am start -n '{package}/{activity}'");
                if (a.Success && !Regex.IsMatch(a.Combined, @"Error|Exception|does not exist", RegexOptions.IgnoreCase))
                    return null;
            }

            // Fall back to letting the system pick the launcher activity.
            var m = await RunAdb(ShellTimeout, "shell", $"monkey -p '{package}' -c android.intent.category.LAUNCHER 1");
            if (m.Success && !Regex.IsMatch(m.Combined, @"No activities found|monkey aborted|Error", RegexOptions.IgnoreCase))
                return null;
            return OutputFilter.KeyLines(OutputFilter.Clean(m.Combined));
        }
    }

    private static bool InstallSucceeded(ProcessResult r) =>
        r.Success && Regex.IsMatch(r.Combined, @"^\s*Success\s*$", RegexOptions.Multiline);

    /// <summary>Failures that uninstalling the existing copy can fix.</summary>
    private static bool IsConflict(string output) =>
        Regex.IsMatch(output, "INSTALL_FAILED_UPDATE_INCOMPATIBLE|signatures do not match|INSTALL_FAILED_VERSION_DOWNGRADE|Downgrade detected", RegexOptions.IgnoreCase);

    private static DeviceInstallResult Failed(AdbDevice d, OperationError e) =>
        new() { Device = d, Error = e, Summary = e.Title };

    private static DeviceInstallResult Cancelled(AdbDevice d) =>
        new() { Device = d, Cancelled = true, Summary = "Cancelled" };
}
