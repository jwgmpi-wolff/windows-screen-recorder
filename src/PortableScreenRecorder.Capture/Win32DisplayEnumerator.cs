using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using PortableScreenRecorder.Core;

namespace PortableScreenRecorder.Capture;

public sealed class Win32DisplayEnumerator(ILogger<Win32DisplayEnumerator> logger) : IDisplayEnumerator
{
    public IReadOnlyList<DisplayInfo> GetActiveDisplays()
    {
        var previousContext = SetThreadDpiAwarenessContext(new nint(-4));
        try
        {
            return EnumeratePhysicalDisplays();
        }
        finally
        {
            if (previousContext != 0)
            {
                SetThreadDpiAwarenessContext(previousContext);
            }
        }
    }

    private IReadOnlyList<DisplayInfo> EnumeratePhysicalDisplays()
    {
        var displays = new List<DisplayInfo>();
        var outputIndex = 0;

        if (!EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = (uint)Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return true;
            }

            var dpiX = 96u;
            var dpiY = 96u;
            _ = GetDpiForMonitor(monitor, MonitorDpiType.Effective, out dpiX, out dpiY);
            var bounds = new PixelRect(
                info.Monitor.Left,
                info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left,
                info.Monitor.Bottom - info.Monitor.Top);
            var deviceName = info.DeviceName.TrimEnd('\0');
            var friendlyName = GetFriendlyName(deviceName, outputIndex + 1);

            displays.Add(new DisplayInfo(
                deviceName,
                friendlyName,
                outputIndex++,
                bounds,
                dpiX,
                dpiY,
                (info.Flags & MonitorInfoPrimary) != 0));
            return true;
        }, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not enumerate active displays.");
        }

        displays.Sort(static (left, right) =>
        {
            if (left.IsPrimary != right.IsPrimary)
            {
                return left.IsPrimary ? -1 : 1;
            }
            return string.Compare(left.DeviceName, right.DeviceName, StringComparison.OrdinalIgnoreCase);
        });

        logger.LogInformation("Enumerated {Count} active displays: {Displays}", displays.Count, string.Join("; ", displays.Select(static d => d.Label)));
        return displays;
    }

    private static string GetFriendlyName(string deviceName, int fallbackNumber)
    {
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        return EnumDisplayDevices(deviceName, 0, ref device, 0) && !string.IsNullOrWhiteSpace(device.DeviceString)
            ? device.DeviceString.TrimEnd('\0')
            : $"Display {fallbackNumber}";
    }

    private const uint MonitorInfoPrimary = 1;

    private delegate bool MonitorEnumProc(nint monitor, nint deviceContext, nint rect, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string device, uint deviceNumber, ref DisplayDevice displayDevice, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint dpiContext);

    private enum MonitorDpiType
    {
        Effective
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }
}
