using InTheHand.Net.Bluetooth;
using InTheHand.Net.Sockets;

namespace AutoLockerApp;

public class BluetoothHelper : IDisposable
{
    private static readonly object Lock = new();
    private bool _isConnecting;
    private BluetoothClient _client = new();

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

    public void RecordDevice(BtDevice device)
    {
        if (_isConnecting)
        {
            return;
        }

        lock (Lock)
        {
            _isConnecting = true;

            try
            {
                if (device.Connected)
                {
                    return;
                }

                if (_client.Connected)
                {
                    _client.Close();
                    _client.Dispose();
                    _client = new BluetoothClient();
                }

                _isConnecting = true;
                _client.Connect(device.DeviceAddress, BluetoothService.SerialPort);
            }
            catch (Exception)
            {
                // ignored
            }
            finally
            {
                _isConnecting = false;
            }
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}