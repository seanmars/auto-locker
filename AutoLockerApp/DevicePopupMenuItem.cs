using H.NotifyIcon.Core;

namespace AutoLockerApp;

public class DevicePopupMenuItem(BtDevice device, EventHandler<EventArgs> eventHandler)
    : PopupMenuItem(device.ToString(), eventHandler)
{
    public BtDevice Device { get; } = device;

    public void UpdateState()
    {
        Text = Device.ToString();
    }
}