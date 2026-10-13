using System.Globalization;
using System.Text.RegularExpressions;

namespace ApkGenerator.Core;

public sealed class AdbDevice
{
    public string Serial { get; init; } = "";
    /// <summary>adb's state word: device, unauthorized, offline …</summary>
    public string State { get; init; } = "";
    public string? Model { get; init; }
    public string? Manufacturer { get; init; }
    public string? AndroidRelease { get; init; }

    public bool IsUsable => State == "device";
    public bool IsWireless => Serial.Contains(':') || Serial.Contains("_adb-tls-");

    /// <summary>"Samsung SM-S906B · Android 14"</summary>
    public string Name
    {
        get
        {
            var model = (Model ?? "").Replace('_', ' ').Trim();
            var maker = Manufacturer?.Trim() ?? "";
            if (maker.Length > 0)
                maker = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(maker.ToLowerInvariant());

            string name;
            if (model.Length == 0) name = Serial;
            else if (maker.Length == 0 || model.StartsWith(maker, StringComparison.OrdinalIgnoreCase)) name = model;
            else name = $"{maker} {model}";

            if (!string.IsNullOrWhiteSpace(AndroidRelease)) name += $" · Android {AndroidRelease}";
            return name;
        }
    }

    /// <summary>What the dropdown shows.</summary>
    public string DisplayName => State switch
    {
        "device" => $"{Name}  [{Serial}]",
        "unauthorized" => $"{Serial} – unauthorised (accept the prompt on the device)",
        "offline" => $"{Serial} – offline",
        _ => $"{Serial} – {State}",
    };
}

public sealed class DeviceListResult
{
    public List<AdbDevice> Devices { get; init; } = new();
    public OperationError? Error { get; init; }
}

public static class Adb
{
    public static async Task<DeviceListResult> ListDevicesAsync(ToolSet tools, CancellationToken ct = default)
    {
        if (tools.AdbPath is null) return new DeviceListResult { Error = ToolErrors.AdbMissing };

        var r = await ProcessRunner.RunAsync(tools.AdbPath, new[] { "devices", "-l" }, ct, timeout: TimeSpan.FromSeconds(25));
        if (r.Cancelled) return new DeviceListResult();
        if (!r.Success)
            return new DeviceListResult
            {
                Error = r.StartError != null
                    ? ErrorInterpreter.Custom("Couldn't start adb", technical: r.StartError)
                    : ErrorInterpreter.Adb("Couldn't list devices", r.Combined),
            };

        var raw = new List<(string serial, string state, string? model)>();
        foreach (var line in r.StdOut.Replace("\r", "").Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith("List of devices", StringComparison.Ordinal) || t.StartsWith('*')) continue;
            var parts = Regex.Split(t, @"\s+");
            if (parts.Length < 2) continue;
            var model = parts.Skip(2).Select(p => p.Split(':', 2)).Where(kv => kv.Length == 2 && kv[0] == "model").Select(kv => kv[1]).FirstOrDefault();
            raw.Add((parts[0], parts[1], model));
        }

        // Ask each usable device for a friendly name – in parallel, never failing the whole list.
        var devices = await Task.WhenAll(raw.Select(async d =>
        {
            if (d.state != "device")
                return new AdbDevice { Serial = d.serial, State = d.state, Model = d.model };
            try
            {
                var props = await ProcessRunner.RunAsync(tools.AdbPath,
                    new[] { "-s", d.serial, "shell", "getprop ro.product.manufacturer; getprop ro.product.model; getprop ro.build.version.release" },
                    ct, timeout: TimeSpan.FromSeconds(6));
                var lines = props.StdOut.Replace("\r", "").Split('\n').Select(l => l.Trim()).ToArray();
                return new AdbDevice
                {
                    Serial = d.serial,
                    State = d.state,
                    Manufacturer = lines.ElementAtOrDefault(0),
                    Model = !string.IsNullOrWhiteSpace(lines.ElementAtOrDefault(1)) ? lines[1] : d.model,
                    AndroidRelease = lines.ElementAtOrDefault(2),
                };
            }
            catch
            {
                return new AdbDevice { Serial = d.serial, State = d.state, Model = d.model };
            }
        }));

        return new DeviceListResult { Devices = devices.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList() };
    }

    /// <summary>adb connect host[:port] – port defaults to 5555.</summary>
    public static async Task<(bool ok, string message, OperationError? error)> ConnectAsync(ToolSet tools, string address, CancellationToken ct = default)
    {
        if (tools.AdbPath is null) return (false, "", ToolErrors.AdbMissing);

        address = address.Trim();
        if (address.Length == 0)
            return (false, "", ErrorInterpreter.Custom("Enter the device's IP address", "For example 192.168.1.50 or 192.168.1.50:5555."));
        if (!address.Contains(':')) address += ":5555";

        var r = await ProcessRunner.RunAsync(tools.AdbPath, new[] { "connect", address }, ct, timeout: TimeSpan.FromSeconds(20));
        var text = OutputFilter.Clean(r.Combined);
        if (r.TimedOut) return (false, "", ErrorInterpreter.Custom($"Timed out connecting to {address}", "Check the IP address and port, and that the device and PC are on the same network."));

        // adb prints failures on stdout with exit code 0, so look at the message.
        if (Regex.IsMatch(text, @"connected to", RegexOptions.IgnoreCase) && !Regex.IsMatch(text, @"failed|cannot|unable", RegexOptions.IgnoreCase))
            return (true, text.Split('\n').Last().Trim(), null);

        return (false, "", ErrorInterpreter.Adb($"Couldn't connect to {address}", text));
    }
}
