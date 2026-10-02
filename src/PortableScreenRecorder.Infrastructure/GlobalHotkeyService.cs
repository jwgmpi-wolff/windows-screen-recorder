using System.ComponentModel;
using System.Runtime.InteropServices;
using PortableScreenRecorder.Core;

namespace PortableScreenRecorder.Infrastructure;

public sealed class GlobalHotkeyService : IHotkeyService
{
    private const int WmHotkey = 0x0312;
    private const int StartStopId = 0x5053;
    private const int PauseResumeId = 0x5054;
    private nint windowHandle;

    public event EventHandler? StartStopPressed;
    public event EventHandler? PauseResumePressed;

    public void Attach(nint handle, string startStop, string pauseResume)
    {
        Dispose();
        windowHandle = handle;
        Register(StartStopId, startStop);
        try
        {
            Register(PauseResumeId, pauseResume);
        }
        catch
        {
            UnregisterHotKey(windowHandle, StartStopId);
            windowHandle = 0;
            throw;
        }
    }

    public bool ProcessMessage(int message, nint wParam)
    {
        if (message != WmHotkey)
        {
            return false;
        }

        switch (wParam.ToInt32())
        {
            case StartStopId:
                StartStopPressed?.Invoke(this, EventArgs.Empty);
                return true;
            case PauseResumeId:
                PauseResumePressed?.Invoke(this, EventArgs.Empty);
                return true;
            default:
                return false;
        }
    }

    private void Register(int id, string gesture)
    {
        var (modifiers, key) = Parse(gesture);
        if (!RegisterHotKey(windowHandle, id, modifiers | 0x4000, key))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"The global hotkey {gesture} is already in use.");
        }
    }

    internal static (uint Modifiers, uint Key) Parse(string gesture)
    {
        uint modifiers = 0;
        uint key = 0;
        foreach (var part in gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) modifiers |= 0x0002;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= 0x0001;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= 0x0004;
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase)) modifiers |= 0x0008;
            else if (part.Length == 1 && char.IsLetterOrDigit(part[0])) key = char.ToUpperInvariant(part[0]);
            else if (part.StartsWith('F') && int.TryParse(part.AsSpan(1), out var function) && function is >= 1 and <= 24) key = (uint)(0x70 + function - 1);
            else throw new FormatException($"Unsupported hotkey component: {part}");
        }

        if (key == 0)
        {
            throw new FormatException($"The hotkey '{gesture}' does not contain a supported key.");
        }
        return (modifiers, key);
    }

    public void Dispose()
    {
        if (windowHandle == 0)
        {
            return;
        }
        UnregisterHotKey(windowHandle, StartStopId);
        UnregisterHotKey(windowHandle, PauseResumeId);
        windowHandle = 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
