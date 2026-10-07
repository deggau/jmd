using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using JMD.Core;
using JMD.Tools;
using JMD.Windows;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using Orientation = System.Windows.Controls.Orientation;
using MessageBox = System.Windows.MessageBox;

namespace JMD.App;

public partial class MainWindow : Window
{
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private readonly SettingsStore _settingsStore = new();
    private readonly UserSettings _settings;
    private ConversionHistoryStore? _historyStore;
    private ConversionHistoryRecorder? _historyRecorder;
    private readonly SqlInListTransformation _transformation = new();
    private readonly StructuredDataFormatter _structuredDataFormatter = new();
    private readonly SqlInConversionGuard _conversionGuard = new();
    private readonly WindowsClipboard _clipboard = new();
    private readonly KeyboardAutomation _keyboard = new();
    private readonly WindowsDisplayAwakeService _displayAwake = new();
    private readonly SelectionTransformer _selectionTransformer;
    private readonly ObservableCollection<CommandRow> _commands = [];
    private readonly AppNavigationState _navigation = new();
    private ICollectionView? _commandView;
    private readonly List<ShortcutRow> _shortcutRows = [];
    private readonly Dictionary<string, TextBlock> _shortcutStatusLabels = new(StringComparer.Ordinal);
    private TextBox? _testInput;
    private TextBlock? _testOutput;
    private GlobalHotkeyService? _hotkeys;
    private IntPtr _previousWindow;
    private bool _closingForExit;
    private bool _conversionRunning;
    private bool _updatingKeepAwakeToggle;
    private bool _showingDialog;

    public event Action<string, string>? TrayNotification;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        if (_settings.PreferredDelimiter is not (null or "," or "|" or ";" or "\r\n" or "\n" or "\r"))
            _settings.PreferredDelimiter = null;
        if (_settings.HistoryRetentionDays is < 1 or > 3650)
            _settings.HistoryRetentionDays = 15;
        try
        {
            var historyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JMD", "history.db");
            _historyStore = new ConversionHistoryStore(historyPath);
            _historyRecorder = new ConversionHistoryRecorder(_historyStore);
            _historyStore.DeleteOlderThan(_settings.HistoryRetentionDays);
            if (_historyStore.GetLatest() is { } lastConversion)
                _conversionGuard.Remember(lastConversion.BeforeValue, lastConversion.AfterValue);
        }
        catch (Exception exception)
        {
            SetStatus($"O histórico está indisponível: {exception.Message}", false);
        }
        _selectionTransformer = new SelectionTransformer(_clipboard, _keyboard);
        BuildCommands();
        CommandsList.ItemsSource = _commands;
        _commandView = CollectionViewSource.GetDefaultView(_commands);
        _commandView.Filter = item => item is CommandRow command && command.Visible;
        BuildSettingsPanel();
        HistoryRetentionBox.Text = _settings.HistoryRetentionDays.ToString(CultureInfo.InvariantCulture);
        RefreshHistory();
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
            "Converte os valores copiados em uma lista SQL entre apóstrofos.", ShortcutDefaults.ForCommand("clipboard")));
        _commands.Add(new CommandRow("selection", "Substituir seleção formatada",
            "Recorta o texto selecionado, formata e cola o resultado no lugar.", ShortcutDefaults.ForCommand("selection")));
        _commands.Add(new CommandRow("jsonPretty", "Formatar JSON/XML",
            "Identifica JSON ou XML e aplica indentação ao conteúdo selecionado.", ShortcutDefaults.ForCommand("jsonPretty")));
        _commands.Add(new CommandRow("jsonCompact", "Compactar JSON/XML",
            "Identifica JSON ou XML e remove espaços e quebras de linha.", ShortcutDefaults.ForCommand("jsonCompact")));
        _commands.Add(new CommandRow("snippets", "Inserir texto salvo",
            "Busca pelo título e cola o texto escolhido no aplicativo anterior.", ShortcutDefaults.ForCommand("snippets")));
        _commands.Add(new CommandRow("saveSnippet", "Salvar seleção como texto",
            "Copia a seleção e pede um título para salvá-la.", ShortcutDefaults.ForCommand("saveSnippet")));
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
        var existingStatuses = _shortcutRows.ToDictionary(row => row.Id, row => (row.Status, row.StatusBrush), StringComparer.Ordinal);
        SettingsPanel.Children.Clear();
        _shortcutRows.Clear();
        _shortcutStatusLabels.Clear();
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
        AddShortcutRow("jsonPretty", "Formatar JSON/XML", "Identifica o formato e aplica indentação.");
        AddShortcutRow("jsonCompact", "Compactar JSON/XML", "Identifica o formato e deixa o conteúdo em uma linha.");
        AddShortcutRow("snippets", "Inserir texto salvo", "Busca pelo título e cola no aplicativo anterior.");
        AddShortcutRow("saveSnippet", "Salvar seleção como texto", "Copia a seleção atual e pede um título.");

        var note = new TextBlock
        {
            Text = "Alt+J pode estar em uso por outro aplicativo. Se aparecer como indisponível, escolha outro atalho.",
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

        SettingsPanel.Children.Add(new TextBlock
        {
            Text = "Textos salvos",
            Foreground = Brushes.White,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(2, 18, 0, 8)
        });
        var addSnippetButton = new Button
        {
            Content = "Adicionar texto",
            FocusVisualStyle = (Style)FindResource("DarkFocusCue"),
            Padding = new Thickness(12, 7, 12, 7),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Background = new SolidColorBrush(Color.FromRgb(180, 243, 106)),
            Foreground = new SolidColorBrush(Color.FromRgb(23, 25, 31)),
            BorderThickness = new Thickness(0)
        };
        addSnippetButton.Click += (_, _) => EditSnippet(null, null);
        SettingsPanel.Children.Add(addSnippetButton);
        RenderSnippetSettings();

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
            FocusVisualStyle = (Style)FindResource("DarkFocusCue"),
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
        foreach (var row in _shortcutRows)
        {
            if (!existingStatuses.TryGetValue(row.Id, out var status)) continue;
            row.Status = status.Status;
            row.StatusBrush = status.StatusBrush;
        }
        RefreshShortcutStatuses();
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

    private void RenderSnippetSettings()
    {
        if (_settings.Snippets.Count == 0)
        {
            SettingsPanel.Children.Add(new TextBlock
            {
                Text = "Nenhum texto salvo ainda. Você também pode selecionar um texto e usar o atalho de cadastro rápido.",
                Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(2, 8, 2, 4)
            });
            return;
        }

        foreach (var snippet in _settings.Snippets.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(actions, Dock.Right);
            var edit = new Button { Content = "Editar", Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(6, 0, 0, 0), Background = new SolidColorBrush(Color.FromRgb(48, 52, 62)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            edit.Click += (_, _) => EditSnippet(snippet, null);
            var delete = new Button { Content = "Excluir", Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(6, 0, 0, 0), Background = new SolidColorBrush(Color.FromRgb(48, 52, 62)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            delete.Click += (_, _) => DeleteSnippet(snippet);
            actions.Children.Add(edit);
            actions.Children.Add(delete);
            row.Children.Add(actions);
            row.Children.Add(new TextBlock { Text = snippet.Title, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            SettingsPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(34, 37, 45)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(49, 52, 61)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Child = row
            });
        }
    }

    private void EditSnippet(SnippetEntry? existing, RichClipboardContent? captured)
    {
        var dialog = new SnippetEditorWindow(existing, captured);
        if (IsVisible) dialog.Owner = this;
        _showingDialog = true;
        bool accepted;
        try { accepted = dialog.ShowDialog() == true; }
        finally { _showingDialog = false; }
        if (!accepted || dialog.Result is not { } edited) return;
        var index = _settings.Snippets.FindIndex(item => item.Id == edited.Id);
        var original = index >= 0 ? _settings.Snippets[index] : null;
        if (index < 0) _settings.Snippets.Add(edited);
        else _settings.Snippets[index] = edited;
        if (!SaveSnippetsAndRefresh())
        {
            if (index < 0) _settings.Snippets.RemoveAll(item => item.Id == edited.Id);
            else _settings.Snippets[index] = original!;
        }
    }

    private void DeleteSnippet(SnippetEntry snippet)
    {
        _showingDialog = true;
        MessageBoxResult result;
        try { result = MessageBox.Show(this, $"Excluir o texto ‘{snippet.Title}’?", "Excluir texto salvo", MessageBoxButton.YesNo, MessageBoxImage.Question); }
        finally { _showingDialog = false; }
        if (result != MessageBoxResult.Yes)
            return;
        var index = _settings.Snippets.FindIndex(item => item.Id == snippet.Id);
        _settings.Snippets.RemoveAt(index);
        if (!SaveSnippetsAndRefresh()) _settings.Snippets.Insert(index, snippet);
    }

    private bool SaveSnippetsAndRefresh()
    {
        try
        {
            _settingsStore.Save(_settings);
            BuildSettingsPanel();
            SetStatus("Textos salvos atualizados.", true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Não foi possível salvar os textos: {exception.Message}", false);
            return false;
        }
    }

    private async Task CaptureSnippetAsync()
    {
        var sourceWindow = _previousWindow;
        if (sourceWindow == IntPtr.Zero || !await _keyboard.WaitForModifiersReleasedAsync())
        {
            Notify("JMD", "Solte Ctrl, Alt, Shift e Win para copiar a seleção.", false);
            return;
        }
        if (_keyboard.GetForegroundWindow() != sourceWindow)
        {
            Notify("JMD", "A janela ativa mudou antes de copiar. Tente novamente.", false);
            return;
        }
        var before = _clipboard.SequenceNumber;
        if (!_keyboard.SendCopy())
        {
            Notify("JMD", "O Windows não permitiu copiar a seleção.", false);
            return;
        }
        for (var attempt = 0; attempt < 24 && _clipboard.SequenceNumber == before; attempt++)
            await Task.Delay(25);
        if (_clipboard.SequenceNumber == before)
        {
            Notify("JMD", "Nenhum texto selecionado foi copiado.", false);
            return;
        }
        var content = _clipboard.TryGetRichContent();
        if (content is null || (string.IsNullOrWhiteSpace(content.Text) && string.IsNullOrWhiteSpace(content.Rtf) && string.IsNullOrWhiteSpace(content.Html)))
        {
            Notify("JMD", "A seleção copiada não contém texto compatível.", false);
            return;
        }

        var dialog = new SnippetTitleWindow();
        if (dialog.ShowDialog() != true || dialog.SnippetTitle is not { } title) return;
        var snippet = new SnippetEntry
        {
            Title = title,
            Text = content.Text,
            Rtf = content.Rtf,
            Html = content.Html
        };
        _settings.Snippets.Add(snippet);
        try
        {
            _settingsStore.Save(_settings);
            Notify("JMD", $"Texto salvo: {snippet.Title}", true);
            if (_navigation.CurrentPage == AppPage.ShortcutSettings) BuildSettingsPanel();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _settings.Snippets.RemoveAll(item => item.Id == snippet.Id);
            Notify("JMD", $"Não foi possível salvar o texto: {exception.Message}", false);
        }
    }

    private async Task InsertSavedSnippetAsync()
    {
        if (_settings.Snippets.Count == 0)
        {
            Notify("JMD", "Ainda não há textos salvos. Cadastre um nas configurações ou use o atalho de captura.", false);
            return;
        }
        var sourceWindow = _previousWindow;
        var picker = new SnippetPickerWindow(_settings.Snippets);
        if (picker.ShowDialog() != true || picker.SelectedSnippet is not { } snippet) return;
        if (!await _keyboard.WaitForModifiersReleasedAsync())
        {
            Notify("JMD", "Solte as teclas modificadoras e tente novamente.", false);
            return;
        }
        var content = new RichClipboardContent(snippet.Text, snippet.Rtf, snippet.Html);
        if (!_clipboard.TrySetRichContent(content))
        {
            Notify("JMD", "Não foi possível preparar o texto no clipboard.", false);
            return;
        }
        await Task.Delay(80);
        if (sourceWindow == IntPtr.Zero || !SetForegroundWindow(sourceWindow))
        {
            Notify("JMD", "Não foi possível voltar ao aplicativo anterior; o texto está no clipboard.", false);
            return;
        }
        await Task.Delay(80);
        if (_keyboard.GetForegroundWindow() != sourceWindow || !_keyboard.SendPaste())
        {
            Notify("JMD", "Não foi possível colar. O texto está disponível no clipboard.", false);
            return;
        }
        Notify("JMD", $"Texto inserido: {snippet.Title}", true);
    }

    private static readonly string[] ShortcutIds = ["palette", "clipboard", "selection", "jsonPretty", "jsonCompact", "snippets", "saveSnippet"];

    private string GetShortcut(string id)
    {
        var defaults = ShortcutDefaults.ForCommand(id);
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
        if (row.Enabled && !_navigation.IsEditingShortcuts && (_hotkeys is null || !_hotkeys.TryRegister(row.Id, binding, out error)))
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
        row.Status = row.Enabled
            ? _navigation.IsEditingShortcuts ? "Ativado ao sair das configurações" : "Ativo"
            : "Desativado";
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
            _previousWindow = _keyboard.GetForegroundWindow();
            switch (id)
            {
                case "palette":
                    ShowPalette();
                    break;
                case "clipboard":
                    await TransformClipboardAsync();
                    break;
                case "selection":
                    await TransformSelectionAsync();
                    break;
                case "jsonPretty":
                    await TransformStructuredDataAsync(StructuredDataLayout.Pretty);
                    break;
                case "jsonCompact":
                    await TransformStructuredDataAsync(StructuredDataLayout.Compact);
                    break;
                case "snippets":
                    await InsertSavedSnippetAsync();
                    break;
                case "saveSnippet":
                    await CaptureSnippetAsync();
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
            if (_navigation.IsEditingShortcuts)
            {
                row.Status = "Ativado ao sair das configurações";
                row.StatusBrush = new SolidColorBrush(Color.FromRgb(180, 243, 106));
            }
            else if (!ShortcutParser.TryParse(GetShortcut(row.Id), out var binding, out activationError))
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
        _navigation.ShowPalette();
        CommandsList.Visibility = Visibility.Visible;
        SettingsTabs.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        HistoryPage.Visibility = Visibility.Collapsed;
        AboutPage.Visibility = Visibility.Collapsed;
        SearchBox.Visibility = Visibility.Visible;
        ManageButton.Content = "Gerenciar atalhos";
        HistoryButton.Content = "Histórico";
        PageTitle.Text = "JMD";
        PageSubtitle.Text = "Ferramentas rápidas para desenvolvimento";
        RefreshCommandStatuses();
        SearchBox.Text = string.Empty;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        RegisterConfiguredShortcuts();
        RefreshCommandStatuses();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    private async Task TransformClipboardAsync()
    {
        if (_conversionRunning) return;
        _conversionRunning = true;
        try
        {
            var input = await _clipboard.TryGetTextAsync();
            if (input is null)
            {
                Notify("JMD", "O clipboard não contém texto Unicode.", false);
                return;
            }
            if (_conversionGuard.TryGetOriginalForConvertedValue(input, out var originalValue))
            {
                Notify("JMD", $"Esse valor já foi convertido para SQL IN. Valor original: {originalValue}", true);
                return;
            }

            var result = FormatText(input);
            if (result.Success && !await _clipboard.TrySetTextAsync(result.Value!))
                result = TransformationResult.Fail("Não foi possível gravar o resultado no clipboard.");
            if (result.Success && !string.Equals(await _clipboard.TryGetTextAsync(), result.Value, StringComparison.Ordinal))
                result = TransformationResult.Fail("O Windows não confirmou a atualização do clipboard. O resultado não foi aplicado.");
            if (result.Success)
            {
                _conversionGuard.Remember(input, result.Value!);
                RecordHistory("Montar SQL IN", input, result);
            }
            var message = result.Success
                ? WithConversionCounts("Lista formatada para SQL IN e pronta para colar.", result.InputItemCount, result.OutputItemCount)
                : result.Error ?? "Não foi possível converter a lista.";
            Notify("JMD", message, result.Success);
        }
        catch (Exception exception) { Notify("JMD", $"Falha ao transformar o clipboard: {exception.Message}", false); }
        finally { _conversionRunning = false; }
    }

    private async Task TransformSelectionAsync()
    {
        if (_conversionRunning) return;
        _conversionRunning = true;
        try
        {
            var result = await _selectionTransformer.ExecuteAsync(FormatTextForExecution);
            if (result.Success && result.BeforeValue is not null && result.AfterValue is not null)
                _conversionGuard.Remember(result.BeforeValue, result.AfterValue);
            RecordSelectionHistory(result);
            var message = result.Success
                ? WithConversionCounts(result.Message, result.InputItemCount, result.OutputItemCount)
                : result.Message;
            Notify("JMD", message, result.Success);
        }
        catch (Exception exception) { Notify("JMD", $"Falha ao substituir a seleção: {exception.Message}", false); }
        finally { _conversionRunning = false; }
    }

    private void RecordHistory(string commandName, string beforeValue, TransformationResult result)
    {
        if (_historyRecorder is null) return;
        try
        {
            if (_historyRecorder.RecordIfSuccessful(commandName, beforeValue, result, GetConversionSource())) RefreshHistory();
        }
        catch (Exception exception)
        {
            SetStatus($"Conversão concluída, mas não foi possível salvar no histórico: {exception.Message}", false);
        }
    }

    private void RecordSelectionHistory(SelectionTransformResult result)
    {
        if (_historyRecorder is null) return;
        try
        {
            var commandName = result.UsedClipboardFallback
                ? "Formatar clipboard (atalho de seleção)"
                : "Substituir seleção (SQL IN)";
            if (_historyRecorder.RecordIfSuccessful(commandName, result, GetConversionSource())) RefreshHistory();
        }
        catch (Exception exception)
        {
            SetStatus($"Conversão concluída, mas não foi possível salvar no histórico: {exception.Message}", false);
        }
    }

    private void RefreshHistory()
    {
        if (_historyStore is null) return;
        try
        {
            var entries = _historyStore.Search(HistorySearchBox?.Text ?? string.Empty)
                .Select(entry => new HistoryDisplayEntry(
                    entry.CommandName,
                    entry.BeforeValue,
                    entry.AfterValue,
                    entry.OccurredAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture),
                    FormatSourceDisplay(entry.ApplicationName, entry.WindowTitle)))
                .ToList();
            HistoryItemsControl.ItemsSource = entries;
            HistoryEmptyText.Text = entries.Count == 0
                ? string.IsNullOrWhiteSpace(HistorySearchBox?.Text) ? "Nenhuma conversão registrada." : "Nenhum resultado para essa busca."
                : string.Empty;
            HistoryEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            HistoryItemsControl.ItemsSource = null;
            HistoryEmptyText.Text = "Não foi possível consultar o histórico.";
            HistoryEmptyText.Visibility = Visibility.Visible;
            SetStatus($"Falha ao consultar o histórico: {exception.Message}", false);
        }
    }

    private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshHistory();

    private async void CopyHistoryValue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value }) return;
        try
        {
            if (await _clipboard.TrySetTextAsync(value) &&
                string.Equals(await _clipboard.TryGetTextAsync(), value, StringComparison.Ordinal))
                SetStatus("Valor copiado para a área de transferência.", true);
            else
                SetStatus("Não foi possível copiar o valor para a área de transferência.", false);
        }
        catch (Exception exception)
        {
            SetStatus($"Falha ao copiar o valor: {exception.Message}", false);
        }
    }

    private void SaveHistoryRetention_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(HistoryRetentionBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days is < 1 or > 3650)
        {
            SetStatus("Informe um prazo entre 1 e 3650 dias.", false);
            return;
        }
        try
        {
            _settings.HistoryRetentionDays = days;
            _settingsStore.Save(_settings);
            _historyStore?.DeleteOlderThan(days);
            RefreshHistory();
            SetStatus($"Histórico configurado para manter {days} dias.", true);
        }
        catch (Exception exception)
        {
            SetStatus($"Não foi possível salvar a retenção do histórico: {exception.Message}", false);
        }
    }

    private sealed record HistoryDisplayEntry(
        string CommandName,
        string BeforeValue,
        string AfterValue,
        string DisplayDate,
        string SourceDisplay);

    private static string FormatSourceDisplay(string? applicationName, string? windowTitle)
    {
        var parts = new[] { applicationName, windowTitle }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return string.Join(" · ", parts);
    }

    private ConversionSource? GetConversionSource() => ForegroundWindowDetails.Read(_previousWindow);

    private TransformationResult FormatText(string input) => _transformation.Transform(input, _settings.PreferredDelimiter);

    private TransformationResult FormatTextForExecution(string input)
        => _conversionGuard.TransformUnlessAlreadyConverted(input, FormatText);

    private async void CommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        Hide();
        if (_settings.DisabledCommands.Contains(id))
        {
            Notify("JMD", "Esse comando está desativado. Ative-o em Gerenciar atalhos.", false);
            return;
        }
        if (id == "clipboard") await TransformClipboardAsync();
        else if (id == "selection")
        {
            await TransformSelectionFromPaletteAsync();
        }
        else if (id is "jsonPretty" or "jsonCompact")
        {
            await TransformStructuredDataFromPaletteAsync(id == "jsonPretty" ? StructuredDataLayout.Pretty : StructuredDataLayout.Compact);
        }
        else if (id == "snippets") await InsertSavedSnippetAsync();
        else if (id == "saveSnippet") await CaptureSnippetAsync();
    }

    private void ManageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_navigation.CurrentPage != AppPage.Palette)
        {
            ShowPalette();
            return;
        }
        ShowShortcutSettings();
    }

    public void ShowShortcutSettings()
    {
        _navigation.ShowShortcutSettings();
        CommandsList.Visibility = Visibility.Collapsed;
        SettingsTabs.Visibility = Visibility.Visible;
        SettingsPage.Visibility = Visibility.Visible;
        HistoryPage.Visibility = Visibility.Collapsed;
        AboutPage.Visibility = Visibility.Collapsed;
        SearchBox.Visibility = Visibility.Collapsed;
        ManageButton.Content = "Voltar aos comandos";
        PageTitle.Text = "Gerenciar atalhos";
        PageSubtitle.Text = "Configure os comandos e combinações globais";
        foreach (var id in ShortcutIds)
            _hotkeys?.Unregister(id);
        RefreshShortcutStatuses();
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
        => ShowHistory();

    public void ShowHistory()
    {
        _navigation.ShowHistory();
        CommandsList.Visibility = Visibility.Collapsed;
        SettingsTabs.Visibility = Visibility.Visible;
        SettingsPage.Visibility = Visibility.Collapsed;
        HistoryPage.Visibility = Visibility.Visible;
        AboutPage.Visibility = Visibility.Collapsed;
        SearchBox.Visibility = Visibility.Collapsed;
        ManageButton.Content = "Voltar aos comandos";
        PageTitle.Text = "Histórico";
        PageSubtitle.Text = "Consulte as conversões recentes";
        RefreshHistory();
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExtraButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExtraButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = ExtraButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void KeepAwakeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingKeepAwakeToggle || sender is not CheckBox toggle) return;
        var enabled = toggle.IsChecked == true;
        if (!_displayAwake.SetEnabled(enabled))
        {
            _updatingKeepAwakeToggle = true;
            toggle.IsChecked = _displayAwake.IsEnabled;
            _updatingKeepAwakeToggle = false;
            SetStatus("O Windows não aceitou a solicitação para manter a tela ativa.", false);
            return;
        }

        SetStatus(enabled
            ? "Solicitado ao Windows que mantenha a tela e o sistema ativos enquanto o JMD estiver aberto."
            : "A solicitação para manter a tela ativa foi removida.", true);
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _navigation.ShowAbout();
        CommandsList.Visibility = Visibility.Collapsed;
        SettingsTabs.Visibility = Visibility.Collapsed;
        AboutPage.Visibility = Visibility.Visible;
        SearchBox.Visibility = Visibility.Collapsed;
        ManageButton.Content = "Voltar aos comandos";
        PageTitle.Text = "Sobre";
        PageSubtitle.Text = "Informações e atualizações do JMD";
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                      ?? "desconhecida";
        AboutVersionText.Text = $"Versão instalada: {version}";
        UpdateStatusText.Text = "Verifique se há uma versão mais recente no GitHub.";
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        CheckUpdatesButton.IsEnabled = true;
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "Consultando a versão mais recente no GitHub…";
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("JMD-App");
            using var response = await client.GetAsync("https://api.github.com/repos/deggau/jmd/releases/latest");
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var latestTag = json.RootElement.GetProperty("tag_name").GetString()?.TrimStart('v', 'V');
            var installed = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                            ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString();
            installed = installed?.Split('+')[0];
            if (installed is null || latestTag is null || !Version.TryParse(installed, out var currentVersion) || !Version.TryParse(latestTag, out var latestVersion))
                throw new InvalidDataException("O GitHub retornou uma versão inválida.");

            if (ReleaseVersionComparison.IsUpdateAvailable(installed, latestTag))
            {
                UpdateStatusText.Text = $"Nova versão disponível: {latestVersion} (instalada: {currentVersion}).";
                InstallUpdateButton.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateStatusText.Text = $"Você já está usando a versão mais recente ({currentVersion}).";
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or KeyNotFoundException)
        {
            UpdateStatusText.Text = $"Não foi possível verificar atualizações: {exception.Message}";
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add("Start-Sleep -Seconds 2; irm https://raw.githubusercontent.com/deggau/jmd/main/installer/install.ps1 | iex");
            Process.Start(startInfo);
            PrepareForExit();
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = $"Não foi possível iniciar a atualização: {exception.Message}";
        }
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
        else if (e.Key == Key.Enter && !_navigation.IsEditingShortcuts && CommandsList.SelectedItem is CommandRow selected)
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
            Notify("JMD", "Esse comando está desativado. Ative-o em Gerenciar atalhos.", false);
            return;
        }
        if (id == "clipboard") await TransformClipboardAsync();
        if (id == "selection")
            await TransformSelectionFromPaletteAsync();
        if (id is "jsonPretty" or "jsonCompact")
            await TransformStructuredDataFromPaletteAsync(id == "jsonPretty" ? StructuredDataLayout.Pretty : StructuredDataLayout.Compact);
    }

    private async Task TransformSelectionFromPaletteAsync()
    {
        if (_previousWindow == IntPtr.Zero)
        {
            Notify("JMD", "Não foi possível identificar o aplicativo com a seleção.", false);
            return;
        }
        if (_keyboard.GetForegroundWindow() != _previousWindow)
        {
            SetForegroundWindow(_previousWindow);
            await Task.Delay(80);
        }
        if (_keyboard.GetForegroundWindow() != _previousWindow)
        {
            Notify("JMD", "Não foi possível devolver o foco ao aplicativo com a seleção.", false);
            return;
        }
        await TransformSelectionAsync();
    }

    private async Task TransformStructuredDataFromPaletteAsync(StructuredDataLayout layout)
    {
        if (_previousWindow == IntPtr.Zero)
        {
            Notify("JMD", "Não foi possível identificar a janela ativa para substituir o texto.", false);
            return;
        }
        if (_keyboard.GetForegroundWindow() != _previousWindow)
        {
            SetForegroundWindow(_previousWindow);
            await Task.Delay(80);
        }
        if (_keyboard.GetForegroundWindow() != _previousWindow)
        {
            Notify("JMD", "Não foi possível devolver o foco ao aplicativo com o texto.", false);
            return;
        }
        await TransformStructuredDataAsync(layout);
    }

    private async Task TransformStructuredDataAsync(StructuredDataLayout layout)
    {
        if (_conversionRunning) return;
        _conversionRunning = true;
        StructuredDataFormat? detectedFormat = null;
        try
        {
            var result = await _selectionTransformer.ExecuteAsync(input =>
            {
                var formatted = _structuredDataFormatter.Transform(input, layout);
                detectedFormat = formatted.Format;
                return formatted.Transformation;
            });
            if (result.Success && result.BeforeValue is not null && result.AfterValue is not null)
            {
                var formatName = detectedFormat == StructuredDataFormat.Json ? "JSON" : "XML";
                var verb = layout == StructuredDataLayout.Pretty ? "Formatar" : "Compactar";
                var commandName = $"{verb} {formatName}";
                if (result.UsedClipboardFallback) commandName += " (clipboard)";
                RecordSelectionHistory(commandName, result);
                var action = layout == StructuredDataLayout.Pretty ? "formatado" : "compactado";
                var subject = detectedFormat == StructuredDataFormat.Json ? "JSON" : "XML";
                var target = result.UsedClipboardFallback ? "O texto do clipboard foi" : "O texto selecionado foi";
                Notify("JMD", $"{target} {subject} {action}.", true);
            }
            else
            {
                Notify("JMD", result.Message, false);
            }
        }
        catch (Exception exception) { Notify("JMD", $"Falha ao transformar JSON/XML: {exception.Message}", false); }
        finally { _conversionRunning = false; }
    }

    private void RecordSelectionHistory(string commandName, SelectionTransformResult result)
    {
        if (_historyRecorder is null) return;
        try
        {
            if (_historyRecorder.RecordIfSuccessful(commandName, result, GetConversionSource())) RefreshHistory();
        }
        catch (Exception exception)
        {
            SetStatus($"Transformação concluída, mas não foi possível salvar no histórico: {exception.Message}", false);
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (IsVisible && !_showingDialog) Hide();
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

    private static string WithConversionCounts(string message, int? identified, int? output)
        => ConversionCountSummary.Append(message, identified, output);

    private void SetStatus(string message, bool success)
    {
        StatusText.Text = message;
        StatusText.Foreground = success
            ? new SolidColorBrush(Color.FromRgb(180, 243, 106))
            : new SolidColorBrush(Color.FromRgb(255, 145, 135));
    }

    public void PrepareForExit() => _closingForExit = true;

    public void DisposeServices()
    {
        _hotkeys?.Dispose();
        _displayAwake.Dispose();
    }

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
