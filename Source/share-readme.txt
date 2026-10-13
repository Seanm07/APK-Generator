APK Generator
===========================

Drag in an Android App Bundle (.aab) or an APK and the program shows what is inside it, turns an
.aab into an APK you can install, signs it with your keystore, installs it on your phone(s)
and can upload the finished APK to an FTP folder.


WHAT YOU NEED
-------------
1. Windows 10 or 11, 64-bit. Nothing to install for the program itself - just unzip this folder
   somewhere you can write to (for example your Documents folder) and run
   "APK Generator.exe".

2. Java (JDK 8 or newer, 17+ recommended). Needed to read and convert .aab files.
   Android Studio and Unity both include one. If you have neither, install "Eclipse Temurin"
   or "Microsoft Build of OpenJDK".

3. adb (Android SDK "Platform-Tools"). Only needed to install to a phone. Download
   "SDK Platform-Tools for Windows" from developer.android.com/tools/releases/platform-tools, then either
   add its folder to your PATH, or copy the whole platform-tools folder into the
   "Dependencies" folder next to the program. (If you already have Android Studio, you have it.)

4. Android SDK "Build-Tools" (apksigner and zipalign). Only needed to re-sign an existing .apk.
   Not needed for .aab files. Install it from Android Studio > SDK Manager.

The footer of the window shows which of these were found.


HOW TO USE
----------
- Drag an .aab or .apk into the window (or press Browse).
- Save APK              builds an APK from the .aab and saves it where you choose.
- Install & Run         installs it on the selected device (or "All devices") and starts it.
- Sign APK              tick it to sign with a keystore. Put your .keystore files in the "Keystores" folder
                        (or press Browse and the program copies it there). Enter the keystore password,
                        pick the alias and enter its password. The details last used with each keystore
                        are remembered (stored encrypted for your Windows account only).
                        If it is not ticked the APK is signed with the Android debug key.
- Settings (cog button) lets you enter FTP details. Once a server is entered a "Save APK to FTP"
                        button appears next to "Save APK".
- If anything goes wrong the reason is shown in the red panel at the bottom, with a suggested fix.


FILES
-----
APK Generator.exe          the program
Dependencies\bundletool-all-....jar      Google's bundletool (does the .aab -> .apk conversion)
Keystores\                               put your keystores here (they are not included in this zip)
Generated APKs\                          created automatically; APKs the program builds are kept here
ApkGenerator.settings.json   created on first use; holds your settings. Don't share this file.


TROUBLESHOOTING
---------------
- Windows says "Windows protected your PC": click "More info", then "Run anyway". The program is not
  code-signed. (If you downloaded the zip, right-click the zip > Properties > Unblock before unzipping.)
- The program does not start at all: install the "Microsoft Visual C++ Redistributable (x64)".
- "Java wasn't found": install a JDK (see above) and restart the program.
- The device list is empty: turn on USB debugging on the phone, plug it in, accept the prompt on the
  phone, then press the refresh button.
