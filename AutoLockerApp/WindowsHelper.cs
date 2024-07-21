using System.Runtime.InteropServices;

namespace AutoLockerApp;

public class WindowsHelper
{
    [DllImport("user32.dll")]
    public static extern bool LockWorkStation();

    public void LockOs()
    {
        LockWorkStation();
    }
}