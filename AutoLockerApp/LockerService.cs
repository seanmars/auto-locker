using System.Drawing;
using H.NotifyIcon.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stateless;

namespace AutoLockerApp;

public enum DeviceState
{
    None,
    Connected,
    WaitingConfirmDisconnect,
    Disconnected,
}

public enum DeviceTrigger
{
    Connected,
    WaitingConfirmDisconnect,
    Disconnected,
    Close,
}

public class LockerService : BackgroundService, IDisposable
{
    private readonly ILogger _logger;
    private readonly IHostApplicationLifetime _appLifetime;

    private readonly WindowsHelper _windowsHelper;
    private readonly BluetoothHelper _bluetoothHelper;

    private readonly StateMachine<DeviceState, DeviceTrigger> _deviceState;

    private int WaitingForConfirmationTimeout { get; set; } = 5;
    private List<int> DefaultWaitingForConfirmationTimeouts { get; } = [3, 5, 15];

    private bool _canLock;
    private DateTimeOffset _waitingForConfirmationTime = DateTimeOffset.UtcNow;

    private Stream? _iconStream;
    private Icon? _icon;
    private TrayIconWithContextMenu? _trayIcon;
    private PopupMenu? _contextMenu;
    private PopupMenuItem? _exitMenuItem;
    private PopupMenuItem? _disableMenuItem;
    private PopupSubMenu? _settingsMenuItem;

    private BtDevice[]? _devices;
    private int? _selectedDeviceIndex;

    private bool IsReady => _devices is { Length: > 0 };

    public LockerService(ILogger<LockerService> logger, IHostApplicationLifetime appLifetime,
        WindowsHelper windowsHelper, BluetoothHelper bluetoothHelper)
    {
        _logger = logger;
        _appLifetime = appLifetime;

        _windowsHelper = windowsHelper;
        _bluetoothHelper = bluetoothHelper;

        _deviceState = ConfigureStateMachine();

        InitNotifyIcon();
    }

    private void InitNotifyIcon()
    {
        _iconStream = typeof(Program).Assembly.GetManifestResourceStream("AutoLockerApp.app.ico");
        _icon = new Icon(_iconStream!);
        _contextMenu = new PopupMenu();
        _exitMenuItem = new PopupMenuItem("Exit", (_, _) =>
        {
            _appLifetime.StopApplication();
        });

        _disableMenuItem = new PopupMenuItem("Disable", (_, _) =>
        {
            _canLock = false;
            _deviceState.Fire(DeviceTrigger.Close);
            _selectedDeviceIndex = null;

            _logger.LogInformation("Disabled");
        });

        _settingsMenuItem = new PopupSubMenu("Settings");
        DefaultWaitingForConfirmationTimeouts.ForEach(time =>
        {
            var menuItem = new PopupMenuItem($"{time} Seconds", (obj, _) =>
            {
                UpdateWaitingForConfirmationTimeoutSetting(obj, time);
            });
            _settingsMenuItem.Items.Add(menuItem);

            if (time == WaitingForConfirmationTimeout)
            {
                menuItem.Checked = true;
            }
        });

        _trayIcon = new TrayIconWithContextMenu
        {
            Icon = _icon.Handle,
            ToolTip = "AutoLocker",
        };

        _trayIcon.ContextMenu = _contextMenu;
        _trayIcon.Create();
    }

    private void UpdateWaitingForConfirmationTimeoutSetting(object? sender, int time)
    {
        if (sender is not PopupMenuItem menuItem)
        {
            return;
        }

        foreach (var item in _settingsMenuItem!.Items)
        {
            if (item is PopupMenuItem settingItem)
            {
                settingItem.Checked = settingItem == menuItem;
            }
        }

        WaitingForConfirmationTimeout = time;
    }

    private void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        _iconStream?.Dispose();
        _icon?.Dispose();
        _trayIcon?.Dispose();
    }

    public override void Dispose()
    {
        Dispose(true);
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    private StateMachine<DeviceState, DeviceTrigger> ConfigureStateMachine()
    {
        var deviceState = new StateMachine<DeviceState, DeviceTrigger>(DeviceState.None);

        deviceState.Configure(DeviceState.None)
            .Permit(DeviceTrigger.Connected, DeviceState.Connected)
            .Permit(DeviceTrigger.WaitingConfirmDisconnect, DeviceState.Disconnected)
            .Permit(DeviceTrigger.Disconnected, DeviceState.Disconnected)
            .Ignore(DeviceTrigger.Close);

        deviceState.Configure(DeviceState.Connected)
            .Ignore(DeviceTrigger.Connected)
            .Permit(DeviceTrigger.WaitingConfirmDisconnect, DeviceState.WaitingConfirmDisconnect)
            .Permit(DeviceTrigger.Disconnected, DeviceState.Disconnected)
            .Permit(DeviceTrigger.Close, DeviceState.None);

        deviceState.Configure(DeviceState.WaitingConfirmDisconnect)
            .Ignore(DeviceTrigger.WaitingConfirmDisconnect)
            .Permit(DeviceTrigger.Connected, DeviceState.Connected)
            .Permit(DeviceTrigger.Disconnected, DeviceState.Disconnected)
            .Permit(DeviceTrigger.Close, DeviceState.None);

        deviceState.Configure(DeviceState.Disconnected)
            .Ignore(DeviceTrigger.Disconnected)
            .Permit(DeviceTrigger.Connected, DeviceState.Connected)
            .Permit(DeviceTrigger.Close, DeviceState.None);

        return deviceState;
    }

    private void OnDeviceStateChanged(DeviceState state, DeviceState from)
    {
        if (state == from)
        {
            return;
        }

        switch (state)
        {
            case DeviceState.Connected:
                _logger.LogInformation("Device connected");
                _canLock = true;
                break;

            case DeviceState.WaitingConfirmDisconnect:
                _logger.LogInformation("Device disconnected. Waiting for confirmation...");
                _waitingForConfirmationTime = DateTimeOffset.UtcNow;
                break;

            case DeviceState.Disconnected:
                _logger.LogInformation("Device disconnected");

                if (_canLock && from == DeviceState.WaitingConfirmDisconnect)
                {
                    _canLock = false;
                    _windowsHelper.LockOs();
                }

                break;

            case DeviceState.None:
                _logger.LogInformation("Device disconnected");
                break;
        }
    }

    private async Task UpdateDeviceState(DeviceTrigger trigger)
    {
        var lastState = _deviceState.State;
        await _deviceState.FireAsync(trigger);
        OnDeviceStateChanged(_deviceState.State, lastState);
    }

    private async Task RefreshDeviceState(BtDevice? target)
    {
        if (target == null)
        {
            return;
        }

        var isConnected = target.Connected;
        var currentState = _deviceState.State;

        if (isConnected)
        {
            await UpdateDeviceState(DeviceTrigger.Connected);
            return;
        }

        switch (currentState)
        {
            case DeviceState.Connected:
                await UpdateDeviceState(DeviceTrigger.WaitingConfirmDisconnect);
                break;

            case DeviceState.WaitingConfirmDisconnect:
                var timeElapsed = DateTimeOffset.UtcNow - _waitingForConfirmationTime;
                _logger.LogInformation("Time elapsed: {TimeElapsed}", timeElapsed);
                if (timeElapsed.TotalSeconds >= WaitingForConfirmationTimeout)
                {
                    await UpdateDeviceState(DeviceTrigger.Disconnected);
                }

                break;
        }
    }

    private Task TryReconnectDevice()
    {
        if (_selectedDeviceIndex == null)
        {
            return Task.CompletedTask;
        }

        var device = _devices![_selectedDeviceIndex.Value];
        if (device.Connected)
        {
            return Task.CompletedTask;
        }

        _logger.LogInformation("Trying to reconnect device: {Device}", device.DeviceName);
        return Task.Run(() => _bluetoothHelper.RecordDevice(device));
    }

    private void RefreshDeviceState()
    {
        if (_devices == null)
        {
            return;
        }

        foreach (var device in _devices)
        {
            device.Refresh();
        }
    }

    private void UpdateDeviceMenu()
    {
        _disableMenuItem!.Checked = _selectedDeviceIndex == null;

        if (_contextMenu == null)
        {
            return;
        }

        foreach (var item in _contextMenu.Items)
        {
            if (item is DevicePopupMenuItem deviceItem)
            {
                deviceItem.UpdateState();

                if (_devices == null)
                {
                    continue;
                }

                deviceItem.Checked = _selectedDeviceIndex == Array.IndexOf(_devices, deviceItem.Device);
            }
        }
    }

    private void ShowNotification(string message, NotificationIcon icon = NotificationIcon.None)
    {
        _trayIcon!.ShowNotification("Auto Locker", message, icon);
    }

    private async Task DiscoverDevices()
    {
        _contextMenu?.Items.Clear();
        _contextMenu?.Items.Add(new PopupMenuItem("Detecting devices...", (_, _) =>
        {
        }));
        _contextMenu?.Items.Add(new PopupMenuSeparator());
        _contextMenu?.Items.Add(_exitMenuItem!);

        var getDevicesTask = Task.Run(() => _bluetoothHelper.GetDevices());
        _devices = await getDevicesTask;
        if (_devices == null || _devices.Length == 0)
        {
            _logger.LogInformation("No devices found");
            return;
        }

        _logger.LogInformation("Found {Count} devices", _devices.Length);
        ShowNotification($"Found {_devices.Length} devices", NotificationIcon.Info);
    }

    private void SettingDeviceMenu()
    {
        if (_devices == null || _devices.Length == 0)
        {
            return;
        }

        _contextMenu?.Items.Clear();
        _contextMenu?.Items.Add(_disableMenuItem!);
        foreach (var device in _devices)
        {
            var menuItem = new DevicePopupMenuItem(device, (_, _) =>
            {
                _canLock = false;
                _deviceState.Fire(DeviceTrigger.Close);

                _selectedDeviceIndex = Array.IndexOf(_devices, device);

                _logger.LogInformation("Selected device: {Device}", device.DeviceName);
            });

            _contextMenu?.Items.Add(menuItem);
        }

        _contextMenu?.Items.Add(new PopupMenuSeparator());
        _contextMenu?.Items.Add(_settingsMenuItem!);
        _contextMenu?.Items.Add(_exitMenuItem!);

        _trayIcon?.Show();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Detecting Bluetooth devices...");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Current Time {Time}", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
                try
                {
                    if (!IsReady)
                    {
                        await DiscoverDevices();
                        SettingDeviceMenu();
                        continue;
                    }

                    _ = TryReconnectDevice();
                    RefreshDeviceState();
                    UpdateDeviceMenu();

                    var currentDevice = _selectedDeviceIndex == null ? null : _devices![_selectedDeviceIndex.Value];
                    await RefreshDeviceState(currentDevice);
                }
                finally
                {
                    await Task.Delay(1000, stoppingToken);
                }
            }
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("Service stopped");
        }
        catch (PlatformNotSupportedException)
        {
            _logger.LogError("Bluetooth is not supported on this platform");
            throw new LockerException();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Unexpected error occurred");
            throw new LockerException();
        }
    }
}