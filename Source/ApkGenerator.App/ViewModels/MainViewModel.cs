using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using ApkGenerator.Core;

namespace ApkGenerator.App.ViewModels;

public enum StatusKind { Idle, Working, Success, Error }
public enum RowState { Waiting, Working, Success, Failed }

/// <summary>One line in the per-device results list.</summary>
public sealed class DeviceResultRow : ObservableObject
{
    private RowState _state = RowState.Waiting;
    private string _message = "Waiting…";

    public DeviceResultRow(string name) => Name = name;
    public string Name { get; }
    public RowState State { get => _state; set => Set(ref _state, value); }
    public string Message { get => _message; set => Set(ref _message, value); }
}

public sealed class MainViewModel : ObservableObject
{
    private const int MaxLogChars = 250_000;

    private readonly SynchronizationContext _ui;
    private readonly StringBuilder _log = new();
    private CancellationTokenSource? _opCts;
    private CancellationTokenSource? _sizeCts;

    private PackageInfo? _package;
    private string _storeSizeText = "";
    private bool _isStoreSizeBusy;
    private string? _storeSizeError;
    private OperationError? _storeSizeFailure;

    private bool _isBusy;
    private StatusKind _statusKind = StatusKind.Idle;
    private string _statusText = "Ready";
    private OperationError? _error;
    private string _logText = "";
    private string? _resultPath;
    private string _toolsSummary = "";
    private bool _toolsHaveProblem;

    public MainViewModel(AppPaths paths, SettingsStore settings)
    {
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        Paths = paths;
        Settings = settings;
        Tools = ToolLocator.Locate(paths, settings.Data.ToolOverrides);

        Devices = new DevicesViewModel(() => Tools, settings);
        Signing = new SigningViewModel(paths, settings, () => Tools);
        // Re-signing an APK only makes sense with "Sign APK" ticked, so the Save button follows that checkbox.
        Signing.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SigningViewModel.Enabled)) Raise(nameof(CanSave));
        };
        UpdateToolsSummary();
    }

    public AppPaths Paths { get; }
    public SettingsStore Settings { get; }
    public ToolSet Tools { get; private set; }
    public DevicesViewModel Devices { get; }
    public SigningViewModel Signing { get; }
    public ObservableCollection<DeviceResultRow> DeviceResults { get; } = new();

    // ------------------------------------------------------------------ install options (persisted)

    public bool AllowDowngrade { get => Settings.Data.AllowDowngrade; set { Settings.Data.AllowDowngrade = value; Settings.Save(); Raise(); } }
    public bool UninstallConflicting { get => Settings.Data.UninstallConflicting; set { Settings.Data.UninstallConflicting = value; Settings.Save(); Raise(); } }
    public bool ClearData { get => Settings.Data.ClearData; set { Settings.Data.ClearData = value; Settings.Save(); Raise(); } }
    public bool GrantPermissions { get => Settings.Data.GrantPermissions; set { Settings.Data.GrantPermissions = value; Settings.Save(); Raise(); } }
    public bool LaunchAfterInstall
    {
        get => Settings.Data.LaunchAfterInstall;
        set { Settings.Data.LaunchAfterInstall = value; Settings.Save(); Raise(); Raise(nameof(InstallButtonText)); }
    }

    private InstallOptions CurrentOptions() => new()
    {
        AllowDowngrade = AllowDowngrade,
        UninstallConflicting = UninstallConflicting,
        ClearData = ClearData,
        GrantPermissions = GrantPermissions,
        Launch = LaunchAfterInstall,
    };

    // ------------------------------------------------------------------ the loaded file

    public PackageInfo? Package
    {
        get => _package;
        private set => Set(ref _package, value);
    }

    public bool HasPackage => Package != null;
    public string KindBadge => Package is null ? "" : Package.IsAab ? "AAB" : "APK";
    public string FileName => Package?.FileName ?? "";
    public string FileFolder => Package is null ? "" : Path.GetDirectoryName(Package.FilePath) ?? "";
    public string FileSizeText => Package is null ? "" : FormatMb(Package.FileSize);
    public string PackageNameText => Package?.PackageName ?? "—";
    public string VersionNameText => Package?.VersionName ?? "—";
    public string VersionCodeText => Package?.VersionCode?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string MinSdkText => AndroidVersions.Describe(Package?.MinSdk);
    public string TargetSdkText => AndroidVersions.Describe(Package?.TargetSdk);
    public bool TargetSdkIsLow => Package?.TargetSdk is { } t && t < PackageInspector.PlayMinTargetSdk;

    public bool PackageNameIsOffFormat =>
        Package?.PackageName is { } n && !string.IsNullOrEmpty(Settings.Data.ExpectedPackagePrefix) &&
        !n.StartsWith(Settings.Data.ExpectedPackagePrefix, StringComparison.OrdinalIgnoreCase);

    public string AbisText => Package is null ? "" : Package.Abis.Count == 0 ? "none (no native code)" : string.Join(", ", Package.Abis);

    public string ModulesText => Package is { IsAab: true } p && p.Modules.Count > 1
        ? $"{p.Modules.Count} modules: {string.Join(", ", p.Modules)}" : "";

    public IReadOnlyList<PackageWarning> Warnings =>
        Package is null ? Array.Empty<PackageWarning>() : PackageInspector.GetWarnings(Package, Settings.Data.ExpectedPackagePrefix);

    public IReadOnlyList<PermissionInfo> Permissions => Package?.Permissions ?? new List<PermissionInfo>();

    public string PermissionsHeader
    {
        get
        {
            if (Package is null) return "";
            var total = Package.Permissions.Count;
            var risky = Package.Permissions.Count(p => p.IsDangerous);
            if (total == 0) return "Permissions: none";
            return risky == 0 ? $"Permissions ({total})" : $"Permissions ({total}, {risky} potentially unwanted)";
        }
    }

    public string StoreSizeLabel => Package is { IsAab: false } ? "File size" : "Estimated store download";
    public string StoreSizeText { get => _storeSizeText; private set => Set(ref _storeSizeText, value); }
    public bool IsStoreSizeBusy { get => _isStoreSizeBusy; private set => Set(ref _isStoreSizeBusy, value); }
    public string? StoreSizeError { get => _storeSizeError; private set => Set(ref _storeSizeError, value); }

    /// <summary>The estimate failed quietly in the background; this shows the full explanation in the error panel.</summary>
    public void ShowStoreSizeError()
    {
        if (_storeSizeFailure != null) Fail(_storeSizeFailure, "Couldn't estimate the store size");
    }

    public void ShowError(OperationError error, string status = "Something went wrong") => Fail(error, status);

    protected override void OnChanged(string? name)
    {
        switch (name)
        {
            case nameof(Package):
                foreach (var n in new[]
                {
                    nameof(HasPackage), nameof(KindBadge), nameof(FileName), nameof(FileFolder), nameof(FileSizeText),
                    nameof(PackageNameText), nameof(VersionNameText), nameof(VersionCodeText), nameof(MinSdkText),
                    nameof(TargetSdkText), nameof(TargetSdkIsLow), nameof(PackageNameIsOffFormat), nameof(AbisText),
                    nameof(ModulesText), nameof(Warnings), nameof(Permissions), nameof(PermissionsHeader),
                    nameof(StoreSizeLabel), nameof(CanInstall), nameof(CanSave), nameof(SaveButtonText), nameof(SaveFtpButtonText),
                })
                    Raise(n);
                break;

            case nameof(IsBusy):
                Raise(nameof(CanInstall));
                Raise(nameof(CanSave));
                break;
        }
    }

    public async Task LoadFileAsync(string path)
    {
        if (IsBusy)
        {
            Fail(ErrorInterpreter.Custom("Please wait", "An operation is still running – wait for it to finish or press Cancel before opening another file."),
                "Busy");
            return;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".aab" or ".apk"))
        {
            Fail(ErrorInterpreter.Custom($"\"{Path.GetFileName(path)}\" isn't an .aab or .apk file",
                "Drag an Android App Bundle (.aab) or an APK (.apk) into the window."), "Can't open that file");
            return;
        }

        PackageInfo info;
        try
        {
            info = await Task.Run(() => PackageInspector.Inspect(path));
        }
        catch (Exception ex)
        {
            Fail(ErrorInterpreter.Custom($"Couldn't read {Path.GetFileName(path)}", technical: ex.Message), "Can't open that file");
            return;
        }

        ClearOutcome();
        DeviceResults.Clear();
        _sizeCts?.Cancel();
        Package = info;
        SetStatus(StatusKind.Idle, "Ready");
        StartSizeEstimate(info);
    }

    private void StartSizeEstimate(PackageInfo info)
    {
        StoreSizeError = null;
        _storeSizeFailure = null;

        if (!info.IsAab)
        {
            IsStoreSizeBusy = false;
            StoreSizeText = FormatMb(info.FileSize);
            return;
        }

        if (Settings.GetCachedSize(info) is { } cached)
        {
            IsStoreSizeBusy = false;
            StoreSizeText = FormatRange(cached);
            return;
        }

        IsStoreSizeBusy = true;
        StoreSizeText = "Calculating in the background…";
        var cts = new CancellationTokenSource();
        _sizeCts = cts;
        _ = RunSizeEstimateAsync(info, cts);
    }

    private async Task RunSizeEstimateAsync(PackageInfo info, CancellationTokenSource cts)
    {
        var tools = Tools;
        SizeEstimateResult result;
        try
        {
            result = await Task.Run(() => BundleSizeEstimator.EstimateAsync(info.FilePath, tools, cts.Token));
        }
        catch (Exception ex)
        {
            result = new SizeEstimateResult { Error = ErrorInterpreter.Custom("Estimating the download size failed", technical: ex.Message) };
        }
        if (cts.IsCancellationRequested || !ReferenceEquals(Package, info)) return;   // a newer file replaced this one

        IsStoreSizeBusy = false;
        if (result.Range != null)
        {
            Settings.CacheSize(info, result.Range);
            StoreSizeText = FormatRange(result.Range);
        }
        else if (result.Error != null)
        {
            StoreSizeText = "Couldn't estimate";
            _storeSizeFailure = result.Error;
            StoreSizeError = result.Error.Title;
        }
    }

    // ------------------------------------------------------------------ status / errors / log

    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public StatusKind Kind { get => _statusKind; private set => Set(ref _statusKind, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public OperationError? Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => Error != null;
    public string LogText { get => _logText; private set => Set(ref _logText, value); }
    public string? ResultPath { get => _resultPath; private set { if (Set(ref _resultPath, value)) Raise(nameof(HasResult)); } }
    public bool HasResult => ResultPath != null;

    public string ToolsSummary { get => _toolsSummary; private set => Set(ref _toolsSummary, value); }
    public bool ToolsHaveProblem { get => _toolsHaveProblem; private set => Set(ref _toolsHaveProblem, value); }

    public bool CanInstall => HasPackage && !IsBusy;
    /// <summary>An AAB can always be saved as an APK; an existing APK can only be saved after re-signing it.</summary>
    public bool CanSave => HasPackage && !IsBusy && (Package!.IsAab || Signing.Enabled);
    public string InstallButtonText => LaunchAfterInstall ? "Install & Run" : "Install";
    public string SaveButtonText => Package is { IsAab: false } ? "Save signed APK" : "Save APK";

    /// <summary>The FTP button only exists once a server has been entered in Settings.</summary>
    public bool FtpConfigured => !string.IsNullOrWhiteSpace(Settings.Data.Ftp.Host);
    public string SaveFtpButtonText => Package is { IsAab: false } ? "Save signed APK to FTP" : "Save APK to FTP";

    /// <summary>Call after the Settings window closes so the FTP button appears / disappears.</summary>
    public void RefreshFtpState()
    {
        Raise(nameof(FtpConfigured));
        Raise(nameof(SaveFtpButtonText));
    }

    /// <summary>Starting point for the Save dialog (folder, file name).</summary>
    public (string folder, string fileName) SuggestedSave()
    {
        Directory.CreateDirectory(Paths.OutputDir);
        var name = Package is null ? "app.apk" : BuildService.DefaultFileName(Package.FilePath, Package.IsAab, Signing.Enabled);
        return (Paths.OutputDir, name);
    }

    private void SetStatus(StatusKind kind, string text)
    {
        Kind = kind;
        StatusText = text;
    }

    /// <summary>Shows the error panel; the status line gets a short label so it doesn't repeat the error's title.</summary>
    private void Fail(OperationError error, string status)
    {
        Error = error;
        SetStatus(StatusKind.Error, status);
    }

    private void ClearOutcome()
    {
        Error = null;
        ResultPath = null;
    }

    public void DismissError()
    {
        Error = null;
        if (Kind == StatusKind.Error) SetStatus(StatusKind.Idle, "Ready");
    }

    private void OnUi(Action a) => _ui.Post(_ => a(), null);

    /// <summary>Thread-safe: tool output arrives on thread-pool threads.</summary>
    private void Log(string line) => OnUi(() => AppendLog(line));

    private void AppendLog(string line)
    {
        _log.AppendLine(line);
        if (_log.Length > MaxLogChars) _log.Remove(0, _log.Length - MaxLogChars * 3 / 4);
        LogText = _log.ToString();
    }

    public void ClearLog()
    {
        _log.Clear();
        LogText = "";
    }

    // ------------------------------------------------------------------ operations

    public void Cancel() => _opCts?.Cancel();

    /// <summary>Called when the window closes: stop everything so no Java/adb processes are left behind.</summary>
    public void Shutdown()
    {
        _sizeCts?.Cancel();
        _opCts?.Cancel();
    }

    /// <summary>The checks Save APK and Save APK to FTP share: a file is loaded and the signing details are usable.</summary>
    private bool TryGetSaveInputs(out PackageInfo pkg, out SigningConfig? signing)
    {
        pkg = Package!;
        signing = null;
        if (Package is null || IsBusy) return false;
        ClearOutcome();

        var (config, signError) = Signing.GetConfig();
        if (signError != null) { Fail(signError, "Signing details needed"); return false; }
        if (!pkg.IsAab && config is null)
        {
            Fail(ErrorInterpreter.Custom("Nothing to save", "This is already an APK. Tick \"Sign APK\" to save a re-signed copy."), "Nothing to save");
            return false;
        }
        signing = config;
        return true;
    }

    private static string BuildingStatus(PackageInfo pkg, SigningConfig? signing) =>
        pkg.IsAab ? (signing != null ? "Building signed APK…" : "Building APK…") : "Signing APK…";

    /// <summary>Save APK: builds the universal APK (signed with the chosen keystore if "Sign APK" is ticked) and writes it to <paramref name="destination"/>.</summary>
    public async Task SaveApkAsync(string destination)
    {
        if (!TryGetSaveInputs(out var pkg, out var signing)) return;

        BeginOperation(BuildingStatus(pkg, signing));
        var ct = _opCts!.Token;
        try
        {
            if (signing != null) Signing.SaveDetails();
            var path = await PrepareApkAsync(pkg, signing, destination, ct);
            if (path is null) return;

            ResultPath = path;
            SetStatus(StatusKind.Success, $"Saved {Path.GetFileName(path)}  ({FormatMb(new FileInfo(path).Length)})");
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>Save APK to FTP: builds the APK as Save APK does (also keeping a copy in Generated APKs), then uploads it to the folder set in Settings.</summary>
    public async Task SaveApkToFtpAsync()
    {
        if (IsBusy || Package is null) return;
        var ftp = Settings.GetFtp();
        if (!ftp.IsConfigured)
        {
            ClearOutcome();
            Fail(ErrorInterpreter.Custom("FTP isn't set up", "Open Settings (the cog button) and enter the FTP details."), "FTP not set up");
            return;
        }
        if (!TryGetSaveInputs(out var pkg, out var signing)) return;

        BeginOperation(BuildingStatus(pkg, signing));
        var ct = _opCts!.Token;
        try
        {
            if (signing != null) Signing.SaveDetails();
            var path = await PrepareApkAsync(pkg, signing, destination: null, ct);
            if (path is null) return;

            ResultPath = path;
            var name = Path.GetFileName(path);
            var total = new FileInfo(path).Length;
            var target = ftp.Describe();
            SetStatus(StatusKind.Working, $"Uploading {name} to {target} …");
            AppendLog($"Uploading {path} to {ftp.Describe(name)}");

            var result = await Task.Run(() => FtpUploader.UploadAsync(ftp, path, name,
                (percent, bytes) => OnUi(() =>
                {
                    if (Kind != StatusKind.Working) return;
                    var pct = percent >= 0 ? $"{percent:0}%  " : "";
                    StatusText = $"Uploading {name} to FTP …  {pct}({FormatMb(bytes)} of {FormatMb(total)})";
                }), ct));

            if (result.Cancelled) { SetStatus(StatusKind.Idle, "Cancelled"); return; }
            if (result.Error != null) { Fail(result.Error, "FTP upload failed"); return; }

            AppendLog("Uploaded to " + result.Message);
            SetStatus(StatusKind.Success, $"Uploaded {name} to {target}  ({FormatMb(total)})");
        }
        finally
        {
            EndOperation();
        }
    }

    public async Task InstallAsync()
    {
        if (Package is not { } pkg || IsBusy) return;
        ClearOutcome();
        DeviceResults.Clear();

        var (targets, deviceError) = Devices.GetTargets();
        if (deviceError != null) { Fail(deviceError, "Can't install yet"); return; }
        var (signing, signError) = Signing.GetConfig();
        if (signError != null) { Fail(signError, "Signing details needed"); return; }

        BeginOperation(pkg.IsAab ? "Building universal APK…" : signing != null ? "Signing APK…" : "Preparing…");
        var ct = _opCts!.Token;
        try
        {
            if (signing != null) Signing.SaveDetails();
            var apk = await PrepareApkAsync(pkg, signing, destination: null, ct);
            if (apk is null) return;

            var options = CurrentOptions();
            var tools = Tools;
            var rows = targets.Select(t => new DeviceResultRow(t.Name)).ToList();
            foreach (var r in rows) DeviceResults.Add(r);
            SetStatus(StatusKind.Working, targets.Count == 1 ? $"Installing to {targets[0].Name}…" : $"Installing to {targets.Count} devices…");

            var results = await Task.WhenAll(targets.Select(async (device, i) =>
            {
                var row = rows[i];
                OnUi(() => { row.State = RowState.Working; row.Message = "Installing…"; });
                var result = await Task.Run(() => DeviceInstaller.InstallAsync(
                    device, apk, pkg, options, tools,
                    status: s => OnUi(() => row.Message = s),
                    log: Log, ct: ct));
                OnUi(() =>
                {
                    row.State = result.Success ? RowState.Success : result.Cancelled ? RowState.Waiting : RowState.Failed;
                    row.Message = result.Summary;
                });
                return result;
            }));

            Summarize(targets, results, ct);
        }
        finally
        {
            EndOperation();
        }
    }

    private void Summarize(List<AdbDevice> targets, DeviceInstallResult[] results, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            SetStatus(StatusKind.Idle, "Cancelled");
            return;
        }

        var failed = results.Where(r => !r.Success).ToList();
        if (failed.Count == 0)
        {
            SetStatus(StatusKind.Success, targets.Count == 1
                ? $"{targets[0].Name}: {results[0].Summary}"
                : $"Installed on all {targets.Count} devices");
            return;
        }

        var first = failed[0].Error ?? ErrorInterpreter.Custom("Install failed");
        if (targets.Count > 1)
            first = new OperationError { Title = $"{failed[0].Device.Name}: {first.Title}", Hint = first.Hint, Technical = first.Technical };

        Fail(first, targets.Count == 1 ? "Install failed" : $"Install failed on {failed.Count} of {targets.Count} devices");
    }

    /// <summary>
    /// Gets an APK ready: builds it from an AAB, re-signs an APK, or uses the APK as-is.
    /// Saved to <paramref name="destination"/>, or into the Generated APKs folder when that is null. Reports its own errors.
    /// </summary>
    private async Task<string?> PrepareApkAsync(PackageInfo pkg, SigningConfig? signing, string? destination, CancellationToken ct)
    {
        if (!pkg.IsAab && signing is null) return pkg.FilePath;

        var tools = Tools;
        var output = destination ?? Path.Combine(Paths.OutputDir, BuildService.DefaultFileName(pkg.FilePath, pkg.IsAab, signing != null));
        Action<string> log = Log;
        var outcome = pkg.IsAab
            ? await Task.Run(() => BuildService.BuildUniversalApkAsync(pkg.FilePath, signing, output, tools, log, ct))
            : await Task.Run(() => BuildService.SignApkAsync(pkg.FilePath, signing!, output, tools, log, ct));

        if (outcome.Cancelled)
        {
            SetStatus(StatusKind.Idle, "Cancelled");
            return null;
        }
        if (!outcome.Success)
        {
            Fail(outcome.Error ?? ErrorInterpreter.Custom("The build failed"), pkg.IsAab ? "Build failed" : "Signing failed");
            return null;
        }
        ResultPath = outcome.OutputPath;
        return outcome.OutputPath;
    }

    private void BeginOperation(string status)
    {
        _opCts = new CancellationTokenSource();
        IsBusy = true;
        SetStatus(StatusKind.Working, status);
        AppendLog($"──── {DateTime.Now:HH:mm:ss}  {status}");
    }

    private void EndOperation()
    {
        IsBusy = false;
        _opCts?.Dispose();
        _opCts = null;
        if (Kind == StatusKind.Working) SetStatus(StatusKind.Idle, "Ready");   // e.g. an early return that set nothing
    }

    // ------------------------------------------------------------------ misc

    public async Task InitializeAsync()
    {
        AppPaths.PurgeOldTempDirs();
        await ToolLocator.ProbeVersionsAsync(Tools);
        UpdateToolsSummary();
        await Devices.RefreshAsync();
    }

    private void UpdateToolsSummary()
    {
        var parts = new List<string>();
        var problem = false;

        void Add(string name, string? version, bool found)
        {
            if (found) parts.Add(version != null ? $"{name} {version}" : name);
            else { parts.Add($"{name} not found"); problem = true; }
        }

        Add("Java", Tools.JavaVersion, Tools.JavaPath != null);
        Add("bundletool", Tools.BundletoolVersion, Tools.BundletoolPath != null);
        Add("adb", Tools.AdbVersion, Tools.AdbPath != null);
        // apksigner is only needed to re-sign APKs, so it's not a hard problem
        if (Tools.ApkSignerJar != null) parts.Add($"build-tools {Tools.BuildToolsVersion}");
        else parts.Add("build-tools not found (needed only to sign APKs)");

        ToolsSummary = string.Join("  ·  ", parts);
        ToolsHaveProblem = problem;
    }

    public void OpenOutputFolder()
    {
        try
        {
            if (ResultPath != null && File.Exists(ResultPath))
                Process.Start("explorer.exe", $"/select,\"{ResultPath}\"");
            else
            {
                Directory.CreateDirectory(Paths.OutputDir);
                Process.Start("explorer.exe", $"\"{Paths.OutputDir}\"");
            }
        }
        catch { /* nothing sensible to do if Explorer can't start */ }
    }

    public static string FormatMb(long bytes) => (bytes / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    private static string FormatRange(SizeRange r)
    {
        var min = r.MinBytes / 1_000_000.0;
        var max = r.MaxBytes / 1_000_000.0;
        return max - min < Math.Max(0.15, max * 0.01)
            ? $"≈ {max.ToString("0.0", CultureInfo.InvariantCulture)} MB"
            : $"{min.ToString("0.0", CultureInfo.InvariantCulture)} – {max.ToString("0.0", CultureInfo.InvariantCulture)} MB";
    }
}
