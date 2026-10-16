using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using ApkGenerator.Core;

namespace ApkGenerator.Core.ViewModels;

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
    private IReadOnlyList<ToolStatusItem> _toolItems = Array.Empty<ToolStatusItem>();


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
                    nameof(StoreSizeLabel), nameof(CanInstall), nameof(CanSave), nameof(SaveButtonText), nameof(SaveFtpButtonText), nameof(ShowSaveAabFtp),
                })
                    Raise(n);
                break;

        }
    }

    public async Task LoadFileAsync(string path)
    {
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
        foreach (var done in Jobs.Where(j => j.IsFinished).ToList()) Jobs.Remove(done);   // finished tasks belong to the previous file
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
    /// <summary>The footer: one entry per tool, and only the missing ones are flagged.</summary>
    public IReadOnlyList<ToolStatusItem> ToolItems { get => _toolItems; private set => Set(ref _toolItems, value); }

    /// <summary>The tools that weren't found (drives the warning shown at launch).</summary>
    public IReadOnlyList<DependencyStatus> MissingDependencies { get; private set; } = Array.Empty<DependencyStatus>();

    public bool CanInstall => HasPackage;
    /// <summary>An AAB can always be saved as an APK; an existing APK can only be saved after re-signing it.</summary>
    public bool CanSave => HasPackage && (Package!.IsAab || Signing.Enabled);
    public string InstallButtonText => LaunchAfterInstall ? "Install & Run" : "Install";
    public string SaveButtonText => Package is { IsAab: false } ? "Save signed APK" : "Save APK";

    /// <summary>The FTP button only exists once a server has been entered in Settings.</summary>
    public bool FtpConfigured => !string.IsNullOrWhiteSpace(Settings.Data.Ftp.Host);
    /// <summary>"Save AAB to FTP" uploads the bundle itself, so it only shows when the loaded file is an AAB.
    public bool ShowSaveAabFtp => FtpConfigured && Package is { IsAab: true };
    public string SaveFtpButtonText => Package is { IsAab: false } ? "Save signed APK to FTP" : "Save APK to FTP";

    /// <summary>Call after the Settings window closes so the FTP button appears / disappears.</summary>
    public void RefreshFtpState()
    {
        Raise(nameof(FtpConfigured));
        Raise(nameof(ShowSaveAabFtp));
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

    // ------------------------------------------------------------------ operations (each runs as its own job, side by side)

    public ObservableCollection<JobViewModel> Jobs { get; } = new();

    public void CancelJob(JobViewModel job) { if (job.IsRunning) job.Cts.Cancel(); }

    public void DismissJob(JobViewModel job)
    {
        if (job.IsRunning) return;
        Jobs.Remove(job);
        RefreshJobState();
    }

    public void ShowJobInFolder(JobViewModel job)
    {
        if (job.ResultPath != null && File.Exists(job.ResultPath)) PlatformShell.Reveal(job.ResultPath);
        else OpenOutputFolder();
    }

    /// <summary>Briefly shows "Copied!" on the job's button after the UI has put the link on the clipboard.</summary>
    public async void MarkUrlCopied(JobViewModel job)
    {
        job.CopyLabel = "Copied!";
        await Task.Delay(1500);
        job.CopyLabel = "Copy URL";
    }

    /// <summary>Cancels every running task.</summary>
    public void Cancel() { foreach (var j in Jobs.ToList()) CancelJob(j); }

    /// <summary>Called when the window closes: stop everything so no Java/adb processes are left behind.</summary>
    public void Shutdown()
    {
        _sizeCts?.Cancel();
        Cancel();
    }

    private JobViewModel StartJob(string title, string status, bool isInstall = false)
    {
        var job = new JobViewModel(title, isInstall) { Message = status };
        Jobs.Add(job);
        AppendLog($"──── {DateTime.Now:HH:mm:ss}  {title}: {status}");
        RefreshJobState();
        return job;
    }

    private void FinishJob(JobViewModel job, StatusKind kind, string message, OperationError? error = null)
    {
        job.Error = error;
        job.Message = message;
        job.Kind = kind;
        RefreshJobState();
    }

    private void RefreshJobState()
    {
        var running = Jobs.Count(j => j.IsRunning);
        IsBusy = Jobs.Any(j => j.IsRunning && j.IsInstall);
        RunningCount = running;
        if (Kind != StatusKind.Error)
            SetStatus(StatusKind.Idle, running > 1 ? $"{running} tasks running" : running == 1 ? "1 task running" : "Ready");
    }

    private int _runningCount;
    public int RunningCount { get => _runningCount; private set { if (Set(ref _runningCount, value)) Raise(nameof(AnyRunning)); Raise(nameof(ShowCancelAll)); } }
    public bool AnyRunning => RunningCount > 0;
    public bool ShowCancelAll => RunningCount > 1;

    /// <summary>Tool output arrives on thread-pool threads; tag each line with its task so interleaved output stays readable.</summary>
    private Action<string> JobLog(JobViewModel job) => line => Log($"[{job.Title}] {line}");

    private void FailJob(JobViewModel job, Exception ex) =>
        FinishJob(job, StatusKind.Error, "Failed", ErrorInterpreter.Custom("Something went wrong", technical: ex.Message));

    /// <summary>The checks Save APK and Save APK to FTP share: a file is loaded and the signing details are usable.</summary>
    private bool TryGetSaveInputs(out PackageInfo pkg, out SigningConfig? signing)
    {
        pkg = Package!;
        signing = null;
        if (Package is null) return false;
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

        var job = StartJob("Save APK", BuildingStatus(pkg, signing));
        try
        {
            if (signing != null) Signing.SaveDetails();
            var path = await PrepareApkAsync(job, pkg, signing, destination);
            if (path is null) return;

            job.ResultPath = path;
            FinishJob(job, StatusKind.Success, $"Saved {Path.GetFileName(path)}  ({FormatMb(new FileInfo(path).Length)})");
        }
        catch (Exception ex) { FailJob(job, ex); }
    }

    /// <summary>Save APK to FTP: builds the APK as Save APK does (also keeping a copy in Generated APKs), then uploads it to the folder set in Settings.</summary>
    public async Task SaveApkToFtpAsync()
    {
        if (Package is null) return;
        var ftp = Settings.GetFtp();
        if (!ftp.IsConfigured)
        {
            ClearOutcome();
            Fail(ErrorInterpreter.Custom("FTP isn't set up", "Open Settings (the cog button) and enter the FTP details."), "FTP not set up");
            return;
        }
        if (!TryGetSaveInputs(out var pkg, out var signing)) return;

        var job = StartJob("Save APK to FTP", BuildingStatus(pkg, signing));
        try
        {
            if (signing != null) Signing.SaveDetails();
            var path = await PrepareApkAsync(job, pkg, signing, destination: null);
            if (path is null) return;

            job.ResultPath = path;
            await UploadToFtpAsync(job, ftp, path, Path.GetFileName(path));
        }
        catch (Exception ex) { FailJob(job, ex); }
    }

    /// <summary>Save AAB to FTP: uploads the dragged-in .aab itself (no build) to the folder set in Settings.</summary>
    public async Task SaveAabToFtpAsync()
    {
        if (Package is not { IsAab: true } pkg) return;
        ClearOutcome();
        var ftp = Settings.GetFtp();
        if (!ftp.IsConfigured)
        {
            Fail(ErrorInterpreter.Custom("FTP isn't set up", "Open Settings (the cog button) and enter the FTP details."), "FTP not set up");
            return;
        }
        if (!File.Exists(pkg.FilePath))
        {
            Fail(ErrorInterpreter.Custom("The file has moved or been deleted", pkg.FilePath), "Upload failed");
            return;
        }

        var job = StartJob("Save AAB to FTP", "Uploading…");
        try { await UploadToFtpAsync(job, ftp, pkg.FilePath, pkg.FileName); }
        catch (Exception ex) { FailJob(job, ex); }
    }

    /// <summary>Uploads one file, showing progress, and on success remembers the web link for "Copy URL".</summary>
    private async Task UploadToFtpAsync(JobViewModel job, FtpSettings ftp, string path, string name)
    {
        var ct = job.Cts.Token;
        var total = new FileInfo(path).Length;
        var target = ftp.Describe();
        job.Message = $"Uploading {name} to {target} …";
        AppendLog($"[{job.Title}] Uploading {path} to {ftp.Describe(name)}");

        var result = await Task.Run(() => FtpUploader.UploadAsync(ftp, path, name,
            (percent, bytes) => OnUi(() =>
            {
                if (!job.IsRunning) return;
                var pct = percent >= 0 ? $"{percent:0}%  " : "";
                job.Message = $"Uploading {name} to FTP …  {pct}({FormatMb(bytes)} of {FormatMb(total)})";
            }), ct));

        if (result.Cancelled) { FinishJob(job, StatusKind.Idle, "Cancelled"); return; }
        if (result.Error != null) { FinishJob(job, StatusKind.Error, "FTP upload failed", result.Error); return; }

        AppendLog($"[{job.Title}] Uploaded to " + result.Message);
        job.WebUrl = ftp.WebUrl(name);
        FinishJob(job, StatusKind.Success, $"Uploaded {name} to {target}  ({FormatMb(total)})");
    }

    public async Task InstallAsync()
    {
        if (Package is not { } pkg) return;
        ClearOutcome();

        var (targets, deviceError) = Devices.GetTargets();
        if (deviceError != null) { Fail(deviceError, "Can't install yet"); return; }
        var (signing, signError) = Signing.GetConfig();
        if (signError != null) { Fail(signError, "Signing details needed"); return; }

        // Two installs to the same phone at once would fight over adb, so refuse that one case.
        var busy = Jobs.Where(j => j.IsRunning && j.IsInstall).SelectMany(j => j.Rows.Select(r => r.Name)).ToHashSet();
        var clash = targets.FirstOrDefault(t => busy.Contains(t.Name));
        if (clash != null)
        {
            Fail(ErrorInterpreter.Custom($"{clash.Name} is already being installed to", "Wait for that install to finish, or pick a different device."), "Can't install yet");
            return;
        }

        var job = StartJob("Install", pkg.IsAab ? "Building universal APK…" : signing != null ? "Signing APK…" : "Preparing…", isInstall: true);
        foreach (var t in targets) job.Rows.Add(new DeviceResultRow(t.Name));
        var ct = job.Cts.Token;
        try
        {
            if (signing != null) Signing.SaveDetails();
            var apk = await PrepareApkAsync(job, pkg, signing, destination: null);
            if (apk is null) return;

            var options = CurrentOptions();
            var tools = Tools;
            job.Message = targets.Count == 1 ? $"Installing to {targets[0].Name}…" : $"Installing to {targets.Count} devices…";
            var log = JobLog(job);

            var results = await Task.WhenAll(targets.Select(async (device, i) =>
            {
                var row = job.Rows[i];
                OnUi(() => { row.State = RowState.Working; row.Message = "Installing…"; });
                var result = await Task.Run(() => DeviceInstaller.InstallAsync(
                    device, apk, pkg, options, tools,
                    status: s => OnUi(() => row.Message = s),
                    log: log, ct: ct));
                OnUi(() =>
                {
                    row.State = result.Success ? RowState.Success : result.Cancelled ? RowState.Waiting : RowState.Failed;
                    row.Message = result.Summary;
                });
                return result;
            }));

            Summarize(job, targets, results, ct);
        }
        catch (Exception ex) { FailJob(job, ex); }
    }

    private void Summarize(JobViewModel job, List<AdbDevice> targets, DeviceInstallResult[] results, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) { FinishJob(job, StatusKind.Idle, "Cancelled"); return; }

        var failed = results.Where(r => !r.Success).ToList();
        if (failed.Count == 0)
        {
            FinishJob(job, StatusKind.Success, targets.Count == 1
                ? $"{targets[0].Name}: {results[0].Summary}"
                : $"Installed on all {targets.Count} devices");
            return;
        }

        var first = failed[0].Error ?? ErrorInterpreter.Custom("Install failed");
        if (targets.Count > 1)
            first = new OperationError { Title = $"{failed[0].Device.Name}: {first.Title}", Hint = first.Hint, Technical = first.Technical };

        FinishJob(job, StatusKind.Error, targets.Count == 1 ? "Install failed" : $"Install failed on {failed.Count} of {targets.Count} devices", first);
    }

    // Jobs that need the same APK (Save APK to FTP + Install, say) share one build instead of racing to write the same file.
    private sealed class SharedBuild
    {
        public Task<BuildOutcome> Task = null!;
        public CancellationTokenSource Cts = new();
        public int Waiters;
    }
    private readonly Dictionary<string, SharedBuild> _builds = new();

    /// <summary>
    /// Gets an APK ready: builds it from an AAB, re-signs an APK, or uses the APK as-is.
    /// Saved to <paramref name="destination"/>, or into the Generated APKs folder when that is null. Reports its own errors on the job.
    /// </summary>
    private async Task<string?> PrepareApkAsync(JobViewModel job, PackageInfo pkg, SigningConfig? signing, string? destination)
    {
        if (!pkg.IsAab && signing is null) return pkg.FilePath;

        var ct = job.Cts.Token;
        var tools = Tools;
        var output = destination ?? Path.Combine(Paths.OutputDir, BuildService.DefaultFileName(pkg.FilePath, pkg.IsAab, signing != null));
        var log = JobLog(job);

        Task<BuildOutcome> Run(CancellationToken token) => pkg.IsAab
            ? Task.Run(() => BuildService.BuildUniversalApkAsync(pkg.FilePath, signing, output, tools, log, token))
            : Task.Run(() => BuildService.SignApkAsync(pkg.FilePath, signing!, output, tools, log, token));

        BuildOutcome outcome;
        if (destination != null)
        {
            outcome = await Run(ct);
        }
        else
        {
            var key = $"{pkg.FilePath}|{signing?.KeystorePath}|{signing?.Alias}";
            if (!_builds.TryGetValue(key, out var shared))
            {
                shared = new SharedBuild();
                shared.Task = Run(shared.Cts.Token);
                _builds[key] = shared;
                var started = shared;
                _ = started.Task.ContinueWith(_ => OnUi(() => { if (_builds.TryGetValue(key, out var s) && ReferenceEquals(s, started)) _builds.Remove(key); }));
            }
            else job.Message = "Waiting for the build already running…";

            shared.Waiters++;
            try
            {
                outcome = await shared.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                outcome = BuildOutcome.Cancel();
            }
            finally
            {
                // The build stops once nobody is waiting for it any more.
                if (--shared.Waiters == 0 && !shared.Task.IsCompleted) shared.Cts.Cancel();
            }
        }

        if (outcome.Cancelled || ct.IsCancellationRequested)
        {
            FinishJob(job, StatusKind.Idle, "Cancelled");
            return null;
        }
        if (!outcome.Success)
        {
            FinishJob(job, StatusKind.Error, pkg.IsAab ? "Build failed" : "Signing failed", outcome.Error ?? ErrorInterpreter.Custom("The build failed"));
            return null;
        }
        job.ResultPath = outcome.OutputPath;
        return outcome.OutputPath;
    }

    // ------------------------------------------------------------------ misc

    public async Task InitializeAsync()
    {
        AppPaths.PurgeOldTempDirs();
        await ToolLocator.ProbeVersionsAsync(Tools);
        UpdateToolsSummary();
        await Devices.RefreshAsync();
    }

    /// <summary>Searches for the tools again (used by the "Retry" button after the user installs something).</summary>
    public async Task RecheckToolsAsync()
    {
        var overrides = Settings.Data.ToolOverrides;
        var found = await Task.Run(() => ToolLocator.Locate(Paths, overrides));
        await ToolLocator.ProbeVersionsAsync(found);
        Tools = found;
        UpdateToolsSummary();
        await Devices.RefreshAsync();
    }

    private void UpdateToolsSummary()
    {
        var all = DependencyChecker.Check(Tools, Paths);
        MissingDependencies = all.Where(d => !d.Found).ToList();
        ToolItems = all.Select((d, i) => new ToolStatusItem(d.Found ? d.FoundText : d.MissingText, !d.Found, i < all.Count - 1)).ToList();
    }
    public void OpenOutputFolder()
    {
        Directory.CreateDirectory(Paths.OutputDir);
        PlatformShell.Reveal(Paths.OutputDir);
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
