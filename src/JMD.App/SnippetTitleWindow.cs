using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using MessageBox = System.Windows.MessageBox;

namespace JMD.App;

internal sealed class SnippetTitleWindow : Window
{
    private readonly TextBox _title = new();
    public string? SnippetTitle { get; private set; }

    public SnippetTitleWindow()
    {
        Title = "Salvar texto selecionado";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "Salvar texto selecionado", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(new TextBlock { Text = "Digite um título curto para encontrar esse texto depois.", Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 12, Margin = new Thickness(0, 0, 0, 14) });
        _title.Height = 42;
        _title.Padding = new Thickness(10, 8, 10, 8);
        _title.FontSize = 14;
        _title.Background = new SolidColorBrush(Color.FromRgb(36, 39, 48));
        _title.Foreground = Brushes.White;
        _title.BorderBrush = new SolidColorBrush(Color.FromRgb(58, 62, 72));
        _title.ToolTip = "Título do texto salvo";
        _title.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; Save(); } };
        panel.Children.Add(_title);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancelar", Padding = new Thickness(14, 7, 14, 7), Background = new SolidColorBrush(Color.FromRgb(42, 45, 53)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = new Button { Content = "Salvar", FocusVisualStyle = (Style)System.Windows.Application.Current.FindResource("DarkFocusCue"), Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(8, 0, 0, 0), Background = new SolidColorBrush(Color.FromRgb(180, 243, 106)), Foreground = new SolidColorBrush(Color.FromRgb(23, 25, 31)), BorderThickness = new Thickness(0) };
        save.Click += (_, _) => Save();
        actions.Children.Add(cancel);
        actions.Children.Add(save);
        panel.Children.Add(actions);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(23, 25, 31)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 62, 72)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = panel
        };
        Loaded += (_, _) => { _title.Focus(); System.Windows.Input.Keyboard.Focus(_title); };
    }

    private void Save()
    {
        var value = _title.Text.Trim();
        if (value.Length == 0)
        {
            MessageBox.Show(this, "Informe um título para localizar o texto.", "Título obrigatório", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SnippetTitle = value;
        DialogResult = true;
        Close();
    }
}
