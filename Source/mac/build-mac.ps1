<#
  Builds the macOS version: "APK Generator.app" for Apple Silicon (arm64) and Intel (x64), as zips ready to share.
  Runs on Windows - nothing here needs a Mac.

  Usage:
    powershell -ExecutionPolicy Bypass -File build-mac.ps1                  unsigned zips (macOS will warn when opening them)
    powershell -ExecutionPolicy Bypass -File build-mac.ps1 -Sign            sign + notarize with the rcodesign setup, then zip
    powershell -ExecutionPolicy Bypass -File build-mac.ps1 -Sign -SkipNotarize   sign only (nothing is sent to Apple)
    ... -Arch arm64                                                          just one architecture

  Output: "APK Generator-mac-arm64.zip" and "APK Generator-mac-x64.zip" in the project folder.
  Needs: the .NET 10 SDK, Python (zip with Unix permissions), and for -Sign the folder from -SigningScript.
#>
param(
    [ValidateSet("arm64", "x64")][string[]]$Arch = @("arm64", "x64"),
    [switch]$Sign,
    [switch]$SkipNotarize,
    [string]$SigningScript = "C:\Users\Sean\signing\sign_mac_build.py"
)

$ErrorActionPreference = "Stop"
$src = Split-Path $PSScriptRoot -Parent          # ...\Source
$root = Split-Path $src -Parent                  # project folder
$proj = Join-Path $src "ApkGenerator.Mac\ApkGenerator.Mac.csproj"
$out = Join-Path $PSScriptRoot "out"
$appName = "APK Generator"

$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = "1.0.0" }

$jar = Get-ChildItem (Join-Path $root "Dependencies") -Filter "bundletool*.jar" |
    Sort-Object { [version]([regex]::Match($_.Name, '\d+(\.\d+)+').Value) } -Descending | Select-Object -First 1
if (-not $jar) { throw "No bundletool jar found in '$root\Dependencies'" }
$icns = Join-Path $src "ApkGenerator.Mac\Assets\AppIcon.icns"
if (-not (Test-Path $icns)) { & (Join-Path $PSScriptRoot "make-icns.ps1") -OutPath $icns }
if ($Sign -and -not (Test-Path $SigningScript)) { throw "Signing script not found: $SigningScript" }

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

# Apple's notary rejects an app that contains unsigned macOS binaries, even inside a jar - and bundletool's jar has two
# (aapt2 and JNA's libjnidispatch). Sign those inside a copy of the jar.
$jarFile = $jar.FullName
if ($Sign) {
    $signDir = Split-Path $SigningScript
    $jarFile = Join-Path $out $jar.Name
    & python (Join-Path $PSScriptRoot "prepare_jar.py") $jar.FullName $jarFile `
        --rcodesign (Join-Path $signDir "rcodesign\rcodesign.exe") `
        --p12 (Join-Path $signDir "developer-id.p12") `
        --p12-password-file (Join-Path $signDir "p12-password.txt")
    if ($LASTEXITCODE -ne 0) { throw "Signing the binaries inside the bundletool jar failed" }
}

foreach ($a in $Arch) {
    $rid = "osx-$a"
    $archDir = Join-Path $out $a
    $publish = Join-Path $archDir "publish"
    $app = Join-Path $archDir "$appName.app"
    Write-Host "`n=== $rid ===" -ForegroundColor Cyan

    # Single-file: the managed assemblies are embedded in the main executable. That matters - rcodesign only carries
    # native (Mach-O) files in Contents/MacOS through signing and silently drops everything else (every .dll!).
    dotnet publish $proj -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false `
        -p:DebugType=none -p:DebugSymbols=false -o $publish
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }

    # .app layout: code in MacOS, read-only resources (icon, bundletool) in Resources
    $macos = Join-Path $app "Contents\MacOS"
    $res = Join-Path $app "Contents\Resources"
    New-Item -ItemType Directory -Force $macos, (Join-Path $res "Dependencies") | Out-Null
    Copy-Item (Join-Path $publish "*") $macos -Recurse -Force
    Copy-Item $icns (Join-Path $res "AppIcon.icns")
    Copy-Item $jarFile (Join-Path $res "Dependencies")
    (Get-Content (Join-Path $PSScriptRoot "Info.plist") -Raw).Replace("@VERSION@", $version) |
        Set-Content (Join-Path $app "Contents\Info.plist") -Encoding UTF8 -NoNewline
    Remove-Item $publish -Recurse -Force

    # Contents/MacOS must hold native binaries only (see above) - fail now rather than ship an app that can't start.
    $machO = @("cafebabe", "bebafeca", "feedface", "feedfacf", "cefaedfe", "cffaedfe")
    $notNative = Get-ChildItem $macos -File | Where-Object {
        $bytes = [byte[]]::new(4); $fs = [IO.File]::OpenRead($_.FullName)
        try { [void]$fs.Read($bytes, 0, 4) } finally { $fs.Dispose() }
        $machO -notcontains (($bytes | ForEach-Object { $_.ToString("x2") }) -join "")
    }
    if ($notNative) { throw "Contents/MacOS has non-native files that signing would drop: $(($notNative | ForEach-Object Name) -join ', ')" }

    $zipName = "$appName-mac-$a.zip"
    if ($Sign) {
        # Pass 1 signs only (nothing is sent to Apple) so a broken bundle is caught before the slow notarization.
        & python $SigningScript $app --skip-notarize
        if ($LASTEXITCODE -ne 0) { throw "Signing failed for $rid - see last-sign.log in the signing folder" }
        $signedApp = Join-Path $archDir "Signed\$appName.app"
        $unsignedFiles = Get-ChildItem $app -Recurse -File | ForEach-Object { $_.FullName.Substring($app.Length) }
        $dropped = $unsignedFiles | Where-Object { -not (Test-Path (Join-Path $signedApp $_)) }
        if ($dropped) { throw "Signing dropped $(@($dropped).Count) file(s) from the app, e.g. $(@($dropped)[0]) - not shipping that." }
        Write-Host "Signed app contains all $(@($unsignedFiles).Count) files." -ForegroundColor Green

        if (-not $SkipNotarize) {
            & python $SigningScript $app
            if ($LASTEXITCODE -ne 0) { throw "Notarization failed for $rid - see last-sign.log in the signing folder" }
        }
        $built = Join-Path $archDir "$appName-signed.zip"
    }
    else {
        $built = Join-Path $archDir "$appName.zip"
        & python (Join-Path $PSScriptRoot "zip_app.py") $app $built
        if ($LASTEXITCODE -ne 0) { throw "zip failed for $rid" }
    }
    Copy-Item $built (Join-Path $root $zipName) -Force
    Write-Host ("{0}: {1:N1} MB" -f $zipName, ((Get-Item (Join-Path $root $zipName)).Length / 1MB)) -ForegroundColor Green
}

Write-Host "`nDone. Zips are in $root" -ForegroundColor Green
