using System.IO.Compression;
using System.Text.RegularExpressions;
using ApkGenerator.Core.Parsing;

namespace ApkGenerator.Core;

/// <summary>Reads package details out of an .aab or .apk instantly, with no external tools.</summary>
public static class PackageInspector
{
    /// <summary>Google Play minimum targetSdk for new apps and updates (Android 16, from 31 Aug 2026).</summary>
    public const int PlayMinTargetSdk = 36;

    public static PackageInfo Inspect(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("File not found.", path);

        // Windows paths are case-insensitive, so use the casing that's actually on disk for display.
        try
        {
            var actual = Directory.EnumerateFiles(fi.DirectoryName!, fi.Name).FirstOrDefault();
            if (actual != null) fi = new FileInfo(actual);
        }
        catch { /* keep the path as given */ }

        var ext = fi.Extension.ToLowerInvariant();
        var kind = ext switch
        {
            ".aab" => PackageKind.Aab,
            ".apk" => PackageKind.Apk,
            _ => throw new NotSupportedException($"'{fi.Extension}' files aren't supported – drop an .aab or .apk."),
        };

        var info = new PackageInfo { Kind = kind, FilePath = fi.FullName, FileSize = fi.Length };

        ZipArchive zip;
        try { zip = ZipFile.OpenRead(fi.FullName); }
        catch (InvalidDataException)
        {
            throw new InvalidDataException($"This doesn't look like a valid {kind.ToString().ToUpperInvariant()} file (it isn't a readable zip archive).");
        }

        using (zip)
        {
            var manifestEntry = zip.GetEntry(kind == PackageKind.Aab ? "base/manifest/AndroidManifest.xml" : "AndroidManifest.xml")
                ?? throw new InvalidDataException(kind == PackageKind.Aab
                    ? "This isn't a valid Android App Bundle (base/manifest/AndroidManifest.xml is missing)."
                    : "This isn't a valid APK (AndroidManifest.xml is missing).");

            byte[] bytes;
            using (var s = manifestEntry.Open())
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                bytes = ms.ToArray();
            }

            var root = kind == PackageKind.Aab ? ProtoXmlReader.Parse(bytes) : AxmlReader.Parse(bytes);
            FillFromManifest(info, root);

            var abis = new SortedSet<string>(StringComparer.Ordinal);
            var libRegex = kind == PackageKind.Aab
                ? new Regex(@"^[^/]+/lib/([^/]+)/[^/]+$")
                : new Regex(@"^lib/([^/]+)/[^/]+$");
            var moduleRegex = new Regex(@"^([^/]+)/manifest/AndroidManifest\.xml$");
            var modules = new List<string>();

            foreach (var entry in zip.Entries)
            {
                var m = libRegex.Match(entry.FullName);
                if (m.Success) abis.Add(m.Groups[1].Value);

                if (kind == PackageKind.Aab)
                {
                    var mm = moduleRegex.Match(entry.FullName);
                    if (mm.Success) modules.Add(mm.Groups[1].Value);
                }
            }
            info.Abis.AddRange(abis);
            info.Modules.AddRange(modules.OrderBy(x => x == "base" ? "" : x, StringComparer.OrdinalIgnoreCase));
        }
        return info;
    }

    private static void FillFromManifest(PackageInfo info, XmlElem root)
    {
        if (root.Name != "manifest")
            throw new InvalidDataException("The AndroidManifest.xml has an unexpected structure.");

        info.PackageName = root.Attr("package");
        if (long.TryParse(root.Attr("android:versionCode"), out var vc)) info.VersionCode = vc;
        info.VersionName = root.Attr("android:versionName");

        var sdk = root.ChildrenNamed("uses-sdk").FirstOrDefault();
        if (sdk != null)
        {
            if (int.TryParse(sdk.Attr("android:minSdkVersion"), out var min)) info.MinSdk = min;
            if (int.TryParse(sdk.Attr("android:targetSdkVersion"), out var target)) info.TargetSdk = target;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var perm in root.Children.Where(c => c.Name is "uses-permission" or "uses-permission-sdk-23" or "uses-permission-sdk-m"))
        {
            var name = perm.Attr("android:name");
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                info.Permissions.Add(new PermissionInfo(name, DangerousPermissions.Contains(name)));
        }
        info.Permissions.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        info.TestOnly = IsTrue(root.Attr("android:testOnly"));

        var app = root.ChildrenNamed("application").FirstOrDefault();
        if (app != null)
        {
            info.Debuggable = IsTrue(app.Attr("android:debuggable"));
            info.LauncherActivity = FindLauncher(app);
        }
    }

    private static bool IsTrue(string? v) => v is "true" or "1" or "0xffffffff" or "-1";

    private static string? FindLauncher(XmlElem app)
    {
        foreach (var comp in app.Children.Where(c => c.Name is "activity" or "activity-alias"))
        {
            foreach (var filter in comp.ChildrenNamed("intent-filter"))
            {
                var isMain = filter.ChildrenNamed("action").Any(a => a.Attr("android:name") == "android.intent.action.MAIN");
                var isLauncher = filter.ChildrenNamed("category").Any(c => c.Attr("android:name") == "android.intent.category.LAUNCHER");
                if (isMain && isLauncher) return comp.Attr("android:name");
            }
        }
        return null;
    }

    /// <summary>Things worth flagging before you upload to the store.</summary>
    public static IReadOnlyList<PackageWarning> GetWarnings(PackageInfo info, string? expectedPackagePrefix)
    {
        var list = new List<PackageWarning>();

        if (info.TargetSdk is { } t && t < PlayMinTargetSdk)
            list.Add(new(WarningLevel.Warning,
                $"Target SDK {t} is below Google Play's minimum of {PlayMinTargetSdk} for new apps and updates."));

        if (!string.IsNullOrEmpty(expectedPackagePrefix) && info.PackageName != null &&
            !info.PackageName.StartsWith(expectedPackagePrefix, StringComparison.OrdinalIgnoreCase))
            list.Add(new(WarningLevel.Warning, $"Package name doesn't use the {expectedPackagePrefix} format."));

        if (info.Debuggable)
            list.Add(new(WarningLevel.Warning, "This build is debuggable – Google Play rejects debuggable builds."));

        if (info.TestOnly)
            list.Add(new(WarningLevel.Warning, "This build is marked test-only – Google Play rejects test-only builds."));

        if (info.Abis.Count > 0 && info.Abis.Contains("armeabi-v7a") && !info.Abis.Contains("arm64-v8a"))
            list.Add(new(WarningLevel.Warning, "Contains 32-bit ARM libraries but no arm64-v8a – Google Play requires 64-bit support."));

        return list;
    }

    private static readonly HashSet<string> DangerousPermissions = new[]
    {
        "ACCEPT_HANDOVER", "ACCESS_BACKGROUND_LOCATION", "ACCESS_COARSE_LOCATION", "ACCESS_FINE_LOCATION",
        "ACCESS_MEDIA_LOCATION", "ACTIVITY_RECOGNITION", "ADD_VOICEMAIL", "ANSWER_PHONE_CALLS",
        "BLUETOOTH_ADVERTISE", "BLUETOOTH_CONNECT", "BLUETOOTH_SCAN", "BODY_SENSORS", "BODY_SENSORS_BACKGROUND",
        "CALL_PHONE", "CAMERA", "GET_ACCOUNTS", "NEARBY_WIFI_DEVICES", "POST_NOTIFICATIONS",
        "PROCESS_OUTGOING_CALLS", "READ_CALENDAR", "READ_CALL_LOG", "READ_CONTACTS", "READ_EXTERNAL_STORAGE",
        "READ_MEDIA_AUDIO", "READ_MEDIA_IMAGES", "READ_MEDIA_VIDEO", "READ_MEDIA_VISUAL_USER_SELECTED",
        "READ_PHONE_NUMBERS", "READ_PHONE_STATE", "READ_SMS", "RECEIVE_MMS", "RECEIVE_SMS", "RECEIVE_WAP_PUSH",
        "RECORD_AUDIO", "SEND_SMS", "UWB_RANGING", "USE_SIP", "WRITE_CALENDAR", "WRITE_CALL_LOG",
        "WRITE_CONTACTS", "WRITE_EXTERNAL_STORAGE",
    }.Select(n => "android.permission." + n).ToHashSet(StringComparer.Ordinal);
}

public static class AndroidVersions
{
    private static readonly Dictionary<int, string> Names = new()
    {
        [21] = "5.0", [22] = "5.1", [23] = "6.0", [24] = "7.0", [25] = "7.1", [26] = "8.0", [27] = "8.1",
        [28] = "9", [29] = "10", [30] = "11", [31] = "12", [32] = "12L", [33] = "13", [34] = "14",
        [35] = "15", [36] = "16", [37] = "17",
    };

    /// <summary>"API 36 · Android 16"</summary>
    public static string Describe(int? sdk)
    {
        if (sdk is not { } s) return "—";
        return Names.TryGetValue(s, out var n) ? $"API {s} · Android {n}" : $"API {s}";
    }
}
