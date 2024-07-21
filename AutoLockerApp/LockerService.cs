﻿using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sharprompt;
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
    private readonly WindowsHelper _windowsHelper;
    private readonly BluetoothHelper _bluetoothHelper;

    private readonly StateMachine<DeviceState, DeviceTrigger> _deviceState;

    private const int WaitingForConfirmationTimeout = 5;
    private bool _canLock;
    private DateTimeOffset _waitingForConfirmationTime = DateTimeOffset.UtcNow;

    public LockerService(ILogger<LockerService> logger, WindowsHelper windowsHelper, BluetoothHelper bluetoothHelper)
    {
        _logger = logger;
        _windowsHelper = windowsHelper;
        _bluetoothHelper = bluetoothHelper;

        _deviceState = ConfigureStateMachine();
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

    private async Task RefreshDeviceState(BtDevice target)
    {
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Detecting Bluetooth devices...");
        try
        {
            var devices = _bluetoothHelper.GetDevices();
            if (devices == null)
            {
                _logger.LogInformation("No devices found");
            }

            var target = Prompt.Select<BtDevice>(options =>
            {
                options.Items = devices;
                options.Message = "Select target device:";
            });

            _logger.LogInformation("Selected device: {TargetDeviceName}", target.DeviceName);

            while (!stoppingToken.IsCancellationRequested)
            {
                target.Refresh();
                await RefreshDeviceState(target);

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