namespace ApkGenerator.Core;

public sealed record DependencyLink(string Text, string Url);

/// <summary>
/// One thing the program relies on, whether it was found, and - if not - what stops working, how to fix it and where to download it.
/// </summary>
public sealed record DependencyStatus(
    string Name,
    string ShortName,
    bool Found,
    /// <summary>"Needed" (core features) versus "Optional" (one specific feature).</summary>
    bool Required,
    string FoundText,
    string MissingText,
    string WhatWontWork,
    string HowToFix,
    IReadOnlyList<DependencyLink> Links);

public static class DependencyChecker
{
    public static IReadOnlyList<DependencyStatus> Check(ToolSet tools, AppPaths paths)
    {
        var mac = OperatingSystem.IsMacOS();
        var studio = new DependencyLink("Download Android Studio", "https://developer.android.com/studio");

        return new[]
        {
            new DependencyStatus(
                Name: "Java (JDK)",
                ShortName: "Java",
                Found: tools.JavaPath != null,
                Required: true,
                FoundText: tools.JavaVersion != null ? $"Java {tools.JavaVersion}" : "Java",
                MissingText: "Java not found",
                WhatWontWork: "Building APKs from .aab files, the store download size estimate, signing, and listing keystore aliases. " +
                              "You can still look at a file's details and install an existing .apk.",
                HowToFix: "Install a JDK (version 17 or newer). Android Studio and Unity both include one, so installing either also works." +
                          (mac ? "\nOr in Terminal: brew install --cask temurin" : ""),
                Links: new[] { new DependencyLink("Download Java (Temurin JDK)", "https://adoptium.net/temurin/releases/"), studio }),

            new DependencyStatus(
                Name: "bundletool",
                ShortName: "bundletool",
                Found: tools.BundletoolPath != null,
                Required: true,
                FoundText: tools.BundletoolVersion != null ? $"bundletool {tools.BundletoolVersion}" : "bundletool",
                MissingText: "bundletool not found",
                WhatWontWork: "Building APKs from .aab files and the store download size estimate.",
                HowToFix: "This file normally comes with the app. Download bundletool-all-<version>.jar and put it in this folder:\n" +
                          paths.UserDependenciesDir,
                Links: new[] { new DependencyLink("Download bundletool", "https://github.com/google/bundletool/releases") }),

            new DependencyStatus(
                Name: "adb (Android Platform-Tools)",
                ShortName: "adb",
                Found: tools.AdbPath != null,
                Required: false,
                FoundText: tools.AdbVersion != null ? $"adb {tools.AdbVersion}" : "adb",
                MissingText: "adb not found",
                WhatWontWork: "Finding connected devices and installing apps to them (Install & Run). Building and saving APKs still works.",
                HowToFix: "Install the Android SDK Platform-Tools, or Android Studio which includes them. " +
                          "If you use Unity, add the Android Build Support module to your editor in Unity Hub." +
                          (mac ? "\nOr in Terminal: brew install --cask android-platform-tools" : ""),
                Links: new[] { new DependencyLink("Download Platform-Tools", "https://developer.android.com/tools/releases/platform-tools"), studio }),

            new DependencyStatus(
                Name: "Android SDK Build-Tools",
                ShortName: "build-tools",
                Found: tools.ApkSignerJar != null,
                Required: false,
                FoundText: tools.BuildToolsVersion != null ? $"build-tools {tools.BuildToolsVersion}" : "build-tools",
                MissingText: "build-tools not found (only needed to re-sign APKs)",
                WhatWontWork: "Re-signing an existing .apk with your keystore. Building signed APKs from .aab files still works.",
                HowToFix: "Install Android Studio, then open SDK Manager > SDK Tools and tick \"Android SDK Build-Tools\". " +
                          "If you use Unity, the Android Build Support module includes it.",
                Links: new[] { studio }),
        };
    }
}
