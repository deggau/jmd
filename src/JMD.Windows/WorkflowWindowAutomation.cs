using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using JMD.Core;

namespace JMD.Windows;

public sealed record WorkflowWindow(IntPtr Handle, uint ProcessId, string Title, string ProcessName);

/// <summary>Small Win32 helpers used by desktop workflow steps.</summary>
public static class WorkflowWindowAutomation
{
    private const int RestoreWindow = 9;
    private const uint NoZOrder = 0x0004;
    private const uint NoActivate = 0x0010;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint RightDown = 0x0008;
    private const uint RightUp = 0x0010;
    private const uint Wheel = 0x0800;
    private const int WheelDelta = 120;

    public static async Task<WorkflowWindow?> WaitForWindowAsync(string target, int timeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        var (isProcess, value) = WorkflowWindowTargetPolicy.Parse(target);
        if (value.Length == 0) return null;
        var timeout = Stopwatch.StartNew();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = FindWindow(isProcess, value);
            if (window is not null) return window;
            await Task.Delay(100, cancellationToken);
        } while (timeout.ElapsedMilliseconds < timeoutMilliseconds);
        return null;
    }

    public static async Task<WorkflowWindow?> WaitForProcessWindowAsync(int processId, int timeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        var timeout = Stopwatch.StartNew();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorkflowWindow? match = null;
            EnumWindows((handle, _) =>
            {
                if (IsWindowVisible(handle) && ReadWindow(handle) is { } candidate && candidate.ProcessId == processId)
                {
                    match = candidate;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            if (match is not null) return match;
            await Task.Delay(100, cancellationToken);
        } while (timeout.ElapsedMilliseconds < timeoutMilliseconds);
        return null;
    }

    public static WorkflowWindow? FindForegroundWindow()
    {
        var handle = GetForegroundWindow();
        return handle == IntPtr.Zero ? null : ReadWindow(handle);
    }

    public static WorkflowWindow? FindWindowByProcessId(int processId)
    {
        WorkflowWindow? match = null;
        EnumWindows((handle, _) =>
        {
            if (IsWindowVisible(handle) && ReadWindow(handle) is { } candidate && candidate.ProcessId == processId)
            {
                match = candidate;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return match;
    }

    public static WorkflowWindow? FindWindowByHandle(IntPtr handle) => ReadWindow(handle);

    public static WorkflowWindow? FindWindow(bool byProcess, string value)
    {
        WorkflowWindow? match = null;
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            var candidate = ReadWindow(handle);
            if (candidate is null) return true;
            var matches = byProcess
                ? string.Equals(candidate.ProcessName, value, StringComparison.OrdinalIgnoreCase)
                : candidate.Title.Length > 0 && candidate.Title.Contains(value, StringComparison.OrdinalIgnoreCase);
            if (!matches) return true;
            match = candidate;
            return false;
        }, IntPtr.Zero);
        return match;
    }

    public static bool Activate(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return false;
        ShowWindow(handle, RestoreWindow);
        return SetForegroundWindow(handle);
    }

    public static bool TryGetBounds(IntPtr handle, out Rectangle bounds)
    {
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var rectangle))
        {
            bounds = Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
            return bounds.Width > 0 && bounds.Height > 0;
        }
        bounds = Rectangle.Empty;
        return false;
    }

    public static bool MoveAndResize(IntPtr handle, Rectangle bounds)
        => handle != IntPtr.Zero && bounds.Width > 0 && bounds.Height > 0 &&
           SetWindowPos(handle, IntPtr.Zero, bounds.Left, bounds.Top, bounds.Width, bounds.Height, NoZOrder | NoActivate);

    public static bool TryClick(IntPtr window, WorkflowRelativePoint relativePoint, WorkflowMouseAction action)
    {
        if (!TryGetBounds(window, out var bounds)) return false;
        var point = relativePoint.ToScreenPoint(bounds);
        if (!SetCursorPos(point.X, point.Y)) return false;
        switch (action)
        {
            case WorkflowMouseAction.LeftClick:
                SendMouseInput(LeftDown, 0);
                SendMouseInput(LeftUp, 0);
                return true;
            case WorkflowMouseAction.DoubleClick:
                SendMouseInput(LeftDown, 0);
                SendMouseInput(LeftUp, 0);
                Thread.Sleep(60);
                SendMouseInput(LeftDown, 0);
                SendMouseInput(LeftUp, 0);
                return true;
            case WorkflowMouseAction.RightClick:
                SendMouseInput(RightDown, 0);
                SendMouseInput(RightUp, 0);
                return true;
            case WorkflowMouseAction.ScrollUp:
                SendMouseInput(Wheel, WheelDelta);
                return true;
            case WorkflowMouseAction.ScrollDown:
                SendMouseInput(Wheel, -WheelDelta);
                return true;
            default:
                return false;
        }
    }

    public static bool TryCaptureRelativePoint(Point screenPoint, out IntPtr windowHandle, out WorkflowRelativePoint point)
    {
        windowHandle = GetAncestor(WindowFromPoint(screenPoint), 2);
        if (!TryGetBounds(windowHandle, out var bounds))
        {
            point = default;
            return false;
        }
        point = WorkflowRelativePoint.FromScreenPoint(screenPoint, bounds);
        return true;
    }

    private static WorkflowWindow? ReadWindow(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);
        var title = new System.Text.StringBuilder(length + 1);
        if (length > 0) _ = GetWindowText(handle, title, title.Capacity);
        _ = GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return new WorkflowWindow(handle, processId, title.ToString(), process.ProcessName);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static void SendMouseInput(uint flags, int data) => mouse_event(flags, 0, 0, unchecked((uint)data), UIntPtr.Zero);

    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int maxCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extraInfo);
}
