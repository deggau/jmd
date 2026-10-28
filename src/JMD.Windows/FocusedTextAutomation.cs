using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using JMD.Core;

namespace JMD.Windows;

/// <summary>Reads and edits text in the currently focused control through UI Automation and keyboard input.</summary>
public sealed class FocusedTextAutomation(KeyboardAutomation keyboard)
{
    /// <summary>Captures the current UIA focus and returns a best-effort action that restores it.</summary>
    public Func<bool>? CaptureFocusRestorer(IntPtr expectedWindow = default)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null) return null;
            if (expectedWindow != IntPtr.Zero)
            {
                var focusedWindow = new IntPtr(focused.Current.NativeWindowHandle);
                var rootWindow = GetAncestor(focusedWindow, 2);
                if (focusedWindow != expectedWindow && rootWindow != expectedWindow) return null;
            }
            return () =>
            {
                try
                {
                    if (!focused.Current.IsEnabled || !focused.Current.IsKeyboardFocusable) return false;
                    focused.SetFocus();
                    return true;
                }
                catch (Exception exception) when (IsAutomationFailure(exception) || exception is UnauthorizedAccessException)
                {
                    return false;
                }
            };
        }
        catch (Exception exception) when (IsAutomationFailure(exception) || exception is UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Finds a visible control by its accessible name or AutomationId and moves keyboard focus to it.</summary>
    public bool FocusControlByName(string accessibleName, IntPtr targetWindow = default)
    {
        var element = FindControl(accessibleName, targetWindow, requireFocusable: true);
        if (element is null) return false;
        try
        {
            element.SetFocus();
            return true;
        }
        catch (Exception exception) when (IsAutomationFailure(exception) || exception is UnauthorizedAccessException)
        {
            return false;
        }
    }

    public WorkflowControlObservation? FindControlObservation(string identifier, IntPtr targetWindow = default)
    {
        var element = FindControl(identifier, targetWindow, requireFocusable: false);
        if (element is null) return null;
        try
        {
            bool? isChecked = null;
            if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var pattern))
                isChecked = ((TogglePattern)pattern).Current.ToggleState == ToggleState.On;
            var current = element.Current;
            return new WorkflowControlObservation(current.IsEnabled, current.IsOffscreen, isChecked);
        }
        catch (Exception exception) when (IsAutomationFailure(exception) || exception is UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static AutomationElement? FindControl(string identifier, IntPtr targetWindow, bool requireFocusable)
    {
        if (!WorkflowControlIdentifierPolicy.TryParse(identifier, out var parsed)) return null;
        try
        {
            var root = targetWindow == IntPtr.Zero
                ? AutomationElement.RootElement
                : AutomationElement.FromHandle(targetWindow);
            var properties = parsed.Kind switch
            {
                WorkflowControlIdentifierKind.Name => new[] { AutomationElement.NameProperty },
                WorkflowControlIdentifierKind.AutomationId => new[] { AutomationElement.AutomationIdProperty },
                _ => new[] { AutomationElement.AutomationIdProperty, AutomationElement.NameProperty }
            };
            foreach (var property in properties)
            {
                var matches = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(property, parsed.Value, PropertyConditionFlags.IgnoreCase));
                foreach (AutomationElement element in matches)
                {
                    var current = element.Current;
                    var actual = property == AutomationElement.AutomationIdProperty ? current.AutomationId : current.Name;
                    if (string.Equals(actual?.Trim(), parsed.Value, StringComparison.OrdinalIgnoreCase) &&
                        (!requireFocusable || current.IsEnabled && current.IsKeyboardFocusable)) return element;
                }
            }
        }
        catch (Exception exception) when (IsAutomationFailure(exception) || exception is UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        return null;
    }

    public sealed record WorkflowControlObservation(bool IsEnabled, bool IsOffscreen, bool? IsChecked);

    public string? ReadSelection()
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is null || !IsTextEntry(element) || !element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return null;
            var textPattern = (TextPattern)pattern;
            var ranges = textPattern.GetSelection();
            return string.Concat(ranges.Select(range => range.GetText(-1)));
        }
        catch (Exception exception) when (IsAutomationFailure(exception) || exception is UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string? ReadText()
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is null || !IsTextEntry(element)) return null;
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            {
                var value = (ValuePattern)pattern;
                return value.Current.IsReadOnly ? null : value.Current.Value;
            }
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
            {
                var text = (TextPattern)pattern;
                return text.DocumentRange.GetText(-1);
            }
            return null;
        }
        catch (Exception exception) when (IsAutomationFailure(exception))
        {
            return null;
        }
    }

    public bool DeleteSelection() => keyboard.SendBackspace();

    public async Task<bool> ReplaceAllAsync(string text, WindowsClipboard clipboard)
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is null || !IsTextEntry(element)) return false;
            if (DraftTextPolicy.ShouldUseClipboard(text))
                return await ReplaceAllWithClipboardAsync(text, clipboard);
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            {
                var value = (ValuePattern)pattern;
                if (!value.Current.IsReadOnly)
                {
                    value.SetValue(text);
                    return true;
                }
            }
            return keyboard.SendSelectAll() && keyboard.SendBackspace() && keyboard.SendUnicodeText(text);
        }
        catch (Exception exception) when (IsAutomationFailure(exception))
        {
            return false;
        }
    }

    private async Task<bool> ReplaceAllWithClipboardAsync(string text, WindowsClipboard clipboard)
    {
        var previousContent = clipboard.TryGetRichContent();
        if (!await clipboard.TrySetTextAsync(text)) return false;
        var pasted = keyboard.SendSelectAll() && keyboard.SendPaste();
        await Task.Delay(120);
        if (previousContent is not null) clipboard.TrySetRichContent(previousContent);
        return pasted;
    }

    public bool HasEditableTextFocus(IntPtr expectedWindow = default)
    {
        var foregroundWindow = keyboard.GetForegroundWindow();
        if (foregroundWindow == IntPtr.Zero || expectedWindow != IntPtr.Zero && foregroundWindow != expectedWindow)
            return false;
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is not null && IsTextEntry(element))
            {
                var elementWindow = new IntPtr(element.Current.NativeWindowHandle);
                if (expectedWindow == IntPtr.Zero || elementWindow == IntPtr.Zero || elementWindow == expectedWindow ||
                    GetAncestor(elementWindow, 2) == expectedWindow)
                    return true;
            }
        }
        catch (Exception exception) when (IsAutomationFailure(exception) || exception is UnauthorizedAccessException)
        {
            // Fall through to Win32 focus information for controls UIA cannot describe.
        }
        return HasEditableNativeFocus(foregroundWindow);
    }

    private static bool HasEditableNativeFocus(IntPtr expectedWindow)
    {
        var threadId = GetWindowThreadProcessId(expectedWindow, out _);
        if (threadId == 0) return false;
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(threadId, ref info) || info.FocusWindow == IntPtr.Zero) return false;
        if (info.FocusWindow != expectedWindow && GetAncestor(info.FocusWindow, 2) != expectedWindow) return false;

        var className = new StringBuilder(256);
        if (GetClassName(info.FocusWindow, className, className.Capacity) == 0) return false;
        return VoiceDictationPolicy.IsEditableNativeControlClass(className.ToString());
    }

    private static bool IsAutomationFailure(Exception exception)
        => exception is ElementNotAvailableException or InvalidOperationException or COMException;

    private static bool IsTextEntry(AutomationElement element)
        => element.Current.ControlType == ControlType.Edit && element.Current.IsEnabled && element.Current.IsKeyboardFocusable;

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint Size;
        public uint Flags;
        public IntPtr ActiveWindow;
        public IntPtr FocusWindow;
        public IntPtr CaptureWindow;
        public IntPtr MenuOwnerWindow;
        public IntPtr MoveSizeWindow;
        public IntPtr CaretWindow;
        public System.Drawing.Rectangle CaretRectangle;
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maximumCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
}
