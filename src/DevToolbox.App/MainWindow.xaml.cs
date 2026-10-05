using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using DevToolbox.Core;
using DevToolbox.Tools;
using DevToolbox.Windows;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;

namespace DevToolbox.App;

public partial class MainWindow : Window
{
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private readonly SettingsStore _settingsStore = new();
    private readonly UserSettings _settings;
    private readonly SqlInListTransformation _transformation = new();
    private readonly WindowsClipboard _clipboard = new();
    private readonly KeyboardAutomation _keyboard = new();
    private readonly SelectionTransformer _selectionTransformer;
    private readonly ObservableCollection<CommandRow> _commands = [];
    private ICollectionView? _commandView;
    private readonly List<ShortcutRow> _shortcutRows = [];
    private readonly Dictionary<string, TextBlock> _shortcutStatusLabels = new(StringComparer.Ordinal);
    private TextBox? _testInput;
    private TextBlock? _testOutput;
    private GlobalHotkeyService? _hotkeys;
    private IntPtr _previousWindow;
    private bool _showingSettings;
    private bool _closingForExit;

    public event Action<string, string>? TrayNotification;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        if (_settings.PreferredDelimiter is not (null or "," or "|" or ";" or "\r\n" or "\n" or "\r"))
            _settings.PreferredDelimiter = null;
        _selectionTransformer = new SelectionTransformer(_clipboard, _keyboard);
        BuildCommands();
        CommandsList.ItemsSource = _commands;
        _commandView = CollectionViewSource.GetDefaultView(_commands);
        _commandView.Filter = item => item is CommandRow command && command.Visible;
        BuildSettingsPanel();
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        _hotkeys = new GlobalHotkeyService(source);
        _hotkeys.HotkeyPressed += id => Dispatcher.BeginInvoke(() => HandleHotkeyAsync(id));
        RegisterConfiguredShortcuts();
        RefreshCommandStatuses();
        RefreshShortcutStatuses();
    }

    private void BuildCommands()
    {
        _commands.Add(new CommandRow("clipboard", "Formatar clipboard para SQL IN",
            "Converte os valores copiados em uma lista SQL entre apóstrofos.", "Ctrl+Alt+I"));
        _commands.Add(new CommandRow("selection", "Substituir seleção formatada",
            "Recorta o texto selecionado, formata e cola o resultado no lugar.", "Ctrl+Shift+I"));
    }

    private void RegisterConfiguredShortcuts()
    {
        if (_hotkeys is null) return;
        foreach (var id in ShortcutIds)
        {
            if (_settings.DisabledCommands.Contains(id))
            {
                _hotkeys.Unregister(id);
                SetShortcutStatus(id, "Desativado", false);
                var row = _shortcutRows.FirstOrDefault(item => item.Id == id);
                if (row is not null) row.StatusBrush = new SolidColorBrush(Color.FromRgb(150, 155, 167));
                continue;
            }
            var value = GetShortcut(id);
            if (!ShortcutParser.TryParse(value, out var binding, out var error))
            {
                SetShortcutStatus(id, error ?? "Atalho inválido.", false);
                continue;
            }
            if (!_hotkeys.TryRegister(id, binding!, out error))
                SetShortcutStatus(id, error ?? "Atalho indisponível.", false);
            else
                SetShortcutStatus(id, "Ativo", true);
        }
    }

    private void BuildSettingsPanel()
    {
        SettingsPanel.Children.Clear();
        SettingsPanel.Children.Add(new TextBlock
        {
            Text = "Combinações globais",
            Foreground = Brushes.White,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5)
        });
        SettingsPanel.Children.Add(new TextBlock
        {
            Text = "Clique no campo e pressione a nova combinação. O Windows informa quando uma combinação não pode ser registrada.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 14)
        });

        AddShortcutRow("palette", "Abrir paleta", "Exibe a lista de comandos e configurações.");
        AddShortcutRow("clipboard", "Formatar clipboard", "Transforma o texto copiado para SQL IN.");
        AddShortcutRow("selection", "Substituir seleção", "Recorta, transforma e cola a seleção atual.");

        var note = new TextBlock
        {
            Text = "Win+J pode ser usado pelo Recall do Windows em alguns computadores. Se aparecer como indisponível, escolha outro atalho.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(235, 196, 119)),
            FontSize = 11,
            Margin = new Thickness(2, 12, 2, 0)
        };
        SettingsPanel.Children.Add(note);

        SettingsPanel.Children.Add(new TextBlock
        {
            Text = "Delimitador da lista",
            Foreground = Brushes.White,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 8, 0, 6)
        });
        var delimiterPicker = new ComboBox
        {
            Height = 36,
            Padding = new Thickness(8, 5, 8, 5),
            Background = new SolidColorBrush(Color.FromRgb(24, 26, 32)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(65, 70, 81))
        };
        delimiterPicker.Items.Add(new ComboBoxItem { Content = "Automático", Tag = null });
        delimiterPicker.Items.Add(new ComboBoxItem { Content = "Vírgula (,) ", Tag = "," });
        delimiterPicker.Items.Add(new ComboBoxItem { Content = "Pipe (|)", Tag = "|" });
        delimiterPicker.Items.Add(new ComboBoxItem { Content = "Ponto e vírgula (;)", Tag = ";" });
        delimiterPicker.Items.Add(new ComboBoxItem { Content = "Quebra de linha", Tag = "\r\n" });
        delimiterPicker.SelectedIndex = _settings.PreferredDelimiter switch
        {
            "," => 1, "|" => 2, ";" => 3, "\r\n" or "\n" or "\r" => 4, _ => 0
        };
        delimiterPicker.SelectionChanged += (_, _) =>
        {
            if (delimiterPicker.SelectedItem is not ComboBoxItem selected) return;
            _settings.PreferredDelimiter = selected.Tag as string;
            try
            {
                _settingsStore.Save(_settings);
                SetStatus(_settings.PreferredDelimiter is null ? "Detecção automática ativada." : "Delimitador preferido salvo.", true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SetStatus($"Não foi possível salvar a preferência: {exception.Message}", false);
            }
        };
        SettingsPanel.Children.Add(delimiterPicker);

        var testCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(34, 37, 45)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(49, 52, 61)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 16, 0, 8)
        };
        var testPanel = new StackPanel();
        testPanel.Children.Add(new TextBlock { Text = "Testar conversão SQL IN", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13 });
        _testInput = new TextBox
        {
            Text = "abc,def,jeg",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 58,
            Padding = new Thickness(9),
            Margin = new Thickness(0, 10, 0, 8),
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(24, 26, 32)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(65, 70, 81))
        };
        testPanel.Children.Add(_testInput);
        var testButton = new Button
        {
            Content = "Testar conversão",
            Padding = new Thickness(12, 7, 12, 7),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromRgb(180, 243, 106)),
            Foreground = new SolidColorBrush(Color.FromRgb(23, 25, 31)),
            BorderThickness = new Thickness(0)
        };
        testButton.Click += TestButton_Click;
        testPanel.Children.Add(testButton);
        _testOutput = new TextBlock
        {
            Text = "O resultado será exibido aqui sem alterar o clipboard.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 243, 106)),
            FontSize = 12,
            Margin = new Thickness(0, 9, 0, 0)
        };
        testPanel.Children.Add(_testOutput);
        testCard.Child = testPanel;
        SettingsPanel.Children.Add(testCard);

        var retryButton = new Button
        {
            Content = "Tentar registrar atalhos novamente",
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 12, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(48, 52, 62)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        retryButton.Click += (_, _) =>
        {
            RegisterConfiguredShortcuts();
            RefreshCommandStatuses();
            RefreshShortcutStatuses();
            SetStatus("Estado dos atalhos atualizado.", true);
        };
        SettingsPanel.Children.Add(retryButton);
    }

    private void AddShortcutRow(string id, string name, string description)
    {
        var row = new ShortcutRow(id, name, description, GetShortcut(id), !_settings.DisabledCommands.Contains(id));
        _shortcutRows.Add(row);
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(34, 37, 45)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(49, 52, 61)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        var labels = new StackPanel();
        labels.Children.Add(new TextBlock { Text = name, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 13 });
        labels.Children.Add(new TextBlock { Text = description, Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 11, Margin = new Thickness(0, 4, 8, 0), TextWrapping = TextWrapping.Wrap });
        var statusLabel = new TextBlock { Text = row.Status, Name = "ShortcutStatus", Tag = row.Id, Foreground = row.StatusBrush, FontSize = 10, Margin = new Thickness(0, 6, 0, 0) };
        _shortcutStatusLabels[row.Id] = statusLabel;
        labels.Children.Add(statusLabel);
        grid.Children.Add(labels);

        var editor = new TextBox
        {
            Text = row.Shortcut,
            Tag = row,
            Height = 36,
            Padding = new Thickness(9, 7, 9, 6),
            FontSize = 12,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(24, 26, 32)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(65, 70, 81)),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "Foque o campo e pressione a combinação desejada"
        };
        Grid.SetColumn(editor, 1);
        editor.PreviewKeyDown += ShortcutEditor_PreviewKeyDown;
        grid.Children.Add(editor);
        var enabledToggle = new CheckBox
        {
            Content = "Ativo",
            IsChecked = row.Enabled,
            Tag = row,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        enabledToggle.Checked += ShortcutEnabled_Changed;
        enabledToggle.Unchecked += ShortcutEnabled_Changed;
        Grid.SetColumn(enabledToggle, 2);
        grid.Children.Add(enabledToggle);
        card.Child = grid;
        SettingsPanel.Children.Add(card);
    }

    private static readonly string[] ShortcutIds = ["palette", "clipboard", "selection"];

    private string GetShortcut(string id)
    {
        var defaults = id switch
        {
            "palette" => "Win+J",
            "clipboard" => "Ctrl+Alt+I",
            _ => "Ctrl+Shift+I"
        };
        return _settings.Shortcuts.TryGetValue(id, out var value) ? value : defaults;
    }

    private void SetShortcutStatus(string id, string status, bool active)
    {
        var row = _shortcutRows.FirstOrDefault(item => item.Id == id);
        if (row is not null)
        {
            row.Status = status;
            row.StatusBrush = active ? new SolidColorBrush(Color.FromRgb(180, 243, 106)) : new SolidColorBrush(Color.FromRgb(255, 145, 135));
            RefreshShortcutStatuses();
        }
    }

    private void RefreshShortcutStatuses()
    {
        foreach (var row in _shortcutRows)
        {
            if (_shortcutStatusLabels.TryGetValue(row.Id, out var textBlock))
            {
                textBlock.Text = row.Status;
                textBlock.Foreground = row.StatusBrush;
            }
        }
    }

    private void RefreshCommandStatuses()
    {
        foreach (var command in _commands)
        {
            command.Shortcut = GetShortcut(command.Id);
            var shortcutRow = _shortcutRows.FirstOrDefault(row => row.Id == command.Id);
            command.Status = shortcutRow?.Status ?? "Atalho configurável";
            command.StatusBrush = shortcutRow?.StatusBrush ?? new SolidColorBrush(Color.FromRgb(150, 155, 167));
        }
    }

    private void ShortcutEditor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor || editor.Tag is not ShortcutRow row) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        var modifiers = ShortcutModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= ShortcutModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= ShortcutModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= ShortcutModifiers.Shift;
        if (IsKeyDown(VkLWin) || IsKeyDown(VkRWin)) modifiers |= ShortcutModifiers.Windows;
        if (modifiers == ShortcutModifiers.None)
        {
            row.Status = "Inclua Ctrl, Alt, Shift ou Win na combinação.";
            RefreshShortcutStatuses();
            return;
        }

        var binding = new ShortcutBinding(modifiers, KeyInterop.VirtualKeyFromKey(key));
        var duplicate = _shortcutRows.FirstOrDefault(other => other.Id != row.Id && other.Enabled &&
            ShortcutParser.TryParse(GetShortcut(other.Id), out var otherBinding, out _) && otherBinding == binding);
        if (duplicate is not null)
        {
            row.Status = $"Já atribuída a {duplicate.Name}.";
            row.StatusBrush = new SolidColorBrush(Color.FromRgb(255, 145, 135));
            RefreshShortcutStatuses();
            return;
        }

        string? error = null;
        if (row.Enabled && (_hotkeys is null || !_hotkeys.TryRegister(row.Id, binding, out error)))
        {
            row.Status = error ?? "Atalho indisponível.";
            row.StatusBrush = new SolidColorBrush(Color.FromRgb(255, 145, 135));
            RefreshShortcutStatuses();
            return;
        }

        var shortcut = binding.ToString();
        _settings.Shortcuts[row.Id] = shortcut;
        var saved = true;
        try { _settingsStore.Save(_settings); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            saved = false;
            SetStatus($"Atalho ativo, mas não foi possível salvar: {exception.Message}", false);
        }
        row.Shortcut = shortcut;
        row.Status = row.Enabled ? "Ativo" : "Desativado";
        row.StatusBrush = row.Enabled
            ? new SolidColorBrush(Color.FromRgb(180, 243, 106))
            : new SolidColorBrush(Color.FromRgb(150, 155, 167));
        editor.Text = shortcut;
        RefreshCommandStatuses();
        RefreshShortcutStatuses();
        if (saved) SetStatus($"Atalho atualizado: {shortcut}", true);
    }

    private async void HandleHotkeyAsync(string id)
    {
        try
        {
            switch (id)
            {
                case "palette":
                    _previousWindow = _keyboard.GetForegroundWindow();
                    ShowPalette();
                    break;
                case "clipboard":
                    await TransformClipboardAsync();
                    break;
                case "selection":
                    await TransformSelectionAsync();
                    break;
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Operação falhou: {exception.Message}", false);
        }
    }

    private void ShortcutEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: ShortcutRow row, IsChecked: bool enabled }) return;
        row.Enabled = enabled;

        if (enabled)
        {
            _settings.DisabledCommands.Remove(row.Id);
            string? activationError;
            if (!ShortcutParser.TryParse(GetShortcut(row.Id), out var binding, out activationError))
            {
                row.Status = activationError ?? "Atalho inválido.";
                row.StatusBrush = new SolidColorBrush(Color.FromRgb(255, 145, 135));
            }
            else if (_hotkeys is null || !_hotkeys.TryRegister(row.Id, binding!, out activationError))
            {
                row.Status = activationError ?? "O gerenciador de atalhos ainda não está pronto.";
                row.StatusBrush = new SolidColorBrush(Color.FromRgb(255, 145, 135));
            }
            else
            {
                row.Status = "Ativo";
                row.StatusBrush = new SolidColorBrush(Color.FromRgb(180, 243, 106));
            }
        }
        else
        {
            _settings.DisabledCommands.Add(row.Id);
            _hotkeys?.Unregister(row.Id);
            row.Status = "Desativado";
            row.StatusBrush = new SolidColorBrush(Color.FromRgb(150, 155, 167));
        }

        var saved = true;
        try { _settingsStore.Save(_settings); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            saved = false;
            SetStatus($"Estado alterado, mas não foi possível salvar: {exception.Message}", false);
        }
        RefreshCommandStatuses();
        RefreshShortcutStatuses();
        if (saved)
            SetStatus(enabled ? $"Comando ativado: {row.Name}" : $"Comando desativado: {row.Name}", true);
    }

    public void ShowPalette()
    {
        _showingSettings = false;
        CommandsList.Visibility = Visibility.Visible;
        SettingsScroll.Visibility = Visibility.Collapsed;
        SearchBox.Visibility = Visibility.Visible;
        ManageButton.Content = "Gerenciar atalhos";
        PageTitle.Text = "DevToolbox";
        PageSubtitle.Text = "Ferramentas rápidas para desenvolvimento";
        RefreshCommandStatuses();
        SearchBox.Text = string.Empty;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    private async Task TransformClipboardAsync()
    {
        try
        {
            var result = await new ClipboardTransformationService(_clipboard).ExecuteAsync(FormatText);
            Notify("DevToolbox", result.Success ? "Lista formatada para SQL IN e pronta para colar." : result.Error ?? "Não foi possível converter a lista.", result.Success);
        }
        catch (Exception exception) { Notify("DevToolbox", $"Falha ao transformar o clipboard: {exception.Message}", false); }
    }

    private async Task TransformSelectionAsync()
    {
        try
        {
            var result = await _selectionTransformer.ExecuteAsync(FormatText);
            Notify("DevToolbox", result.Message, result.Success);
        }
        catch (Exception exception) { Notify("DevToolbox", $"Falha ao substituir a seleção: {exception.Message}", false); }
    }

    private TransformationResult FormatText(string input) => _transformation.Transform(input, _settings.PreferredDelimiter);

    private async void CommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        Hide();
        if (_settings.DisabledCommands.Contains(id))
        {
            Notify("DevToolbox", "Esse comando está desativado. Ative-o em Gerenciar atalhos.", false);
            return;
        }
        if (id == "clipboard") await TransformClipboardAsync();
        else if (id == "selection")
        {
            await TransformSelectionFromPaletteAsync();
        }
    }

    private void ManageButton_Click(object sender, RoutedEventArgs e)
    {
        _showingSettings = !_showingSettings;
        CommandsList.Visibility = _showingSettings ? Visibility.Collapsed : Visibility.Visible;
        SettingsScroll.Visibility = _showingSettings ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Visibility = _showingSettings ? Visibility.Collapsed : Visibility.Visible;
        ManageButton.Content = _showingSettings ? "Voltar aos comandos" : "Gerenciar atalhos";
        PageTitle.Text = _showingSettings ? "Atalhos" : "DevToolbox";
        PageSubtitle.Text = _showingSettings ? "Capture uma combinação para alterar cada atalho" : "Ferramentas rápidas para desenvolvimento";
        RefreshShortcutStatuses();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        foreach (var command in _commands)
            command.Visible = query.Length == 0 || command.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                command.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        _commandView?.Refresh();
    }

    private void TestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_testInput is null || _testOutput is null) return;
        var result = FormatText(_testInput.Text);
        _testOutput.Text = result.Success ? result.Value! : result.Error ?? "Não foi possível converter a entrada.";
        _testOutput.Foreground = result.Success
            ? new SolidColorBrush(Color.FromRgb(180, 243, 106))
            : new SolidColorBrush(Color.FromRgb(255, 145, 135));
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !_showingSettings && CommandsList.SelectedItem is CommandRow selected)
        {
            _ = ExecuteCommandAsync(selected.Id);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && SearchBox.IsKeyboardFocusWithin && _commands.Count > 0)
        {
            var first = _commands.FirstOrDefault(command => command.Visible);
            if (first is not null)
            {
                CommandsList.SelectedItem = first;
                CommandsList.Focus();
                Keyboard.Focus(CommandsList);
            }
        }
    }

    private async Task ExecuteCommandAsync(string id)
    {
        Hide();
        if (_settings.DisabledCommands.Contains(id))
        {
            Notify("DevToolbox", "Esse comando está desativado. Ative-o em Gerenciar atalhos.", false);
            return;
        }
        if (id == "clipboard") await TransformClipboardAsync();
        if (id == "selection")
            await TransformSelectionFromPaletteAsync();
    }

    private async Task TransformSelectionFromPaletteAsync()
    {
        if (_previousWindow == IntPtr.Zero)
        {
            Notify("DevToolbox", "Não foi possível identificar o aplicativo com a seleção.", false);
            return;
        }
        if (_keyboard.GetForegroundWindow() != _previousWindow)
        {
            SetForegroundWindow(_previousWindow);
            await Task.Delay(80);
        }
        if (_keyboard.GetForegroundWindow() != _previousWindow)
        {
            Notify("DevToolbox", "Não foi possível devolver o foco ao aplicativo com a seleção.", false);
            return;
        }
        await TransformSelectionAsync();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (IsVisible) Hide();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingForExit) return;
        e.Cancel = true;
        Hide();
    }

    private void Notify(string title, string message, bool success)
    {
        SetStatus(message, success);
        TrayNotification?.Invoke(title, message);
    }

    private void SetStatus(string message, bool success)
    {
        StatusText.Text = message;
        StatusText.Foreground = success
            ? new SolidColorBrush(Color.FromRgb(180, 243, 106))
            : new SolidColorBrush(Color.FromRgb(255, 145, 135));
    }

    public void PrepareForExit() => _closingForExit = true;

    public void DisposeServices() => _hotkeys?.Dispose();

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static bool SetForegroundWindow(IntPtr window) => SetForegroundWindowNative(window);

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindowNative(IntPtr window);
}

internal sealed class CommandRow(string id, string name, string description, string shortcut) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    private string _shortcut = shortcut;
    private string _status = "Atalho configurável";
    private Brush _statusBrush = new SolidColorBrush(Color.FromRgb(150, 155, 167));
    private bool _visible = true;
    public string Shortcut { get => _shortcut; set { _shortcut = value; Changed(nameof(Shortcut)); } }
    public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }
    public Brush StatusBrush { get => _statusBrush; set { _statusBrush = value; Changed(nameof(StatusBrush)); } }
    public bool Visible { get => _visible; set { _visible = value; Changed(nameof(Visible)); } }
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class ShortcutRow(string id, string name, string description, string shortcut, bool enabled)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public string Shortcut { get; set; } = shortcut;
    public bool Enabled { get; set; } = enabled;
    public string Status { get; set; } = "Verificando…";
    public Brush StatusBrush { get; set; } = new SolidColorBrush(Color.FromRgb(150, 155, 167));
}
