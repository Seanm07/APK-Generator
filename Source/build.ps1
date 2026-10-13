<#
  Builds "APK Generator.exe" as a single self-contained file (no .NET install needed)
  and copies it into the tool folder, next to "Dependencies" and "Keystores".

  Usage:   powershell -ExecutionPolicy Bypass -File build.ps1            build + copy the exe
           powershell -ExecutionPolicy Bypass -File build.ps1 -Package   ...and also make "APK Generator-win.zip"
                                                                          (the minimum files to share the program)
  Needs:   the .NET 10 SDK (only on the machine that builds - not to run the exe).
#>
param(
    [string]$Configuration = "Release",
    [switch]$Package
)

$ErrorActionPreference = "Stop"
$src = $PSScriptRoot
$toolFolder = Split-Path $src -Parent
$publishDir = Join-Path $src "publish"
$exeName = "APK Generator.exe"

Write-Host "Publishing $exeName ..." -ForegroundColor Cyan
dotnet publish (Join-Path $src "ApkGenerator.App\ApkGenerator.App.csproj") `
    -c $Configuration -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Copy-Item (Join-Path $publishDir "ApkGenerator.exe") (Join-Path $toolFolder $exeName) -Force
Remove-Item $publishDir -Recurse -Force

$size = [math]::Round((Get-Item (Join-Path $toolFolder $exeName)).Length / 1MB, 1)
Write-Host "Done: $toolFolder\$exeName ($size MB)" -ForegroundColor Green

if ($Package) {
    # The minimum needed to run it elsewhere: the exe, bundletool, and a short readme.
    # NOT included: Keystores (private keys), settings (encrypted for this Windows user only), adb/Java (see readme).
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $folderName = "APK Generator"
    $stageRoot = Join-Path ([IO.Path]::GetTempPath()) ("gpbg-package-" + [guid]::NewGuid().ToString("N"))
    $stage = Join-Path $stageRoot $folderName
    New-Item -ItemType Directory -Force (Join-Path $stage "Dependencies") | Out-Null
    New-Item -ItemType Directory -Force (Join-Path $stage "Keystores") | Out-Null

    $jar = Get-ChildItem (Join-Path $toolFolder "Dependencies") -Filter "bundletool*.jar" |
        Sort-Object { [version]([regex]::Match($_.Name, '\d+(\.\d+)+').Value) } -Descending | Select-Object -First 1
    if (-not $jar) { throw "No bundletool jar found in the 'Dependencies' folder" }

    Copy-Item (Join-Path $toolFolder $exeName) $stage
    Copy-Item $jar.FullName (Join-Path $stage "Dependencies")
    Copy-Item (Join-Path $src "share-readme.txt") (Join-Path $stage "README.txt")

    $zip = Join-Path $toolFolder "$folderName-win.zip"   # "-win" so it is not mistaken for the Mac zips
    if (Test-Path $zip) { Remove-Item $zip -Force }

    # Written by hand so entry names use "/" (Windows PowerShell's CreateFromDirectory uses "\", which other unzip tools mis-read).
    Add-Type -AssemblyName System.IO.Compression
    $stream = [IO.File]::Open($zip, [IO.FileMode]::Create)
    $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
    foreach ($item in Get-ChildItem $stageRoot -Recurse) {
        $entry = $item.FullName.Substring($stageRoot.Length + 1).Replace('\', '/')
        if ($item.PSIsContainer) {
            if (-not (Get-ChildItem $item.FullName)) { [void]$archive.CreateEntry($entry + '/') }   # keep the empty Keystores folder
        } else {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $item.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    $archive.Dispose()
    $stream.Dispose()
    Remove-Item $stageRoot -Recurse -Force

    $zsize = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "Package: $zip ($zsize MB) - exe + $($jar.Name) + README" -ForegroundColor Green
}
