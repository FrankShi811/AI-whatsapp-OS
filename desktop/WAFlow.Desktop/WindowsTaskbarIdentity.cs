using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WAFlow.Desktop;

internal static class WindowsTaskbarIdentity
{
    internal const string AppUserModelId = "AI.Sales.OS.Desktop";

    private const uint WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const int IconSmall2 = 2;

    private static IntPtr _largeIcon;
    private static IntPtr _smallIcon;

    internal static void InitializeProcess()
    {
        if (!OperatingSystem.IsWindows()) return;

        var result = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    internal static void ApplyWindowIcon(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;

        var windowHandle = new WindowInteropHelper(window).Handle;
        var executablePath = Environment.ProcessPath;
        if (windowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(executablePath)) return;

        ReleaseWindowIcon();

        var largeIcons = new IntPtr[1];
        var smallIcons = new IntPtr[1];
        if (ExtractIconEx(executablePath, 0, largeIcons, smallIcons, 1) == 0) return;

        _largeIcon = largeIcons[0];
        _smallIcon = smallIcons[0];

        if (_largeIcon != IntPtr.Zero)
        {
            SendMessage(windowHandle, WmSetIcon, new IntPtr(IconBig), _largeIcon);
        }

        if (_smallIcon != IntPtr.Zero)
        {
            SendMessage(windowHandle, WmSetIcon, new IntPtr(IconSmall), _smallIcon);
            SendMessage(windowHandle, WmSetIcon, new IntPtr(IconSmall2), _smallIcon);
        }
    }

    internal static void ReleaseWindowIcon()
    {
        if (_largeIcon != IntPtr.Zero)
        {
            DestroyIcon(_largeIcon);
            _largeIcon = IntPtr.Zero;
        }

        if (_smallIcon != IntPtr.Zero)
        {
            DestroyIcon(_smallIcon);
            _smallIcon = IntPtr.Zero;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(
        string fileName,
        int iconIndex,
        IntPtr[] largeIcons,
        IntPtr[] smallIcons,
        uint iconCount);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);
}
