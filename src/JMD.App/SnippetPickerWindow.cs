using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace JMD.App;

internal sealed class SnippetPickerWindow : Window
{
    private readonly TextBox _search = new();
    private readonly ListBox _results = new();
    private readonly IReadOnlyList<SnippetEntry> _snippets;

    public SnippetEntry? SelectedSnippet { get; private set; }

    public SnippetPickerWindow(IEnumerable<SnippetEntry> snippets)
    {
        _snippets = snippets.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        Width = 600;
        Height = 400;
        MinWidth = 440;
        MinHeight = 280;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        Title = "Inserir texto salvo";

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(23, 25, 31)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 62, 72)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(20),
            Child = CreateContent()
        };

        _search.TextChanged += (_, _) => RefreshResults();
        _search.PreviewKeyDown += Search_PreviewKeyDown;
        _results.PreviewKeyDown += Search_PreviewKeyDown;
        _results.MouseDoubleClick += (_, _) => AcceptSelection();
        Loaded += (_, _) => { _search.Focus(); Keyboard.Focus(_search); };
        RefreshResults();
    }

    private UIElement CreateContent()
    {
        var panel = new DockPanel();
        var heading = new TextBlock
        {
            Text = "Inserir texto salvo",
            Foreground = Brushes.White,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5)
        };
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        var note = new TextBlock
        {
            Text = "Digite parte do título e pressione Enter",
            Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 12)
        };
        DockPanel.SetDock(note, Dock.Top);
        panel.Children.Add(note);
        _search.Height = 44;
        _search.Padding = new Thickness(12, 9, 12, 9);
        _search.FontSize = 15;
        _search.Background = new SolidColorBrush(Color.FromRgb(36, 39, 48));
        _search.Foreground = Brushes.White;
        _search.BorderBrush = new SolidColorBrush(Color.FromRgb(58, 62, 72));
        _search.CaretBrush = new SolidColorBrush(Color.FromRgb(180, 243, 106));
        _search.ToolTip = "Buscar pelo título";
        DockPanel.SetDock(_search, Dock.Top);
        panel.Children.Add(_search);
        _results.Background = Brushes.Transparent;
        _results.BorderThickness = new Thickness(0);
        _results.Margin = new Thickness(0, 10, 0, 0);
        _results.FocusVisualStyle = (Style)System.Windows.Application.Current.FindResource("AccentFocusCue");
        _results.ItemContainerStyle = (Style)System.Windows.Application.Current.FindResource("KeyboardListItemStyle");
        panel.Children.Add(_results);
        return panel;
    }

    private void RefreshResults()
    {
        var query = _search.Text.Trim();
        _results.Items.Clear();
        foreach (var snippet in _snippets.Where(item => query.Length == 0 || item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
        {
            var label = new TextBlock
            {
                Text = snippet.Title,
                Foreground = Brushes.White,
                FontSize = 14,
                Padding = new Thickness(10, 12, 10, 12)
            };
            _results.Items.Add(new ListBoxItem { Content = label, Tag = snippet, Background = new SolidColorBrush(Color.FromRgb(34, 37, 45)), Margin = new Thickness(0, 0, 0, 5) });
        }
        if (_results.Items.Count > 0) _results.SelectedIndex = 0;
    }

    private void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { AcceptSelection(); e.Handled = true; }
        else if (e.Key == Key.Escape) { DialogResult = false; Close(); e.Handled = true; }
        else if (e.Key == Key.Down && _results.Items.Count > 0) { _results.Focus(); _results.SelectedIndex = 0; e.Handled = true; }
    }

    private void AcceptSelection()
    {
        if (_results.SelectedItem is not ListBoxItem { Tag: SnippetEntry snippet }) return;
        SelectedSnippet = snippet;
        DialogResult = true;
        Close();
    }
}
