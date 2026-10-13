# APK Generator

Drag an `.aab` or `.apk` to get information about the build.
You can then optionally sign it, save it as an APK, or install to connected devices.
Also includes functionality for directly uploading built APKs to FTP.

![APK Generator preview](docs/preview.png)

## What is it

Long ago I wrote a custom batch script to generate APKs from AABs, automate installs and give other build info without needing to use the command line.
This is basically a port of my old batch script to an executable with simpler UI, built by Claude.

## How to use it

1. Run `APK Generator.exe` and drag your file into the window.
2. Pick a device and choose **Install & Run**, or **Save APK**.
3. (Optional) Tick **Sign APK**, select the keystore and enter the passwords - details are remembered for future.
4. (Optional) Press the cog to enter the FTP details to enable **Save APK to FTP** for direct FTP uploads.

Build and install failures directly appear in the window error log so you don't need to look through logcat.

## What you need

- Java (If you have Android Studio or Unity installed you should already have this)
- ADB (Included with Android Studio, required if you want to install to a connected device)

Add keystores into the `Keystores` folder, browsing for keystores will also store a copy in the folder if they don't exist.

## Building from source

Needs the .NET 10 SDK. Run `Source\build.ps1`, or `Source\build.ps1 -Package` to also generate the shared zip.
