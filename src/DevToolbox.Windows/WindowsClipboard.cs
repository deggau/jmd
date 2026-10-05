using System.Runtime.InteropServices;
using System.Windows;
using DevToolbox.Core;

namespace DevToolbox.Windows;

public sealed class WindowsClipboard : IClipboardText
{
    public async Task<string?> TryGetTextAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return Clipboard.ContainsText(TextDataFormat.UnicodeText)
                    ? Clipboard.GetText(TextDataFormat.UnicodeText)
                    : null;
            }
            catch (Exception exception) when (exception is ExternalException or ArgumentException)
            {
                if (attempt == 5) return null;
                await Task.Delay(35, cancellationToken);
            }
        }
        return null;
    }

    public async Task<bool> TrySetTextAsync(string value, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Clipboard.SetText(value, TextDataFormat.UnicodeText);
                return true;
            }
            catch (Exception exception) when (exception is ExternalException or ArgumentException)
            {
                if (attempt == 5) return false;
                await Task.Delay(35, cancellationToken);
            }
        }
        return false;
    }

    public uint SequenceNumber => GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
