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
        return $"{DeviceName} (Connected: {Connected})";
    }
}