using InTheHand.Net;
using InTheHand.Net.Sockets;

namespace AutoLockerApp;

public class BtDevice
{
    public BluetoothDeviceInfo Device { get; private set; }

    public bool Connected => Device.Connected;
    public string DeviceName => Device.DeviceName;
    public BluetoothAddress DeviceAddress => Device.DeviceAddress;

    public BtDevice(BluetoothAddress address)
    {
        Device = new BluetoothDeviceInfo(address);
    }

    public void Refresh()
    {
        Device.Refresh();
    }

    public override string ToString()
    {
        var connected = Device.Connected ? "Connected" : "Disconnected";

        return $"{Device.DeviceName} ({connected})";
    }
}