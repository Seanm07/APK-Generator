using System.Text.RegularExpressions;

namespace ApkGenerator.Core;

/// <summary>Resets the Amazon Appstore app on Fire devices (needed when testing Amazon IAP / DRM, which caches state in it).</summary>
public static class AmazonAppstore
{
    public const string PackageName = "com.amazon.venezia";
    private static readonly TimeSpan ShellTimeout = TimeSpan.FromSeconds(60);

    /// <summary>adb shell am force-stop com.amazon.venezia, then adb shell pm clear com.amazon.venezia.</summary>
    public static async Task<DeviceInstallResult> ResetAsync(AdbDevice device, ToolSet tools, Action<string> status, Action<string> log, CancellationToken ct)
    {
        if (tools.AdbPath is null) return Failed(device, ToolErrors.AdbMissing);
        if (!device.IsUsable) return Failed(device, ErrorInterpreter.Adb("The device isn't ready", device.State));

        var tag = $"[{device.Serial}] ";
        async Task<ProcessResult> RunAdb(params string[] args)
        {
            var full = new List<string> { "-s", device.Serial };
            full.AddRange(args);
            log(tag + "> adb " + string.Join(' ', args));
            return await ProcessRunner.RunAsync(tools.AdbPath, full, ct, onLine: l => log(tag + l), timeout: ShellTimeout);
        }

        try
        {
            status("Stopping the Appstore…");
            var stop = await RunAdb("shell", "am", "force-stop", PackageName);
            if (stop.Cancelled) return new DeviceInstallResult { Device = device, Cancelled = true, Summary = "Cancelled" };
            if (!stop.Success)
                return Failed(device, stop.StartError != null
                    ? ErrorInterpreter.Custom("Couldn't start adb", technical: stop.StartError)
                    : ErrorInterpreter.Custom("Couldn't stop the Amazon Appstore", technical: OutputFilter.KeyLines(OutputFilter.Clean(stop.Combined))));

            status("Clearing the Appstore's data…");
            var clear = await RunAdb("shell", "pm", "clear", PackageName);
            if (clear.Cancelled) return new DeviceInstallResult { Device = device, Cancelled = true, Summary = "Cancelled" };
            if (!Regex.IsMatch(clear.Combined, @"^\s*Success\s*$", RegexOptions.Multiline))
                return Failed(device, ErrorInterpreter.Custom("Couldn't clear the Amazon Appstore's data",
                    "This only works on Amazon Fire devices that have the Appstore installed.",
                    OutputFilter.KeyLines(OutputFilter.Clean(clear.Combined))));

            return new DeviceInstallResult { Device = device, Success = true, Summary = "Appstore stopped and its data cleared" };
        }
        catch (OperationCanceledException)
        {
            return new DeviceInstallResult { Device = device, Cancelled = true, Summary = "Cancelled" };
        }
    }

    private static DeviceInstallResult Failed(AdbDevice device, OperationError error) =>
        new() { Device = device, Error = error, Summary = error.Title };
}
