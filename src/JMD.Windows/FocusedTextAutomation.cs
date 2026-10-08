using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace JMD.Windows;

/// <summary>Reads and edits text in the currently focused control through UI Automation and keyboard input.</summary>
public sealed class FocusedTextAutomation(KeyboardAutomation keyboard)
{
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
        catch (Exception exception) when (IsAutomationFailure(exception))
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

    public bool ReplaceAll(string text)
    {
        try
        {
            var element = AutomationElement.FocusedElement;
            if (element is null || !IsTextEntry(element)) return false;
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

    private static bool IsAutomationFailure(Exception exception)
        => exception is ElementNotAvailableException or InvalidOperationException or COMException;

    private static bool IsTextEntry(AutomationElement element)
        => element.Current.ControlType == ControlType.Edit && element.Current.IsEnabled && element.Current.IsKeyboardFocusable;
}
