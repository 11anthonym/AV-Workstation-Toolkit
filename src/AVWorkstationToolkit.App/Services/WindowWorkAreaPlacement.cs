using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AVWorkstationToolkit.App.Services;

internal static class WindowWorkAreaPlacement
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoZOrder = 0x0004;
    private const double DefaultDpi = 96d;

    internal static bool TryFitToCurrentMonitor(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var current)) return false;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return false;
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return false;

        var workArea = PixelBounds.FromRect(monitorInfo.WorkArea);
        var fitted = Fit(PixelBounds.FromRect(current), workArea);
        uint dpi;
        try { dpi = GetDpiForWindow(handle); }
        catch (EntryPointNotFoundException) { dpi = 0; }
        var scale = dpi == 0 ? 1d : dpi / DefaultDpi;
        window.MinWidth = Math.Min(window.MinWidth, workArea.Width / scale);
        window.MinHeight = Math.Min(window.MinHeight, workArea.Height / scale);
        window.Width = fitted.Width / scale;
        window.Height = fitted.Height / scale;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        return SetWindowPos(handle, IntPtr.Zero, fitted.Left, fitted.Top, fitted.Width, fitted.Height,
            SwpNoActivate | SwpNoZOrder);
    }

    internal static bool IsTitleBarWithinCurrentWorkArea(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var current)) return false;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return false;
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return false;
        return current.Top >= monitorInfo.WorkArea.Top && current.Top < monitorInfo.WorkArea.Bottom &&
            current.Left < monitorInfo.WorkArea.Right && current.Right > monitorInfo.WorkArea.Left;
    }

    internal static PixelBounds Fit(PixelBounds requested, PixelBounds workArea)
    {
        if (requested.Width <= 0 || requested.Height <= 0) throw new ArgumentOutOfRangeException(nameof(requested));
        if (workArea.Width <= 0 || workArea.Height <= 0) throw new ArgumentOutOfRangeException(nameof(workArea));
        var width = Math.Min(requested.Width, workArea.Width);
        var height = Math.Min(requested.Height, workArea.Height);
        var left = workArea.Left + ((workArea.Width - width) / 2);
        var top = workArea.Top + ((workArea.Height - height) / 2);
        return new PixelBounds(left, top, width, height);
    }

    internal readonly record struct PixelBounds(int Left, int Top, int Width, int Height)
    {
        internal int Right => checked(Left + Width);
        internal int Bottom => checked(Top + Height);

        internal static PixelBounds FromRect(NativeRect value) =>
            new(value.Left, value.Top, checked(value.Right - value.Left), checked(value.Bottom - value.Top));
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal int Size;
        internal NativeRect Monitor;
        internal NativeRect WorkArea;
        internal uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}
