using System.Text.RegularExpressions;

namespace ApkGenerator.Core;

/// <summary>What the UI shows when something fails: a plain-English title, a suggested fix, and the key lines of tool output.</summary>
public sealed class OperationError
{
    public string Title { get; init; } = "";
    public string? Hint { get; init; }
    /// <summary>The relevant lines of raw tool output (noise and stack traces removed).</summary>
    public string? Technical { get; init; }

    public override string ToString() =>
        string.Join(Environment.NewLine, new[] { Title, Hint, Technical }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public static class ErrorInterpreter
{
    private sealed record Rule(Regex Pattern, string Title, string? Hint);

    private static Rule R(string pattern, string title, string? hint = null) =>
        new(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant), title, hint);

    // ------------------------------------------------------------------ adb / install

    private static readonly Rule[] InstallRules =
    {
        R("INSTALL_FAILED_UPDATE_INCOMPATIBLE|signatures do not match",
          "A copy of this app is already installed, signed with a different key",
          "Tick \"Uninstall conflicting versions if needed\" (this deletes the installed app's data), or sign this build with the same keystore as the installed one."),
        R("INSTALL_FAILED_VERSION_DOWNGRADE|Downgrade detected",
          "The device already has a newer version of this app",
          "Android only allows downgrades of debuggable apps. Tick \"Uninstall conflicting versions if needed\" to remove the newer version first."),
        R("INSTALL_FAILED_INSUFFICIENT_STORAGE|No space left on device",
          "The device doesn't have enough free storage", "Free up some space on the device and try again."),
        R("INSTALL_FAILED_USER_RESTRICTED|INSTALL_CANCELED_BY_USER|INSTALL_FAILED_ABORTED|Install canceled",
          "The device blocked or cancelled the install",
          "Check the phone screen for an install prompt and accept it. On Xiaomi/Oppo/Vivo/Realme turn on \"Install via USB\" (developer options)."),
        R("INSTALL_FAILED_VERIFICATION_FAILURE|INSTALL_FAILED_VERIFICATION_TIMEOUT",
          "The device's package verifier rejected the install",
          "Turn off \"Verify apps over USB\" / Play Protect scanning on the device, or accept the prompt shown on the phone."),
        R("INSTALL_FAILED_NO_MATCHING_ABIS",
          "The app has no native libraries for this device's CPU",
          "The build doesn't include the CPU architecture (ABI) this device needs. Check the ABIs shown for the file and your Unity build's target architectures."),
        R("INSTALL_FAILED_OLDER_SDK",
          "The device's Android version is older than the app supports",
          "Raise the device's Android version, or lower the app's minimum SDK."),
        R("INSTALL_FAILED_DEPRECATED_SDK_VERSION|target at least SDK",
          "The device refuses apps that target such an old Android version",
          "Raise the app's target SDK, or install it with adb using --bypass-low-target-sdk-block."),
        R("INSTALL_PARSE_FAILED_NO_CERTIFICATES|INSTALL_PARSE_FAILED_INCONSISTENT_CERTIFICATES|no certificates",
          "The APK isn't signed correctly",
          "Tick \"Sign APK\" and pick a keystore, or make sure the debug keystore exists."),
        R("INSTALL_PARSE_FAILED_NOT_APK|INSTALL_PARSE_FAILED_BAD_MANIFEST|INSTALL_PARSE_FAILED_MANIFEST_MALFORMED|INSTALL_FAILED_INVALID_APK",
          "Android couldn't read the APK", "The file may be corrupt or incomplete – rebuild it and try again."),
        R("INSTALL_FAILED_TEST_ONLY",
          "The app is marked test-only", null),
        R("INSTALL_FAILED_DUPLICATE_PERMISSION",
          "Another installed app already defines one of this app's custom permissions",
          "Uninstall the other app that uses the same permission name."),
        R("INSTALL_FAILED_SHARED_USER_INCOMPATIBLE|INSTALL_FAILED_CONFLICTING_PROVIDER",
          "This app conflicts with another app on the device", "Uninstall the conflicting app and try again."),
        R("INSTALL_FAILED_MISSING_SHARED_LIBRARY",
          "The device is missing a shared library the app needs", null),
        R("INSTALL_FAILED_DEXOPT",
          "The device couldn't optimise the app's code",
          "Free up storage, restart the device, or rebuild the app."),
    };

    private static readonly Rule[] AdbRules =
    {
        R("unauthorized",
          "The device hasn't authorised this computer",
          "Unlock the phone and accept the \"Allow USB debugging?\" prompt, then press Refresh."),
        R("device offline|error: closed|device '.*' not found|no devices/emulators found|device not found",
          "The device is offline or not connected",
          "Reconnect the cable, or switch the phone's USB mode (e.g. File transfer / PTP), then press Refresh."),
        R("more than one device",
          "More than one device is connected", "Pick a specific device in the device list."),
        R("protocol fault|Connection reset|broken pipe|Error during Sync|connection to a device was lost|couldn't read status",
          "The connection to the device was lost",
          "Reconnect the cable or try another USB port/mode, then try again."),
        R("cannot connect to daemon|failed to start daemon|could not install \\*smartsocket\\* listener|server version .* doesn't match",
          "The adb server couldn't be started",
          "Close other Android tools (Android Studio, other adb copies), or run \"adb kill-server\", then press Refresh."),
        R("failed to connect to|cannot connect to|Connection refused|connect.*timed out|\\(10060\\)|\\(10061\\)",
          "Couldn't connect to the device over the network",
          "Check the IP address and port, that the device and PC are on the same network, and that wireless debugging is enabled."),
    };

    // ------------------------------------------------------------------ bundletool

    private static readonly Rule[] BundletoolRules =
    {
        R("Incorrect keystore password|Keystore was tampered with|password was incorrect",
          "The keystore password is incorrect", "Re-enter the keystore password."),
        R("Incorrect key password|Cannot recover key",
          "The alias (key) password is incorrect",
          "Re-enter the alias password. If the key uses the same password as the keystore, leave the alias password blank."),
        R("No key found with alias",
          "That alias doesn't exist in the keystore", "Pick one of the aliases from the dropdown."),
        R("UnsupportedClassVersionError",
          "The installed Java is too old for this version of bundletool",
          "Install a newer JDK (17 or later), or put an older bundletool jar in the Dependencies folder."),
        R("OutOfMemoryError",
          "Bundletool ran out of memory", "Close other programs and try again."),
        R("No space left on device|not enough space",
          "The disk is full", "Free up disk space (bundletool works in your Temp folder)."),
        R("Unable to access jarfile|Invalid or corrupt jarfile",
          "The bundletool jar couldn't be opened", "Re-download bundletool into the Dependencies folder."),
        R("InvalidBundleException|is not a valid|Bundle is invalid",
          "The app bundle isn't valid", "Rebuild the .aab from Unity / Android Studio."),
    };

    // apksigner's wording. Order matters: all three failures start with "Failed to load signer".
    private static readonly Rule[] SigningRules =
    {
        R("does not contain a key|No key with alias|No key found with alias",
          "That alias doesn't exist in the keystore", "Pick one of the aliases from the dropdown."),
        R("Failed to obtain key|Cannot recover key|Wrong password\\?|Incorrect key password",
          "The alias (key) password is incorrect",
          "Re-enter the alias password. If the key uses the same password as the keystore, leave the alias password blank."),
        R("Keystore was tampered with|password was incorrect|Password verification failed|Incorrect keystore password",
          "The keystore password is incorrect", "Re-enter the keystore password."),
    };

    // ------------------------------------------------------------------ public API

    public static OperationError Build(string stage, string output) =>
        Interpret($"{stage} failed", output, BundletoolRules);

    public static OperationError Sign(string output) =>
        Interpret("Signing the APK failed", output, SigningRules.Concat(BundletoolRules).ToArray());

    public static OperationError Install(string output, string? packageName = null) =>
        Interpret("Install failed", output, InstallRules.Concat(AdbRules).ToArray());

    public static OperationError Adb(string title, string output) =>
        Interpret(title, output, AdbRules);

    public static OperationError Custom(string title, string? hint = null, string? technical = null) =>
        new() { Title = title, Hint = hint, Technical = technical };

    private static OperationError Interpret(string fallbackTitle, string output, Rule[] rules)
    {
        var clean = OutputFilter.Clean(output);
        var technical = OutputFilter.KeyLines(clean);

        // Prefer bundletool's own "[BT:x] Error: message" – it's already human-readable.
        var bt = Regex.Match(clean, @"\[BT:[^\]]*\]\s*Error:\s*(.+)");

        foreach (var rule in rules)
        {
            if (rule.Pattern.IsMatch(clean))
                return new OperationError { Title = rule.Title, Hint = rule.Hint, Technical = technical };
        }

        if (bt.Success)
            return new OperationError { Title = fallbackTitle + ": " + bt.Groups[1].Value.Trim(), Technical = technical };

        // adb: "Failure [INSTALL_FAILED_X: details]"
        var fail = Regex.Match(clean, @"Failure \[([^\]]+)\]");
        if (fail.Success)
            return new OperationError { Title = fallbackTitle + ": " + fail.Groups[1].Value.Trim(), Technical = technical };

        return new OperationError
        {
            Title = fallbackTitle,
            Technical = string.IsNullOrWhiteSpace(technical) ? "The tool produced no output." : technical,
        };
    }
}

public static class OutputFilter
{
    /// <summary>Drops the JVM's harmless sun.misc.Unsafe warnings and blank lines.</summary>
    public static string Clean(string output)
    {
        var lines = output.Replace("\r", "").Split('\n')
            .Where(l => !IsNoise(l));
        return string.Join("\n", lines).Trim();
    }

    // Harmless JVM warnings that newer Javas print when running bundletool / apksigner.
    private static readonly Regex JvmNoise = new(
        @"^WARNING: .*(sun\.misc\.Unsafe|restricted method|enable-native-access|Restricted methods|java\.lang\.System::|Please consider reporting)",
        RegexOptions.Compiled);

    public static bool IsNoise(string line) =>
        string.IsNullOrWhiteSpace(line) ||
        JvmNoise.IsMatch(line) ||
        line.StartsWith("Picked up JAVA_TOOL_OPTIONS", StringComparison.Ordinal);

    /// <summary>First lines of the output that aren't stack frames – the part worth showing in an error box.</summary>
    public static string KeyLines(string cleaned, int max = 8)
    {
        var keep = new List<string>();
        foreach (var raw in cleaned.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (Regex.IsMatch(line, @"^\s+at [\w$.<>/]+\(.*\)$")) continue;      // stack frame
            if (Regex.IsMatch(line, @"^\s*\.\.\. \d+ more$")) continue;           // stack trace elision
            if (line.StartsWith("INFO:", StringComparison.Ordinal)) continue;
            if (line.Length > 400) line = line[..400] + "…";
            keep.Add(line);
            if (keep.Count >= max) break;
        }
        return string.Join(Environment.NewLine, keep);
    }
}
