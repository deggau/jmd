using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using JMD.Windows;
using Button = System.Windows.Controls.Button;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using MessageBox = System.Windows.MessageBox;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace JMD.App;

internal sealed class SnippetEditorWindow : Window
{
    private readonly TextBox _titleBox = new();
    private readonly RichTextBox _editor = new();
    private readonly RichClipboardContent? _original;
    private readonly string? _existingId;
    private bool _contentChanged;

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

        _editor.Document = new System.Windows.Documents.FlowDocument
        {
            PagePadding = new Thickness(10),
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 14,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(32, 35, 42))
        };
        _editor.AcceptsTab = true;
        _editor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _editor.Padding = new Thickness(2);
        _editor.Background = new SolidColorBrush(Color.FromRgb(32, 35, 42));
        _editor.Foreground = Brushes.White;
        _editor.BorderBrush = new SolidColorBrush(Color.FromRgb(58, 62, 72));
        _editor.Margin = new Thickness(0, 0, 0, 4);
        _editor.TextChanged += (_, _) => _contentChanged = true;
        body.Children.Add(_editor);
        Loaded += (_, _) =>
        {
            LoadContent();
            _contentChanged = false;
            _titleBox.Focus();
        };
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
            _editor.Focus();
            switch (command)
            {
                case "bold": System.Windows.Documents.EditingCommands.ToggleBold.Execute(null, _editor); break;
                case "italic": System.Windows.Documents.EditingCommands.ToggleItalic.Execute(null, _editor); break;
                case "underline": System.Windows.Documents.EditingCommands.ToggleUnderline.Execute(null, _editor); break;
                case "insertUnorderedList": System.Windows.Documents.EditingCommands.ToggleBullets.Execute(null, _editor); break;
                case "insertOrderedList": System.Windows.Documents.EditingCommands.ToggleNumbering.Execute(null, _editor); break;
            }
        };
        toolbar.Children.Add(button);
    }

    private void LoadContent()
    {
        try
        {
            if (_original?.Rtf is { Length: > 0 } sourceRtf)
            {
                using var stream = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(sourceRtf));
                new System.Windows.Documents.TextRange(_editor.Document.ContentStart, _editor.Document.ContentEnd)
                    .Load(stream, System.Windows.DataFormats.Rtf);
                return;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or System.IO.IOException or System.Windows.Markup.XamlParseException) { }

        if (_original?.Html is { Length: > 0 } sourceHtml && TryLoadHtml(sourceHtml)) return;

        var initialText = _original?.Text ?? string.Empty;
        if (initialText.Length > 0)
            new System.Windows.Documents.TextRange(_editor.Document.ContentStart, _editor.Document.ContentEnd).Text = initialText;
    }

    private bool TryLoadHtml(string html)
    {
        try
        {
            var fragment = ExtractHtmlFragment(html);
            fragment = Regex.Replace(fragment, @"<(br|hr)(\s[^>]*)?>", "<$1/>", RegexOptions.IgnoreCase);
            fragment = Regex.Replace(fragment, @"&nbsp;", "&#160;", RegexOptions.IgnoreCase);
            fragment = Regex.Replace(fragment, @"&(?!#\d+;|#x[0-9a-f]+;|amp;|lt;|gt;|quot;|apos;)", "&amp;", RegexOptions.IgnoreCase);
            var root = XElement.Parse($"<root>{fragment}</root>", LoadOptions.PreserveWhitespace);
            var document = _editor.Document;
            document.Blocks.Clear();
            foreach (var node in root.Nodes())
            {
                if (node is XElement element && element.Name.LocalName.Equals("ul", StringComparison.OrdinalIgnoreCase))
                    document.Blocks.Add(CreateHtmlList(element, System.Windows.TextMarkerStyle.Disc));
                else if (node is XElement ordered && ordered.Name.LocalName.Equals("ol", StringComparison.OrdinalIgnoreCase))
                    document.Blocks.Add(CreateHtmlList(ordered, System.Windows.TextMarkerStyle.Decimal));
                else if (node is XElement block && IsHtmlBlock(block.Name.LocalName))
                    document.Blocks.Add(CreateHtmlParagraph(block));
                else
                {
                    var paragraph = new System.Windows.Documents.Paragraph();
                    AddHtmlInline(node, paragraph.Inlines);
                    document.Blocks.Add(paragraph);
                }
            }
            if (document.Blocks.Count == 0) document.Blocks.Add(new System.Windows.Documents.Paragraph());
            return true;
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or ArgumentException or InvalidOperationException)
        {
            _editor.Document.Blocks.Clear();
            return false;
        }
    }

    private static bool IsHtmlBlock(string name) => name.Equals("p", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("div", StringComparison.OrdinalIgnoreCase) || name.Equals("blockquote", StringComparison.OrdinalIgnoreCase);

    private static System.Windows.Documents.Paragraph CreateHtmlParagraph(XElement element)
    {
        var paragraph = new System.Windows.Documents.Paragraph();
        AddHtmlInline(element, paragraph.Inlines);
        return paragraph;
    }

    private static System.Windows.Documents.List CreateHtmlList(XElement element, System.Windows.TextMarkerStyle markerStyle)
    {
        var list = new System.Windows.Documents.List { MarkerStyle = markerStyle };
        foreach (var item in element.Elements().Where(child => child.Name.LocalName.Equals("li", StringComparison.OrdinalIgnoreCase)))
        {
            var listItem = new System.Windows.Documents.ListItem();
            var paragraph = new System.Windows.Documents.Paragraph();
            AddHtmlInline(item, paragraph.Inlines);
            listItem.Blocks.Add(paragraph);
            list.ListItems.Add(listItem);
        }
        return list;
    }

    private static void AddHtmlInline(XNode node, System.Windows.Documents.InlineCollection target)
    {
        if (node is XText text)
        {
            if (text.Value.Length > 0) target.Add(new System.Windows.Documents.Run(text.Value));
            return;
        }
        if (node is not XElement element) return;
        var name = element.Name.LocalName.ToLowerInvariant();
        if (name is "br" or "hr") { target.Add(new System.Windows.Documents.LineBreak()); return; }
        System.Windows.Documents.Span? wrapper = name switch
        {
            "b" or "strong" => new System.Windows.Documents.Bold(),
            "i" or "em" => new System.Windows.Documents.Italic(),
            "u" => new System.Windows.Documents.Underline(),
            "span" => new System.Windows.Documents.Span(),
            _ => null
        };
        if (wrapper is not null)
        {
            if (name == "span" && element.Attribute("style") is { } style)
                ApplyHtmlStyles(wrapper, style.Value);
            foreach (var child in element.Nodes()) AddHtmlInline(child, wrapper.Inlines);
            target.Add(wrapper);
            return;
        }
        foreach (var child in element.Nodes()) AddHtmlInline(child, target);
    }

    private static void ApplyHtmlStyles(System.Windows.Documents.Span span, string style)
    {
        foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = declaration.IndexOf(':');
            if (separator < 0) continue;
            var name = declaration[..separator].Trim().ToLowerInvariant();
            var value = declaration[(separator + 1)..].Trim();
            switch (name)
            {
                case "font-weight" when value.Equals("bold", StringComparison.OrdinalIgnoreCase) || value == "600" || value == "700":
                    span.FontWeight = FontWeights.Bold;
                    break;
                case "font-style" when value.Equals("italic", StringComparison.OrdinalIgnoreCase):
                    span.FontStyle = FontStyles.Italic;
                    break;
                case "text-decoration" when value.Contains("underline", StringComparison.OrdinalIgnoreCase):
                    span.TextDecorations = TextDecorations.Underline;
                    break;
                case "font-size":
                    var unit = value.EndsWith("pt", StringComparison.OrdinalIgnoreCase) ? "pt" : "px";
                    if (double.TryParse(value[..^unit.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var size))
                        span.FontSize = unit == "pt" ? size * 96 / 72 : size;
                    break;
                case "color":
                    try { span.Foreground = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(value)); }
                    catch (FormatException) { }
                    break;
                case "background-color":
                    try { span.Background = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(value)); }
                    catch (FormatException) { }
                    break;
            }
        }
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
        var range = new System.Windows.Documents.TextRange(_editor.Document.ContentStart, _editor.Document.ContentEnd);
        var text = range.Text.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(text))
        {
            MessageBox.Show(this, "Digite o texto que será inserido.", "Texto obrigatório", MessageBoxButton.OK, MessageBoxImage.Information);
            _editor.Focus();
            return;
        }
        var changed = _contentChanged;
        string? rtf = _original?.Rtf;
        string? html = _original?.Html;
        if (changed || rtf is null && html is null)
        {
            using var stream = new System.IO.MemoryStream();
            range.Save(stream, System.Windows.DataFormats.Rtf);
            rtf = Encoding.UTF8.GetString(stream.ToArray());
            html = BuildClipboardHtml(SerializeBlocks(_editor.Document.Blocks));
        }
        Result = new SnippetEntry
        {
            Id = _existingId ?? Guid.NewGuid().ToString("N"),
            Title = title,
            Text = text,
            Html = html,
            Rtf = rtf
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
                    var styles = new List<string>();
                    if (span.FontWeight == FontWeights.Bold) styles.Add("font-weight:bold");
                    if (span.FontStyle == FontStyles.Italic) styles.Add("font-style:italic");
                    if (span.TextDecorations?.Contains(TextDecorations.Underline[0]) == true) styles.Add("text-decoration:underline");
                    if (!double.IsNaN(span.FontSize) && Math.Abs(span.FontSize - 14) > 0.1) styles.Add($"font-size:{span.FontSize.ToString("0.##", CultureInfo.InvariantCulture)}px");
                    if (span.Foreground is SolidColorBrush foreground) styles.Add($"color:{foreground.Color}");
                    if (span.Background is SolidColorBrush background) styles.Add($"background-color:{background.Color}");
                    output.Append(styles.Count == 0 ? "<span>" : $"<span style=\"{string.Join(';', styles)}\">")
                        .Append(SerializeInlines(span.Inlines)).Append("</span>");
                    break;
            }
        }
        return output.ToString();
    }

    private static string EscapeHtml(string value) => System.Net.WebUtility.HtmlEncode(value);

}
