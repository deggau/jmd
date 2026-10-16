using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JMD.Core;
using JMD.Windows;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;

namespace JMD.App;

internal sealed class CustomShortcutEditorWindow : Window
{
    private readonly TextBox _name = CreateBox(36);
    private readonly TextBox _target = CreateBox(36);
    private readonly TextBox _shortcut = CreateBox(36);
    private readonly TextBox _phrases = CreateBox(72, multiline: true);
    private readonly CheckBox _focusIfAlreadyOpen = new() { Content = "Se já estiver aberto, trazer para frente", Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(2, 10, 0, 0) };

    public string CommandName => _name.Text.Trim();
    public string Target => _target.Text.Trim();
    public string Shortcut => _shortcut.Text.Trim();
    public string[] VoicePhrases => _phrases.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public bool FocusIfAlreadyOpen => _focusIfAlreadyOpen.IsChecked == true;

    public CustomShortcutEditorWindow(CustomShortcutEntry? existing = null, IEnumerable<string>? existingPhrases = null)
    {
        if (existing is not null)
        {
            _name.Text = existing.Name;
            _target.Text = existing.Target;
            _shortcut.Text = existing.Shortcut;
            _focusIfAlreadyOpen.IsChecked = existing.FocusIfAlreadyOpen;
            _phrases.Text = string.Join(Environment.NewLine, existingPhrases ?? []);
        }
        Title = existing is null ? "Criar atalho personalizado" : "Editar atalho personalizado";
        Width = 460;
        Height = 500;
        MinWidth = 420;
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
            Padding = new Thickness(16)
        };
        Content = outer;
        var layout = new DockPanel();
        outer.Child = layout;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        var cancel = MakeButton("Cancelar", false);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = MakeButton("Salvar comando", true);
        save.Margin = new Thickness(8, 0, 0, 0);
        save.Click += (_, _) => { DialogResult = true; Close(); };
        footer.Children.Add(cancel);
        footer.Children.Add(save);

        var form = new StackPanel();
        layout.Children.Add(form);
        var heading = new TextBlock { Text = existing is null ? "Novo atalho personalizado" : "Editar atalho personalizado", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(heading, Dock.Top);
        layout.Children.Add(heading);
        AddField(form, "Nome do comando", _name, "Nome exibido na lista de comandos");
        AddField(form, "Programa, parâmetros ou URL", _target, "Use aspas em caminhos e argumentos com espaços");
        form.Children.Add(new TextBlock { Text = "Exemplos: notepad.exe · explorer.exe \"C:\\Atalhos\\\" · \"C:\\Apps\\Minha App.exe\" /abrir · https://example.com", Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 2, 0) });
        form.Children.Add(_focusIfAlreadyOpen);
        AddField(form, "Atalho de teclado (opcional)", _shortcut, "Clique no campo e pressione Ctrl, Alt, Shift ou Win + outra tecla");
        AddField(form, "Frases do Jey (opcional)", _phrases, "Uma frase por linha");
        _shortcut.PreviewKeyDown += CaptureShortcut;
        Loaded += (_, _) => _name.Focus();
    }

    private static TextBox CreateBox(double height, bool multiline = false) => new()
    {
        Height = height,
        Padding = new Thickness(9, 6, 9, 6),
        FontSize = 12,
        Foreground = Brushes.White,
        Background = new SolidColorBrush(Color.FromRgb(24, 26, 32)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(65, 70, 81)),
        TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        AcceptsReturn = multiline,
        VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
    };

    private static void AddField(Panel parent, string label, TextBox box, string hint)
    {
        var field = new StackPanel { Margin = new Thickness(0, 7, 0, 0) };
        field.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 11, Margin = new Thickness(2, 0, 2, 3) });
        box.ToolTip = hint;
        field.Children.Add(box);
        parent.Children.Add(field);
    }

    private static Button MakeButton(string text, bool primary) => new()
    {
        Content = text,
        Padding = new Thickness(13, 7, 13, 7),
        Background = primary ? new SolidColorBrush(Color.FromRgb(180, 243, 106)) : new SolidColorBrush(Color.FromRgb(42, 45, 53)),
        Foreground = primary ? new SolidColorBrush(Color.FromRgb(23, 25, 31)) : Brushes.White,
        BorderThickness = new Thickness(0)
    };

    private void CaptureShortcut(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if ((key is Key.Back or Key.Delete) && Keyboard.Modifiers == ModifierKeys.None) { _shortcut.Clear(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var modifiers = ShortcutModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= ShortcutModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= ShortcutModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= ShortcutModifiers.Shift;
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) modifiers |= ShortcutModifiers.Windows;
        if (modifiers != ShortcutModifiers.None)
            _shortcut.Text = new ShortcutBinding(modifiers, KeyInterop.VirtualKeyFromKey(key)).ToString();
    }
}
