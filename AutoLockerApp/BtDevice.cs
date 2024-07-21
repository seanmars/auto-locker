using InTheHand.Net;
using InTheHand.Net.Sockets;

namespace AutoLockerApp;

public class BtDevice : BluetoothDeviceInfo
{
    public BtDevice(BluetoothAddress address) : base(address)
    {
    }

    public override string ToString()
    {
        var connected = Connected ? "Connected" : "Disconnected";

        return $"{DeviceName} ({connected})";
    }
}