using System.Collections.ObjectModel;
using ApkGenerator.Core;

namespace ApkGenerator.Core.ViewModels;

public enum DeviceChoiceKind { AllDevices, Device, EnterIp }

public sealed class DeviceChoice
{
    public DeviceChoiceKind Kind { get; init; }
    public AdbDevice? Device { get; init; }
    public string Label { get; init; } = "";
    /// <summary>Dropdown dot colour hint: true = ready to install to.</summary>
    public bool IsReady => Kind != DeviceChoiceKind.Device || Device!.IsUsable;
    public override string ToString() => Label;
}

public sealed class DevicesViewModel : ObservableObject
{
    private const string AllKey = "*";

    private readonly Func<ToolSet> _tools;
    private readonly SettingsStore _settings;
    private bool _rebuilding;
    private bool _hasListed;               // false until the first real device list arrives
    private DateTime _lastRefreshUtc = DateTime.MinValue;

    private DeviceChoice? _selected;
    private bool _showIpEntry;
    private string _ipAddress = "";
    private bool _isRefreshing;
    private bool _isConnecting;
    private string _statusText = "";
    private bool _statusIsError;

    public DevicesViewModel(Func<ToolSet> tools, SettingsStore settings)
    {
        _tools = tools;
        _settings = settings;
        RebuildChoices(new List<AdbDevice>(), null);
    }

    public ObservableCollection<DeviceChoice> Choices { get; } = new();
    public List<AdbDevice> Devices { get; private set; } = new();

    public DeviceChoice? Selected { get => _selected; set => Set(ref _selected, value); }
    public bool ShowIpEntry { get => _showIpEntry; private set => Set(ref _showIpEntry, value); }
    public string IpAddress { get => _ipAddress; set => Set(ref _ipAddress, value); }
    public bool IsRefreshing { get => _isRefreshing; private set => Set(ref _isRefreshing, value); }
    public bool IsConnecting { get => _isConnecting; private set => Set(ref _isConnecting, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public bool StatusIsError { get => _statusIsError; private set => Set(ref _statusIsError, value); }

    protected override void OnChanged(string? name)
    {
        if (name != nameof(Selected) || _rebuilding) return;
        ShowIpEntry = Selected?.Kind == DeviceChoiceKind.EnterIp;
        if (Selected is { Kind: DeviceChoiceKind.AllDevices }) _settings.Data.LastDevice = AllKey;
        else if (Selected is { Kind: DeviceChoiceKind.Device } c) _settings.Data.LastDevice = c.Device!.Serial;
        else return;
        _settings.Save();
    }

    /// <summary>Refreshes at most every couple of seconds unless forced (used when the window regains focus).</summary>
    public async Task RefreshAsync(bool force = true)
    {
        if (IsRefreshing) return;
        if (!force && (DateTime.UtcNow - _lastRefreshUtc).TotalSeconds < 3) return;

        IsRefreshing = true;
        try
        {
            // Keep whatever the user has picked; on the very first refresh fall back to the remembered device.
            var wanted = !_hasListed
                ? _settings.Data.LastDevice
                : Selected switch
                {
                    { Kind: DeviceChoiceKind.AllDevices } => AllKey,
                    { Kind: DeviceChoiceKind.Device } c => c.Device!.Serial,
                    _ => _settings.Data.LastDevice,
                };

            var result = await Task.Run(() => Adb.ListDevicesAsync(_tools()));
            _lastRefreshUtc = DateTime.UtcNow;
            _hasListed = true;
            Devices = result.Devices;
            RebuildChoices(result.Devices, wanted);

            if (result.Error != null)
            {
                StatusText = result.Error.Title + (result.Error.Hint != null ? " – " + result.Error.Hint : "");
                StatusIsError = true;
            }
            else if (result.Devices.Count == 0)
            {
                StatusText = "No devices found. Connect one by USB (with USB debugging on), or choose \"Enter IP address…\".";
                StatusIsError = false;
            }
            else
            {
                var notReady = result.Devices.Count(d => !d.IsUsable);
                StatusText = notReady == 0
                    ? $"{result.Devices.Count} device{(result.Devices.Count == 1 ? "" : "s")} found."
                    : $"{result.Devices.Count} found, {notReady} not ready (unlock it and accept the USB debugging prompt, then refresh).";
                StatusIsError = false;
            }
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void RebuildChoices(List<AdbDevice> devices, string? wantedKey)
    {
        _rebuilding = true;
        try
        {
            Choices.Clear();
            var usable = devices.Count(d => d.IsUsable);
            var all = new DeviceChoice { Kind = DeviceChoiceKind.AllDevices, Label = $"All devices ({usable} connected)" };
            Choices.Add(all);
            foreach (var d in devices)
                Choices.Add(new DeviceChoice { Kind = DeviceChoiceKind.Device, Device = d, Label = d.DisplayName });
            Choices.Add(new DeviceChoice { Kind = DeviceChoiceKind.EnterIp, Label = "Enter IP address…" });

            DeviceChoice? pick = null;
            if (wantedKey == AllKey) pick = all;
            else if (wantedKey != null)
                pick = Choices.FirstOrDefault(c => c.Kind == DeviceChoiceKind.Device && c.Device!.Serial == wantedKey);

            pick ??= Choices.FirstOrDefault(c => c.Kind == DeviceChoiceKind.Device && c.Device!.IsUsable) ?? all;
            Selected = pick;
            ShowIpEntry = false;
        }
        finally
        {
            _rebuilding = false;
        }
    }

    public async Task ConnectAsync()
    {
        if (IsConnecting) return;
        IsConnecting = true;
        StatusText = "Connecting…";
        StatusIsError = false;
        try
        {
            var address = IpAddress;
            var (ok, message, error) = await Task.Run(() => Adb.ConnectAsync(_tools(), address));
            if (!ok)
            {
                StatusText = error!.Title + (error.Hint != null ? " – " + error.Hint : "");
                StatusIsError = true;
                return;
            }

            await RefreshAsync();
            var serial = address.Contains(':') ? address.Trim() : address.Trim() + ":5555";
            var pick = Choices.FirstOrDefault(c => c.Kind == DeviceChoiceKind.Device && c.Device!.Serial == serial);
            if (pick != null) Selected = pick;
            StatusText = message;
            StatusIsError = false;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    /// <summary>The devices an install would go to, or an error explaining why there are none.</summary>
    public (List<AdbDevice> targets, OperationError? error) GetTargets()
    {
        switch (Selected)
        {
            case { Kind: DeviceChoiceKind.AllDevices }:
                var ready = Devices.Where(d => d.IsUsable).ToList();
                if (ready.Count == 0)
                    return (ready, ErrorInterpreter.Custom("No devices are connected",
                        "Connect a device with USB debugging on (or use \"Enter IP address…\"), then press Refresh."));
                return (ready, null);

            case { Kind: DeviceChoiceKind.Device } c:
                if (!c.Device!.IsUsable)
                    return (new(), ErrorInterpreter.Adb("The selected device isn't ready", c.Device.State));
                return (new List<AdbDevice> { c.Device }, null);

            case { Kind: DeviceChoiceKind.EnterIp }:
                return (new(), ErrorInterpreter.Custom("Enter the device's IP address and press Connect",
                    "Or pick a device from the list."));

            default:
                return (new(), ErrorInterpreter.Custom("Choose a device to install to"));
        }
    }
}
