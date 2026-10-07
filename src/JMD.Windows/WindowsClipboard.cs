using System.Runtime.InteropServices;
using System.Windows;
using JMD.Core;

namespace JMD.Windows;

public sealed record RichClipboardContent(string Text, string? Rtf = null, string? Html = null);

public sealed class WindowsClipboard : IClipboardText
{
    public RichClipboardContent? TryGetRichContent()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null) return null;
            var text = data.GetDataPresent(DataFormats.UnicodeText)
                ? data.GetData(DataFormats.UnicodeText) as string
                : null;
            var rtf = data.GetDataPresent(DataFormats.Rtf) ? data.GetData(DataFormats.Rtf) as string : null;
            var html = data.GetDataPresent(DataFormats.Html) ? data.GetData(DataFormats.Html) as string : null;
            return string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(rtf) && string.IsNullOrWhiteSpace(html)
                ? null
                : new RichClipboardContent(text ?? string.Empty, rtf, html);
        }
        catch (Exception exception) when (exception is ExternalException or ArgumentException)
        {
            return null;
        }
    }

    public bool TrySetRichContent(RichClipboardContent content)
    {
        try
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, content.Text);
            if (!string.IsNullOrWhiteSpace(content.Rtf)) data.SetData(DataFormats.Rtf, content.Rtf);
            if (!string.IsNullOrWhiteSpace(content.Html)) data.SetData(DataFormats.Html, content.Html);
            Clipboard.SetDataObject(data, true);
            return true;
        }
        catch (Exception exception) when (exception is ExternalException or ArgumentException)
        {
            return false;
        }
    }

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
                var writtenText = Clipboard.ContainsText(TextDataFormat.UnicodeText)
                    ? Clipboard.GetText(TextDataFormat.UnicodeText)
                    : null;
                return string.Equals(writtenText, value, StringComparison.Ordinal);
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
