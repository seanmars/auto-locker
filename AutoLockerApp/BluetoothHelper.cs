using InTheHand.Net.Sockets;

namespace AutoLockerApp;

public class BluetoothHelper
{
    public BtDevice[]? GetDevices()
    {
        try
        {
            using var client = new BluetoothClient();
            return client.DiscoverDevices()
                .Select(device => new BtDevice(device.DeviceAddress)).ToArray();
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }
}