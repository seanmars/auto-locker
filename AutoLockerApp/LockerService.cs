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

public class LockerService : BackgroundService
{
    private readonly ILogger _logger;
    private readonly IHostApplicationLifetime _appLifetime;

    private readonly WindowsHelper _windowsHelper;
    private readonly BluetoothHelper _bluetoothHelper;

    private readonly StateMachine<DeviceState, DeviceTrigger> _deviceState;

    private const int WaitingForConfirmationTimeout = 5;
    private bool _canLock;
    private DateTimeOffset _waitingForConfirmationTime = DateTimeOffset.UtcNow;

    private readonly Stream? _iconStream;
    private readonly Icon _icon;
    private readonly TrayIconWithContextMenu _trayIcon;
    private readonly PopupMenu? _contextMenu;
    private readonly PopupMenuItem? _exitMenuItem;

    private BtDevice[]? _devices;
    private int? _selectedDeviceIndex = null;

    public LockerService(ILogger<LockerService> logger, IHostApplicationLifetime appLifetime,
        WindowsHelper windowsHelper, BluetoothHelper bluetoothHelper)
    {
        _logger = logger;
        _appLifetime = appLifetime;

        _windowsHelper = windowsHelper;
        _bluetoothHelper = bluetoothHelper;

        _deviceState = ConfigureStateMachine();

        _iconStream = typeof(Program).Assembly.GetManifestResourceStream("AutoLockerApp.app.ico");
        _icon = new Icon(_iconStream!);
        _contextMenu = new PopupMenu();
        _exitMenuItem = new PopupMenuItem("Exit", (_, _) =>
        {
            _appLifetime.StopApplication();
        });

        _trayIcon = new TrayIconWithContextMenu
        {
            Icon = _icon.Handle,
            ToolTip = "AutoLocker",
        };
        _contextMenu.Items.Add(_exitMenuItem);
        _trayIcon.ContextMenu = _contextMenu;

        _trayIcon.Create();
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        _iconStream?.Dispose();
        _icon.Dispose();
        _trayIcon.Dispose();
    }

    public sealed override void Dispose()
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
                if (timeElapsed.TotalSeconds >= WaitingForConfirmationTimeout)
                {
                    await UpdateDeviceState(DeviceTrigger.Disconnected);
                }

                break;
        }
    }

    private void RefreshDevice()
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
        if (_contextMenu == null)
        {
            return;
        }

        foreach (var item in _contextMenu.Items)
        {
            if (item is DevicePopupMenuItem deviceItem)
            {
                deviceItem.UpdateState();
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Detecting Bluetooth devices...");
        try
        {
            _devices = _bluetoothHelper.GetDevices();
            if (_devices == null)
            {
                _logger.LogInformation("No devices found");
                return;
            }

            _contextMenu?.Items.Clear();
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
            _contextMenu?.Items.Add(_exitMenuItem!);

            _trayIcon.Show();

            while (!stoppingToken.IsCancellationRequested)
            {
                RefreshDevice();
                UpdateDeviceMenu();

                var currentDevice = _selectedDeviceIndex == null ? null : _devices[_selectedDeviceIndex.Value];
                await RefreshDeviceState(currentDevice);

                await Task.Delay(1000, stoppingToken);
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