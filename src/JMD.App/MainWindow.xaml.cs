using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
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
using NAudio.Wave;

namespace JMD.App;

public partial class MainWindow : Window
{
    private const int MaxWakeRecognitionHistoryEntries = 100;
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
    private readonly FocusedTextAutomation _focusedText;
    private readonly WindowsDisplayAwakeService _displayAwake = new();
    private VoiceWakeWordService? _voiceWakeWord;
    private MediaPlayer? _historyAudioPlayer;
    private string? _historyAudioPlaybackPath;
    private bool _updatingVoiceWakeWordToggle;
    private bool _updatingVoiceDebugToggle;
    private bool _updatingVoiceRecognitionDebugToggle;
    private bool _loadingJeyModelSettings;
    private string? _downloadingVoiceModel;
    private string? _benchmarkAudioPath;
    private string? _benchmarkRecordedAudioPath;
    private bool _benchmarkRunning;
    private WaveInEvent? _benchmarkRecorder;
    private MemoryStream? _benchmarkRecordingBuffer;
    private readonly object _benchmarkRecordingLock = new();
    private readonly System.Windows.Threading.DispatcherTimer _benchmarkRecordingTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DateTimeOffset _benchmarkRecordingStartedAt;
    private readonly System.Windows.Threading.DispatcherTimer _memoryRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly SelectionTransformer _selectionTransformer;
    private readonly ObservableCollection<CommandRow> _commands = [];
    private readonly AppNavigationState _navigation = new();
    private ICollectionView? _commandView;
    private readonly List<ShortcutRow> _shortcutRows = [];
    private readonly DraftStack _draftStack = new();
    private readonly Dictionary<string, TextBlock> _shortcutStatusLabels = new(StringComparer.Ordinal);
    private GlobalHotkeyService? _hotkeys;
    private IntPtr _previousWindow;
    private bool _closingForExit;
    private bool _conversionRunning;
    private bool _updatingKeepAwakeToggle;
    private bool _loadingAdvancedNotificationToggle;
    private bool _showingDialog;
    private bool _checkingUpdates;
    private string _wakeRecognitionPartial = string.Empty;
    private readonly ObservableCollection<WakeRecognitionHistoryRow> _wakeRecognitionHistory = [];

    public event Action<string, string, bool>? TrayNotification;

    public MainWindow()
    {
        InitializeComponent();
        VoiceRecognitionDebugHistory.ItemsSource = _wakeRecognitionHistory;
        FooterVersionText.Text = $"v{GetInstalledVersion()}";
        _settings = _settingsStore.Load();
        ConfigureDefaultVoiceCommands();
        _updatingKeepAwakeToggle = true;
        KeepAwakeToggle.IsChecked = _settings.KeepAwakeEnabled;
        _updatingKeepAwakeToggle = false;
        _updatingVoiceWakeWordToggle = true;
        VoiceWakeWordToggle.IsChecked = _settings.VoiceWakeWordEnabled;
        _updatingVoiceWakeWordToggle = false;
        _updatingVoiceDebugToggle = true;
        VoiceDebugToggle.IsChecked = _settings.VoiceDebugEnabled;
        _updatingVoiceDebugToggle = false;
        _updatingVoiceRecognitionDebugToggle = true;
        VoiceRecognitionDebugToggle.IsChecked = _settings.VoiceWakeRecognitionDebugEnabled;
        VoiceRecognitionDebugPanel.Visibility = _settings.VoiceWakeRecognitionDebugEnabled ? Visibility.Visible : Visibility.Collapsed;
        UpdateVoiceRecognitionDebugText();
        _updatingVoiceRecognitionDebugToggle = false;
        _loadingJeyModelSettings = true;
        ClosedWakeWordGrammarToggle.IsChecked = _settings.JeyWakeWordClosedGrammarEnabled;
        ClosedCommandGrammarToggle.IsChecked = _settings.JeyCommandClosedGrammarEnabled;
        WakeWordMinimumConfidenceSlider.Value = _settings.JeyWakeWordMinimumConfidencePercent;
        UpdateWakeWordMinimumConfidenceText(_settings.JeyWakeWordMinimumConfidencePercent);
        CommandMinimumConfidenceSlider.Value = _settings.JeyCommandMinimumConfidencePercent;
        UpdateCommandMinimumConfidenceText(_settings.JeyCommandMinimumConfidencePercent);
        SelectVoiceModel(PortugueseModelCombo, _settings.JeyPortugueseModel);
        SelectVoiceModel(EnglishModelCombo, _settings.JeyEnglishModel);
        _loadingJeyModelSettings = false;
        UpdateVoiceModelStatuses();
        if (_settings.KeepAwakeEnabled && !_displayAwake.SetEnabled(true))
            SetStatus("A preferência para manter a tela ativa foi carregada, mas o Windows não aceitou a solicitação.", false);
        if (_settings.PreferredDelimiter is not (null or "," or "|" or ";" or "\r\n" or "\n" or "\r"))
            _settings.PreferredDelimiter = null;
        SqlDelimiterPicker.SelectedIndex = _settings.PreferredDelimiter switch
        {
            "," => 1, "|" => 2, ";" => 3, "\r\n" or "\n" or "\r" => 4, _ => 0
        };
        SqlDelimiterPicker.SelectionChanged += SqlDelimiterPicker_SelectionChanged;
        _loadingAdvancedNotificationToggle = true;
        AdvancedNotificationsToggle.IsChecked = _settings.UseJmdNotifications;
        _loadingAdvancedNotificationToggle = false;
        if (_settings.HistoryRetentionDays is < 1 or > 3650)
            _settings.HistoryRetentionDays = 15;
        try
        {
            var historyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JMD", "history.db");
            _historyStore = new ConversionHistoryStore(historyPath);
            _historyRecorder = new ConversionHistoryRecorder(_historyStore);
            _historyStore.DeleteOlderThan(_settings.HistoryRetentionDays);
            _draftStack.RestoreFromHistory(_historyStore.Search(string.Empty));
            if (_historyStore.GetLatest() is { } lastConversion &&
                (lastConversion.CommandName == "Montar SQL IN" ||
                 lastConversion.CommandName.StartsWith("Substituir seleção", StringComparison.Ordinal)))
                _conversionGuard.Remember(lastConversion.BeforeValue, lastConversion.AfterValue);
        }
        catch (Exception exception)
        {
            SetStatus($"O histórico está indisponível: {exception.Message}", false);
        }
        _selectionTransformer = new SelectionTransformer(_clipboard, _keyboard);
        _focusedText = new FocusedTextAutomation(_keyboard);
        BuildCommands();
        CommandsList.ItemsSource = _commands;
        _commandView = CollectionViewSource.GetDefaultView(_commands);
        _commandView.Filter = item => item is CommandRow command && command.Visible;
        BuildSettingsPanel();
        BuildSnippetManagementPage();
        if (_settings.VoiceWakeWordEnabled)
        {
            try { StartVoiceWakeWord(); }
            catch (Exception exception)
            {
                _settings.VoiceWakeWordEnabled = false;
                StopVoiceWakeWord();
                UpdateVoiceWakeWordToggle(false);
                try { _settingsStore.Save(_settings); }
                catch (Exception saveException) when (saveException is IOException or UnauthorizedAccessException) { }
                SetVoiceWakeWordUnavailable(exception);
            }
        }
        HistoryRetentionBox.Text = _settings.HistoryRetentionDays.ToString(CultureInfo.InvariantCulture);
        RefreshHistory();
        SourceInitialized += MainWindow_SourceInitialized;
        _memoryRefreshTimer.Tick += (_, _) => RefreshMemoryStatus();
        _memoryRefreshTimer.Start();
        RefreshMemoryStatus();
        _benchmarkRecordingTimer.Tick += BenchmarkRecordingTimer_Tick;
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
        _commands.Add(new CommandRow("jsonPretty", "Formatar",
            "Identifica JSON ou XML e aplica indentação ao conteúdo selecionado.", ShortcutDefaults.ForCommand("jsonPretty")));
        _commands.Add(new CommandRow("jsonCompact", "Compactar",
            "Identifica JSON ou XML e remove espaços e quebras de linha.", ShortcutDefaults.ForCommand("jsonCompact")));
        _commands.Add(new CommandRow("snippets", "Inserir texto salvo",
            "Busca pelo título e cola o texto escolhido no aplicativo anterior.", ShortcutDefaults.ForCommand("snippets")));
        _commands.Add(new CommandRow("saveSnippet", "Salvar seleção como texto",
            "Copia a seleção e pede um título para salvá-la.", ShortcutDefaults.ForCommand("saveSnippet")));
        _commands.Add(new CommandRow("draftSave", "Salvar rascunho",
            "Salva a seleção ou a linha do cursor e limpa o campo.", ShortcutDefaults.ForCommand("draftSave")));
        _commands.Add(new CommandRow("draftRestore", "Usar rascunho",
            "Guarda o conteúdo atual e recupera o último rascunho.", ShortcutDefaults.ForCommand("draftRestore")));
        _commands.Add(new CommandRow("about", "Abrir Sobre", "Abre a tela Sobre do JMD.", "Sem atalho")
        {
            VoicePhrasesDisplay = GetVoicePhrasesDisplay("about")
        });
        _commands.Add(new CommandRow("checkUpdates", "Verificar atualizações JMD", "Abre Sobre e consulta uma nova versão.", "Sem atalho")
        {
            VoicePhrasesDisplay = GetVoicePhrasesDisplay("checkUpdates")
        });
        _commands.Add(new CommandRow("createCustomShortcut", "Criar atalho personalizado",
            "Cadastra um comando para abrir um programa ou endereço da web.", "Sem atalho"));
        foreach (var shortcut in _settings.CustomShortcuts)
        {
            var row = new CommandRow(shortcut.Id, shortcut.Name, shortcut.Target,
                string.IsNullOrWhiteSpace(shortcut.Shortcut) ? "Comando personalizado" : shortcut.Shortcut)
            {
                IsCustomShortcut = true,
                VoicePhrasesDisplay = GetVoicePhrasesDisplay(shortcut.Id)
            };
            _commands.Add(row);
        }
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
            if (IsJeyAction(id) && string.IsNullOrWhiteSpace(value))
            {
                _hotkeys.Unregister(id);
                SetShortcutStatus(id, "Ativo por voz", true);
                continue;
            }
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
        foreach (var shortcut in _settings.CustomShortcuts)
        {
            if (string.IsNullOrWhiteSpace(shortcut.Shortcut))
            {
                _hotkeys.Unregister(shortcut.Id);
                continue;
            }
            if (!ShortcutParser.TryParse(shortcut.Shortcut, out var binding, out _) ||
                !_hotkeys.TryRegister(shortcut.Id, binding!, out _))
                SetStatus($"Atalho personalizado indisponível: {shortcut.Name}", false);
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
            Text = "Clique no campo e pressione a nova combinação. Para ativar por voz, associe uma ou mais frases ao comando, uma por linha. Diga ‘Jey’ seguido de uma dessas frases.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 14)
        });

        AddShortcutRow("palette", "Abrir paleta", "Exibe a lista de comandos e configurações.");
        AddShortcutRow("clipboard", "Formatar clipboard", "Transforma o texto copiado para SQL IN.");
        AddShortcutRow("selection", "Substituir seleção", "Recorta, transforma e cola a seleção atual.");
        AddShortcutRow("jsonPretty", "Formatar", "Identifica JSON ou XML e aplica indentação.");
        AddShortcutRow("jsonCompact", "Compactar", "Identifica JSON ou XML e deixa o conteúdo em uma linha.");
        AddShortcutRow("snippets", "Inserir texto salvo", "Busca pelo título e cola no aplicativo anterior.");
        AddShortcutRow("saveSnippet", "Salvar seleção como texto", "Copia a seleção atual e pede um título.");
        AddShortcutRow("draftSave", "Salvar rascunho", "Salva a seleção ou a linha do cursor e limpa o campo.");
        AddShortcutRow("draftRestore", "Usar rascunho", "Guarda o conteúdo atual e recupera o último rascunho.");

        AddShortcutRow("about", "Abrir Sobre", "Abre a tela Sobre do JMD.");
        AddShortcutRow("checkUpdates", "Verificar atualizações JMD", "Abre Sobre e consulta uma nova versão.");

        SettingsPanel.Children.Add(new TextBlock
        {
            Text = "Atalhos personalizados",
            Foreground = Brushes.White,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 6)
        });
        var addCustomShortcut = new Button
        {
            Content = "+ Criar atalho personalizado",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Background = new SolidColorBrush(Color.FromRgb(180, 243, 106)),
            Foreground = new SolidColorBrush(Color.FromRgb(23, 25, 31)),
            BorderThickness = new Thickness(0)
        };
        addCustomShortcut.Click += (_, _) =>
        {
            CreateCustomShortcutFromDialog();
            BuildSettingsPanel();
        };
        SettingsPanel.Children.Add(addCustomShortcut);
        foreach (var customShortcut in _settings.CustomShortcuts)
        {
            var customRow = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            customRow.ColumnDefinitions.Add(new ColumnDefinition());
            customRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            customRow.Children.Add(new TextBlock
            {
                Text = $"{customShortcut.Name} · {customShortcut.Target}",
                Foreground = new SolidColorBrush(Color.FromRgb(220, 223, 230)),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = customShortcut.Target
            });
            var editCustomShortcut = new Button
            {
                Content = "Editar",
                Tag = customShortcut.Id,
                Padding = new Thickness(10, 5, 10, 5),
                Background = new SolidColorBrush(Color.FromRgb(48, 52, 62)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            editCustomShortcut.Click += (_, _) =>
            {
                CreateCustomShortcutFromDialog(customShortcut);
                BuildSettingsPanel();
            };
            Grid.SetColumn(editCustomShortcut, 1);
            customRow.Children.Add(editCustomShortcut);
            SettingsPanel.Children.Add(customRow);
        }

        SettingsPanel.Children.Add(new TextBlock
        {
            Text = "A pilha de rascunhos usa os atalhos configurados acima. Os rascunhos ficam disponíveis no Histórico.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 243, 106)),
            FontSize = 12,
            Margin = new Thickness(2, 12, 2, 4)
        });


        var note = new TextBlock
        {
            Text = "Alt+J pode estar em uso por outro aplicativo. Se aparecer como indisponível, escolha outro atalho.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(235, 196, 119)),
            FontSize = 11,
            Margin = new Thickness(2, 12, 2, 0)
        };
        SettingsPanel.Children.Add(note);

        foreach (var row in _shortcutRows)
        {
            if (!existingStatuses.TryGetValue(row.Id, out var status)) continue;
            row.Status = status.Status;
            row.StatusBrush = status.StatusBrush;
        }
        RefreshShortcutStatuses();
    }

    private void ConfigureDefaultVoiceCommands()
    {
        var defaults = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["about"] = ["abrir sobre", "abrir tela sobre"],
            ["checkUpdates"] = ["verificar atualizações", "verificar atualização", "atualizar jmd"]
        };
        var changed = false;
        foreach (var (commandId, phrases) in defaults)
        {
            if (_settings.VoicePhrases.ContainsKey(commandId)) continue;
            var existing = _settings.VoicePhrases.Where(pair => pair.Key != commandId).SelectMany(pair => pair.Value);
            _settings.VoicePhrases[commandId] = phrases
                .Where(phrase => !VoicePhrasePolicy.ConflictsWith([phrase], existing))
                .ToList();
            changed = true;
        }
        if (!changed) return;
        try { _settingsStore.Save(_settings); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"As frases padrão do Jey não puderam ser salvas: {exception.Message}", false);
        }
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
        var content = new StackPanel();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(86) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
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
        if (id is "clipboard" or "selection" or "snippets" or "saveSnippet")
        {
            var toolsButton = new Button
            {
                Content = "\uE713",
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                Tag = id,
                Background = new SolidColorBrush(Color.FromRgb(48, 52, 62)),
                Foreground = new SolidColorBrush(Color.FromRgb(180, 243, 106)),
                BorderThickness = new Thickness(0),
                ToolTip = id is "clipboard" or "selection" ? "Abrir ferramentas SQL IN" : "Abrir textos salvos",
                VerticalAlignment = VerticalAlignment.Center
            };
            toolsButton.Click += ShortcutToolsButton_Click;
            Grid.SetColumn(toolsButton, 3);
            grid.Children.Add(toolsButton);
        }
        content.Children.Add(grid);
        content.Children.Add(new TextBlock
        {
            Text = "Frases de voz (uma por linha)",
            Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)),
            FontSize = 10,
            Margin = new Thickness(0, 10, 0, 4)
        });
        var voicePhrasesEditor = new TextBox
        {
            Text = GetVoicePhrasesText(id),
            Tag = id,
            MinHeight = 48,
            MaxHeight = 96,
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 12,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(24, 26, 32)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(65, 70, 81)),
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ToolTip = "Digite uma frase por linha; deixe em branco para não associar frases de voz."
        };
        voicePhrasesEditor.TextChanged += VoicePhrasesEditor_TextChanged;
        content.Children.Add(voicePhrasesEditor);
        card.Child = content;
        SettingsPanel.Children.Add(card);
    }

    private string GetVoicePhrasesText(string commandId)
        => _settings.VoicePhrases.TryGetValue(commandId, out var phrases)
            ? string.Join(Environment.NewLine, phrases.Where(phrase => !string.IsNullOrWhiteSpace(phrase)))
            : string.Empty;

    private string GetVoicePhrasesDisplay(string commandId)
    {
        if (!_settings.VoicePhrases.TryGetValue(commandId, out var phrases)) return string.Empty;
        var display = string.Join(" · ", phrases.Where(phrase => !string.IsNullOrWhiteSpace(phrase)));
        return display.Length == 0 ? string.Empty : $"Jey: {display}";
    }

    private void VoicePhrasesEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: string commandId } editor) return;
        var phrases = editor.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(phrase => !string.IsNullOrWhiteSpace(phrase))
            .ToList();
        var existing = _settings.VoicePhrases.Where(pair => pair.Key != commandId).SelectMany(pair => pair.Value);
        if (VoicePhrasePolicy.HasDuplicate(phrases) || VoicePhrasePolicy.ConflictsWith(phrases, existing))
        {
            SetStatus("Essa frase do Jey já está associada a outro comando. Use uma frase diferente.", false);
            editor.Text = GetVoicePhrasesText(commandId);
            editor.CaretIndex = editor.Text.Length;
            return;
        }
        _settings.VoicePhrases[commandId] = phrases;
        UpdateVoiceCommandPhrases();
        var command = _commands.FirstOrDefault(item => item.Id == commandId);
        if (command is not null) command.VoicePhrasesDisplay = GetVoicePhrasesDisplay(commandId);
        try { _settingsStore.Save(_settings); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Não foi possível salvar as frases de voz: {exception.Message}", false);
        }
    }

    private void CustomVoicePhrasesEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: string commandId } editor) return;
        var phrases = ParseVoicePhrases(editor.Text);
        var existing = _settings.VoicePhrases.Where(pair => pair.Key != commandId).SelectMany(pair => pair.Value);
        if (!VoicePhrasePolicy.HasDuplicate(phrases) && !VoicePhrasePolicy.ConflictsWith(phrases, existing)) return;
        SetStatus("Essa frase do Jey já está associada a outro comando. Use uma frase diferente.", false);
        editor.Text = GetVoicePhrasesText(commandId);
        editor.CaretIndex = editor.Text.Length;
    }

    private string? FindVoiceCommand(string? transcript)
    {
        if (NormalizeVoicePhrase(transcript).Length == 0) return null;

        string? matchedCommand = null;
        foreach (var commandId in ShortcutIds.Concat(_settings.CustomShortcuts.Select(shortcut => shortcut.Id)))
        {
            if (_settings.DisabledCommands.Contains(commandId) ||
                !_settings.VoicePhrases.TryGetValue(commandId, out var phrases))
                continue;

            if (!phrases.Any(phrase => VoicePhrasePolicy.Matches(phrase, transcript))) continue;
            if (matchedCommand is not null)
            {
                SetStatus("Essa frase está associada a mais de um comando. Deixe-a em apenas um comando para usá-la por voz.", false);
                return null;
            }
            matchedCommand = commandId;
        }
        return matchedCommand;
    }

    private string[] GetEnabledVoiceCommandPhrases()
        => _settings.VoicePhrases
            .Where(pair => !_settings.DisabledCommands.Contains(pair.Key))
            .SelectMany(pair => pair.Value)
            .Where(phrase => !string.IsNullOrWhiteSpace(phrase))
            .ToArray();

    private void UpdateVoiceCommandPhrases()
        => _voiceWakeWord?.SetVoiceCommandPhrases(GetEnabledVoiceCommandPhrases());

    private static string NormalizeVoicePhrase(string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return string.Empty;
        return VoicePhrasePolicy.Normalize(phrase);
    }

    private string GetVoiceCommandName(string commandId)
        => _commands.FirstOrDefault(command => command.Id == commandId)?.Name
           ?? _settings.CustomShortcuts.FirstOrDefault(shortcut => shortcut.Id == commandId)?.Name
           ?? commandId;

    private static TextBox CreateSettingsTextBox(string text, double height, bool multiline = false)
        => new()
        {
            Text = text,
            Height = height,
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 12,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(24, 26, 32)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(65, 70, 81)),
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            AcceptsReturn = multiline,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
        };

    private static List<string> ParseVoicePhrases(string text)
        => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(phrase => !string.IsNullOrWhiteSpace(phrase)).ToList();

    private static bool IsValidCustomTarget(string target)
    {
        if (!target.Contains("://", StringComparison.Ordinal)) return true;
        return Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
               uri.Scheme is "http" or "https";
    }

    private bool HasShortcutConflict(string shortcutText, string? exceptId)
    {
        if (!ShortcutParser.TryParse(shortcutText, out var candidate, out _)) return false;
        var values = _settings.Shortcuts.Where(pair => pair.Key != exceptId).Select(pair => pair.Value)
            .Concat(_settings.CustomShortcuts.Where(entry => entry.Id != exceptId).Select(entry => entry.Shortcut));
        return values.Any(value => ShortcutParser.TryParse(value, out var existing, out _) && existing == candidate);
    }

    private void CustomShortcutCapture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if ((key is Key.Back or Key.Delete) && Keyboard.Modifiers == ModifierKeys.None)
        {
            editor.Clear();
            return;
        }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var modifiers = ShortcutModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= ShortcutModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= ShortcutModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= ShortcutModifiers.Shift;
        if (IsKeyDown(VkLWin) || IsKeyDown(VkRWin)) modifiers |= ShortcutModifiers.Windows;
        if (modifiers == ShortcutModifiers.None)
        {
            SetStatus("Inclua Ctrl, Alt, Shift ou Win na combinação.", false);
            return;
        }
        editor.Text = new ShortcutBinding(modifiers, KeyInterop.VirtualKeyFromKey(key)).ToString();
    }

    private void CreateCustomShortcutFromDialog(CustomShortcutEntry? existing = null)
    {
        (string Name, string Target, string Shortcut, bool FocusIfAlreadyOpen)? previous = existing is null
            ? null : (existing.Name, existing.Target, existing.Shortcut, existing.FocusIfAlreadyOpen);
        var previousPhrases = existing is not null && _settings.VoicePhrases.TryGetValue(existing.Id, out var savedPhrases)
            ? savedPhrases.ToList() : [];
        var dialog = new CustomShortcutEditorWindow(existing, previousPhrases);
        _showingDialog = true;
        bool accepted;
        try { accepted = dialog.ShowDialog() == true; }
        finally { _showingDialog = false; }
        if (!accepted) return;

        var name = dialog.CommandName;
        var target = dialog.Target;
        var shortcutText = dialog.Shortcut;
        var focusIfAlreadyOpen = dialog.FocusIfAlreadyOpen;
        var phrases = dialog.VoicePhrases.ToList();
        string? error = null;
        if (name.Length == 0 || target.Length == 0) error = "Informe o nome e o programa ou URL do atalho.";
        else if (!IsValidCustomTarget(target)) error = "Informe um caminho de programa ou uma URL http/https válida.";
        else if (shortcutText.Length == 0 && phrases.Count == 0) error = "Configure um atalho de teclado ou uma frase do Jey.";
        else if (shortcutText.Length > 0 && !ShortcutParser.TryParse(shortcutText, out _, out error)) { }
        else if (shortcutText.Length > 0 && HasShortcutConflict(shortcutText, existing?.Id)) error = "Essa combinação de teclas já está associada a outro comando.";
        else if (VoicePhrasePolicy.HasDuplicate(phrases) || VoicePhrasePolicy.ConflictsWith(phrases,
                     _settings.VoicePhrases.Where(pair => pair.Key != existing?.Id).SelectMany(pair => pair.Value)))
            error = "Uma dessas frases do Jey já está associada a outro comando. Escolha frases exclusivas.";
        if (error is not null)
        {
            Notify("JMD", error, false);
            return;
        }

        var entry = existing ?? new CustomShortcutEntry();
        entry.Name = name;
        entry.Target = target;
        entry.Shortcut = shortcutText;
        entry.FocusIfAlreadyOpen = focusIfAlreadyOpen;
        if (existing is null) _settings.CustomShortcuts.Add(entry);
        _settings.VoicePhrases[entry.Id] = phrases;
        try
        {
            _settingsStore.Save(_settings);
            var oldRow = _commands.FirstOrDefault(command => command.Id == entry.Id);
            if (oldRow is not null) _commands.Remove(oldRow);
            _commands.Add(new CommandRow(entry.Id, entry.Name, entry.Target,
                string.IsNullOrWhiteSpace(entry.Shortcut) ? "Comando personalizado" : entry.Shortcut)
            {
                IsCustomShortcut = true,
                VoicePhrasesDisplay = GetVoicePhrasesDisplay(entry.Id)
            });
            if (_hotkeys is not null)
            {
                _hotkeys.Unregister(entry.Id);
                if (!string.IsNullOrWhiteSpace(entry.Shortcut) && ShortcutParser.TryParse(entry.Shortcut, out var binding, out _) &&
                    !_settings.DisabledCommands.Contains(entry.Id))
                    _hotkeys.TryRegister(entry.Id, binding!, out _);
            }
            _commandView?.Refresh();
            Notify("JMD", $"Atalho personalizado {(existing is null ? "criado" : "atualizado")}: {name}.", true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (existing is null)
            {
                _settings.CustomShortcuts.Remove(entry);
                _settings.VoicePhrases.Remove(entry.Id);
            }
            else if (previous is not null)
            {
                entry.Name = previous.Value.Name;
                entry.Target = previous.Value.Target;
                entry.Shortcut = previous.Value.Shortcut;
                entry.FocusIfAlreadyOpen = previous.Value.FocusIfAlreadyOpen;
                _settings.VoicePhrases[entry.Id] = previousPhrases;
            }
            Notify("JMD", $"Não foi possível salvar o atalho: {exception.Message}", false);
        }
        UpdateVoiceCommandPhrases();
    }

    private void EditCustomCommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var entry = _settings.CustomShortcuts.FirstOrDefault(item => item.Id == id);
        if (entry is not null) CreateCustomShortcutFromDialog(entry);
    }

    private void RemoveCustomCommandButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var entry = _settings.CustomShortcuts.FirstOrDefault(item => item.Id == id);
        if (entry is null) return;
        RemoveCustomShortcut_Click(new Button { Tag = entry }, e);
    }

    private void RemoveCustomShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CustomShortcutEntry entry }) return;
        var savedPhrases = _settings.VoicePhrases.TryGetValue(entry.Id, out var phrases) ? phrases : null;
        _hotkeys?.Unregister(entry.Id);
        _settings.CustomShortcuts.Remove(entry);
        _settings.VoicePhrases.Remove(entry.Id);
        var commandRow = _commands.FirstOrDefault(command => command.Id == entry.Id);
        if (commandRow is not null) _commands.Remove(commandRow);
        try
        {
            _settingsStore.Save(_settings);
            BuildSettingsPanel();
            SetStatus($"Atalho personalizado removido: {entry.Name}.", true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _settings.CustomShortcuts.Add(entry);
            if (savedPhrases is not null) _settings.VoicePhrases[entry.Id] = savedPhrases;
            _commands.Add(new CommandRow(entry.Id, entry.Name, entry.Target, entry.Shortcut)
            {
                IsCustomShortcut = true,
                VoicePhrasesDisplay = GetVoicePhrasesDisplay(entry.Id)
            });
            SetStatus($"Não foi possível remover o atalho: {exception.Message}", false);
        }
        UpdateVoiceCommandPhrases();
    }

    private void BuildSnippetManagementPage()
    {
        SnippetPageTitle.Text = $"Textos salvos ({_settings.Snippets.Count})";
        SnippetManagementPanel.Children.Clear();
        RenderSnippetSettings(SnippetManagementPanel);
    }

    private void AddSnippetButton_Click(object sender, RoutedEventArgs e)
        => EditSnippet(null, null);

    private void RenderSnippetSettings(System.Windows.Controls.Panel targetPanel)
    {
        if (_settings.Snippets.Count == 0)
        {
            targetPanel.Children.Add(new TextBlock
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
            var description = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            description.Children.Add(new TextBlock { Text = snippet.Title, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis });
            description.Children.Add(new TextBlock
            {
                Text = snippet.Text,
                Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)),
                FontSize = 11,
                Margin = new Thickness(0, 3, 8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            row.Children.Add(description);
            targetPanel.Children.Add(new Border
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
            BuildSnippetManagementPage();
            SetStatus("Textos salvos atualizados.", true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Não foi possível salvar os textos: {exception.Message}", false);
            return false;
        }
    }

    private async Task CaptureSnippetAsync(VoiceCommandInvocation? voiceCommand = null)
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
            RecordActionHistory($"Salvar texto: {snippet.Title}", string.Empty, snippet.Text, voiceCommand);
            Notify("JMD", $"Texto salvo: {snippet.Title}", true);
            if (_navigation.CurrentPage == AppPage.ShortcutSettings) BuildSnippetManagementPage();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _settings.Snippets.RemoveAll(item => item.Id == snippet.Id);
            Notify("JMD", $"Não foi possível salvar o texto: {exception.Message}", false);
        }
    }

    private async Task InsertSavedSnippetAsync(VoiceCommandInvocation? voiceCommand = null)
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
        RecordActionHistory($"Inserir texto salvo: {snippet.Title}", snippet.Title, snippet.Text, voiceCommand);
        Notify("JMD", $"Texto inserido: {snippet.Title}", true);
    }

    private void RecordActionHistory(string commandName, string beforeValue, string afterValue, VoiceCommandInvocation? voiceCommand = null)
    {
        if (_historyStore is null) return;
        try
        {
            RecordCommandHistory(commandName, beforeValue, afterValue, voiceCommand);
        }
        catch (Exception exception)
        {
            SetStatus($"Ação concluída, mas não foi possível salvar no histórico: {exception.Message}", false);
        }
    }

    private static readonly string[] ShortcutIds = ["palette", "clipboard", "selection", "jsonPretty", "jsonCompact", "snippets", "saveSnippet", "draftSave", "draftRestore", "about", "checkUpdates"];

    private static bool IsJeyAction(string id) => id is "about" or "checkUpdates";

    private string GetShortcut(string id)
    {
        if (_settings.Shortcuts.TryGetValue(id, out var value)) return value;
        var customShortcut = _settings.CustomShortcuts.FirstOrDefault(shortcut => shortcut.Id == id);
        if (customShortcut is not null)
            return string.IsNullOrWhiteSpace(customShortcut.Shortcut) ? "Comando personalizado" : customShortcut.Shortcut;
        try { return ShortcutDefaults.ForCommand(id); }
        catch (ArgumentOutOfRangeException) { return string.Empty; }
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
            var isVoiceAction = IsJeyAction(command.Id);
            var shortcut = GetShortcut(command.Id);
            command.Shortcut = isVoiceAction && string.IsNullOrWhiteSpace(shortcut) ? "Sem atalho" : shortcut;
            command.VoicePhrasesDisplay = GetVoicePhrasesDisplay(command.Id);
            var shortcutRow = _shortcutRows.FirstOrDefault(row => row.Id == command.Id);
            command.Status = shortcutRow?.Status ?? (isVoiceAction ? "Comando por voz" : "Atalho configurável");
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

        if (HasShortcutConflict(new ShortcutBinding(modifiers, KeyInterop.VirtualKeyFromKey(key)).ToString(), row.Id))
        {
            row.Status = "Já atribuída a outro comando, inclusive personalizado.";
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

    private async void HandleHotkeyAsync(string id, VoiceCommandInvocation? voiceCommand = null)
    {
        try
        {
            if (voiceCommand is null)
                ActivationSoundService.PlayShortcutActivated();
            else
                ActivationSoundService.PlayVoiceCommandFound();
            _previousWindow = _keyboard.GetForegroundWindow();
            switch (id)
            {
                case "palette":
                    ShowPalette();
                    if (voiceCommand is not null)
                        RecordActionHistory("Abrir paleta", string.Empty, string.Empty, voiceCommand);
                    break;
                case "about":
                    ShowAboutPage();
                    if (voiceCommand is not null)
                        RecordActionHistory("Abrir Sobre", string.Empty, string.Empty, voiceCommand);
                    break;
                case "checkUpdates":
                    ShowAboutPage();
                    if (voiceCommand is not null)
                        RecordActionHistory("Verificar atualizações JMD", string.Empty, string.Empty, voiceCommand);
                    CheckUpdatesButton_Click(this, new RoutedEventArgs());
                    break;
                case "clipboard":
                    await TransformClipboardAsync(voiceCommand);
                    break;
                case "selection":
                    await TransformSelectionAsync(voiceCommand);
                    break;
                case "jsonPretty":
                    await TransformStructuredDataAsync(StructuredDataLayout.Pretty, voiceCommand);
                    break;
                case "jsonCompact":
                    await TransformStructuredDataAsync(StructuredDataLayout.Compact, voiceCommand);
                    break;
                case "snippets":
                    await InsertSavedSnippetAsync(voiceCommand);
                    break;
                case "saveSnippet":
                    await CaptureSnippetAsync(voiceCommand);
                    break;
                case "draftSave":
                    await SaveDraftAsync(voiceCommand);
                    break;
                case "draftRestore":
                    await RestoreDraftAsync(voiceCommand);
                    break;
                default:
                    var customShortcut = _settings.CustomShortcuts.FirstOrDefault(shortcut => shortcut.Id == id);
                    if (customShortcut is not null) OpenCustomShortcut(customShortcut, voiceCommand);
                    break;
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Operação falhou: {exception.Message}", false);
        }
    }

    private async Task SaveDraftAsync(VoiceCommandInvocation? voiceCommand = null)
    {
        if (!await _keyboard.WaitForModifiersReleasedAsync()) return;
        if (!_focusedText.HasEditableTextFocus())
        {
            Notify("JMD", "O foco atual não está em um campo de texto editável.", false);
            return;
        }
        var draft = _focusedText.ReadSelection();
        if (string.IsNullOrWhiteSpace(draft))
        {
            draft = await CaptureFocusedSelectionAsync();
        }
        if (string.IsNullOrWhiteSpace(draft))
        {
            _keyboard.SendHome();
            _keyboard.SendShiftEnd();
            await Task.Delay(40);
            draft = _focusedText.ReadSelection();
            if (string.IsNullOrWhiteSpace(draft)) draft = await CaptureFocusedSelectionAsync();
        }
        if (string.IsNullOrWhiteSpace(draft))
        {
            Notify("JMD", "Nenhum texto selecionado no campo ativo.", false);
            return;
        }
        if (!_focusedText.DeleteSelection())
        {
            Notify("JMD", "Não foi possível apagar o texto selecionado.", false);
            return;
        }
        _draftStack.Push(draft);
        RecordActionHistory(DraftStack.SavedCommandName, string.Empty, draft, voiceCommand);
        Notify("JMD", $"Rascunho salvo ({_draftStack.Count} na pilha).", true);
    }

    private void OpenCustomShortcut(CustomShortcutEntry shortcut, VoiceCommandInvocation? voiceCommand = null)
    {
        try
        {
            if (shortcut.FocusIfAlreadyOpen && TryFocusExistingTarget(shortcut.Target))
            {
                if (voiceCommand is not null)
                    RecordActionHistory($"Focar {shortcut.Name}", shortcut.Target, string.Empty, voiceCommand);
                return;
            }
            Process.Start(new ProcessStartInfo { FileName = shortcut.Target, UseShellExecute = true });
            if (voiceCommand is not null)
                RecordActionHistory($"Abrir {shortcut.Name}", shortcut.Target, string.Empty, voiceCommand);
        }
        catch (Exception exception)
        {
            SetStatus($"Não foi possível abrir {shortcut.Name}: {exception.Message}", false);
        }
    }

    private static bool TryFocusExistingTarget(string target)
    {
        var isUrl = Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        var processName = Path.GetFileNameWithoutExtension(target);
        var processNames = isUrl
            ? new HashSet<string>(["chrome", "msedge", "firefox", "brave", "opera", "vivaldi"], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>([processName], StringComparer.OrdinalIgnoreCase);
        var domain = isUrl ? uri!.Host : string.Empty;
        var targetMatches = false;
        EnumWindows((window, parameter) =>
        {
            if (!IsWindowVisible(window)) return true;
            var details = ForegroundWindowDetails.Read(window);
            if (details is null) return true;
            var processMatches = details.ApplicationName is not null && processNames.Contains(details.ApplicationName);
            var titleMatches = details.WindowTitle is not null &&
                (details.WindowTitle.Contains(target, StringComparison.OrdinalIgnoreCase) ||
                 (domain.Length > 0 && details.WindowTitle.Contains(domain, StringComparison.OrdinalIgnoreCase)));
            if (!(isUrl ? processMatches && titleMatches : processMatches)) return true;
            ShowWindow(window, 9);
            targetMatches = SetForegroundWindowNative(window);
            return false;
        }, IntPtr.Zero);
        return targetMatches;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    private static extern bool ShowWindow(IntPtr window, int command);

    private async Task RestoreDraftAsync(VoiceCommandInvocation? voiceCommand = null)
    {
        if (_draftStack.Count == 0)
        {
            Notify("JMD", "A pilha de rascunhos está vazia. Você pode copiá-los pelo histórico.", false);
            return;
        }
        if (!await _keyboard.WaitForModifiersReleasedAsync()) return;
        var currentText = _focusedText.ReadText();
        if (currentText is null)
        {
            Notify("JMD", "O campo ativo não permite ler o texto atual.", false);
            return;
        }
        if (!_draftStack.TryPop(out var draft) || draft is null) return;
        if (!await _focusedText.ReplaceAllAsync(draft, _clipboard))
        {
            if (!string.IsNullOrWhiteSpace(currentText))
            {
                _draftStack.Push(currentText);
                RecordActionHistory(DraftStack.SavedCommandName, string.Empty, currentText);
            }
            _draftStack.Push(draft);
            RecordActionHistory(DraftStack.SavedCommandName, string.Empty, draft);
            Notify("JMD", "Não foi possível inserir o rascunho no campo ativo.", false);
            return;
        }
        RecordActionHistory(DraftStack.RestoredCommandName, string.Empty, draft, voiceCommand);
        if (!string.IsNullOrWhiteSpace(currentText))
        {
            _draftStack.Push(currentText);
            RecordActionHistory(DraftStack.SavedCommandName, string.Empty, currentText);
        }
        Notify("JMD", $"Rascunho restaurado ({_draftStack.Count} na pilha).", true);
    }

    private async Task<string?> CaptureFocusedSelectionAsync()
    {
        var previousContent = _clipboard.TryGetRichContent();
        var sequenceBeforeCopy = _clipboard.SequenceNumber;
        if (!_keyboard.SendCopy()) return null;
        await Task.Delay(60);
        var selectedText = _clipboard.SequenceNumber != sequenceBeforeCopy
            ? await _clipboard.TryGetTextAsync()
            : null;
        if (previousContent is not null) _clipboard.TrySetRichContent(previousContent);
        return selectedText;
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
            else if (IsJeyAction(row.Id) && string.IsNullOrWhiteSpace(GetShortcut(row.Id)))
            {
                _hotkeys?.Unregister(row.Id);
                row.Status = "Ativo por voz";
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
        UpdateVoiceCommandPhrases();
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
        SqlInTestPage.Visibility = Visibility.Collapsed;
        SnippetManagementPage.Visibility = Visibility.Collapsed;
        HistoryPage.Visibility = Visibility.Collapsed;
        AboutPage.Visibility = Visibility.Collapsed;
        JeySettingsPage.Visibility = Visibility.Collapsed;
        AdvancedSettingsPage.Visibility = Visibility.Collapsed;
        SettingsRetryButton.Visibility = Visibility.Collapsed;
        FooterHints.Visibility = Visibility.Visible;
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

    private async Task TransformClipboardAsync(VoiceCommandInvocation? voiceCommand = null)
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
                RecordHistory("Montar SQL IN", input, result, voiceCommand);
            }
            var message = result.Success
                ? WithConversionCounts("Lista formatada para SQL IN e pronta para colar.", result.InputItemCount, result.OutputItemCount)
                : result.Error ?? "Não foi possível converter a lista.";
            Notify("JMD", message, result.Success);
        }
        catch (Exception exception) { Notify("JMD", $"Falha ao transformar o clipboard: {exception.Message}", false); }
        finally { _conversionRunning = false; }
    }

    private async Task TransformSelectionAsync(VoiceCommandInvocation? voiceCommand = null)
    {
        if (_conversionRunning) return;
        _conversionRunning = true;
        try
        {
            var result = await _selectionTransformer.ExecuteAsync(FormatTextForExecution);
            if (result.Success && result.BeforeValue is not null && result.AfterValue is not null)
                _conversionGuard.Remember(result.BeforeValue, result.AfterValue);
            RecordSelectionHistory(result, voiceCommand);
            var message = result.Success
                ? WithConversionCounts(result.Message, result.InputItemCount, result.OutputItemCount)
                : result.Message;
            Notify("JMD", message, result.Success);
        }
        catch (Exception exception) { Notify("JMD", $"Falha ao substituir a seleção: {exception.Message}", false); }
        finally { _conversionRunning = false; }
    }

    private void RecordHistory(string commandName, string beforeValue, TransformationResult result, VoiceCommandInvocation? voiceCommand = null)
    {
        if (!result.Success || result.Value is null || _historyStore is null) return;
        try
        {
            if (voiceCommand is not null)
                RecordCommandHistory(commandName, beforeValue, result.Value, voiceCommand);
            else if (_historyRecorder?.RecordIfSuccessful(commandName, beforeValue, result, GetConversionSource()) == true)
                RefreshHistory();
        }
        catch (Exception exception)
        {
            SetStatus($"Conversão concluída, mas não foi possível salvar no histórico: {exception.Message}", false);
        }
    }

    private void RecordSelectionHistory(SelectionTransformResult result, VoiceCommandInvocation? voiceCommand = null)
    {
        if (!result.Success || result.BeforeValue is null || result.AfterValue is null || _historyStore is null) return;
        try
        {
            var commandName = result.UsedClipboardFallback
                ? "Formatar clipboard (atalho de seleção)"
                : "Substituir seleção (SQL IN)";
            if (voiceCommand is not null)
                RecordCommandHistory(commandName, result.BeforeValue, result.AfterValue, voiceCommand);
            else if (_historyRecorder?.RecordIfSuccessful(commandName, result, GetConversionSource()) == true)
                RefreshHistory();
        }
        catch (Exception exception)
        {
            SetStatus($"Conversão concluída, mas não foi possível salvar no histórico: {exception.Message}", false);
        }
    }

    private void RecordCommandHistory(string commandName, string beforeValue, string afterValue, VoiceCommandInvocation? voiceCommand = null)
    {
        if (_historyStore is null) return;
        var historyCommandName = voiceCommand is null
            ? commandName
            : $"Ativação por voz (Jey) - {voiceCommand.CommandName}";
        _historyStore.Add(historyCommandName, beforeValue, afterValue,
            source: GetConversionSource(), isVoiceTriggered: voiceCommand is not null,
            voiceTranscript: voiceCommand?.Transcript, voiceAudioWav: voiceCommand?.AudioWav,
            voiceDurationMilliseconds: voiceCommand?.DebugInfo?.DurationMilliseconds,
            voiceCutMilliseconds: voiceCommand?.DebugInfo?.CutMilliseconds,
            voiceTranscriptionAudioWav: voiceCommand?.DebugInfo?.TranscriptionAudioWav,
            portugueseTranscriptionTimeMilliseconds: voiceCommand?.DebugInfo?.PortugueseTranscriptionTimeMilliseconds,
            englishTranscriptionTimeMilliseconds: voiceCommand?.DebugInfo?.EnglishTranscriptionTimeMilliseconds);
        RefreshHistory();
    }

    private void RefreshHistory()
    {
        if (_historyStore is null) return;
        try
        {
            var entries = _historyStore.Search(HistorySearchBox?.Text ?? string.Empty)
                .Select(entry => new HistoryDisplayEntry(
                    entry.Id,
                    entry.CommandName,
                    entry.BeforeValue,
                    entry.AfterValue,
                    entry.OccurredAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture),
                    FormatSourceDisplay(entry.ApplicationName, entry.WindowTitle),
                    entry.IsVoiceTriggered,
                    entry.VoiceTranscript ?? "Transcrição indisponível",
                    entry.HasVoiceAudio,
                    entry.IsVoiceTriggered && entry.CommandName.StartsWith("Ativação por voz (Jey) - ", StringComparison.Ordinal),
                    entry.VoiceDurationMilliseconds is not null || entry.VoiceCutMilliseconds is not null || entry.HasVoiceTranscriptionAudio || entry.PortugueseTranscriptionTimeMilliseconds is not null || entry.EnglishTranscriptionTimeMilliseconds is not null,
                    entry.VoiceDurationMilliseconds is { } duration ? $"Duração total: {duration} ms" : "Duração total: indisponível",
                    $"Tempo por chamada: Português {FormatElapsed(entry.PortugueseTranscriptionTimeMilliseconds)} · Inglês {FormatElapsed(entry.EnglishTranscriptionTimeMilliseconds)}",
                    entry.VoiceCutMilliseconds is { } cut ? $"Milissegundo do corte: {cut} ms" : "Milissegundo do corte: indisponível",
                    entry.HasVoiceTranscriptionAudio,
                    new HistoryAudioPlaybackRequest(entry.Id, false),
                    new HistoryAudioPlaybackRequest(entry.Id, true),
                    entry.CommandName.EndsWith(DraftStack.SavedCommandName, StringComparison.Ordinal),
                    entry.CommandName.EndsWith(DraftStack.RestoredCommandName, StringComparison.Ordinal)))
                .ToList();
            HistoryItemsControl.ItemsSource = entries;
            HistoryScrollViewer.ScrollToTop();
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

    private void PlayHistoryAudio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HistoryAudioPlaybackRequest request }) return;
        try
        {
            var audio = request.IsTranscriptionAudio
                ? _historyStore?.GetVoiceTranscriptionAudio(request.HistoryId)
                : _historyStore?.GetVoiceAudio(request.HistoryId);
            if (audio is null)
            {
                SetStatus("O áudio deste registro não está disponível.", false);
                return;
            }

            StopHistoryAudioPlayback();
            var path = Path.Combine(Path.GetTempPath(), $"JMD-history-{Guid.NewGuid():N}.wav");
            File.WriteAllBytes(path, audio);
            var player = new MediaPlayer();
            _historyAudioPlayer = player;
            _historyAudioPlaybackPath = path;
            player.MediaOpened += (_, _) =>
            {
                if (ReferenceEquals(_historyAudioPlayer, player)) player.Play();
            };
            player.MediaEnded += (_, _) =>
            {
                if (ReferenceEquals(_historyAudioPlayer, player)) StopHistoryAudioPlayback();
            };
            player.MediaFailed += (_, args) =>
            {
                if (!ReferenceEquals(_historyAudioPlayer, player)) return;
                SetStatus($"Não foi possível reproduzir o áudio: {args.ErrorException.Message}", false);
                StopHistoryAudioPlayback();
            };
            player.Open(new Uri(path, UriKind.Absolute));
            SetStatus(request.IsTranscriptionAudio ? "Reproduzindo o trecho enviado à transcrição." : "Reproduzindo áudio do comando de voz.", true);
        }
        catch (Exception exception)
        {
            StopHistoryAudioPlayback();
            SetStatus($"Não foi possível reproduzir o áudio: {exception.Message}", false);
        }
    }

    private void StopHistoryAudioPlayback()
    {
        var player = _historyAudioPlayer;
        _historyAudioPlayer = null;
        if (player is not null)
        {
            player.Stop();
            player.Close();
        }

        var path = _historyAudioPlaybackPath;
        _historyAudioPlaybackPath = null;
        if (path is null) return;
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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

    private sealed record VoiceCommandInvocation(string CommandName, string Transcript, byte[] AudioWav, VoiceCaptureDebugInfo? DebugInfo);
    private sealed record WakeRecognitionHistoryRow(string Time, string Message);
    private sealed record HistoryAudioPlaybackRequest(long HistoryId, bool IsTranscriptionAudio);

    private sealed record HistoryDisplayEntry(
        long Id,
        string CommandName,
        string BeforeValue,
        string AfterValue,
        string DisplayDate,
        string SourceDisplay,
        bool IsVoiceTriggered,
        string VoiceTranscript,
        bool HasVoiceAudio,
        bool IsVoiceCommand,
        bool HasVoiceDebugInfo,
        string VoiceDebugSummary,
        string VoiceTranscriptionTimeDisplay,
        string VoiceCutDisplay,
        bool HasVoiceTranscriptionAudio,
        HistoryAudioPlaybackRequest OriginalAudioRequest,
        HistoryAudioPlaybackRequest TranscriptionAudioRequest,
        bool IsDraftSaved,
        bool IsDraftRestored);

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
        else if (id == "createCustomShortcut") CreateCustomShortcutFromDialog();
        else if (id == "draftSave") await SaveDraftAsync();
        else if (id == "draftRestore") await RestoreDraftAsync();
        else if (id == "about") AboutMenuItem_Click(this, new RoutedEventArgs());
        else if (id == "checkUpdates")
        {
            AboutMenuItem_Click(this, new RoutedEventArgs());
            CheckUpdatesButton_Click(this, new RoutedEventArgs());
        }
        else if (_settings.CustomShortcuts.FirstOrDefault(shortcut => shortcut.Id == id) is { } customShortcut)
            OpenCustomShortcut(customShortcut);
    }

    private void ManageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_navigation.CurrentPage == AppPage.ShortcutSettings &&
            (SqlInTestPage.Visibility == Visibility.Visible || SnippetManagementPage.Visibility == Visibility.Visible))
        {
            ShowShortcutSettings();
            return;
        }
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
        SqlInTestPage.Visibility = Visibility.Collapsed;
        SnippetManagementPage.Visibility = Visibility.Collapsed;
        HistoryPage.Visibility = Visibility.Collapsed;
        AboutPage.Visibility = Visibility.Collapsed;
        JeySettingsPage.Visibility = Visibility.Collapsed;
        AdvancedSettingsPage.Visibility = Visibility.Collapsed;
        SettingsRetryButton.Visibility = Visibility.Visible;
        FooterHints.Visibility = Visibility.Collapsed;
        SearchBox.Visibility = Visibility.Collapsed;
        ManageButton.Content = "Voltar aos comandos";
        PageTitle.Text = "Gerenciar atalhos";
        PageSubtitle.Text = "Configure os comandos e combinações globais";
        foreach (var id in ShortcutIds.Concat(_settings.CustomShortcuts.Select(shortcut => shortcut.Id)))
            _hotkeys?.Unregister(id);
        RefreshShortcutStatuses();
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ShortcutToolsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        if (id is "clipboard" or "selection") ShowSqlInTestPage();
        else ShowSnippetManagementPage();
    }

    private void ShowSqlInTestPage()
    {
        SettingsTabs.Visibility = Visibility.Visible;
        SettingsPage.Visibility = Visibility.Collapsed;
        SqlInTestPage.Visibility = Visibility.Visible;
        SnippetManagementPage.Visibility = Visibility.Collapsed;
        HistoryPage.Visibility = Visibility.Collapsed;
        AdvancedSettingsPage.Visibility = Visibility.Collapsed;
        SettingsRetryButton.Visibility = Visibility.Visible;
        FooterHints.Visibility = Visibility.Collapsed;
        ManageButton.Content = "Voltar aos atalhos";
        PageTitle.Text = "Testar conversão SQL IN";
        PageSubtitle.Text = "Ferramenta vinculada ao comando Formatar clipboard";
    }

    private void ShowSnippetManagementPage()
    {
        BuildSnippetManagementPage();
        SettingsTabs.Visibility = Visibility.Visible;
        SettingsPage.Visibility = Visibility.Collapsed;
        SqlInTestPage.Visibility = Visibility.Collapsed;
        SnippetManagementPage.Visibility = Visibility.Visible;
        HistoryPage.Visibility = Visibility.Collapsed;
        SettingsRetryButton.Visibility = Visibility.Visible;
        FooterHints.Visibility = Visibility.Collapsed;
        ManageButton.Content = "Voltar aos atalhos";
        PageTitle.Text = "Textos salvos";
        PageSubtitle.Text = "Gerencie os textos usados pelos comandos de inserção";
    }

    private void BackToShortcutSettings_Click(object sender, RoutedEventArgs e)
        => ShowShortcutSettings();

    private void RetryShortcuts_Click(object sender, RoutedEventArgs e)
    {
        RegisterConfiguredShortcuts();
        RefreshCommandStatuses();
        RefreshShortcutStatuses();
        SetStatus("Estado dos atalhos atualizado.", true);
    }

    private void SetVoiceWakeWordEnabled(bool enabled)
    {
        if (enabled)
        {
            try { StartVoiceWakeWord(); }
            catch (Exception exception)
            {
                StopVoiceWakeWord();
                DisableVoiceWakeWordPreference();
                SetVoiceWakeWordUnavailable(exception);
                return;
            }
        }
        else StopVoiceWakeWord();

        _settings.VoiceWakeWordEnabled = enabled;
        try
        {
            _settingsStore.Save(_settings);
            SetStatus(enabled ? "Ativação por voz local ativada." : "Ativação por voz desativada; o JMD não está ouvindo o microfone.", true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StopVoiceWakeWord();
            DisableVoiceWakeWordPreference();
            SetStatus("Não foi possível salvar a opção “Ativar Jey”.", false, exception.ToString());
        }
    }

    private void DisableVoiceWakeWordPreference()
    {
        _settings.VoiceWakeWordEnabled = false;
        UpdateVoiceWakeWordToggle(false);
        try { _settingsStore.Save(_settings); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private void SetVoiceWakeWordUnavailable(Exception exception)
    {
        _settings.VoiceWakeWordEnabled = false;
        UpdateVoiceWakeWordToggle(false);
        SetStatus("Ativar Jey não iniciou. Confira o modelo local e o acesso ao microfone.", false, exception.ToString());
    }

    private void StartVoiceWakeWord()
    {
        StopVoiceWakeWord();
        var service = new VoiceWakeWordService(
            _settings.JeyPortugueseModel,
            _settings.JeyEnglishModel,
            _settings.JeyWakeWordClosedGrammarEnabled,
            _settings.JeyWakeWordMinimumConfidencePercent,
            _settings.JeyCommandClosedGrammarEnabled,
            _settings.JeyCommandMinimumConfidencePercent);
        _voiceWakeWord = service;
        service.SetVoiceCommandPhrases(GetEnabledVoiceCommandPhrases());
        service.DebugEnabled = _settings.VoiceDebugEnabled;
        service.WakeWordRecognitionDebugEnabled = _settings.VoiceWakeRecognitionDebugEnabled;
        service.WakeWordRecognitionDebugUpdated += debugInfo => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(_voiceWakeWord, service) || !_settings.VoiceWakeRecognitionDebugEnabled) return;
            if (debugInfo.IsFinal)
            {
                var heard = string.IsNullOrWhiteSpace(debugInfo.Transcript) ? "(sem transcrição)" : debugInfo.Transcript;
                var confidence = debugInfo.Confidence is { } value
                    ? value.ToString("P0", CultureInfo.CurrentCulture)
                    : "indisponível";
                var decision = debugInfo.WakeWordDetected != true
                    ? "não reconheceu Jey"
                    : debugInfo.ConfidenceAccepted
                        ? "ativação aceita"
                        : $"rejeitada pelo mínimo de {debugInfo.MinimumConfidencePercent}%";
                var grammar = debugInfo.ClosedGrammarEnabled ? "gramática fechada" : "vocabulário aberto";
                AddWakeRecognitionHistoryEntry(
                    $"Vosk ativação ({grammar}): {heard} — confiança {confidence} — {decision}", debugInfo.AudioCapturedAt);
                _wakeRecognitionPartial = string.Empty;
            }
            else
            {
                _wakeRecognitionPartial = debugInfo.Transcript;
            }
            UpdateVoiceRecognitionDebugText();
        }));
        service.WakeWordTranscriptionDebugUpdated += debugInfo => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(_voiceWakeWord, service) || !_settings.VoiceWakeRecognitionDebugEnabled) return;
            var cut = debugInfo.CutMilliseconds is { } cutMilliseconds
                ? $"; corte em {cutMilliseconds} ms"
                : string.Empty;
            var total = debugInfo.TotalElapsedMilliseconds is { } elapsed ? $" · total real: {elapsed} ms" : string.Empty;
            var message = $"Trecho para transcrição ({debugInfo.AudioDurationMilliseconds} ms{cut}){total}\n" +
                string.Join("\n", debugInfo.Attempts.Select(FormatTranscriptionAttempt));
            AddWakeRecognitionHistoryEntry(message, debugInfo.AudioCapturedAt);
        }));
        service.AudioProcessingFailed += exception => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ReferenceEquals(_voiceWakeWord, service))
                SetStatus($"Falha ao processar áudio do Jey; a escuta continua ativa. {exception.Message}", false, exception.ToString());
        }));
        service.WakeWordRecognized += (transcript, audioWav, debugInfo) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_settings.VoiceWakeWordEnabled && ReferenceEquals(_voiceWakeWord, service))
            {
                var wakeWordOnly = NormalizeVoicePhrase(transcript).Length == 0;
                var voiceCommandId = FindVoiceCommand(transcript);
                if (voiceCommandId is not null && transcript is not null)
                {
                    var commandName = GetVoiceCommandName(voiceCommandId);
                    HandleHotkeyAsync(voiceCommandId, new VoiceCommandInvocation(commandName, transcript, audioWav, debugInfo));
                    return;
                }

                try
                {
                    var historyId = _historyStore?.Add("Ativação por voz (Jey)", string.Empty, transcript ?? string.Empty,
                        source: GetConversionSource(), isVoiceTriggered: true, voiceTranscript: transcript,
                        voiceAudioWav: audioWav,
                        voiceDurationMilliseconds: debugInfo?.DurationMilliseconds,
                        voiceCutMilliseconds: debugInfo?.CutMilliseconds,
                        voiceTranscriptionAudioWav: debugInfo?.TranscriptionAudioWav,
                        portugueseTranscriptionTimeMilliseconds: debugInfo?.PortugueseTranscriptionTimeMilliseconds,
                        englishTranscriptionTimeMilliseconds: debugInfo?.EnglishTranscriptionTimeMilliseconds);
                    RefreshHistory();
                    if (wakeWordOnly)
                    {
                        Action<string?, byte[], VoiceCaptureDebugInfo?>? followupHandler = null;
                        followupHandler = (followupTranscript, completeAudio, followupDebugInfo) => Dispatcher.BeginInvoke(new Action(() =>
                        {
                            service.FollowupSpeechCaptured -= followupHandler;
                            if (!_settings.VoiceWakeWordEnabled || !ReferenceEquals(_voiceWakeWord, service) || historyId is null) return;
                            try
                            {
                                _historyStore?.UpdateVoiceCapture(historyId.Value, followupTranscript, completeAudio,
                                    followupDebugInfo?.DurationMilliseconds, followupDebugInfo?.CutMilliseconds,
                                    followupDebugInfo?.TranscriptionAudioWav,
                                    followupDebugInfo?.PortugueseTranscriptionTimeMilliseconds,
                                    followupDebugInfo?.EnglishTranscriptionTimeMilliseconds);
                                RefreshHistory();
                            }
                            catch (Exception exception)
                            {
                                SetStatus($"Jey foi reconhecido, mas não foi possível atualizar a gravação: {exception.Message}", false);
                            }
                        }));
                        service.FollowupSpeechCaptured += followupHandler;
                    }
                }
                catch (Exception exception)
                {
                    SetStatus($"Jey foi reconhecido, mas não foi possível salvar no histórico: {exception.Message}", false);
                }
                if (IsVisible)
                {
                    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                    Activate();
                }
                else if (wakeWordOnly)
                {
                    ShowPalette();
                }
                else
                {
                    JeyMenuItem_Click(this, new RoutedEventArgs());
                }
            }
        }));
        service.Start();
    }

    private void StopVoiceWakeWord()
    {
        _voiceWakeWord?.Dispose();
        _voiceWakeWord = null;
    }

    private void UpdateVoiceWakeWordToggle(bool enabled)
    {
        _updatingVoiceWakeWordToggle = true;
        VoiceWakeWordToggle.IsChecked = enabled;
        _updatingVoiceWakeWordToggle = false;
    }

    private void HistoryButton_Click(object sender, RoutedEventArgs e)
        => ShowHistory();

    public void ShowHistory()
    {
        _navigation.ShowHistory();
        CommandsList.Visibility = Visibility.Collapsed;
        SettingsTabs.Visibility = Visibility.Visible;
        SettingsPage.Visibility = Visibility.Collapsed;
        SqlInTestPage.Visibility = Visibility.Collapsed;
        SnippetManagementPage.Visibility = Visibility.Collapsed;
        HistoryPage.Visibility = Visibility.Visible;
        AboutPage.Visibility = Visibility.Collapsed;
        JeySettingsPage.Visibility = Visibility.Collapsed;
        AdvancedSettingsPage.Visibility = Visibility.Collapsed;
        SettingsRetryButton.Visibility = Visibility.Collapsed;
        FooterHints.Visibility = Visibility.Visible;
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

        _settings.KeepAwakeEnabled = enabled;
        try
        {
            _settingsStore.Save(_settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"A opção foi alterada, mas não foi possível salvar a preferência: {exception.Message}", false);
            return;
        }

        SetStatus(enabled
            ? "Solicitado ao Windows que mantenha a tela e o sistema ativos enquanto o JMD estiver aberto."
            : "A solicitação para manter a tela ativa foi removida.", true);
    }

    private void VoiceWakeWordToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingVoiceWakeWordToggle || sender is not CheckBox toggle) return;
        SetVoiceWakeWordEnabled(toggle.IsChecked == true);
    }

    private void VoiceDebugToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingVoiceDebugToggle || sender is not CheckBox toggle) return;
        _settings.VoiceDebugEnabled = toggle.IsChecked == true;
        if (_voiceWakeWord is not null) _voiceWakeWord.DebugEnabled = _settings.VoiceDebugEnabled;
        try
        {
            _settingsStore.Save(_settings);
            SetStatus(_settings.VoiceDebugEnabled ? "Histórico detalhado de áudio ativado." : "Histórico detalhado de áudio desativado.", true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _updatingVoiceDebugToggle = true;
            toggle.IsChecked = !_settings.VoiceDebugEnabled;
            _settings.VoiceDebugEnabled = toggle.IsChecked == true;
            _updatingVoiceDebugToggle = false;
            if (_voiceWakeWord is not null) _voiceWakeWord.DebugEnabled = _settings.VoiceDebugEnabled;
            SetStatus($"Não foi possível salvar a opção de depuração do Jey: {exception.Message}", false);
        }
    }

    private void VoiceRecognitionDebugToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingVoiceRecognitionDebugToggle || sender is not CheckBox toggle) return;
        var enabled = toggle.IsChecked == true;
        _settings.VoiceWakeRecognitionDebugEnabled = enabled;
        VoiceRecognitionDebugPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (_voiceWakeWord is not null) _voiceWakeWord.WakeWordRecognitionDebugEnabled = enabled;
        if (enabled)
        {
            _wakeRecognitionHistory.Clear();
            _wakeRecognitionPartial = string.Empty;
            UpdateVoiceRecognitionDebugText();
        }
        try
        {
            _settingsStore.Save(_settings);
            SetStatus(enabled ? "Depuração de reconhecimento ativada." : "Depuração de reconhecimento desativada.", true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _settings.VoiceWakeRecognitionDebugEnabled = !enabled;
            _updatingVoiceRecognitionDebugToggle = true;
            toggle.IsChecked = !enabled;
            _updatingVoiceRecognitionDebugToggle = false;
            VoiceRecognitionDebugPanel.Visibility = !enabled ? Visibility.Visible : Visibility.Collapsed;
            if (_voiceWakeWord is not null) _voiceWakeWord.WakeWordRecognitionDebugEnabled = !enabled;
            SetStatus($"Não foi possível salvar a opção de depuração: {exception.Message}", false);
        }
    }

    private void ClosedWakeWordGrammarToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingJeyModelSettings || sender is not CheckBox toggle) return;
        var enabled = toggle.IsChecked == true;
        var previous = _settings.JeyWakeWordClosedGrammarEnabled;
        if (enabled == previous) return;

        _settings.JeyWakeWordClosedGrammarEnabled = enabled;
        try
        {
            if (_settings.VoiceWakeWordEnabled) StartVoiceWakeWord();
            _settingsStore.Save(_settings);
            SetStatus(enabled
                ? "Gramática fechada do Vosk de ativação ativada."
                : "Gramática fechada do Vosk de ativação desativada.", true);
        }
        catch (Exception exception)
        {
            _settings.JeyWakeWordClosedGrammarEnabled = previous;
            _loadingJeyModelSettings = true;
            toggle.IsChecked = previous;
            _loadingJeyModelSettings = false;
            if (_settings.VoiceWakeWordEnabled)
            {
                try { StartVoiceWakeWord(); }
                catch { }
            }
            SetStatus($"Não foi possível aplicar a gramática fechada: {exception.Message}", false);
        }
    }

    private void ClosedCommandGrammarToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingJeyModelSettings || sender is not CheckBox toggle) return;
        var enabled = toggle.IsChecked == true;
        var previous = _settings.JeyCommandClosedGrammarEnabled;
        if (enabled == previous) return;

        _settings.JeyCommandClosedGrammarEnabled = enabled;
        try
        {
            if (_settings.VoiceWakeWordEnabled) StartVoiceWakeWord();
            _settingsStore.Save(_settings);
            SetStatus(enabled
                ? "Gramática de comandos ativada; Sherpa usa hotwords e Vosk usa vocabulário restrito."
                : "Gramática de comandos desativada.", true);
        }
        catch (Exception exception)
        {
            _settings.JeyCommandClosedGrammarEnabled = previous;
            _loadingJeyModelSettings = true;
            toggle.IsChecked = previous;
            _loadingJeyModelSettings = false;
            if (_settings.VoiceWakeWordEnabled)
            {
                try { StartVoiceWakeWord(); }
                catch { }
            }
            SetStatus($"Não foi possível aplicar a gramática fechada aos comandos: {exception.Message}", false);
        }
    }

    private void CommandMinimumConfidenceSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var value = (int)Math.Round(e.NewValue);
        UpdateCommandMinimumConfidenceText(value);
        if (_loadingJeyModelSettings || sender is not Slider) return;

        var previous = _settings.JeyCommandMinimumConfidencePercent;
        if (value == previous) return;
        _settings.JeyCommandMinimumConfidencePercent = value;
        if (_voiceWakeWord is not null) _voiceWakeWord.MinimumCommandConfidencePercent = value;
        try
        {
            _settingsStore.Save(_settings);
            SetStatus(value == 0
                ? "Confiança mínima dos comandos desativada."
                : $"Confiança mínima dos comandos ajustada para {value}%.", true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _settings.JeyCommandMinimumConfidencePercent = previous;
            if (_voiceWakeWord is not null) _voiceWakeWord.MinimumCommandConfidencePercent = previous;
            _loadingJeyModelSettings = true;
            CommandMinimumConfidenceSlider.Value = previous;
            _loadingJeyModelSettings = false;
            SetStatus($"Não foi possível salvar a confiança mínima dos comandos: {exception.Message}", false);
        }
    }

    private void UpdateCommandMinimumConfidenceText(int value)
        => CommandMinimumConfidenceText.Text = value == 0 ? "Desativada" : $"{value}%";

    private void WakeWordMinimumConfidenceSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var value = (int)Math.Round(e.NewValue);
        UpdateWakeWordMinimumConfidenceText(value);
        if (_loadingJeyModelSettings || sender is not Slider) return;

        var previous = _settings.JeyWakeWordMinimumConfidencePercent;
        if (value == previous) return;
        _settings.JeyWakeWordMinimumConfidencePercent = value;
        if (_voiceWakeWord is not null) _voiceWakeWord.MinimumWakeWordConfidencePercent = value;
        try
        {
            _settingsStore.Save(_settings);
            SetStatus(value == 0
                ? "Limite mínimo de confiança desativado."
                : $"Confiança mínima da ativação ajustada para {value}%.", true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _settings.JeyWakeWordMinimumConfidencePercent = previous;
            if (_voiceWakeWord is not null) _voiceWakeWord.MinimumWakeWordConfidencePercent = previous;
            _loadingJeyModelSettings = true;
            WakeWordMinimumConfidenceSlider.Value = previous;
            _loadingJeyModelSettings = false;
            SetStatus($"Não foi possível salvar a confiança mínima: {exception.Message}", false);
        }
    }

    private void UpdateWakeWordMinimumConfidenceText(int value)
        => WakeWordMinimumConfidenceText.Text = value == 0 ? "Desativada" : $"{value}%";

    private void ClearVoiceRecognitionDebugHistory_Click(object sender, RoutedEventArgs e)
    {
        _wakeRecognitionHistory.Clear();
        _wakeRecognitionPartial = string.Empty;
        UpdateVoiceRecognitionDebugText();
    }

    private void AddWakeRecognitionHistoryEntry(string message, DateTimeOffset? audioCapturedAt = null)
    {
        if (_wakeRecognitionHistory.Count >= MaxWakeRecognitionHistoryEntries)
            _wakeRecognitionHistory.RemoveAt(_wakeRecognitionHistory.Count - 1);
        var timestamp = (audioCapturedAt ?? DateTimeOffset.Now).ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        _wakeRecognitionHistory.Insert(0, new WakeRecognitionHistoryRow(timestamp, message));
        VoiceRecognitionDebugHistory.UpdateLayout();
        VoiceRecognitionDebugScrollViewer.ScrollToTop();
    }

    private static string FormatTranscriptionAttempt(VoiceTranscriptionAttemptDebugInfo attempt)
    {
        var result = !string.IsNullOrWhiteSpace(attempt.Error)
            ? $"erro: {attempt.Error}"
            : string.IsNullOrWhiteSpace(attempt.Transcript) ? "sem transcrição" : $"texto: {attempt.Transcript}";
        var confidence = attempt.Confidence is { } value
            ? value.ToString("P0", CultureInfo.CurrentCulture)
            : "indisponível";
        var selected = attempt.Selected ? " · selecionado" : string.Empty;
        var elapsed = attempt.ElapsedMilliseconds is { } milliseconds ? $" · tempo: {milliseconds} ms" : string.Empty;
        return $"{attempt.Language} · {attempt.Engine}{elapsed}{selected} · confiança estimada: {confidence} · {result}";
    }

    private static string FormatElapsed(int? milliseconds)
        => milliseconds is { } value ? $"{value} ms" : "indisponível";

    private void UpdateVoiceRecognitionDebugText()
    {
        VoiceRecognitionDebugText.Text = string.IsNullOrWhiteSpace(_wakeRecognitionPartial)
            ? "Parcial: (aguardando)"
            : $"Parcial: {_wakeRecognitionPartial}";
    }

    private static void SelectVoiceModel(ComboBox combo, string model)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, model, StringComparison.Ordinal)) ?? combo.Items[0];
    }

    private void UpdateVoiceModelStatuses()
    {
        UpdateWhisperModelStatus("whisperTiny", WhisperTinyStatusText, DownloadWhisperTinyButton, DeleteWhisperTinyButton);
        UpdateWhisperModelStatus("whisperBase", WhisperBaseStatusText, DownloadWhisperBaseButton, DeleteWhisperBaseButton);
        var gemmaReady = Gemma4Transcriber.IsModelDownloaded;
        var gemmaFilesExist = File.Exists(Gemma4Transcriber.ModelPath) || File.Exists(Gemma4Transcriber.AudioProjectorPath);
        GemmaStatusText.Text = gemmaReady
            ? File.Exists(Gemma4Transcriber.ExecutablePath) ? "Modelo e runtime prontos." : "Modelo baixado; runtime indisponível nesta instalação."
            : gemmaFilesExist ? "Download parcial detectado; remova e tente novamente." : "Ainda não baixado.";
        DownloadGemmaButton.IsEnabled = _downloadingVoiceModel is null && !gemmaReady;
        DeleteGemmaButton.IsEnabled = _downloadingVoiceModel is null && gemmaFilesExist &&
            _settings.JeyPortugueseModel != "gemma4E2B" && _settings.JeyEnglishModel != "gemma4E2B";
        UpdateSherpaModelStatus(VoiceModelOptions.SherpaParakeetPtBr, SherpaPtStatusText, DownloadSherpaPtButton, DeleteSherpaPtButton);
        UpdateSherpaModelStatus(VoiceModelOptions.SherpaZipformerEn, SherpaEnStatusText, DownloadSherpaEnButton, DeleteSherpaEnButton);
        DownloadWhisperTinyButton.IsEnabled &= File.Exists(WhisperCppTranscriber.ExecutablePath);
        DownloadWhisperBaseButton.IsEnabled &= File.Exists(WhisperCppTranscriber.ExecutablePath);
    }

    private void UpdateSherpaModelStatus(string model, TextBlock status, Button download, Button delete)
    {
        var downloaded = SherpaParakeetTranscriber.IsModelDownloaded(model);
        status.Text = downloaded ? "Modelo verificado e instalado." : SherpaParakeetTranscriber.HasAnyModelFiles(model) ? "Download parcial detectado." : "Ainda não baixado.";
        download.IsEnabled = _downloadingVoiceModel is null && !downloaded;
        delete.IsEnabled = _downloadingVoiceModel is null && SherpaParakeetTranscriber.HasAnyModelFiles(model) &&
            _settings.JeyPortugueseModel != model && _settings.JeyEnglishModel != model;
    }

    private void UpdateWhisperModelStatus(string model, TextBlock status, Button download, Button delete)
    {
        var downloaded = WhisperCppTranscriber.IsModelDownloaded(model);
        status.Text = downloaded ? "Modelo verificado e instalado." : "Ainda não baixado.";
        download.IsEnabled = _downloadingVoiceModel is null && !downloaded;
        delete.IsEnabled = _downloadingVoiceModel is null && downloaded &&
            _settings.JeyPortugueseModel != model && _settings.JeyEnglishModel != model;
    }

    private void JeyTranscriptionModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingJeyModelSettings || sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem selected) return;
        var requestedModel = selected.Tag as string ?? "vosk";
        if (requestedModel is "whisperTiny" or "whisperBase" && !File.Exists(WhisperCppTranscriber.ExecutablePath))
        {
            SetVoiceModelStatus(requestedModel, "Atualize o JMD para incluir o mecanismo Whisper antes de selecionar este modelo.");
            _loadingJeyModelSettings = true;
            SelectVoiceModel(combo, "vosk");
            _loadingJeyModelSettings = false;
            return;
        }
        if (requestedModel is "whisperTiny" or "whisperBase" && !WhisperCppTranscriber.IsModelDownloaded(requestedModel))
        {
            SetVoiceModelStatus(requestedModel, $"Baixe {GetVoiceModelName(requestedModel)} para habilitar esta opção.");
            _loadingJeyModelSettings = true;
            SelectVoiceModel(combo, "vosk");
            _loadingJeyModelSettings = false;
            return;
        }
        if (requestedModel == "gemma4E2B" && (!File.Exists(Gemma4Transcriber.ExecutablePath) || !Gemma4Transcriber.IsModelDownloaded))
        {
            SetVoiceModelStatus(requestedModel, !File.Exists(Gemma4Transcriber.ExecutablePath)
                ? "O runtime experimental Gemma não está nesta instalação."
                : "Baixe Gemma E2B e o codificador de áudio antes de selecioná-lo.");
            _loadingJeyModelSettings = true;
            SelectVoiceModel(combo, "vosk");
            _loadingJeyModelSettings = false;
            return;
        }
        if (requestedModel is VoiceModelOptions.SherpaParakeetPtBr or VoiceModelOptions.SherpaZipformerEn && !SherpaParakeetTranscriber.IsModelDownloaded(requestedModel))
        {
            SetVoiceModelStatus(requestedModel, $"Baixe {GetVoiceModelName(requestedModel)} para habilitar esta opção.");
            _loadingJeyModelSettings = true;
            SelectVoiceModel(combo, "vosk");
            _loadingJeyModelSettings = false;
            return;
        }

        var settingName = ReferenceEquals(combo, PortugueseModelCombo) ? "pt" : "en";
        var previousModel = settingName == "pt" ? _settings.JeyPortugueseModel : _settings.JeyEnglishModel;
        if (settingName == "pt") _settings.JeyPortugueseModel = requestedModel;
        else _settings.JeyEnglishModel = requestedModel;
        try
        {
            if (_settings.VoiceWakeWordEnabled) StartVoiceWakeWord();
            _settingsStore.Save(_settings);
            SetStatus($"Modelo de transcrição {settingName} atualizado.", true);
        }
        catch (Exception exception)
        {
            if (settingName == "pt") _settings.JeyPortugueseModel = previousModel;
            else _settings.JeyEnglishModel = previousModel;
            _loadingJeyModelSettings = true;
            SelectVoiceModel(combo, previousModel);
            _loadingJeyModelSettings = false;
            if (_settings.VoiceWakeWordEnabled)
            {
                try { StartVoiceWakeWord(); } catch { }
            }
            SetStatus($"Não foi possível aplicar o modelo de transcrição: {exception.Message}", false);
        }
    }

    private async void DownloadVoiceModel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string model } || _downloadingVoiceModel is not null) return;
        _downloadingVoiceModel = model;
        var (status, progressBar) = GetVoiceModelControls(model);
        progressBar.Value = 0;
        progressBar.Visibility = Visibility.Visible;
        status.Text = "Conectando ao repositório do modelo…";
        UpdateVoiceModelStatuses();
        var progress = new Progress<WhisperDownloadProgress>(value =>
        {
            if (_downloadingVoiceModel != model) return;
            status.Text = value.Fraction is { } fraction
                ? $"{value.Message} · {fraction:P0}"
                : value.Message;
            progressBar.IsIndeterminate = value.Fraction is null;
            if (value.Fraction is { } current) progressBar.Value = current * 100;
        });
        string? downloadErrorMessage = null;
        try
        {
            if (model == "gemma4E2B")
            {
                var root = Path.GetPathRoot(Gemma4Transcriber.ModelPath);
                if (root is not null && new DriveInfo(root).AvailableFreeSpace < Gemma4Transcriber.EstimatedDownloadBytes + 256L * 1024 * 1024)
                    throw new IOException("São necessários pelo menos 3,5 GiB livres para baixar Gemma E2B.");
                await Gemma4Transcriber.DownloadModelAsync(progress);
            }
            else if (model is VoiceModelOptions.SherpaParakeetPtBr or VoiceModelOptions.SherpaZipformerEn)
            {
                var root = Path.GetPathRoot(SherpaParakeetTranscriber.ModelDirectoryPath(model));
                var expectedBytes = model == VoiceModelOptions.SherpaParakeetPtBr
                    ? SherpaModelDownloads.EstimatedDownloadBytes
                    : SherpaModelDownloads.EnglishEstimatedDownloadBytes;
                if (root is not null && new DriveInfo(root).AvailableFreeSpace < expectedBytes + 128L * 1024 * 1024)
                    throw new IOException($"São necessários pelo menos {expectedBytes / (1024 * 1024)} MiB livres para baixar este modelo Sherpa.");
                await SherpaParakeetTranscriber.DownloadModelAsync(model, progress);
            }
            else
            {
                await WhisperCppTranscriber.DownloadModelAsync(model, progress);
            }
            progressBar.IsIndeterminate = false;
            progressBar.Value = 100;
            status.Text = $"{GetVoiceModelName(model)} instalado e verificado. Selecione-o para português ou inglês.";
        }
        catch (Exception exception)
        {
            progressBar.Visibility = Visibility.Collapsed;
            downloadErrorMessage = exception is InvalidDataException
                ? $"O checksum não confere e o arquivo incompleto foi descartado. {exception.Message}"
                : $"Não foi possível instalar {GetVoiceModelName(model)}: {exception.Message}";
            status.Text = downloadErrorMessage;
        }
        finally
        {
            _downloadingVoiceModel = null;
            UpdateVoiceModelStatuses();
            SetVoiceModelStatus(model, VoiceModelDownloadFeedback.AfterStatusRefresh(
                downloadErrorMessage, status.Text));
        }
    }

    private void DeleteVoiceModel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string model }) return;
        if (_settings.JeyPortugueseModel == model || _settings.JeyEnglishModel == model)
        {
            SetVoiceModelStatus(model, $"Selecione outro modelo para {GetVoiceModelName(model)} antes de removê-lo.");
            return;
        }
        try
        {
            if (model == "gemma4E2B")
            {
                if (File.Exists(Gemma4Transcriber.ModelPath)) File.Delete(Gemma4Transcriber.ModelPath);
                if (File.Exists(Gemma4Transcriber.AudioProjectorPath)) File.Delete(Gemma4Transcriber.AudioProjectorPath);
            }
            else if (model is VoiceModelOptions.SherpaParakeetPtBr or VoiceModelOptions.SherpaZipformerEn)
            {
                var directory = SherpaParakeetTranscriber.ModelDirectoryPath(model);
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            else if (File.Exists(WhisperCppTranscriber.ModelPath(model)))
                File.Delete(WhisperCppTranscriber.ModelPath(model));
            UpdateVoiceModelStatuses();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetVoiceModelStatus(model, $"Não foi possível remover {GetVoiceModelName(model)}: {exception.Message}");
        }
    }

    private (TextBlock Status, System.Windows.Controls.ProgressBar Progress) GetVoiceModelControls(string model) => model switch
    {
        "whisperTiny" => (WhisperTinyStatusText, WhisperTinyDownloadProgress),
        "whisperBase" => (WhisperBaseStatusText, WhisperBaseDownloadProgress),
        "gemma4E2B" => (GemmaStatusText, GemmaDownloadProgress),
        "sherpaParakeetPtBr" => (SherpaPtStatusText, SherpaPtDownloadProgress),
        "sherpaZipformerEn" => (SherpaEnStatusText, SherpaEnDownloadProgress),
        _ => throw new ArgumentOutOfRangeException(nameof(model), model, "Modelo não pode ser baixado.")
    };

    private void SetVoiceModelStatus(string model, string message)
    {
        if (model == "gemma4E2B") GemmaStatusText.Text = message;
        else if (model == "whisperTiny") WhisperTinyStatusText.Text = message;
        else if (model == "whisperBase") WhisperBaseStatusText.Text = message;
        else if (model == VoiceModelOptions.SherpaParakeetPtBr) SherpaPtStatusText.Text = message;
        else if (model == VoiceModelOptions.SherpaZipformerEn) SherpaEnStatusText.Text = message;
    }

    private static string GetVoiceModelName(string model) => model switch
    {
        "whisperTiny" => "Whisper Tiny",
        "whisperBase" => "Whisper Base",
        "gemma4E2B" => "Gemma E2B",
        "sherpaParakeetPtBr" => "Sherpa Parakeet pt-BR",
        "sherpaZipformerEn" => "Sherpa Zipformer inglês",
        _ => "Vosk"
    };

    private void SelectBenchmarkAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_benchmarkRecorder is not null || _benchmarkRunning) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Selecionar áudio base para o benchmark",
            Filter = "Arquivos de áudio|*.wav;*.mp3;*.wma;*.aiff;*.aif|Todos os arquivos|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        bool? accepted;
        _showingDialog = true;
        try { accepted = dialog.ShowDialog(this); }
        finally { _showingDialog = false; }
        if (accepted != true) return;
        _benchmarkAudioPath = dialog.FileName;
        DeleteBenchmarkRecordingFile();
        BenchmarkAudioPathText.Text = Path.GetFileName(dialog.FileName);
        BenchmarkAudioPathText.ToolTip = dialog.FileName;
        BenchmarkResultText.Text = "Áudio selecionado. O trecho deve ter até 30 segundos.";
    }

    private void RecordBenchmarkAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_benchmarkRecorder is not null)
        {
            StopBenchmarkRecording();
            return;
        }
        if (_benchmarkRunning) return;

        try
        {
            if (_settings.VoiceWakeWordEnabled) StopVoiceWakeWord();
            var buffer = new MemoryStream();
            var recorder = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 100
            };
            recorder.DataAvailable += BenchmarkRecording_DataAvailable;
            recorder.RecordingStopped += BenchmarkRecording_Stopped;
            _benchmarkRecordingBuffer = buffer;
            _benchmarkRecorder = recorder;
            _benchmarkRecordingStartedAt = DateTimeOffset.Now;
            _benchmarkRecordingTimer.Start();
            RecordBenchmarkAudioButton.Content = "Parar gravação";
            SelectBenchmarkAudioButton.IsEnabled = false;
            RunBenchmarkButton.IsEnabled = false;
            BenchmarkAudioPathText.Text = "Gravando… 30 s restantes";
            BenchmarkResultText.Text = _settings.VoiceWakeWordEnabled
                ? "Gravação em andamento. A escuta de ativação Jey será retomada ao terminar."
                : "Gravação em andamento.";
            recorder.StartRecording();
        }
        catch (Exception exception)
        {
            CleanupBenchmarkRecorder();
            ResumeVoiceWakeWordAfterBenchmarkRecording();
            BenchmarkResultText.Text = $"Não foi possível iniciar a gravação: {exception.Message}";
        }
    }

    private void BenchmarkRecording_DataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        lock (_benchmarkRecordingLock)
            _benchmarkRecordingBuffer?.Write(e.Buffer, 0, e.BytesRecorded);
    }

    private void BenchmarkRecordingTimer_Tick(object? sender, EventArgs e)
    {
        var elapsed = DateTimeOffset.Now - _benchmarkRecordingStartedAt;
        var remainingSeconds = Math.Max(0, (int)Math.Ceiling(VoiceModelOptions.GemmaMaximumAudioDuration.TotalSeconds - elapsed.TotalSeconds));
        BenchmarkAudioPathText.Text = $"Gravando… {remainingSeconds} s restantes";
        if (remainingSeconds == 0) StopBenchmarkRecording();
    }

    private void StopBenchmarkRecording()
    {
        var recorder = _benchmarkRecorder;
        if (recorder is null) return;
        RecordBenchmarkAudioButton.IsEnabled = false;
        BenchmarkResultText.Text = "Finalizando gravação…";
        try { recorder.StopRecording(); }
        catch (Exception exception)
        {
            CleanupBenchmarkRecorder();
            ResumeVoiceWakeWordAfterBenchmarkRecording();
            BenchmarkResultText.Text = $"A gravação não pôde ser finalizada: {exception.Message}";
        }
    }

    private void BenchmarkRecording_Stopped(object? sender, StoppedEventArgs e)
        => Dispatcher.BeginInvoke(new Action(() =>
        {
            byte[] pcm;
            lock (_benchmarkRecordingLock)
                pcm = _benchmarkRecordingBuffer?.ToArray() ?? [];
            CleanupBenchmarkRecorder();
            ResumeVoiceWakeWordAfterBenchmarkRecording();
            if (e.Exception is not null)
            {
                BenchmarkResultText.Text = $"A gravação falhou: {e.Exception.Message}";
                return;
            }
            if (pcm.Length < VoiceModelOptions.GemmaPcmBytesPerSecond / 2)
            {
                BenchmarkResultText.Text = "Gravação muito curta ou sem áudio. Tente novamente.";
                return;
            }

            try
            {
                DeleteBenchmarkRecordingFile();
                _benchmarkRecordedAudioPath = VoiceModelBenchmark.WriteTemporaryWave(pcm);
                _benchmarkAudioPath = _benchmarkRecordedAudioPath;
                BenchmarkAudioPathText.Text = $"Gravação pronta · {pcm.Length / (double)VoiceModelOptions.GemmaPcmBytesPerSecond:N1} s";
                BenchmarkAudioPathText.ToolTip = "Áudio gravado para este benchmark";
                BenchmarkResultText.Text = "Gravação pronta para comparar. Escolha o idioma e inicie o benchmark.";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                BenchmarkResultText.Text = $"Não foi possível salvar a gravação temporária: {exception.Message}";
            }
        }));

    private void CleanupBenchmarkRecorder()
    {
        _benchmarkRecordingTimer.Stop();
        if (_benchmarkRecorder is { } recorder)
        {
            recorder.DataAvailable -= BenchmarkRecording_DataAvailable;
            recorder.RecordingStopped -= BenchmarkRecording_Stopped;
            recorder.Dispose();
        }
        _benchmarkRecorder = null;
        lock (_benchmarkRecordingLock)
        {
            _benchmarkRecordingBuffer?.Dispose();
            _benchmarkRecordingBuffer = null;
        }
        RecordBenchmarkAudioButton.Content = "Gravar áudio · até 30 s";
        RecordBenchmarkAudioButton.IsEnabled = true;
        SelectBenchmarkAudioButton.IsEnabled = true;
        RunBenchmarkButton.IsEnabled = true;
    }

    private void ResumeVoiceWakeWordAfterBenchmarkRecording()
    {
        if (!_settings.VoiceWakeWordEnabled || _voiceWakeWord is not null || _closingForExit) return;
        try { StartVoiceWakeWord(); }
        catch (Exception exception) { SetStatus($"A gravação terminou, mas a escuta Jey não reiniciou: {exception.Message}", false); }
    }

    private void DeleteBenchmarkRecordingFile()
    {
        if (_benchmarkRecordedAudioPath is null) return;
        var directory = Path.GetDirectoryName(_benchmarkRecordedAudioPath);
        try { if (File.Exists(_benchmarkRecordedAudioPath)) File.Delete(_benchmarkRecordedAudioPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        try { if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (_benchmarkAudioPath == _benchmarkRecordedAudioPath) _benchmarkAudioPath = null;
        _benchmarkRecordedAudioPath = null;
    }

    private async void RunVoiceBenchmark_Click(object sender, RoutedEventArgs e)
    {
        if (_benchmarkRecorder is not null) return;
        if (_benchmarkRunning) return;
        if (string.IsNullOrWhiteSpace(_benchmarkAudioPath) || !File.Exists(_benchmarkAudioPath))
        {
            BenchmarkResultText.Text = "Escolha primeiro um arquivo de áudio base.";
            return;
        }
        var language = (BenchmarkLanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "pt";
        _benchmarkRunning = true;
        RunBenchmarkButton.IsEnabled = false;
        SelectBenchmarkAudioButton.IsEnabled = false;
        BenchmarkProgress.Visibility = Visibility.Visible;
        BenchmarkResultText.Text = "Preparando o áudio…";
        var progress = new Progress<string>(message => BenchmarkResultText.Text = message);
        try
        {
            var report = await new VoiceModelBenchmark().RunAsync(_benchmarkAudioPath, language, progress);
            BenchmarkResultText.Text = report;
        }
        catch (Exception exception)
        {
            BenchmarkResultText.Text = $"O benchmark não pôde ser concluído: {exception.Message}";
        }
        finally
        {
            _benchmarkRunning = false;
            BenchmarkProgress.Visibility = Visibility.Collapsed;
            RunBenchmarkButton.IsEnabled = true;
            SelectBenchmarkAudioButton.IsEnabled = true;
        }
    }

    private void RefreshMemoryStatus()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var appMib = process.WorkingSet64 / (1024d * 1024d);
            var externalBytes = TranscriptionMemoryStatus.HasActiveExternalModels
                ? TranscriptionMemoryStatus.CurrentExternalModelBytes
                : TranscriptionMemoryStatus.LastExternalModelPeakBytes;
            var label = TranscriptionMemoryStatus.HasActiveExternalModels ? "agora" : "pico externo";
            FooterMemoryText.Text = $"RAM JMD+Vosk {appMib:N0} MiB · {label} {externalBytes / (1024d * 1024d):N0} MiB";
        }
        catch (InvalidOperationException)
        {
            FooterMemoryText.Text = "RAM indisponível";
        }
    }

    private void JeyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _navigation.ShowJeySettings();
        CommandsList.Visibility = Visibility.Collapsed;
        SettingsTabs.Visibility = Visibility.Collapsed;
        SettingsRetryButton.Visibility = Visibility.Collapsed;
        FooterHints.Visibility = Visibility.Visible;
        AboutPage.Visibility = Visibility.Collapsed;
        JeySettingsPage.Visibility = Visibility.Visible;
        SearchBox.Visibility = Visibility.Collapsed;
        ManageButton.Content = "Voltar aos comandos";
        PageTitle.Text = "Jey";
        PageSubtitle.Text = "Opções para comandos de voz";
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ShowAboutPage();
    }

    private void AdvancedSettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        SettingsTabs.Visibility = Visibility.Visible;
        SettingsPage.Visibility = Visibility.Collapsed;
        SqlInTestPage.Visibility = Visibility.Collapsed;
        SnippetManagementPage.Visibility = Visibility.Collapsed;
        HistoryPage.Visibility = Visibility.Collapsed;
        AdvancedSettingsPage.Visibility = Visibility.Visible;
        CommandsList.Visibility = Visibility.Collapsed;
        SettingsRetryButton.Visibility = Visibility.Collapsed;
        FooterHints.Visibility = Visibility.Visible;
        SearchBox.Visibility = Visibility.Collapsed;
        ManageButton.Content = "Voltar aos comandos";
        PageTitle.Text = "Configurações avançadas";
        PageSubtitle.Text = "Ajustes de comportamento do JMD";
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void AdvancedNotificationsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingAdvancedNotificationToggle) return;
        _settings.UseJmdNotifications = AdvancedNotificationsToggle.IsChecked == true;
        try
        {
            _settingsStore.Save(_settings);
            SetStatus(_settings.UseJmdNotifications
                ? "Notificações próprias do JMD ativadas."
                : "Notificações padrão do Windows ativadas.", true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Não foi possível salvar a preferência de notificação: {exception.Message}", false);
        }
    }

    private void ShowAboutPage()
    {
        _navigation.ShowAbout();
        CommandsList.Visibility = Visibility.Collapsed;
        SettingsTabs.Visibility = Visibility.Collapsed;
        SettingsRetryButton.Visibility = Visibility.Collapsed;
        FooterHints.Visibility = Visibility.Visible;
        AboutPage.Visibility = Visibility.Visible;
        JeySettingsPage.Visibility = Visibility.Collapsed;
        SearchBox.Visibility = Visibility.Collapsed;
        ManageButton.Content = "Voltar aos comandos";
        PageTitle.Text = "Sobre";
        PageSubtitle.Text = "Informações e atualizações do JMD";
        var version = GetInformationalVersion();
        AboutVersionText.Text = $"Versão instalada: {version}";
        UpdateStatusText.Text = "Verifique se há uma versão mais recente no GitHub.";
        InstallUpdateButton.Visibility = Visibility.Collapsed;
    }

    private static string GetInstalledVersion()
        => GetInformationalVersion().Split('+')[0];

    private static string GetInformationalVersion()
        => Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
           ?? "desconhecida";

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
        => await CheckForUpdatesAsync(installWhenAvailable: false);

    public async Task UpdateFromCommandLineAsync()
    {
        ShowAboutPage();
        await CheckForUpdatesAsync(installWhenAvailable: true);
    }

    private async Task CheckForUpdatesAsync(bool installWhenAvailable)
    {
        if (_checkingUpdates) return;
        _checkingUpdates = true;
        var shouldInstall = false;
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
                if (installWhenAvailable)
                    shouldInstall = true;
                else
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
            _checkingUpdates = false;
        }
        if (shouldInstall) await InstallUpdateAsync();
    }

    private async void InstallUpdateButton_Click(object sender, RoutedEventArgs e)
        => await InstallUpdateAsync();

    private async Task InstallUpdateAsync()
    {
        if (_checkingUpdates) return;
        _checkingUpdates = true;
        InstallUpdateButton.IsEnabled = false;
        string? downloadDirectory = null;
        var installerStarted = false;
        try
        {
            UpdateStatusText.Text = "Baixando e validando o instalador…";
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("JMD-App");
            using var releaseResponse = await client.GetAsync("https://api.github.com/repos/deggau/jmd/releases/latest");
            releaseResponse.EnsureSuccessStatusCode();
            using var release = JsonDocument.Parse(await releaseResponse.Content.ReadAsStringAsync());

            var root = release.RootElement;
            var tag = root.GetProperty("tag_name").GetString();
            var version = tag?.TrimStart('v', 'V');
            if (version is null || !Version.TryParse(version, out _))
                throw new InvalidDataException("O GitHub retornou uma versão inválida.");

            var installerName = $"JMD-{version}-win-x64-setup.exe";
            var assets = root.GetProperty("assets").EnumerateArray().ToArray();
            var installerAsset = assets.FirstOrDefault(asset =>
                string.Equals(asset.GetProperty("name").GetString(), installerName, StringComparison.Ordinal));
            var checksumAsset = assets.FirstOrDefault(asset =>
                string.Equals(asset.GetProperty("name").GetString(), $"{installerName}.sha256", StringComparison.Ordinal));
            if (installerAsset.ValueKind == JsonValueKind.Undefined || checksumAsset.ValueKind == JsonValueKind.Undefined)
                throw new InvalidDataException("A release não contém o instalador x64 e seu checksum SHA-256.");

            var installerUri = GetGitHubAssetUri(installerAsset);
            var checksumUri = GetGitHubAssetUri(checksumAsset);
            var expectedHashText = await client.GetStringAsync(checksumUri);
            var expectedHash = expectedHashText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (expectedHash is null || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit))
                throw new InvalidDataException("O checksum SHA-256 publicado é inválido.");

            downloadDirectory = Path.Combine(Path.GetTempPath(), $"JMD-Update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(downloadDirectory);
            var installerPath = Path.Combine(downloadDirectory, installerName);
            UpdateDownloadProgress.IsIndeterminate = true;
            UpdateDownloadProgress.Value = 0;
            UpdateDownloadProgress.Visibility = Visibility.Visible;
            UpdateProgressText.Text = "Conectando ao servidor para baixar o instalador…";
            UpdateProgressText.Visibility = Visibility.Visible;
            CheckUpdatesButton.IsEnabled = false;

            using (var installerResponse = await client.GetAsync(installerUri, HttpCompletionOption.ResponseHeadersRead))
            {
                installerResponse.EnsureSuccessStatusCode();
                var totalBytes = installerResponse.Content.Headers.ContentLength;
                await using var installerStream = await installerResponse.Content.ReadAsStreamAsync();
                await using var output = new FileStream(installerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[81920];
                long downloadedBytes = 0;
                int bytesRead;
                while ((bytesRead = await installerStream.ReadAsync(buffer)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, bytesRead));
                    downloadedBytes += bytesRead;
                    if (totalBytes is > 0)
                    {
                        var fraction = Math.Clamp((double)downloadedBytes / totalBytes.Value, 0, 1);
                        UpdateDownloadProgress.IsIndeterminate = false;
                        UpdateDownloadProgress.Value = fraction * 100;
                        UpdateProgressText.Text = $"Baixando instalador… {fraction:P0}";
                    }
                    else
                    {
                        UpdateProgressText.Text = "Baixando instalador…";
                    }
                }
            }

            UpdateDownloadProgress.IsIndeterminate = false;
            UpdateDownloadProgress.Value = 100;
            UpdateProgressText.Text = "Download concluído. Validando checksum SHA-256…";
            await using (var downloadedInstaller = File.OpenRead(installerPath))
            {
                var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(downloadedInstaller));
                if (!CryptographicOperations.FixedTimeEquals(
                        Convert.FromHexString(actualHash), Convert.FromHexString(expectedHash)))
                    throw new InvalidDataException("O checksum SHA-256 do instalador não confere; a atualização foi cancelada.");
            }

            var startedProcess = Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = true
            });
            if (startedProcess is null)
                throw new InvalidOperationException("O Windows não iniciou o instalador.");
            installerStarted = true;
            UpdateProgressText.Text = "Checksum válido. Iniciando o instalador…";
            PrepareForExit();
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            UpdateStatusText.Text = $"Não foi possível iniciar a atualização: {exception.Message}";
        }
        finally
        {
            InstallUpdateButton.IsEnabled = true;
            CheckUpdatesButton.IsEnabled = true;
            _checkingUpdates = false;
            if (!installerStarted)
            {
                UpdateDownloadProgress.Visibility = Visibility.Collapsed;
                UpdateProgressText.Visibility = Visibility.Collapsed;
            }
            if (!installerStarted && downloadDirectory is not null)
            {
                try { Directory.Delete(downloadDirectory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static Uri GetGitHubAssetUri(JsonElement asset)
    {
        var url = asset.GetProperty("browser_download_url").GetString();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A release contém um link de download inválido.");
        return uri;
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
        var result = FormatText(TestInput.Text);
        TestOutput.Text = result.Success ? result.Value! : result.Error ?? "Não foi possível converter a entrada.";
        TestOutput.Foreground = result.Success
            ? new SolidColorBrush(Color.FromRgb(180, 243, 106))
            : new SolidColorBrush(Color.FromRgb(255, 145, 135));
    }

    private void SqlDelimiterPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SqlDelimiterPicker.SelectedItem is not ComboBoxItem selected) return;
        var tag = selected.Tag as string;
        _settings.PreferredDelimiter = tag switch
        {
            "" => null,
            "newline" => "\r\n",
            _ => tag
        };
        try
        {
            _settingsStore.Save(_settings);
            SetStatus(_settings.PreferredDelimiter is null ? "Detecção automática ativada." : "Delimitador preferido salvo.", true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Não foi possível salvar a preferência: {exception.Message}", false);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _navigation.CurrentPage == AppPage.Palette &&
                 (SearchBox.IsKeyboardFocusWithin || CommandsList.IsKeyboardFocusWithin))
        {
            var selected = CommandsList.SelectedItem as CommandRow;
            if (selected is null || !selected.Visible)
                selected = _commands.FirstOrDefault(command => command.Visible);
            if (selected is not null)
            {
                _ = ExecuteCommandAsync(selected.Id);
                e.Handled = true;
            }
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
        if (id == "snippets") await InsertSavedSnippetAsync();
        if (id == "saveSnippet") await CaptureSnippetAsync();
        if (id == "createCustomShortcut") CreateCustomShortcutFromDialog();
        if (_settings.CustomShortcuts.FirstOrDefault(shortcut => shortcut.Id == id) is { } customShortcut)
            OpenCustomShortcut(customShortcut);
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

    private async Task TransformStructuredDataAsync(StructuredDataLayout layout, VoiceCommandInvocation? voiceCommand = null)
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
                RecordSelectionHistory(commandName, result, voiceCommand);
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

    private void RecordSelectionHistory(string commandName, SelectionTransformResult result, VoiceCommandInvocation? voiceCommand = null)
    {
        if (!result.Success || result.BeforeValue is null || result.AfterValue is null || _historyStore is null) return;
        try
        {
            if (voiceCommand is not null)
                RecordCommandHistory(commandName, result.BeforeValue, result.AfterValue, voiceCommand);
            else if (_historyRecorder?.RecordIfSuccessful(commandName, result, GetConversionSource()) == true)
                RefreshHistory();
        }
        catch (Exception exception)
        {
            SetStatus($"Transformação concluída, mas não foi possível salvar no histórico: {exception.Message}", false);
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!IsVisible || _showingDialog) return;
        Hide();
        if (_navigation.IsEditingShortcuts)
        {
            RegisterConfiguredShortcuts();
            RefreshCommandStatuses();
            RefreshShortcutStatuses();
        }
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
        TrayNotification?.Invoke(title, message, _settings.UseJmdNotifications);
    }

    private static string WithConversionCounts(string message, int? identified, int? output)
        => ConversionCountSummary.Append(message, identified, output);

    private void SetStatus(string message, bool success, string? details = null)
    {
        StatusText.Text = message;
        StatusText.ToolTip = details ?? message;
        StatusText.Foreground = success
            ? new SolidColorBrush(Color.FromRgb(180, 243, 106))
            : new SolidColorBrush(Color.FromRgb(255, 145, 135));
    }

    public void PrepareForExit() => _closingForExit = true;

    public void DisposeServices()
    {
        _memoryRefreshTimer.Stop();
        _benchmarkRecordingTimer.Stop();
        if (_benchmarkRecorder is { } recorder)
        {
            recorder.DataAvailable -= BenchmarkRecording_DataAvailable;
            recorder.RecordingStopped -= BenchmarkRecording_Stopped;
            try { recorder.StopRecording(); } catch (InvalidOperationException) { }
            recorder.Dispose();
            _benchmarkRecorder = null;
        }
        lock (_benchmarkRecordingLock)
        {
            _benchmarkRecordingBuffer?.Dispose();
            _benchmarkRecordingBuffer = null;
        }
        DeleteBenchmarkRecordingFile();
        StopVoiceWakeWord();
        StopHistoryAudioPlayback();
        _hotkeys?.Dispose();
        _displayAwake.Dispose();
    }

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static bool SetForegroundWindow(IntPtr window) => SetForegroundWindowNative(window);

    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
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
    private string _voicePhrasesDisplay = string.Empty;
    private bool _isCustomShortcut;
    public bool IsCustomShortcut { get => _isCustomShortcut; set { _isCustomShortcut = value; Changed(nameof(IsCustomShortcut)); } }
    public string Shortcut { get => _shortcut; set { _shortcut = value; Changed(nameof(Shortcut)); } }
    public string Status { get => _status; set { _status = value; Changed(nameof(Status)); } }
    public Brush StatusBrush { get => _statusBrush; set { _statusBrush = value; Changed(nameof(StatusBrush)); } }
    public bool Visible { get => _visible; set { _visible = value; Changed(nameof(Visible)); } }
    public string VoicePhrasesDisplay
    {
        get => _voicePhrasesDisplay;
        set
        {
            _voicePhrasesDisplay = value;
            Changed(nameof(VoicePhrasesDisplay));
            Changed(nameof(HasVoicePhrases));
        }
    }
    public bool HasVoicePhrases => !string.IsNullOrWhiteSpace(_voicePhrasesDisplay);
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
