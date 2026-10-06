using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using JMD.Core;

namespace JMD.Windows;

public static class ForegroundWindowDetails
{
    public static ConversionSource? Read(IntPtr window)
    {
        if (window == IntPtr.Zero) return null;

        var titleLength = GetWindowTextLength(window);
        var titleBuilder = new StringBuilder(Math.Max(titleLength, 0) + 1);
        if (titleLength > 0)
            _ = GetWindowText(window, titleBuilder, titleBuilder.Capacity);
        var title = titleBuilder.ToString().Trim();

        _ = GetWindowThreadProcessId(window, out var processId);
        string? applicationName = null;
        if (processId != 0)
        {
            try
            {
                using var process = Process.GetProcessById((int)processId);
                applicationName = process.ProcessName;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // Keep the window title when the process name is unavailable.
            }
        }

        return applicationName is null && title.Length == 0
            ? null
            : new ConversionSource(applicationName, title.Length == 0 ? null : title);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
