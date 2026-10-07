using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using JMD.Windows;
using Button = System.Windows.Controls.Button;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;
using WebBrowser = System.Windows.Controls.WebBrowser;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using MessageBox = System.Windows.MessageBox;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace JMD.App;

internal sealed class SnippetEditorWindow : Window
{
    private readonly TextBox _titleBox = new();
    private readonly WebBrowser _editor = new();
    private readonly RichClipboardContent? _original;
    private readonly string? _existingId;
    private bool _editorReady;

    public SnippetEntry? Result { get; private set; }

    public SnippetEditorWindow(SnippetEntry? existing = null, RichClipboardContent? captured = null)
    {
        _existingId = existing?.Id;
        _original = captured ?? (existing is null ? null : new(existing.Text, existing.Rtf, existing.Html));
        Title = existing is null ? "Adicionar texto" : "Editar texto";
        Width = 680;
        Height = 560;
        MinWidth = 520;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;

        var outer = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(23, 25, 31)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 62, 72)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(20)
        };
        var panel = new DockPanel();
        outer.Child = panel;
        Content = outer;

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(footer);
        var cancel = MakeButton("Cancelar", false);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = MakeButton("Salvar", true);
        save.Margin = new Thickness(8, 0, 0, 0);
        save.Click += Save_Click;
        footer.Children.Add(cancel);
        footer.Children.Add(save);

        var heading = new TextBlock
        {
            Text = existing is null ? "Novo texto salvo" : "Editar texto salvo",
            Foreground = Brushes.White,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);

        var body = new DockPanel();
        panel.Children.Add(body);
        _titleBox.Text = existing?.Title ?? string.Empty;
        _titleBox.Height = 38;
        _titleBox.Padding = new Thickness(10, 7, 10, 7);
        _titleBox.FontSize = 14;
        _titleBox.Background = new SolidColorBrush(Color.FromRgb(36, 39, 48));
        _titleBox.Foreground = Brushes.White;
        _titleBox.BorderBrush = new SolidColorBrush(Color.FromRgb(58, 62, 72));
        _titleBox.ToolTip = "Título usado para encontrar este texto";
        DockPanel.SetDock(_titleBox, Dock.Top);
        body.Children.Add(_titleBox);

        var toolbar = new WrapPanel { Margin = new Thickness(0, 10, 0, 8) };
        DockPanel.SetDock(toolbar, Dock.Top);
        body.Children.Add(toolbar);
        AddFormatButton(toolbar, "B", "bold", true);
        AddFormatButton(toolbar, "I", "italic", true);
        AddFormatButton(toolbar, "U", "underline", true);
        AddFormatButton(toolbar, "• Lista", "insertUnorderedList");
        AddFormatButton(toolbar, "1. Lista", "insertOrderedList");

        _editor.LoadCompleted += Editor_LoadCompleted;
        body.Children.Add(_editor);
        Loaded += (_, _) => { _titleBox.Focus(); _editor.NavigateToString(EditorDocument); };
    }

    private void AddFormatButton(Panel toolbar, string label, string command, bool emphasized = false)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(Color.FromRgb(42, 45, 53)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontWeight = emphasized ? FontWeights.Bold : FontWeights.Normal
        };
        button.Click += (_, _) =>
        {
            if (_editorReady)
            {
                _editor.InvokeScript("format", command);
                _editor.Focus();
            }
        };
        toolbar.Children.Add(button);
    }

    private void Editor_LoadCompleted(object? sender, NavigationEventArgs e)
    {
        if (_editorReady) return;
        _editorReady = true;
        var html = _original?.Html is { Length: > 0 } sourceHtml
            ? ExtractHtmlFragment(sourceHtml)
            : _original?.Rtf is { Length: > 0 } sourceRtf
                ? ExtractRtfHtml(sourceRtf)
                : EscapeHtml(_original?.Text ?? string.Empty).Replace("\r\n", "<br>", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal);
        _editor.InvokeScript("setContent", html);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var title = _titleBox.Text.Trim();
        if (title.Length == 0)
        {
            MessageBox.Show(this, "Informe um título para localizar o texto.", "Título obrigatório", MessageBoxButton.OK, MessageBoxImage.Information);
            _titleBox.Focus();
            return;
        }
        if (!_editorReady) return;

        var html = _editor.InvokeScript("getHtml") as string ?? string.Empty;
        var text = _editor.InvokeScript("getText") as string ?? string.Empty;
        var changed = Equals(_editor.InvokeScript("isChanged"), true);
        Result = new SnippetEntry
        {
            Id = _existingId ?? Guid.NewGuid().ToString("N"),
            Title = title,
            Text = text,
            Html = BuildClipboardHtml(html),
            Rtf = changed ? null : _original?.Rtf
        };
        DialogResult = true;
        Close();
    }

    private static Button MakeButton(string label, bool primary) => new()
    {
        Content = label,
        FocusVisualStyle = (Style)System.Windows.Application.Current.FindResource(primary ? "DarkFocusCue" : "AccentFocusCue"),
        Padding = new Thickness(16, 8, 16, 8),
        Background = primary ? new SolidColorBrush(Color.FromRgb(180, 243, 106)) : new SolidColorBrush(Color.FromRgb(42, 45, 53)),
        Foreground = primary ? new SolidColorBrush(Color.FromRgb(23, 25, 31)) : Brushes.White,
        BorderThickness = new Thickness(0)
    };

    internal static string ExtractHtmlFragment(string html)
    {
        const string start = "<!--StartFragment-->";
        const string end = "<!--EndFragment-->";
        var startAt = html.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        var endAt = html.IndexOf(end, StringComparison.OrdinalIgnoreCase);
        if (startAt >= 0 && endAt > startAt) return html[(startAt + start.Length)..endAt];
        var bodyAt = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        if (bodyAt >= 0)
        {
            bodyAt = html.IndexOf('>', bodyAt);
            var bodyEnd = html.IndexOf("</body", bodyAt, StringComparison.OrdinalIgnoreCase);
            if (bodyAt >= 0 && bodyEnd > bodyAt) return html[(bodyAt + 1)..bodyEnd];
        }
        return html;
    }

    internal static string BuildClipboardHtml(string fragment)
    {
        const string prefix = "<html><body><!--StartFragment-->";
        const string suffix = "<!--EndFragment--></body></html>";
        const string headerTemplate = "Version:0.9\r\nStartHTML:00000000\r\nEndHTML:00000000\r\nStartFragment:00000000\r\nEndFragment:00000000\r\n";
        var headerLength = Encoding.UTF8.GetByteCount(headerTemplate);
        var html = prefix + fragment + suffix;
        var startHtml = headerLength;
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(prefix);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = startHtml + Encoding.UTF8.GetByteCount(html);
        var header = $"Version:0.9\r\nStartHTML:{startHtml:D8}\r\nEndHTML:{endHtml:D8}\r\nStartFragment:{startFragment:D8}\r\nEndFragment:{endFragment:D8}\r\n";
        return header + html;
    }

    private static string ExtractRtfHtml(string rtf)
    {
        var box = new RichTextBox();
        try
        {
            using var stream = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(rtf));
            new System.Windows.Documents.TextRange(box.Document.ContentStart, box.Document.ContentEnd).Load(stream, System.Windows.DataFormats.Rtf);
            return SerializeBlocks(box.Document.Blocks);
        }
        catch (Exception exception) when (exception is ArgumentException or System.IO.IOException) { return string.Empty; }
    }

    private static string SerializeBlocks(System.Windows.Documents.BlockCollection blocks)
    {
        var output = new StringBuilder();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case System.Windows.Documents.Paragraph paragraph:
                    output.Append("<p>").Append(SerializeInlines(paragraph.Inlines)).Append("</p>");
                    break;
                case System.Windows.Documents.List list:
                    var listTag = list.MarkerStyle == System.Windows.TextMarkerStyle.Decimal ? "ol" : "ul";
                    output.Append('<').Append(listTag).Append('>');
                    foreach (var item in list.ListItems)
                        output.Append("<li>").Append(SerializeBlocks(item.Blocks)).Append("</li>");
                    output.Append("</").Append(listTag).Append('>');
                    break;
                case System.Windows.Documents.Section section:
                    output.Append(SerializeBlocks(section.Blocks));
                    break;
            }
        }
        return output.ToString();
    }

    private static string SerializeInlines(System.Windows.Documents.InlineCollection inlines)
    {
        var output = new StringBuilder();
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case System.Windows.Documents.Run run:
                    output.Append(EscapeHtml(run.Text));
                    break;
                case System.Windows.Documents.LineBreak:
                    output.Append("<br>");
                    break;
                case System.Windows.Documents.Bold bold:
                    output.Append("<strong>").Append(SerializeInlines(bold.Inlines)).Append("</strong>");
                    break;
                case System.Windows.Documents.Italic italic:
                    output.Append("<em>").Append(SerializeInlines(italic.Inlines)).Append("</em>");
                    break;
                case System.Windows.Documents.Underline underline:
                    output.Append("<u>").Append(SerializeInlines(underline.Inlines)).Append("</u>");
                    break;
                case System.Windows.Documents.Hyperlink link:
                    output.Append("<a href=\"").Append(EscapeHtml(link.NavigateUri?.ToString() ?? string.Empty)).Append("\">")
                        .Append(SerializeInlines(link.Inlines)).Append("</a>");
                    break;
                case System.Windows.Documents.Span span:
                    output.Append(SerializeInlines(span.Inlines));
                    break;
            }
        }
        return output.ToString();
    }

    private static string EscapeHtml(string value) => System.Net.WebUtility.HtmlEncode(value);

    private const string EditorDocument = """
        <!doctype html><html><head><meta http-equiv='X-UA-Compatible' content='IE=edge'><meta charset='utf-8'>
        <style>html,body{height:100%;margin:0;background:#17191f;color:#f0f1f4;font:14px Segoe UI,Arial}#editor{box-sizing:border-box;height:100%;min-height:260px;overflow:auto;padding:12px;background:#20232a;border:1px solid #3a3e48;border-radius:8px;outline:none}a{color:#b4f36a}</style>
        <script>
        function clean(node){var bad=node.querySelectorAll('script,iframe,object,embed');for(var i=bad.length-1;i>=0;i--)bad[i].parentNode.removeChild(bad[i]);var all=node.querySelectorAll('*');for(var j=0;j<all.length;j++){var attrs=[];for(var k=0;k<all[j].attributes.length;k++)attrs.push(all[j].attributes[k].name);for(var n=0;n<attrs.length;n++){var name=attrs[n],value=all[j].getAttribute(name)||'';if(name.toLowerCase().indexOf('on')===0||(name.toLowerCase()==='href'&&value.toLowerCase().indexOf('javascript:')===0))all[j].removeAttribute(name);}}}
        function setContent(value){var e=document.getElementById('editor');e.innerHTML=value;clean(e);window.changed=false;}
        function setRtfFallback(value){if(!document.getElementById('editor').innerText)document.getElementById('editor').innerHTML=value;}
        function format(command){document.execCommand(command,false,null);document.getElementById('editor').focus();}
        function getHtml(){return document.getElementById('editor').innerHTML;}
        function getText(){return document.getElementById('editor').innerText;}
        function isChanged(){return window.changed;}
        window.onload=function(){document.getElementById('editor').oninput=function(){window.changed=true;};};
        </script></head><body><div id='editor' contenteditable='true'></div></body></html>
        """;
}
