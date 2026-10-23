using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private const int HistoryPageSize = 15;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const uint WindowDisplayAffinityNone = 0;
    private const uint WindowDisplayAffinityExcludeFromCapture = 0x11;
    private const int DwmWindowCornerPreferenceAttribute = 33;
    private const int DwmWindowCornerPreferenceRound = 2;
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
    private JmdNotificationWindow? _jeyProcessingNotification;
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
    private bool _updatingCaptureExclusionToggle;
    private bool _usesRoundedWindowRegion;
    private bool _loadingAdvancedNotificationToggle;
    private bool _loadingAdvancedSoundSettings;
    private bool _showingDialog;
    private bool _checkingUpdates;
    private readonly AsyncLocal<CliCommandExecutionState?> _cliCommandExecution = new();
    private IntPtr _dictationTargetWindow;
    private bool _dictationHasInsertedText;
    private string _wakeRecognitionPartial = string.Empty;
    private long? _historyTargetId;
    private int _historyPageIndex;
    private int _historyPageCount;
    private readonly ObservableCollection<WakeRecognitionHistoryRow> _wakeRecognitionHistory = [];
    private readonly TodoTaskStore _todoTaskStore;
    private readonly ObservableCollection<TodoTask> _todoTasks;
    private TodoTaskPanelWindow? _todoTaskPanel;
    private readonly System.Windows.Threading.DispatcherTimer _todoReminderTimer = new() { Interval = TimeSpan.FromSeconds(15) };

    public event Action<string, string, bool, long?, TimeSpan>? TrayNotification;
    public bool IsVoiceWakeWordEnabled => _settings.VoiceWakeWordEnabled;

    public void ToggleVoiceWakeWordFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ToggleVoiceWakeWordFromTray));
            return;
        }
        SetVoiceWakeWordEnabled(!_settings.VoiceWakeWordEnabled);
    }

    public MainWindow()
    {
        InitializeComponent();
        VoiceRecognitionDebugHistory.ItemsSource = _wakeRecognitionHistory;
        FooterVersionText.Text = $"v{GetInstalledVersion()}";
        _settings = _settingsStore.Load();
        var appDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JMD");
        _todoTaskStore = new TodoTaskStore(Path.Combine(appDataDirectory, "tasks.db"));
        var legacyTasksPath = Path.Combine(appDataDirectory, "tasks.json");
        List<TodoTask> legacyTasks = [];
        try
        {
            if (File.Exists(legacyTasksPath))
                legacyTasks = JsonSerializer.Deserialize<List<TodoTask>>(File.ReadAllText(legacyTasksPath)) ?? [];
        }
        catch (JsonException) { }
        _todoTaskStore.ImportLegacyOnce(legacyTasks.Select(ToTodoTaskEntry));
        _todoTasks = new ObservableCollection<TodoTask>(_todoTaskStore.GetAll().Select(FromTodoTaskEntry));
        _todoReminderTimer.Tick += CheckTodoTaskReminders;
        _todoReminderTimer.Start();
        HideFromCaptureToggle.Visibility = ScreenCaptureExclusionPolicy.IsSupported(Environment.OSVersion.Version)
            ? Visibility.Visible : Visibility.Collapsed;
        ConfigureDefaultVoiceCommands();
        _updatingKeepAwakeToggle = true;
        KeepAwakeToggle.IsChecked = _settings.KeepAwakeEnabled;
        _updatingKeepAwakeToggle = false;
        _updatingCaptureExclusionToggle = true;
        HideFromCaptureToggle.IsChecked = _settings.HideFromScreenCapture;
        _updatingCaptureExclusionToggle = false;
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
        _settings.ShortcutActivatedSound = ActivationSoundService.Normalize(_settings.ShortcutActivatedSound, ActivationSoundService.Classic);
        _settings.VoiceCommandFoundSound = ActivationSoundService.Normalize(_settings.VoiceCommandFoundSound, ActivationSoundService.Classic);
        _settings.JeyRecognizedSound = ActivationSoundService.Normalize(_settings.JeyRecognizedSound, ActivationSoundService.Classic);
        _settings.TaskReminderSound = ActivationSoundService.Normalize(_settings.TaskReminderSound, ActivationSoundService.Soft);
        _loadingAdvancedSoundSettings = true;
        SelectAdvancedSound(ShortcutSoundCombo, _settings.ShortcutActivatedSound);
        SelectAdvancedSound(VoiceCommandSoundCombo, _settings.VoiceCommandFoundSound);
        SelectAdvancedSound(JeySoundCombo, _settings.JeyRecognizedSound);
        SelectAdvancedSound(TaskReminderSoundCombo, _settings.TaskReminderSound);
        _loadingAdvancedSoundSettings = false;
        if (_settings.HistoryRetentionDays is < 1 or > 3650)
            _settings.HistoryRetentionDays = 15;
        try
        {
            var historyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JMD", "history.db");
            _historyStore = new ConversionHistoryStore(historyPath);
            _historyRecorder = new ConversionHistoryRecorder(_historyStore);
            _historyStore.DeleteOlderThan(_settings.HistoryRetentionDays);
            _draftStack.RestoreFromHistory(_historyStore.GetDraftHistory());
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
        _commandView.SortDescriptions.Add(new SortDescription(nameof(CommandRow.SearchRank), ListSortDirection.Ascending));
        _commandView.SortDescriptions.Add(new SortDescription(nameof(CommandRow.Name), ListSortDirection.Ascending));
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
        IsVisibleChanged += MainWindow_IsVisibleChanged;
    }

    private void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible || !_settings.HideFromScreenCapture ||
            !ScreenCaptureExclusionPolicy.IsSupported(Environment.OSVersion.Version))
            return;

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            if (IsVisible && _settings.HideFromScreenCapture && !TrySetCaptureExcluded(this, true))
                SetStatus("O Windows não aceitou ocultar a janela do JMD nas capturas.", false);
        }));
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource)PresentationSource.FromVisual(this)!;
        _usesRoundedWindowRegion = !TrySetNativeRoundedCorners(source.Handle);
        if (_usesRoundedWindowRegion)
        {
            ApplyRoundedWindowRegion();
            SizeChanged += (_, _) => Dispatcher.BeginInvoke(new Action(ApplyRoundedWindowRegion));
        }
        if (ScreenCaptureExclusionPolicy.IsSupported(Environment.OSVersion.Version) &&
            _settings.HideFromScreenCapture && !TrySetCaptureExcluded(true))
            SetStatus("A preferência para ocultar o JMD foi carregada, mas o Windows não aceitou a solicitação.", false);
        _hotkeys = new GlobalHotkeyService(source);
        _hotkeys.HotkeyPressed += id => Dispatcher.BeginInvoke(new Action(() => _ = HandleHotkeyAsync(id)));
        RegisterConfiguredShortcuts();
        RefreshCommandStatuses();
        RefreshShortcutStatuses();
    }

    private void Header_DragMove(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void BuildCommands()
    {
        _commands.Add(new CommandRow("clipboard", "Formatar clipboard para SQL IN",
            "Converte os valores copiados em uma lista SQL entre apóstrofos.", ShortcutDefaults.ForCommand("clipboard")));
        _commands.Add(new CommandRow("selection", "Substituir seleção formatada",
            "Recorta o texto selecionado, formata e cola o resultado no lugar.", ShortcutDefaults.ForCommand("selection")));
        _commands.Add(new CommandRow("upperCase", "UpperCase · Maiúsculas",
            "Recorta a seleção, transforma em maiúsculas e cola no lugar.", GetShortcut("upperCase")));
        _commands.Add(new CommandRow("lowerCase", "LowerCase · Minúsculas",
            "Recorta a seleção, transforma em minúsculas e cola no lugar.", GetShortcut("lowerCase")));
        _commands.Add(new CommandRow("titleCase", "TitleCase · Título",
            "Capitaliza as palavras da seleção, mantendo preposições em minúsculas.", GetShortcut("titleCase")));
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
        _commands.Add(new CommandRow("todoCreate", "Criar tarefa",
            "Abre o cadastro de uma tarefa e aproveita o texto selecionado na descrição.", GetShortcut("todoCreate"))
        {
            VoicePhrasesDisplay = GetVoicePhrasesDisplay("todoCreate")
        });
        _commands.Add(new CommandRow("todoPanel", "Abrir painel de tarefas",
            "Mostra as tarefas em aberto no painel lateral.", GetShortcut("todoPanel"))
        {
            VoicePhrasesDisplay = GetVoicePhrasesDisplay("todoPanel")
        });
        _commands.Add(new CommandRow("todoPanelClose", "Fechar painel de tarefas",
            "Fecha o painel lateral de tarefas.", GetShortcut("todoPanelClose"))
        {
            VoicePhrasesDisplay = GetVoicePhrasesDisplay("todoPanelClose")
        });
        _commands.Add(new CommandRow("about", "Abrir Sobre", "Abre a tela Sobre do JMD.", "Sem atalho")
        {
            VoicePhrasesDisplay = GetVoicePhrasesDisplay("about")
        });
        _commands.Add(new CommandRow("checkUpdates", "Verificar atualizações JMD", "Abre Sobre e consulta uma nova versão.", "Sem atalho")
        {
            VoicePhrasesDisplay = GetVoicePhrasesDisplay("checkUpdates")
        });
        _commands.Add(new CommandRow("transcribe", "Transcrever", "Digita a fala reconhecida pelo Jey; uma pausa de 3 segundos encerra.", "Sem atalho")
        {
            VoicePhrasesDisplay = GetVoicePhrasesDisplay("transcribe")
        });
        _commands.Add(new CommandRow("createCustomShortcut", "Criar atalho personalizado",
            "Cria workflows com etapas de abertura, teclado e texto.", "Sem atalho"));
        foreach (var shortcut in _settings.CustomShortcuts)
        {
            var row = new CommandRow(shortcut.Id, shortcut.Name, DescribeCustomWorkflow(shortcut),
                string.IsNullOrWhiteSpace(shortcut.Shortcut) ? "Sem atalho" : shortcut.Shortcut)
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
        AddManagementNavigation(SettingsPanel, workflowsSelected: false);
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

        (string Id, string Name, string Description)[] configurableCommands =
        [
            ("palette", "Abrir paleta", "Exibe a lista de comandos e configurações."),
            ("about", "Abrir Sobre", "Abre a tela Sobre do JMD."),
            ("clipboard", "Formatar clipboard", "Transforma o texto copiado para SQL IN."),
            ("jsonPretty", "Formatar", "Identifica JSON ou XML e aplica indentação."),
            ("jsonCompact", "Compactar", "Identifica JSON ou XML e deixa o conteúdo em uma linha."),
            ("draftRestore", "Usar rascunho", "Guarda o conteúdo atual e recupera o último rascunho."),
            ("snippets", "Inserir texto salvo", "Busca pelo título e cola no aplicativo anterior."),
            ("draftSave", "Salvar rascunho", "Salva a seleção ou a linha do cursor e limpa o campo."),
            ("saveSnippet", "Salvar seleção como texto", "Copia a seleção atual e pede um título."),
            ("selection", "Substituir seleção", "Recorta, transforma e cola a seleção atual."),
            ("upperCase", "UpperCase · Maiúsculas", "Transforma a seleção em maiúsculas."),
            ("lowerCase", "LowerCase · Minúsculas", "Transforma a seleção em minúsculas."),
            ("titleCase", "TitleCase · Título", "Capitaliza as palavras, exceto preposições."),
            ("todoCreate", "Criar tarefa", "Abre o cadastro de uma tarefa; a seleção atual vira a descrição."),
            ("todoPanel", "Abrir painel de tarefas", "Mostra as tarefas em aberto no painel lateral."),
            ("todoPanelClose", "Fechar painel de tarefas", "Fecha o painel lateral de tarefas."),
            ("transcribe", "Transcrever", "Digita o que você disser após o comando; pausa de 3 segundos encerra."),
            ("checkUpdates", "Verificar atualizações JMD", "Abre Sobre e consulta uma nova versão.")
        ];
        foreach (var command in configurableCommands.OrderBy(command => command.Name, StringComparer.CurrentCultureIgnoreCase))
            AddShortcutRow(command.Id, command.Name, command.Description);

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

    private void BuildWorkflowSettingsPanel()
    {
        var existingStatuses = _shortcutRows.ToDictionary(row => row.Id, row => (row.Status, row.StatusBrush), StringComparer.Ordinal);
        WorkflowSettingsPanel.Children.Clear();
        _shortcutRows.Clear();
        _shortcutStatusLabels.Clear();
        AddManagementNavigation(WorkflowSettingsPanel, workflowsSelected: true);
        WorkflowSettingsPanel.Children.Add(new TextBlock
        {
            Text = "Workflows personalizados",
            Foreground = Brushes.White,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5)
        });
        WorkflowSettingsPanel.Children.Add(new TextBlock
        {
            Text = "Monte sequências de abertura, teclado e texto, com atalhos e frases de voz próprios.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 12)
        });
        AddShortcutRow("createCustomShortcut", "Criar workflow personalizado", "Configura o atalho global e as frases de voz para abrir o editor de workflows.", WorkflowSettingsPanel);

        var addCustomShortcut = new Button
        {
            Content = "+ Criar workflow",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 10),
            Background = new SolidColorBrush(Color.FromRgb(180, 243, 106)),
            Foreground = new SolidColorBrush(Color.FromRgb(23, 25, 31)),
            BorderThickness = new Thickness(0)
        };
        addCustomShortcut.Click += (_, _) =>
        {
            CreateCustomShortcutFromDialog();
            BuildWorkflowSettingsPanel();
        };
        WorkflowSettingsPanel.Children.Add(addCustomShortcut);

        foreach (var customShortcut in _settings.CustomShortcuts.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var customCard = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(34, 37, 45)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(49, 52, 61)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 6)
            };
            var customRow = new Grid();
            customRow.ColumnDefinitions.Add(new ColumnDefinition());
            customRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var customDetails = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            customDetails.Children.Add(new TextBlock
            {
                Text = customShortcut.Name,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            customDetails.Children.Add(new TextBlock
            {
                Text = DescribeCustomWorkflow(customShortcut),
                Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)),
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = DescribeCustomWorkflow(customShortcut)
            });
            customRow.Children.Add(customDetails);
            var customActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            customActions.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(48, 52, 62)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 6, 0),
                Child = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(customShortcut.Shortcut) ? "Sem atalho" : customShortcut.Shortcut,
                    Foreground = new SolidColorBrush(Color.FromRgb(228, 231, 236)),
                    FontSize = 11
                }
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
                BuildWorkflowSettingsPanel();
            };
            var removeCustomShortcut = new Button
            {
                Content = "Remover",
                Tag = customShortcut,
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(6, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(48, 52, 62)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0)
            };
            removeCustomShortcut.Click += RemoveCustomShortcut_Click;
            customActions.Children.Add(editCustomShortcut);
            customActions.Children.Add(removeCustomShortcut);
            Grid.SetColumn(customActions, 1);
            customRow.Children.Add(customActions);
            customCard.Child = customRow;
            WorkflowSettingsPanel.Children.Add(customCard);
        }

        WorkflowSettingsPanel.Children.Add(new TextBlock
        {
            Text = "Variáveis globais",
            Foreground = Brushes.White,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 6)
        });
        var manageVariables = new Button
        {
            Content = $"Gerenciar variáveis ({_settings.WorkflowVariables.Count})",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Padding = new Thickness(12, 8, 12, 8),
            Background = new SolidColorBrush(Color.FromRgb(48, 52, 62)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        manageVariables.Click += (_, _) => ManageWorkflowVariables();
        WorkflowSettingsPanel.Children.Add(manageVariables);

        foreach (var row in _shortcutRows)
        {
            if (!existingStatuses.TryGetValue(row.Id, out var status)) continue;
            row.Status = status.Status;
            row.StatusBrush = status.StatusBrush;
        }
        RefreshShortcutStatuses();
    }

    private void AddManagementNavigation(StackPanel panel, bool workflowsSelected)
    {
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        var shortcutButton = new Button
        {
            Content = "Atalhos",
            Padding = new Thickness(12, 7, 12, 7),
            Margin = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(workflowsSelected ? Color.FromRgb(48, 52, 62) : Color.FromRgb(180, 243, 106)),
            Foreground = workflowsSelected ? Brushes.White : new SolidColorBrush(Color.FromRgb(23, 25, 31)),
            BorderThickness = new Thickness(0)
        };
        shortcutButton.Click += (_, _) => ShowShortcutSettings();
        var workflowButton = new Button
        {
            Content = "Workflows personalizados",
            Padding = new Thickness(12, 7, 12, 7),
            Background = new SolidColorBrush(workflowsSelected ? Color.FromRgb(180, 243, 106) : Color.FromRgb(48, 52, 62)),
            Foreground = workflowsSelected ? new SolidColorBrush(Color.FromRgb(23, 25, 31)) : Brushes.White,
            BorderThickness = new Thickness(0)
        };
        workflowButton.Click += (_, _) => ShowWorkflowSettingsPage();
        navigation.Children.Add(shortcutButton);
        navigation.Children.Add(workflowButton);
        panel.Children.Add(navigation);
    }

    private void ConfigureDefaultVoiceCommands()
    {
        var defaults = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["about"] = ["abrir sobre", "abrir tela sobre"],
            ["checkUpdates"] = ["verificar atualizações", "verificar atualização", "atualizar jmd"],
            ["transcribe"] = ["transcrever"],
            ["todoCreate"] = ["criar tarefa"],
            ["todoPanel"] = ["abrir tarefas", "abrir painel de tarefas"],
            ["todoPanelClose"] = ["fechar tarefas", "fechar painel de tarefas"]
        };
        var changed = false;
        foreach (var (commandId, phrases) in defaults)
        {
            if (_settings.VoicePhrases.TryGetValue(commandId, out var configured) && configured.Count > 0) continue;
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

    private void AddShortcutRow(string id, string name, string description, StackPanel? target = null)
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
        (target ?? SettingsPanel).Children.Add(card);
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

    private VoiceCommandMatch? FindVoiceCommand(string? transcript)
    {
        if (NormalizeVoicePhrase(transcript).Length == 0) return null;

        VoiceCommandMatch? matchedCommand = null;
        foreach (var commandId in ShortcutIds.Concat(_settings.CustomShortcuts.Select(shortcut => shortcut.Id)))
        {
            if (_settings.DisabledCommands.Contains(commandId) ||
                !_settings.VoicePhrases.TryGetValue(commandId, out var phrases))
                continue;

            var phraseMatch = phrases.Select(phrase =>
            {
                if (VoicePhrasePolicy.TryMatchTemplate(phrase, transcript, out var captured)) return captured;
                var exact = VoicePhrasePolicy.Matches(phrase, transcript) ||
                    commandId == "transcribe" && VoicePhrasePolicy.StartsWith(phrase, transcript);
                return exact ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : null;
            }).FirstOrDefault(captured => captured is not null);
            if (phraseMatch is null) continue;
            if (matchedCommand is not null)
            {
                SetStatus("Essa frase está associada a mais de um comando. Deixe-a em apenas um comando para usá-la por voz.", false);
                return null;
            }
            matchedCommand = new VoiceCommandMatch(commandId, phraseMatch);
        }
        return matchedCommand;
    }

    private string[] GetEnabledVoiceCommandPhrases()
        => _settings.VoicePhrases
            .Where(pair => !_settings.DisabledCommands.Contains(pair.Key))
            .SelectMany(pair => pair.Value)
            .Where(phrase => !string.IsNullOrWhiteSpace(phrase))
            .ToArray();

    private VoiceCommandMatch? FindTypedWorkflowMatch(string? input)
    {
        VoiceCommandMatch? result = null;
        foreach (var shortcut in _settings.CustomShortcuts)
        {
            if (_settings.DisabledCommands.Contains(shortcut.Id) || !_settings.VoicePhrases.TryGetValue(shortcut.Id, out var phrases)) continue;
            foreach (var phrase in phrases)
            {
                if (!VoicePhrasePolicy.TryMatchTemplate(phrase, input, out var variables)) continue;
                if (result is not null) return null;
                result = new VoiceCommandMatch(shortcut.Id, variables);
            }
        }
        return result;
    }

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
        if (target.Contains("://", StringComparison.Ordinal))
            return Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
                   (uri.Scheme is "http" or "https") && !target.Any(char.IsWhiteSpace);
        return CustomCommandLine.TryParse(target, out _, out _);
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
        (string Name, string Target, string Shortcut, bool FocusIfAlreadyOpen, List<CustomWorkflowStep> Steps)? previous = existing is null
            ? null : (existing.Name, existing.Target, existing.Shortcut, existing.FocusIfAlreadyOpen,
                existing.Steps.Select(step => new CustomWorkflowStep { Action = step.Action, Value = step.Value, DelayMilliseconds = step.DelayMilliseconds, RepeatCount = step.RepeatCount }).ToList());
        var previousPhrases = existing is not null && _settings.VoicePhrases.TryGetValue(existing.Id, out var savedPhrases)
            ? savedPhrases.ToList() : [];
        var dialog = new CustomShortcutEditorWindow(existing, previousPhrases,
            _settings.WorkflowVariables.Select(variable => variable.Name),
            draft => ValidateCustomWorkflowDraft(draft, existing?.Id),
            draft => RunCustomWorkflowAsync(new CustomShortcutEntry
            {
                Id = existing?.Id ?? $"custom:test:{Guid.NewGuid():N}",
                Name = draft.CommandName,
                Target = draft.Steps.FirstOrDefault(step => step.Action == "Open")?.Value ?? string.Empty,
                Shortcut = draft.Shortcut,
                FocusIfAlreadyOpen = draft.FocusIfAlreadyOpen,
                Steps = draft.Steps
            }, isTestExecution: true));
        PrepareWindowForCaptureExclusion(dialog);
        _showingDialog = true;
        bool accepted;
        try { accepted = dialog.ShowDialog() == true; }
        finally { _showingDialog = false; }
        if (!accepted) return;

        var name = dialog.CommandName;
        var steps = dialog.Steps;
        var target = steps.FirstOrDefault(step => step.Action == "Open")?.Value ?? string.Empty;
        var shortcutText = dialog.Shortcut;
        var focusIfAlreadyOpen = dialog.FocusIfAlreadyOpen;
        var phrases = dialog.VoicePhrases.ToList();
        var error = ValidateCustomWorkflowDraft(dialog, existing?.Id);
        if (error is not null)
        {
            Notify("JMD", error, false);
            return;
        }

        var entry = existing ?? new CustomShortcutEntry();
        entry.Name = name;
        entry.Target = target;
        entry.Steps = steps;
        entry.Shortcut = shortcutText;
        entry.FocusIfAlreadyOpen = focusIfAlreadyOpen;
        if (existing is null) _settings.CustomShortcuts.Add(entry);
        _settings.VoicePhrases[entry.Id] = phrases;
        try
        {
            _settingsStore.Save(_settings);
            var oldRow = _commands.FirstOrDefault(command => command.Id == entry.Id);
            if (oldRow is not null) _commands.Remove(oldRow);
            _commands.Add(new CommandRow(entry.Id, entry.Name, DescribeCustomWorkflow(entry),
                string.IsNullOrWhiteSpace(entry.Shortcut) ? "Sem atalho" : entry.Shortcut)
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
                entry.Steps = previous.Value.Steps;
                _settings.VoicePhrases[entry.Id] = previousPhrases;
            }
            Notify("JMD", $"Não foi possível salvar o atalho: {exception.Message}", false);
        }
        UpdateVoiceCommandPhrases();
    }

    private string? ValidateCustomWorkflowDraft(CustomShortcutEditorWindow dialog, string? exceptId)
    {
        var steps = dialog.Steps;
        var shortcutText = dialog.Shortcut;
        var phrases = dialog.VoicePhrases;
        if (dialog.CommandName.Length == 0) return "Informe o nome do workflow.";
        if (steps.Count == 0) return "Adicione ao menos uma etapa ao workflow.";
        if (steps.Any(step => step.DelayMilliseconds is < 0 or > 60000)) return "A pausa após cada etapa deve ficar entre 0 e 60000 ms.";
        if (steps.Any(step => step.Action == "Open" && !IsValidCustomTarget(step.Value))) return "Informe um caminho de programa ou uma URL http/https válida em cada etapa de abertura.";
        if (steps.Any(step => step.Action == "Key" && !TryGetWorkflowKey(step.Value, out _))) return "Informe uma tecla válida, como Tab, Enter, Escape ou F1.";
        if (steps.Any(step => step.Action == "Key" && !WorkflowRepeatPolicy.IsValidCount(step.RepeatCount))) return $"A repetição da tecla deve ficar entre {WorkflowRepeatPolicy.MinimumCount} e {WorkflowRepeatPolicy.MaximumCount} vezes.";
        if (steps.Any(step => step.Action == "Type" && string.IsNullOrEmpty(step.Value))) return "Informe o texto a digitar em cada etapa de texto.";
        if (steps.Any(step => step.Action == "Wait" && (!int.TryParse(step.Value, out var milliseconds) || milliseconds is < 1 or > 60000))) return "A etapa de espera deve ter entre 1 e 60000 ms.";
        if (steps.Any(step => step.Action == "Focus" && string.IsNullOrWhiteSpace(step.Value))) return "Informe o nome acessível do campo ou botão a focar.";
        if (steps.Any(step => step.Action is not ("Open" or "Key" or "Type" or "Wait" or "Focus"))) return "O workflow contém uma etapa desconhecida.";
        var dynamicNames = dialog.VoicePhrases
            .SelectMany(phrase => Regex.Matches(phrase, "\\{\\{([A-Za-z][A-Za-z0-9_]{0,39})\\}\\}").Select(match => match.Groups[1].Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var token in steps.Where(step => step.Action is "Open" or "Type" or "Focus")
                     .SelectMany(step => Regex.Matches(step.Value, "\\{\\{([A-Za-z][A-Za-z0-9_]{0,39})\\}\\}").Select(match => match.Groups[1].Value)))
        {
            if (!_settings.WorkflowVariables.Any(variable => variable.Name.Equals(token, StringComparison.OrdinalIgnoreCase)) && !dynamicNames.Contains(token))
                return $"A variável {{{{{token}}}}} não está cadastrada nem definida como trecho variável em uma frase de voz.";
        }
        if (shortcutText.Length > 0 && !ShortcutParser.TryParse(shortcutText, out _, out var shortcutError)) return shortcutError ?? "Atalho inválido.";
        if (shortcutText.Length > 0 && HasShortcutConflict(shortcutText, exceptId)) return "Essa combinação de teclas já está associada a outro comando.";
        if (VoicePhrasePolicy.HasDuplicate(phrases) || VoicePhrasePolicy.ConflictsWith(phrases,
                _settings.VoicePhrases.Where(pair => pair.Key != exceptId).SelectMany(pair => pair.Value)))
            return "Uma dessas frases do Jey já está associada a outro comando. Escolha frases exclusivas.";
        return null;
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

    private void ManageWorkflowVariables()
    {
        var dialog = new WorkflowVariablesWindow(_settings.WorkflowVariables, PrepareWindowForCaptureExclusion);
        PrepareWindowForCaptureExclusion(dialog);
        _showingDialog = true;
        bool accepted;
        try { accepted = dialog.ShowDialog() == true; }
        finally { _showingDialog = false; }
        if (!accepted) return;

        var previous = _settings.WorkflowVariables;
        _settings.WorkflowVariables = dialog.Variables;
        try
        {
            _settingsStore.Save(_settings);
            BuildWorkflowSettingsPanel();
            SetStatus("Variáveis dos workflows salvas.", true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _settings.WorkflowVariables = previous;
            SetStatus($"Não foi possível salvar as variáveis: {exception.Message}", false);
        }
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
            BuildWorkflowSettingsPanel();
            SetStatus($"Atalho personalizado removido: {entry.Name}.", true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _settings.CustomShortcuts.Add(entry);
            if (savedPhrases is not null) _settings.VoicePhrases[entry.Id] = savedPhrases;
            BuildWorkflowSettingsPanel();
            _commands.Add(new CommandRow(entry.Id, entry.Name, DescribeCustomWorkflow(entry), entry.Shortcut)
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
        PrepareWindowForCaptureExclusion(dialog);
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
        PrepareWindowForCaptureExclusion(dialog);
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
            var historyId = RecordActionHistory($"Salvar texto: {snippet.Title}", string.Empty, snippet.Text, voiceCommand);
            Notify("JMD", $"Texto salvo: {snippet.Title}", true, historyId);
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
        PrepareWindowForCaptureExclusion(picker);
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
        var historyId = RecordActionHistory($"Inserir texto salvo: {snippet.Title}", snippet.Title, snippet.Text, voiceCommand);
        Notify("JMD", $"Texto inserido: {snippet.Title}", true, historyId);
    }

    private long? RecordActionHistory(string commandName, string beforeValue, string afterValue,
        VoiceCommandInvocation? voiceCommand = null, bool isFailure = false, string? failureDetails = null)
    {
        if (_historyStore is null) return null;
        try
        {
            return RecordCommandHistory(commandName, beforeValue, afterValue, voiceCommand, isFailure, failureDetails);
        }
        catch (Exception exception)
        {
            SetStatus($"Não foi possível salvar a ação no histórico: {exception.Message}", false);
            return null;
        }
    }

    private static readonly string[] ShortcutIds = ["palette", "clipboard", "selection", "upperCase", "lowerCase", "titleCase", "jsonPretty", "jsonCompact", "snippets", "saveSnippet", "draftSave", "draftRestore", "todoCreate", "todoPanel", "todoPanelClose", "about", "checkUpdates", "transcribe", "createCustomShortcut"];

    private static bool IsJeyAction(string id) => id is "about" or "checkUpdates" or "transcribe";

    private string GetShortcut(string id)
    {
        if (_settings.Shortcuts.TryGetValue(id, out var value)) return value;
        var customShortcut = _settings.CustomShortcuts.FirstOrDefault(shortcut => shortcut.Id == id);
        if (customShortcut is not null)
            return string.IsNullOrWhiteSpace(customShortcut.Shortcut) ? "Sem atalho" : customShortcut.Shortcut;
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

    private async Task<bool> HandleHotkeyAsync(string id, VoiceCommandInvocation? voiceCommand = null,
        IReadOnlyDictionary<string, string>? dynamicVariables = null)
    {
        try
        {
            if (voiceCommand is null)
                ActivationSoundService.PlayShortcutActivated(_settings.ShortcutActivatedSound);
            else
                ActivationSoundService.PlayVoiceCommandFound(_settings.VoiceCommandFoundSound);
            _previousWindow = _keyboard.GetForegroundWindow();
            var succeeded = true;
            switch (id)
            {
                case "todoCreate":
                    await CreateTodoTaskAsync();
                    break;
                case "todoPanel":
                    ShowTodoTaskPanel();
                    break;
                case "todoPanelClose":
                    _todoTaskPanel?.Hide();
                    break;
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
                case "transcribe":
                    StartDictation(voiceCommand?.Transcript);
                    break;
                case "createCustomShortcut":
                    CreateCustomShortcutFromDialog();
                    break;
                case "clipboard":
                    await TransformClipboardAsync(voiceCommand);
                    break;
                case "selection":
                    await TransformSelectionAsync(voiceCommand);
                    break;
                case "upperCase":
                case "lowerCase":
                case "titleCase":
                    await TransformTextCaseAsync(id, voiceCommand);
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
                    if (customShortcut is not null) succeeded = await RunCustomWorkflowAsync(customShortcut, voiceCommand, dynamicVariables);
                    break;
            }
            return succeeded;
        }
        catch (Exception exception)
        {
            SetStatus($"Operação falhou: {exception.Message}", false);
            return false;
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
        var historyId = RecordActionHistory(DraftStack.SavedCommandName, string.Empty, draft, voiceCommand);
        Notify("JMD", $"Rascunho salvo ({_draftStack.Count} na pilha).", true, historyId);
    }

    private async Task<bool> RunCustomWorkflowAsync(CustomShortcutEntry shortcut, VoiceCommandInvocation? voiceCommand = null,
        IReadOnlyDictionary<string, string>? dynamicVariables = null, bool isTestExecution = false)
    {
        var currentStepDescription = "Preparação do workflow";
        var restoreInitialFocus = _focusedText.CaptureFocusRestorer(isTestExecution ? IntPtr.Zero : _previousWindow);
        var launchedProgram = false;
        try
        {
            if (!await _keyboard.WaitForModifiersReleasedAsync())
                throw new InvalidOperationException("Solte as teclas modificadoras para iniciar o workflow.");
            var steps = shortcut.Steps ?? [];
            if (steps.Count == 0 && !string.IsNullOrWhiteSpace(shortcut.Target))
                steps = [new CustomWorkflowStep { Action = "Open", Value = shortcut.Target }];
            if (steps.Count == 0)
                throw new InvalidOperationException($"O workflow {shortcut.Name} não tem etapas.");

            var firstLaunch = true;
            var hasTypingOrKeys = steps.Any(step => step.Action is "Key" or "Type" or "Focus");
            var hasOpenStep = steps.Any(step => step.Action == "Open");
            if (hasTypingOrKeys && !hasOpenStep && _previousWindow == IntPtr.Zero)
                throw new InvalidOperationException("Não foi possível identificar a janela que deve receber o texto.");
            if (hasTypingOrKeys && !hasOpenStep)
            {
                SetForegroundWindow(_previousWindow);
                await Task.Delay(100);
            }

            foreach (var step in steps)
            {
                currentStepDescription = step.Action switch
                {
                    "Open" => "Abrir programa ou URL",
                    "Key" => "Pressionar tecla",
                    "Type" => "Digitar texto",
                    "Focus" => "Localizar controle",
                    "Wait" => "Aguardar",
                    _ => "Executar etapa"
                };
                switch (step.Action)
                {
                    case "Open":
                        var commandVariables = voiceCommand?.Variables ?? dynamicVariables;
                        var openTarget = ExpandWorkflowVariables(step.Value, commandVariables,
                            urlEncodeValues: step.Value.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                step.Value.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase));
                        if (firstLaunch && shortcut.FocusIfAlreadyOpen && TryFocusExistingTarget(openTarget))
                        {
                            await Task.Delay(step.DelayMilliseconds > 0 ? step.DelayMilliseconds : 100);
                        }
                        else
                        {
                            if (!CustomCommandLine.TryParse(openTarget, out var executable, out var arguments))
                                throw new InvalidOperationException($"Programa ou URL inválido: {openTarget}");
                            var isUrl = Uri.TryCreate(openTarget, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
                            var workingDirectory = string.Empty;
                            if (!isUrl && Path.GetExtension(executable).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                            {
                                var shortcutTarget = WindowsShortcutResolver.Resolve(executable);
                                if (!CustomCommandLine.TryParse($"shortcut.exe {shortcutTarget.Arguments}", out _, out var shortcutArguments))
                                    throw new InvalidOperationException($"O atalho ‘{Path.GetFileName(executable)}’ possui argumentos inválidos.");
                                arguments = WorkflowArgumentPolicy.Combine(shortcutArguments, arguments);
                                executable = shortcutTarget.TargetPath;
                                workingDirectory = shortcutTarget.WorkingDirectory;
                                isUrl = Uri.TryCreate(executable, UriKind.Absolute, out uri) && uri.Scheme is "http" or "https";
                            }
                            var startInfo = new ProcessStartInfo
                            {
                                FileName = executable,
                                WorkingDirectory = workingDirectory,
                                UseShellExecute = isUrl
                            };
                            if (isUrl)
                                startInfo.Arguments = CustomCommandLine.JoinArguments(arguments);
                            else
                                foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
                            try
                            {
                                if (Process.Start(startInfo) is null)
                                    throw new InvalidOperationException("O Windows não iniciou o aplicativo ou endereço.");
                            }
                            catch (System.ComponentModel.Win32Exception exception) when (!isUrl && IsElevationRequiredError(exception.NativeErrorCode))
                            {
                                var answer = MessageBox.Show(this,
                                    $"O Windows informou que ‘{executable}’ precisa de mais permissões. Quer tentar executá-lo como administrador? O Windows pedirá confirmação.",
                                    "Permissão necessária", MessageBoxButton.YesNo, MessageBoxImage.Question);
                                if (answer != MessageBoxResult.Yes)
                                    throw new OperationCanceledException("A execução com permissões elevadas foi recusada. O workflow foi interrompido.", exception);

                                var elevatedStartInfo = new ProcessStartInfo
                                {
                                    FileName = executable,
                                    Arguments = CustomCommandLine.JoinArguments(arguments),
                                    WorkingDirectory = !string.IsNullOrWhiteSpace(workingDirectory)
                                        ? workingDirectory
                                        : Path.IsPathRooted(executable) ? Path.GetDirectoryName(executable) ?? string.Empty : string.Empty,
                                    UseShellExecute = true,
                                    Verb = "runas"
                                };
                                try
                                {
                                    if (Process.Start(elevatedStartInfo) is null)
                                        throw new InvalidOperationException("O Windows não iniciou o aplicativo com permissões elevadas.");
                                }
                                catch (System.ComponentModel.Win32Exception elevationException) when (elevationException.NativeErrorCode == 1223)
                                {
                                    throw new OperationCanceledException("O pedido de permissão do Windows foi cancelado. O workflow foi interrompido.", elevationException);
                                }
                            }
                            launchedProgram = true;
                            await Task.Delay(step.DelayMilliseconds > 0 ? step.DelayMilliseconds : 1000);
                        }
                        firstLaunch = false;
                        break;
                    case "Key":
                        if (!TryGetWorkflowKey(step.Value, out var virtualKey))
                            throw new InvalidOperationException($"Tecla inválida: {step.Value}.");
                        if (!WorkflowRepeatPolicy.IsValidCount(step.RepeatCount))
                            throw new InvalidOperationException($"A repetição da tecla deve ficar entre {WorkflowRepeatPolicy.MinimumCount} e {WorkflowRepeatPolicy.MaximumCount} vezes.");
                        for (var repetition = 0; repetition < step.RepeatCount; repetition++)
                        {
                            if (!_keyboard.SendVirtualKey(virtualKey))
                                throw new InvalidOperationException($"Não foi possível pressionar a tecla {step.Value} (repetição {repetition + 1} de {step.RepeatCount}).");
                        }
                        break;
                    case "Type":
                        var textToType = ExpandWorkflowVariables(step.Value, voiceCommand?.Variables ?? dynamicVariables);
                        if (string.IsNullOrEmpty(textToType) || !_keyboard.SendUnicodeText(textToType))
                            throw new InvalidOperationException("Não foi possível digitar o texto da etapa.");
                        break;
                    case "Focus":
                        var controlName = ExpandWorkflowVariables(step.Value, voiceCommand?.Variables ?? dynamicVariables);
                        if (!_focusedText.FocusControlByName(controlName))
                            throw new InvalidOperationException($"Não encontrei um campo ou botão acessível com o nome ou AutomationId ‘{controlName}’. Se o inspetor mostrar apenas Chrome Legacy Window, o aplicativo não expôs os controles internos à Automação do Windows; nesse caso, avance até o campo usando etapas Pressionar tecla (Tab/Shift+Tab).");
                        break;
                    case "Wait":
                        if (!int.TryParse(step.Value, out var waitMilliseconds) || waitMilliseconds is < 1 or > 60000)
                            throw new InvalidOperationException("A etapa de espera deve ter entre 1 e 60000 ms.");
                        await Task.Delay(waitMilliseconds);
                        break;
                    default:
                        throw new InvalidOperationException("O workflow contém uma etapa desconhecida.");
                }

                if (step.Action is not ("Wait" or "Open") && step.DelayMilliseconds > 0)
                    await Task.Delay(Math.Clamp(step.DelayMilliseconds, 1, 60000));
            }
            RecordActionHistory($"{(isTestExecution ? "Testar" : "Executar")} {shortcut.Name}", string.Empty, string.Empty, voiceCommand);
            if (!isTestExecution) Notify("JMD", $"Workflow concluído: {shortcut.Name}.", true);
            return true;
        }
        catch (Exception exception)
        {
            var focusRestored = !launchedProgram && restoreInitialFocus?.Invoke() == true;
            var failureDetails = $"{currentStepDescription}: {exception.Message}" +
                                 (focusRestored ? " O foco anterior foi restaurado." : string.Empty);
            RecordActionHistory($"{(isTestExecution ? "Testar" : "Executar")} {shortcut.Name}", string.Empty, string.Empty,
                voiceCommand, isFailure: true, failureDetails: failureDetails);
            SetStatus($"Workflow interrompido ({shortcut.Name}): {exception.Message}", false, failureDetails);
            return false;
        }
    }

    private static bool IsElevationRequiredError(int nativeErrorCode)
        => nativeErrorCode is 5 or 740 or 1314;

    private static bool TryGetWorkflowKey(string value, out ushort virtualKey)
    {
        virtualKey = 0;
        var keyName = value.Trim() switch { "Enter" => nameof(Key.Return), "Esc" => nameof(Key.Escape), "Up" => nameof(Key.Up), "Down" => nameof(Key.Down), "Left" => nameof(Key.Left), "Right" => nameof(Key.Right), var other => other };
        if (!Enum.TryParse<Key>(keyName, true, out var key) || key is Key.None or Key.System or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return false;
        var result = KeyInterop.VirtualKeyFromKey(key);
        if (result <= 0 || result > ushort.MaxValue) return false;
        virtualKey = (ushort)result;
        return true;
    }

    private string ExpandWorkflowVariables(string text, IReadOnlyDictionary<string, string>? commandVariables = null,
        bool urlEncodeValues = false)
        => Regex.Replace(text, "\\{\\{([A-Za-z][A-Za-z0-9_]{0,39})\\}\\}", match =>
        {
            var value = commandVariables is not null && commandVariables.TryGetValue(match.Groups[1].Value, out var captured)
                ? captured
                : null;
            var variable = _settings.WorkflowVariables.FirstOrDefault(item => item.Name.Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
            if (value is null && variable is null)
                throw new InvalidOperationException($"O valor {{{{{match.Groups[1].Value}}}}} não foi capturado pela frase de voz e não está cadastrado como variável global.");
            value ??= variable!.IsSecret ? WorkflowVariableProtection.Unprotect(variable.ProtectedValue) : variable.Value;
            return urlEncodeValues ? Uri.EscapeDataString(value) : value;
        });

    private static string DescribeCustomWorkflow(CustomShortcutEntry shortcut)
    {
        var count = shortcut.Steps?.Count ?? 0;
        if (count == 0 && !string.IsNullOrWhiteSpace(shortcut.Target)) count = 1;
        return count == 1 && !string.IsNullOrWhiteSpace(shortcut.Target) ? shortcut.Target : $"Workflow · {count} etapas";
    }

    private static bool TryFocusExistingTarget(string target)
    {
        var isUrl = Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
        var processName = string.Empty;
        if (!isUrl && CustomCommandLine.TryParse(target, out var executable, out var arguments))
        {
            // A command with parameters can identify a specific document or folder.
            // Focusing any existing window of the same executable would skip opening it.
            if (arguments.Count > 0) return false;
            processName = Path.GetFileNameWithoutExtension(executable);
        }
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
        var historyId = RecordActionHistory(DraftStack.RestoredCommandName, string.Empty, draft, voiceCommand);
        if (!string.IsNullOrWhiteSpace(currentText))
        {
            _draftStack.Push(currentText);
            RecordActionHistory(DraftStack.SavedCommandName, string.Empty, currentText);
        }
        Notify("JMD", $"Rascunho restaurado ({_draftStack.Count} na pilha).", true, historyId);
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
        WorkflowSettingsPage.Visibility = Visibility.Collapsed;
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

    private async Task CreateTodoTaskAsync()
    {
        var selectedText = string.Empty;
        var oldClipboard = _clipboard.TryGetRichContent();
        var modifiersReleased = await _keyboard.WaitForModifiersReleasedAsync();
        if (modifiersReleased && _previousWindow != IntPtr.Zero && _keyboard.GetForegroundWindow() == _previousWindow)
        {
            var clipboardSequenceBeforeCopy = _clipboard.SequenceNumber;
            if (_keyboard.SendCopy())
            {
                await Task.Delay(180);
                if (_clipboard.SequenceNumber != clipboardSequenceBeforeCopy)
                    selectedText = _clipboard.TryGetRichContent()?.Text ?? string.Empty;
                if (oldClipboard is not null) _clipboard.TrySetRichContent(oldClipboard);
            }
        }
        var dialog = new TodoTaskEditWindow(initialDescription: selectedText);
        if (!ShowTodoTaskDialog(dialog)) return;
        var task = FromTodoTaskEntry(_todoTaskStore.Create(dialog.TaskTitle, dialog.Description, DateTimeOffset.Now,
            dialog.ReminderIntervalMinutes, dialog.CategoryColor, dialog.Urls, dialog.Attachments));
        task.Checklist = dialog.Checklist.ToList();
        _todoTasks.Add(task);
        SaveTodoTasks();
        RecordTodoTaskChange("criada", task, $"Tarefa criada: {TodoTaskDisplay.Reference(task)} {task.Title}");
        _todoTaskPanel?.Refresh();
    }

    private void ShowTodoTaskPanel()
    {
        _todoTaskPanel ??= new TodoTaskPanelWindow(_todoTasks, CompleteTodoTask, EditTodoTask, DeleteTodoTask,
            ReopenTodoTask, PauseTodoTask, UnpauseTodoTask, ToggleTodoTaskChecklist, () => _ = CreateTodoTaskAsync(), ShowTodoTaskReport);
        _todoTaskPanel.ShowOpenTasks();
        _todoTaskPanel.Show();
        _todoTaskPanel.Activate();
        Dispatcher.BeginInvoke(new Action(_todoTaskPanel.FocusFirstAction));
    }

    private void ShowTodoTaskReport()
    {
        var panel = _todoTaskPanel;
        if (panel is null) return;
        var report = new TodoTaskReportWindow(_historyStore?.Search("Tarefa") ?? []);
        report.Owner = panel;
        panel.SetModalChildActive(true);
        try { report.ShowDialog(); }
        finally { panel.SetModalChildActive(false); }
    }

    private void EditTodoTask(TodoTask task)
    {
        var dialog = new TodoTaskEditWindow(task);
        if (!ShowTodoTaskDialog(dialog)) return;
        var previousTask = FromTodoTaskEntry(ToTodoTaskEntry(task));
        var newChecklist = dialog.Checklist.ToList();
        var checklistChanged = !task.Checklist.SequenceEqual(newChecklist);
        var changed = task.Title != dialog.TaskTitle || task.Description != dialog.Description || task.Urls != dialog.Urls || task.Attachments != dialog.Attachments || checklistChanged;
        var reminderChanged = task.ReminderIntervalMinutes != dialog.ReminderIntervalMinutes;
        changed |= reminderChanged || task.CategoryColor != dialog.CategoryColor;
        task.Title = dialog.TaskTitle;
        task.Description = dialog.Description;
        task.ReminderIntervalMinutes = dialog.ReminderIntervalMinutes;
        task.CategoryColor = dialog.CategoryColor;
        task.Urls = dialog.Urls;
        task.Attachments = dialog.Attachments;
        task.Checklist = newChecklist;
        if (reminderChanged) task.LastReminderAt = task.ReminderIntervalMinutes is null ? null : DateTimeOffset.Now;
        SaveTodoTasks();
        if (changed) RecordTodoTaskChange("editada", task, DescribeTodoTaskChanges(previousTask, task));
        _todoTaskPanel?.Refresh();
    }

    private void ToggleTodoTaskChecklist(TodoTask task, int index)
    {
        if (index < 0 || index >= task.Checklist.Count) return;
        task.Checklist[index] = task.Checklist[index] with { IsCompleted = !task.Checklist[index].IsCompleted };
        var completedPercent = TodoTaskChecklistWeightPolicy.CompletedPercent(task.Checklist);
        RecordTodoTaskChange("checklist", task,
            $"Item \"{task.Checklist[index].Title}\" {(task.Checklist[index].IsCompleted ? "concluído" : "reaberto")} na tarefa {TodoTaskDisplay.Reference(task)} {task.Title} — {completedPercent:0.#}% concluído");
        if (task.CompletedAt is null && TodoTaskChecklistWeightPolicy.IsComplete(task.Checklist))
        {
            CompleteTodoTask(task);
            return;
        }
        SaveTodoTasks();
        _todoTaskPanel?.Refresh();
    }

    private void CompleteTodoTask(TodoTask task)
    {
        if (task.CompletedAt is not null) return;
        if (task.PausedAt is not null) UnpauseTodoTask(task);
        task.CompletedAt = DateTimeOffset.Now;
        SaveTodoTasks();
        RecordTodoTaskChange("concluída", task, $"Tarefa concluída: {TodoTaskDisplay.Reference(task)} {task.Title}");
        _todoTaskPanel?.Refresh();
    }

    private void ReopenTodoTask(TodoTask task)
    {
        if (task.CompletedAt is null) return;
        task.CompletedAt = null;
        SaveTodoTasks();
        RecordTodoTaskChange("reaberta", task, $"Tarefa reaberta: {TodoTaskDisplay.Reference(task)} {task.Title}");
        _todoTaskPanel?.Refresh();
    }

    private void PauseTodoTask(TodoTask task)
    {
        if (task.CompletedAt is not null || task.PausedAt is not null) return;
        var dialog = new TodoTaskPauseWindow($"{TodoTaskDisplay.Reference(task)} {task.Title}");
        if (!ShowTodoTaskDialog(dialog)) return;

        task.PausedAt = DateTimeOffset.Now;
        task.PauseReason = dialog.Reason;
        task.PausedBy = dialog.Responsible;
        SaveTodoTasks();
        var pauseDetails = string.Join(" - ", new[]
        {
            string.IsNullOrWhiteSpace(task.PausedBy) ? null : $"Responsável: {task.PausedBy}",
            string.IsNullOrWhiteSpace(task.PauseReason) ? null : $"Motivo: {task.PauseReason}"
        }.Where(detail => detail is not null));
        RecordTodoTaskChange("pausada", task, $"Tarefa pausada: {TodoTaskDisplay.Reference(task)} {task.Title}" + (pauseDetails.Length == 0 ? "" : $" - {pauseDetails}"));
        _todoTaskPanel?.Refresh();
    }

    private void UnpauseTodoTask(TodoTask task)
    {
        if (task.PausedAt is null) return;
        var pausedBy = task.PausedBy;
        task.PausedAt = null;
        task.PauseReason = string.Empty;
        task.PausedBy = string.Empty;
        task.LastReminderAt = DateTimeOffset.Now;
        SaveTodoTasks();
        RecordTodoTaskChange("destravada", task, $"Tarefa destravada por {pausedBy}: {TodoTaskDisplay.Reference(task)} {task.Title}");
        _todoTaskPanel?.Refresh();
    }

    private bool ShowTodoTaskDialog(Window dialog)
    {
        var panel = _todoTaskPanel;
        if (panel is null || !panel.IsVisible) return dialog.ShowDialog() == true;

        panel.SetModalChildActive(true);
        dialog.Owner = panel;
        try
        {
            return dialog.ShowDialog() == true;
        }
        finally
        {
            panel.SetModalChildActive(false);
        }
    }

    private void DeleteTodoTask(TodoTask task)
    {
        _todoTasks.Remove(task);
        SaveTodoTasks();
        RecordTodoTaskChange("excluída", task, $"Tarefa excluída: {TodoTaskDisplay.Reference(task)} {task.Title}");
        _todoTaskPanel?.Refresh();
    }

    private void RecordTodoTaskChange(string change, TodoTask task, string summary)
        => RecordActionHistory($"Tarefa {change} — {TodoTaskDisplay.Reference(task)} {task.Title}", string.Empty, summary);

    private static string DescribeTodoTaskChanges(TodoTask before, TodoTask after)
    {
        var changes = new List<string>();
        var taskLabel = $"na tarefa {TodoTaskDisplay.Reference(after)} {after.Title}";
        if (before.Title != after.Title) changes.Add($"Título alterado para: {after.Title}");
        if (before.Description != after.Description) changes.Add("Descrição alterada");
        if (before.CategoryColor != after.CategoryColor) changes.Add($"Prioridade alterada para {after.CategoryColor}");
        if (before.ReminderIntervalMinutes != after.ReminderIntervalMinutes)
            changes.Add(after.ReminderIntervalMinutes is { } interval ? $"Lembrete alterado para cada {interval} minutos" : "Lembrete desativado");
        changes.AddRange(DescribeLineChanges(before.Attachments, after.Attachments, "anexo", taskLabel));
        changes.AddRange(DescribeLineChanges(before.Urls, after.Urls, "URL", taskLabel));

        var beforeByTitle = before.Checklist.GroupBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.CurrentCultureIgnoreCase);
        var afterByTitle = after.Checklist.GroupBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.CurrentCultureIgnoreCase);
        changes.AddRange(after.Checklist.Where(item => !beforeByTitle.ContainsKey(item.Title)).Select(item => $"Etapa adicionada: {item.Title}"));
        changes.AddRange(before.Checklist.Where(item => !afterByTitle.ContainsKey(item.Title)).Select(item => $"Etapa removida: {item.Title}"));
        foreach (var item in after.Checklist.Where(item => beforeByTitle.ContainsKey(item.Title)))
        {
            var previous = beforeByTitle[item.Title];
            if (previous.Weight != item.Weight) changes.Add($"Peso da etapa \"{item.Title}\" alterado para {item.Weight:0.#}%");
            if (previous.IsCompleted != item.IsCompleted) changes.Add($"Etapa \"{item.Title}\" {(item.IsCompleted ? "concluída" : "reaberta")}");
        }
        return changes.Count == 0 ? "Nenhuma alteração registrada" : string.Join(Environment.NewLine, changes);
    }

    private static IEnumerable<string> DescribeLineChanges(string before, string after, string itemType, string taskLabel)
    {
        var oldItems = before.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Select(item => item.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newItems = after.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Select(item => item.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return newItems.Except(oldItems).Select(item => $"Adicionado {itemType} {item} {taskLabel}")
            .Concat(oldItems.Except(newItems).Select(item => $"Removido {itemType} {item} {taskLabel}"));
    }

    private void SaveTodoTasks() => _todoTaskStore.SaveAll(_todoTasks.Select(ToTodoTaskEntry));

    private void CheckTodoTaskReminders(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.Now;
        var dueTasks = _todoTasks.Where(task => task.CompletedAt is null && task.PausedAt is null && task.ReminderIntervalMinutes is { } interval &&
            now - (task.LastReminderAt ?? task.CreatedAt) >= TimeSpan.FromMinutes(interval)).ToList();
        foreach (var task in dueTasks)
        {
            task.LastReminderAt = now;
            _todoTaskStore.MarkReminderSent(task.Id, now);
            ActivationSoundService.PlayTaskReminder(_settings.TaskReminderSound);
            Notify("Lembrete de tarefa", $"{TodoTaskDisplay.Reference(task)} {task.Title}", true,
                displayDuration: TimeSpan.FromSeconds(12));
        }
    }

    private static TodoTaskEntry ToTodoTaskEntry(TodoTask task)
        => new(task.Id, task.Title, task.Description, task.CreatedAt, task.CompletedAt, task.TaskNumber,
            task.ReminderIntervalMinutes, task.CategoryColor, task.LastReminderAt, task.Urls, task.Attachments,
            task.PausedAt, task.PauseReason, task.PausedBy, task.Checklist);

    private static TodoTask FromTodoTaskEntry(TodoTaskEntry task)
        => new() { Id = task.Id, Title = task.Title, Description = task.Description, CreatedAt = task.CreatedAt, CompletedAt = task.CompletedAt,
            TaskNumber = task.TaskNumber, ReminderIntervalMinutes = task.ReminderIntervalMinutes, CategoryColor = task.CategoryColor,
            LastReminderAt = task.LastReminderAt, Urls = task.Urls, Attachments = task.Attachments, PausedAt = task.PausedAt,
            PauseReason = task.PauseReason, PausedBy = task.PausedBy, Checklist = task.Checklist ?? [] };

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
                var historyId = RecordHistory("Montar SQL IN", input, result, voiceCommand);
                var successMessage = WithConversionCounts("Lista formatada para SQL IN e pronta para colar.", result.InputItemCount, result.OutputItemCount);
                Notify("JMD", successMessage, true, historyId);
                return;
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
            var historyId = result.Success ? RecordSelectionHistory(result, voiceCommand) : null;
            var message = result.Success
                ? WithConversionCounts(result.Message, result.InputItemCount, result.OutputItemCount)
                : result.Message;
            Notify("JMD", message, result.Success, historyId);
        }
        catch (Exception exception) { Notify("JMD", $"Falha ao substituir a seleção: {exception.Message}", false); }
        finally { _conversionRunning = false; }
    }

    private long? RecordHistory(string commandName, string beforeValue, TransformationResult result, VoiceCommandInvocation? voiceCommand = null)
    {
        if (!result.Success || result.Value is null || _historyStore is null) return null;
        try
        {
            if (voiceCommand is not null)
                return RecordCommandHistory(commandName, beforeValue, result.Value, voiceCommand);
            else if (_historyRecorder?.RecordIfSuccessful(commandName, beforeValue, result, GetConversionSource()) == true)
            {
                RefreshHistory();
                return _historyStore.GetLatest()?.Id;
            }
            return null;
        }
        catch (Exception exception)
        {
            SetStatus($"Conversão concluída, mas não foi possível salvar no histórico: {exception.Message}", false);
            return null;
        }
    }

    private long? RecordSelectionHistory(SelectionTransformResult result, VoiceCommandInvocation? voiceCommand = null)
    {
        if (!result.Success || result.BeforeValue is null || result.AfterValue is null || _historyStore is null) return null;
        try
        {
            var commandName = result.UsedClipboardFallback
                ? "Formatar clipboard (atalho de seleção)"
                : "Substituir seleção (SQL IN)";
            if (voiceCommand is not null)
                return RecordCommandHistory(commandName, result.BeforeValue, result.AfterValue, voiceCommand);
            else if (_historyRecorder?.RecordIfSuccessful(commandName, result, GetConversionSource()) == true)
            {
                RefreshHistory();
                return _historyStore.GetLatest()?.Id;
            }
            return null;
        }
        catch (Exception exception)
        {
            SetStatus($"Conversão concluída, mas não foi possível salvar no histórico: {exception.Message}", false);
            return null;
        }
    }

    private long? RecordCommandHistory(string commandName, string beforeValue, string afterValue,
        VoiceCommandInvocation? voiceCommand = null, bool isFailure = false, string? failureDetails = null)
    {
        if (_historyStore is null) return null;
        var historyCommandName = voiceCommand is null
            ? commandName
            : $"Ativação por voz (Jey) - {voiceCommand.CommandName}";
        var historyId = _historyStore.Add(historyCommandName, beforeValue, afterValue,
            source: GetConversionSource(), isVoiceTriggered: voiceCommand is not null,
            voiceTranscript: voiceCommand?.Transcript, voiceAudioWav: voiceCommand?.AudioWav,
            voiceDurationMilliseconds: voiceCommand?.DebugInfo?.DurationMilliseconds,
            voiceCutMilliseconds: voiceCommand?.DebugInfo?.CutMilliseconds,
            voiceTranscriptionAudioWav: voiceCommand?.DebugInfo?.TranscriptionAudioWav,
            portugueseTranscriptionTimeMilliseconds: voiceCommand?.DebugInfo?.PortugueseTranscriptionTimeMilliseconds,
            englishTranscriptionTimeMilliseconds: voiceCommand?.DebugInfo?.EnglishTranscriptionTimeMilliseconds,
            isFailure: isFailure,
            failureDetails: failureDetails);
        RefreshHistory();
        return historyId;
    }

    private void RefreshHistory()
    {
        if (_historyStore is null) return;
        try
        {
            var query = HistorySearchBox?.Text ?? string.Empty;
            var totalEntries = _historyStore.CountSearch(query);
            _historyPageCount = (totalEntries + HistoryPageSize - 1) / HistoryPageSize;
            _historyPageIndex = Math.Clamp(_historyPageIndex, 0, Math.Max(0, _historyPageCount - 1));
            var entries = _historyStore.SearchPage(query, HistoryPageSize, _historyPageIndex)
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
                    HistoryEntryDisplayPolicy.IsApplicationLaunch(entry.CommandName, entry.BeforeValue, entry.AfterValue),
                    HistoryEntryDisplayPolicy.IsWorkflowExecution(entry.CommandName),
                    entry.CommandName.StartsWith("Tarefa ", StringComparison.OrdinalIgnoreCase),
                    entry.IsFailure,
                    entry.FailureDetails ?? string.Empty,
                    entry.CommandName.EndsWith(DraftStack.SavedCommandName, StringComparison.Ordinal),
                    entry.CommandName.EndsWith(DraftStack.RestoredCommandName, StringComparison.Ordinal),
                    entry.Id == _historyTargetId))
                .ToList();
            HistoryItemsControl.ItemsSource = entries;
            HistoryScrollViewer.ScrollToTop();
            HistoryPageText.Text = _historyPageCount == 0
                ? "Página 0 de 0"
                : $"Página {_historyPageIndex + 1} de {_historyPageCount}";
            PreviousHistoryPageButton.IsEnabled = _historyPageIndex > 0;
            NextHistoryPageButton.IsEnabled = _historyPageIndex + 1 < _historyPageCount;
            HistoryEmptyText.Text = entries.Count == 0
                ? string.IsNullOrWhiteSpace(HistorySearchBox?.Text) ? "Nenhuma conversão ou alteração de tarefa registrada." : "Nenhum resultado para essa busca."
                : string.Empty;
            HistoryEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception exception)
        {
            HistoryItemsControl.ItemsSource = null;
            _historyPageCount = 0;
            HistoryPageText.Text = "Página 0 de 0";
            PreviousHistoryPageButton.IsEnabled = false;
            NextHistoryPageButton.IsEnabled = false;
            HistoryEmptyText.Text = "Não foi possível consultar o histórico.";
            HistoryEmptyText.Visibility = Visibility.Visible;
            SetStatus($"Falha ao consultar o histórico: {exception.Message}", false);
        }
    }

    private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _historyPageIndex = 0;
        RefreshHistory();
    }

    private void PreviousHistoryPage_Click(object sender, RoutedEventArgs e)
    {
        if (_historyPageIndex == 0) return;
        _historyPageIndex--;
        RefreshHistory();
    }

    private void NextHistoryPage_Click(object sender, RoutedEventArgs e)
    {
        if (_historyPageIndex + 1 >= _historyPageCount) return;
        _historyPageIndex++;
        RefreshHistory();
    }

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

    private sealed record VoiceCommandMatch(string CommandId, IReadOnlyDictionary<string, string> Variables);
    private sealed record VoiceCommandInvocation(string CommandName, string Transcript, byte[] AudioWav, VoiceCaptureDebugInfo? DebugInfo,
        IReadOnlyDictionary<string, string>? Variables = null);
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
        bool IsApplicationLaunch,
        bool IsWorkflowExecution,
        bool IsTaskAction,
        bool IsFailure,
        string FailureDetails,
        bool IsDraftSaved,
        bool IsDraftRestored,
        bool IsNotificationTarget);

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
        else if (id is "upperCase" or "lowerCase" or "titleCase") await TransformTextCaseFromPaletteAsync(id);
        else if (id is "jsonPretty" or "jsonCompact")
        {
            await TransformStructuredDataFromPaletteAsync(id == "jsonPretty" ? StructuredDataLayout.Pretty : StructuredDataLayout.Compact);
        }
        else if (id == "snippets") await InsertSavedSnippetAsync();
        else if (id == "saveSnippet") await CaptureSnippetAsync();
        else if (id == "createCustomShortcut") CreateCustomShortcutFromDialog();
        else if (id == "draftSave") await SaveDraftAsync();
        else if (id == "draftRestore") await RestoreDraftAsync();
        else if (id == "todoCreate") await CreateTodoTaskAsync();
        else if (id == "todoPanel") ShowTodoTaskPanel();
        else if (id == "todoPanelClose") _todoTaskPanel?.Hide();
        else if (id == "about") AboutMenuItem_Click(this, new RoutedEventArgs());
        else if (id == "checkUpdates")
        {
            AboutMenuItem_Click(this, new RoutedEventArgs());
            CheckUpdatesButton_Click(this, new RoutedEventArgs());
        }
        else if (_settings.CustomShortcuts.FirstOrDefault(shortcut => shortcut.Id == id) is { } customShortcut)
            await RunCustomWorkflowAsync(customShortcut);
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
        BuildSettingsPanel();
        CommandsList.Visibility = Visibility.Collapsed;
        SettingsTabs.Visibility = Visibility.Visible;
        SettingsPage.Visibility = Visibility.Visible;
        WorkflowSettingsPage.Visibility = Visibility.Collapsed;
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

    private void ShowWorkflowSettingsPage()
    {
        _navigation.ShowShortcutSettings();
        BuildWorkflowSettingsPanel();
        CommandsList.Visibility = Visibility.Collapsed;
        SettingsTabs.Visibility = Visibility.Visible;
        SettingsPage.Visibility = Visibility.Collapsed;
        WorkflowSettingsPage.Visibility = Visibility.Visible;
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
        PageTitle.Text = "Workflows personalizados";
        PageSubtitle.Text = "Crie fluxos de ações e gerencie as variáveis usadas por eles";
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
        WorkflowSettingsPage.Visibility = Visibility.Collapsed;
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
        WorkflowSettingsPage.Visibility = Visibility.Collapsed;
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
        service.WakeWordProcessingStarted += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(_voiceWakeWord, service) || !_settings.VoiceWakeWordEnabled) return;
            ShowJeyProcessingNotification("Jey reconhecido · identificando o comando…");
            ActivationSoundService.PlayJeyRecognized(_settings.JeyRecognizedSound);
        }));
        service.WakeWordProcessingEnded += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ReferenceEquals(_voiceWakeWord, service)) CloseJeyProcessingNotification();
        }));
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
        service.DictationTextRecognized += text => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(_voiceWakeWord, service)) return;
            if (_keyboard.GetForegroundWindow() != _dictationTargetWindow || !_focusedText.HasEditableTextFocus())
            {
                service.StopDictation();
                SetStatus("Transcrição encerrada porque o foco saiu do campo de texto.", false);
                return;
            }
            InsertDictationText(text);
        }));
        service.DictationPartialRecognized += text => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(_voiceWakeWord, service) || _dictationTargetWindow == IntPtr.Zero) return;
            if (_keyboard.GetForegroundWindow() != _dictationTargetWindow || !_focusedText.HasEditableTextFocus())
            {
                service.StopDictation();
                SetStatus("Transcrição encerrada porque o foco saiu do campo de texto.", false);
                return;
            }
            ShowJeyProcessingNotification($"Transcrevendo…\n{text}");
        }));
        service.DictationEnded += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!ReferenceEquals(_voiceWakeWord, service)) return;
            _dictationTargetWindow = IntPtr.Zero;
            _dictationHasInsertedText = false;
            CloseJeyProcessingNotification();
            SetStatus("Transcrição encerrada após 3 segundos sem fala.", true);
        }));
        service.WakeWordRecognized += (transcript, audioWav, debugInfo) => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_settings.VoiceWakeWordEnabled && ReferenceEquals(_voiceWakeWord, service))
            {
                var wakeWordOnly = NormalizeVoicePhrase(transcript).Length == 0;
                var voiceMatch = FindVoiceCommand(transcript);
                var voiceCommandId = voiceMatch?.CommandId;
                if (voiceMatch is not null && transcript is not null)
                {
                    CloseJeyProcessingNotification();
                    var commandName = GetVoiceCommandName(voiceMatch.CommandId);
                    _ = HandleHotkeyAsync(voiceMatch.CommandId, new VoiceCommandInvocation(commandName, transcript, audioWav, debugInfo, voiceMatch.Variables));
                    return;
                }

                if (wakeWordOnly)
                    ShowJeyProcessingNotification("Jey reconhecido · aguardando o comando…");
                else
                    CloseJeyProcessingNotification();

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
                if (voiceCommandId != "transcribe" && IsVisible)
                {
                    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                    Activate();
                }
                else if (voiceCommandId == "transcribe")
                {
                    // Keep the foreground text editor active while dictation runs.
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

    private void StartDictation(string? transcript)
    {
        if (_voiceWakeWord is null || !_focusedText.HasEditableTextFocus())
        {
            Notify("JMD", "Para transcrever, deixe o cursor em um campo de texto editável.", false);
            return;
        }
        _dictationTargetWindow = _keyboard.GetForegroundWindow();
        if (_dictationTargetWindow == IntPtr.Zero)
        {
            Notify("JMD", "Não foi possível identificar a janela do campo de texto.", false);
            return;
        }

        _dictationHasInsertedText = false;
        var phrase = _settings.VoicePhrases.TryGetValue("transcribe", out var phrases)
            ? phrases.FirstOrDefault(value => VoicePhrasePolicy.StartsWith(value, transcript))
                ?? phrases.FirstOrDefault(value => VoicePhrasePolicy.Matches(value, transcript))
            : null;
        var initialText = VoicePhrasePolicy.RemovePrefix(phrase, transcript);
        if (!string.IsNullOrWhiteSpace(initialText)) InsertDictationText(initialText);
        _voiceWakeWord.StartDictation();
        ShowJeyProcessingNotification("Transcrevendo…");
        SetStatus("Transcrição do Jey iniciada. Uma pausa de 3 segundos encerra o ditado.", true);
    }

    private void InsertDictationText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var normalizedText = _dictationHasInsertedText && char.IsLetterOrDigit(text[0]) ? " " + text : text;
        if (!_keyboard.SendUnicodeText(normalizedText))
        {
            _voiceWakeWord?.StopDictation();
            SetStatus("Não foi possível digitar a transcrição no campo ativo.", false);
            return;
        }
        _dictationHasInsertedText = true;
    }

    private void StopVoiceWakeWord()
    {
        CloseJeyProcessingNotification();
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

    public void ShowHistory() => ShowHistory(null);

    public void ShowHistory(long? historyId)
    {
        _historyTargetId = historyId;
        if (historyId is not null && HistorySearchBox is not null)
            HistorySearchBox.Text = string.Empty;
        _historyPageIndex = historyId is { } targetId && _historyStore is not null
            ? _historyStore.GetPageIndexContaining(targetId, HistorySearchBox?.Text ?? string.Empty, HistoryPageSize)
            : 0;
        _navigation.ShowHistory();
        CommandsList.Visibility = Visibility.Collapsed;
        SettingsTabs.Visibility = Visibility.Visible;
        SettingsPage.Visibility = Visibility.Collapsed;
        WorkflowSettingsPage.Visibility = Visibility.Collapsed;
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
        if (historyId is not null)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                HistoryItemsControl.UpdateLayout();
                var entry = HistoryItemsControl.Items.OfType<HistoryDisplayEntry>().FirstOrDefault(item => item.Id == historyId);
                if (entry is not null)
                    (HistoryItemsControl.ItemContainerGenerator.ContainerFromItem(entry) as FrameworkElement)?.BringIntoView();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
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

    private void HideFromCaptureToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingCaptureExclusionToggle || sender is not CheckBox toggle) return;
        var enabled = toggle.IsChecked == true;
        if (!TrySetCaptureExcluded(enabled))
        {
            _ = TrySetCaptureExcluded(_settings.HideFromScreenCapture);
            _updatingCaptureExclusionToggle = true;
            toggle.IsChecked = _settings.HideFromScreenCapture;
            _updatingCaptureExclusionToggle = false;
            SetStatus(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) || !enabled
                ? "O Windows não aceitou a solicitação para ocultar a janela em capturas."
                : "Ocultar a janela em capturas requer Windows 10 versão 2004 ou posterior.", false);
            return;
        }

        _settings.HideFromScreenCapture = enabled;
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
            ? "A janela do JMD será omitida das capturas compatíveis com o Windows."
            : "A janela do JMD voltou a aparecer nas capturas.", true);
    }

    private bool TrySetCaptureExcluded(bool enabled)
    {
        if (enabled && !ScreenCaptureExclusionPolicy.IsSupported(Environment.OSVersion.Version)) return false;
        var accepted = true;
        foreach (Window window in System.Windows.Application.Current.Windows)
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle != IntPtr.Zero && !SetWindowDisplayAffinity(handle,
                    enabled ? WindowDisplayAffinityExcludeFromCapture : WindowDisplayAffinityNone))
                accepted = false;
        }
        return accepted;
    }

    internal void PrepareWindowForCaptureExclusion(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            if (ScreenCaptureExclusionPolicy.IsSupported(Environment.OSVersion.Version) &&
                _settings.HideFromScreenCapture && !TrySetCaptureExcluded(window, true))
                SetStatus("O Windows não aceitou ocultar uma janela do JMD nas capturas.", false);
        };
    }

    private static bool TrySetCaptureExcluded(Window window, bool enabled)
    {
        if (enabled && !ScreenCaptureExclusionPolicy.IsSupported(Environment.OSVersion.Version)) return false;
        var handle = new WindowInteropHelper(window).Handle;
        return handle != IntPtr.Zero && SetWindowDisplayAffinity(handle,
            enabled ? WindowDisplayAffinityExcludeFromCapture : WindowDisplayAffinityNone);
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
        WorkflowSettingsPage.Visibility = Visibility.Collapsed;
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

    private static void SelectAdvancedSound(ComboBox combo, string sound)
    {
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, sound, StringComparison.Ordinal)) ?? combo.Items[0];
    }

    private void AdvancedSoundSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAdvancedSoundSettings || sender is not ComboBox combo || combo.SelectedItem is not ComboBoxItem selected || selected.Tag is not string sound)
            return;

        switch (combo.Name)
        {
            case nameof(ShortcutSoundCombo): _settings.ShortcutActivatedSound = sound; break;
            case nameof(VoiceCommandSoundCombo): _settings.VoiceCommandFoundSound = sound; break;
            case nameof(JeySoundCombo): _settings.JeyRecognizedSound = sound; break;
            case nameof(TaskReminderSoundCombo): _settings.TaskReminderSound = sound; break;
            default: return;
        }

        try
        {
            _settingsStore.Save(_settings);
            SetStatus("Preferência de som salva.", true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Não foi possível salvar a preferência de som: {exception.Message}", false);
        }
    }

    private void PreviewSignalSound_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string signal }) return;
        switch (signal)
        {
            case "shortcut": ActivationSoundService.PlayShortcutActivated(GetSelectedSound(ShortcutSoundCombo)); break;
            case "voice": ActivationSoundService.PlayVoiceCommandFound(GetSelectedSound(VoiceCommandSoundCombo)); break;
            case "jey": ActivationSoundService.PlayJeyRecognized(GetSelectedSound(JeySoundCombo)); break;
            case "reminder": ActivationSoundService.PlayTaskReminder(GetSelectedSound(TaskReminderSoundCombo)); break;
        }
    }

    private static string GetSelectedSound(ComboBox combo)
        => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? ActivationSoundService.None;

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
        SilentUpdateToggle.Visibility = Visibility.Collapsed;
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
        SilentUpdateToggle.Visibility = Visibility.Collapsed;
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
                {
                    SilentUpdateToggle.IsChecked = _settings.SilentUpdatesEnabled;
                    SilentUpdateToggle.Visibility = Visibility.Visible;
                    InstallUpdateButton.Visibility = Visibility.Visible;
                }
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
                Arguments = _settings.SilentUpdatesEnabled ? "/S" : string.Empty,
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

    private void SilentUpdateToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_settings is null || SilentUpdateToggle is null) return;
        _settings.SilentUpdatesEnabled = SilentUpdateToggle.IsChecked == true;
        try { _settingsStore.Save(_settings); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            UpdateStatusText.Text = $"A preferência de atualização silenciosa não pôde ser salva: {exception.Message}";
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
        var dynamicMatch = FindTypedWorkflowMatch(query);
        foreach (var command in _commands)
        {
            var phrases = command.VoicePhrasesDisplay
                .Replace("Jey:", string.Empty, StringComparison.CurrentCultureIgnoreCase)
                .Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var fields = new[] { command.Name, command.Description, command.Shortcut };
            var exactMatch = fields.Any(field => string.Equals(field, query, StringComparison.CurrentCultureIgnoreCase)) ||
                phrases.Any(phrase => string.Equals(phrase, query, StringComparison.CurrentCultureIgnoreCase));
            var startsWithMatch = fields.Any(field => field.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)) ||
                phrases.Any(phrase => phrase.StartsWith(query, StringComparison.CurrentCultureIgnoreCase));
            var containsMatch = fields.Any(field => field.Contains(query, StringComparison.CurrentCultureIgnoreCase)) ||
                phrases.Any(phrase => phrase.Contains(query, StringComparison.CurrentCultureIgnoreCase));

            command.SearchRank = query.Length == 0 || exactMatch ? 0 : startsWithMatch ? 1 : 2;
            command.Visible = query.Length == 0 || containsMatch || command.Id == dynamicMatch?.CommandId;
        }
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
            var dynamicMatch = FindTypedWorkflowMatch(SearchBox.Text);
            if (dynamicMatch is not null)
            {
                _ = ExecuteCommandAsync(dynamicMatch.CommandId, dynamicMatch.Variables);
                e.Handled = true;
                return;
            }
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
        else if (e.Key == Key.Up && CommandsList.IsKeyboardFocusWithin &&
                 CommandsList.SelectedItem is CommandRow selected &&
                 ReferenceEquals(selected, _commands.FirstOrDefault(command => command.Visible)))
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        }
    }

    private async Task ExecuteCommandAsync(string id, IReadOnlyDictionary<string, string>? dynamicVariables = null)
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
        if (id is "upperCase" or "lowerCase" or "titleCase")
            await TransformTextCaseFromPaletteAsync(id);
        if (id is "jsonPretty" or "jsonCompact")
            await TransformStructuredDataFromPaletteAsync(id == "jsonPretty" ? StructuredDataLayout.Pretty : StructuredDataLayout.Compact);
        if (id == "snippets") await InsertSavedSnippetAsync();
        if (id == "saveSnippet") await CaptureSnippetAsync();
        if (id == "createCustomShortcut") CreateCustomShortcutFromDialog();
        if (_settings.CustomShortcuts.FirstOrDefault(shortcut => shortcut.Id == id) is { } customShortcut)
            await RunCustomWorkflowAsync(customShortcut, dynamicVariables: dynamicVariables);
    }

    internal async Task<JmdCliResponse> ExecuteCliRequestAsync(JmdCliRequest request)
    {
        if (!Dispatcher.CheckAccess())
            return await Dispatcher.InvokeAsync(() => ExecuteCliRequestAsync(request)).Task.Unwrap();
        if (request.Operation == "list")
        {
            var lines = _commands.OrderBy(command => command.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select((command, index) => request.Verbose
                    ? $"{index + 1}. {command.Name}{Environment.NewLine}   Atalho: {command.Shortcut}{Environment.NewLine}   Estado: {(_settings.DisabledCommands.Contains(command.Id) ? "desativado" : "ativo")}{Environment.NewLine}   {command.Description}"
                    : $"{index + 1}. {command.Name} [{command.Shortcut}]");
            return new JmdCliResponse(true, string.Join(Environment.NewLine, lines), 0);
        }
        if (request.Operation == "invalid")
            return new JmdCliResponse(false, request.Query, 2);
        if ((request.Operation is "search" or "execute" or "run") && string.IsNullOrWhiteSpace(request.Query) && request.Operation != "run")
            return new JmdCliResponse(false, "Informe o texto para a busca. Use jmd -help para ver as opções.", 2);
        if (request.Operation is not ("search" or "execute" or "run" or "complete"))
            return new JmdCliResponse(false, "Opção da CLI desconhecida. Use jmd -help.", 2);

        var results = request.Operation == "complete"
            ? _commands.Where(command => command.Name.StartsWith(request.Query, StringComparison.CurrentCultureIgnoreCase)).OrderBy(command => command.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
            : string.IsNullOrWhiteSpace(request.Query)
                ? _commands.OrderBy(command => command.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
                : GetCliSearchResults(request.Query);
        if (request.Operation == "complete")
            return new JmdCliResponse(true, string.Join(Environment.NewLine, results.Select(command => command.Name)), 0);
        if (results.Count == 0)
            return new JmdCliResponse(false, $"Comando não encontrado: {request.Query}", 1);
        if (request.Operation == "search")
            return new JmdCliResponse(true, string.Join(Environment.NewLine, results.Select((command, index) => $"{index + 1}. {command.Name} [{command.Shortcut}]")), 0);

        var selectedIndex = JmdCommandLinePolicy.ResolveSelectedIndex(request.Operation, request.Index);
        if (!JmdCommandLinePolicy.IsValidSelectedIndex(selectedIndex, results.Count))
            return new JmdCliResponse(false, $"Não existe resultado número {request.Index}.", 1);
        var command = results[selectedIndex];
        if (_settings.DisabledCommands.Contains(command.Id))
            return new JmdCliResponse(false, $"O comando ‘{command.Name}’ está desativado.", 2);

        var dynamicMatch = FindTypedWorkflowMatch(request.Query);
        Hide();
        var executionState = new CliCommandExecutionState();
        var previousExecutionState = _cliCommandExecution.Value;
        _cliCommandExecution.Value = executionState;
        bool succeeded;
        try
        {
            succeeded = await HandleHotkeyAsync(command.Id,
                dynamicVariables: dynamicMatch?.CommandId == command.Id ? dynamicMatch.Variables : null);
        }
        finally { _cliCommandExecution.Value = previousExecutionState; }
        if (!succeeded || executionState.Failed)
            return new JmdCliResponse(false, $"Falha ao executar o comando: {command.Name}", 2);
        return new JmdCliResponse(true, $"Comando acionado: {command.Name}", 0);
    }

    private List<CommandRow> GetCliSearchResults(string query)
    {
        var exactWorkflow = _settings.CustomShortcuts.FirstOrDefault(shortcut =>
            string.Equals(shortcut.Name.Trim(), query.Trim(), StringComparison.CurrentCultureIgnoreCase));
        SearchBox.Text = query.Trim();
        var results = CommandsList.Items.OfType<CommandRow>().ToList();
        if (exactWorkflow is not null && _commands.FirstOrDefault(command => command.Id == exactWorkflow.Id) is { } exactCommand)
        {
            results.Remove(exactCommand);
            results.Insert(0, exactCommand);
        }
        else if (FindTypedWorkflowMatch(query) is { } dynamicMatch && _commands.FirstOrDefault(command => command.Id == dynamicMatch.CommandId) is { } dynamicCommand)
        {
            results.Remove(dynamicCommand);
            results.Insert(0, dynamicCommand);
        }
        return results;
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

    private async Task TransformTextCaseFromPaletteAsync(string id)
    {
        if (_previousWindow == IntPtr.Zero || _keyboard.GetForegroundWindow() != _previousWindow)
        {
            if (_previousWindow == IntPtr.Zero || !SetForegroundWindow(_previousWindow))
            {
                Notify("JMD", "Não foi possível devolver o foco ao aplicativo com a seleção.", false);
                return;
            }
            await Task.Delay(80);
        }
        if (_keyboard.GetForegroundWindow() != _previousWindow)
        {
            Notify("JMD", "Não foi possível devolver o foco ao aplicativo com a seleção.", false);
            return;
        }
        await TransformTextCaseAsync(id);
    }

    private async Task TransformTextCaseAsync(string id, VoiceCommandInvocation? voiceCommand = null)
    {
        if (_conversionRunning) return;
        var (name, transform) = id switch
        {
            "upperCase" => ("UpperCase", (Func<string, string>)TextCaseTransform.Upper),
            "lowerCase" => ("LowerCase", (Func<string, string>)TextCaseTransform.Lower),
            _ => ("TitleCase", (Func<string, string>)TextCaseTransform.Title)
        };
        _conversionRunning = true;
        try
        {
            var result = await _selectionTransformer.ExecuteAsync(
                input => TransformationResult.Ok(transform(input)), allowClipboardFallback: false);
            var historyId = result.Success ? RecordSelectionHistory(name, result, voiceCommand) : null;
            Notify("JMD", result.Success ? $"Texto selecionado transformado para {name}." : result.Message, result.Success, historyId);
        }
        catch (Exception exception) { Notify("JMD", $"Falha ao transformar a seleção: {exception.Message}", false); }
        finally { _conversionRunning = false; }
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
                var historyId = RecordSelectionHistory(commandName, result, voiceCommand);
                var action = layout == StructuredDataLayout.Pretty ? "formatado" : "compactado";
                var subject = detectedFormat == StructuredDataFormat.Json ? "JSON" : "XML";
                var target = result.UsedClipboardFallback ? "O texto do clipboard foi" : "O texto selecionado foi";
                Notify("JMD", $"{target} {subject} {action}.", true, historyId);
            }
            else
            {
                Notify("JMD", result.Message, false);
            }
        }
        catch (Exception exception) { Notify("JMD", $"Falha ao transformar JSON/XML: {exception.Message}", false); }
        finally { _conversionRunning = false; }
    }

    private long? RecordSelectionHistory(string commandName, SelectionTransformResult result, VoiceCommandInvocation? voiceCommand = null)
    {
        if (!result.Success || result.BeforeValue is null || result.AfterValue is null || _historyStore is null) return null;
        try
        {
            if (voiceCommand is not null)
                return RecordCommandHistory(commandName, result.BeforeValue, result.AfterValue, voiceCommand);
            else if (_historyRecorder?.RecordIfSuccessful(commandName, result, GetConversionSource()) == true)
            {
                RefreshHistory();
                return _historyStore.GetLatest()?.Id;
            }
            return null;
        }
        catch (Exception exception)
        {
            SetStatus($"Transformação concluída, mas não foi possível salvar no histórico: {exception.Message}", false);
            return null;
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

    private void Notify(string title, string message, bool success, long? historyId = null,
        TimeSpan? displayDuration = null)
    {
        if (!success && _cliCommandExecution.Value is { } executionState) executionState.Failed = true;
        SetStatus(message, success);
        TrayNotification?.Invoke(title, message, _settings.UseJmdNotifications, historyId,
            displayDuration ?? TimeSpan.FromSeconds(4));
    }

    private sealed class CliCommandExecutionState
    {
        public bool Failed { get; set; }
    }

    private void ShowJeyProcessingNotification(string message)
    {
        if (_jeyProcessingNotification is { IsVisible: true } current)
        {
            current.UpdateProcessingMessage(message);
            return;
        }

        var notification = new JmdNotificationWindow(message, historyId: null, isProcessing: true);
        PrepareWindowForCaptureExclusion(notification);
        _jeyProcessingNotification = notification;
        notification.Closed += (_, _) =>
        {
            if (ReferenceEquals(_jeyProcessingNotification, notification)) _jeyProcessingNotification = null;
        };
        notification.Show();
    }

    private void CloseJeyProcessingNotification()
    {
        var notification = _jeyProcessingNotification;
        _jeyProcessingNotification = null;
        if (notification?.IsVisible == true) notification.Close();
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
        _todoReminderTimer.Stop();
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

    private static bool TrySetNativeRoundedCorners(IntPtr window)
    {
        var preference = DwmWindowCornerPreferenceRound;
        return DwmSetWindowAttribute(window, DwmWindowCornerPreferenceAttribute, ref preference, sizeof(int)) == 0;
    }

    private void ApplyRoundedWindowRegion()
    {
        var window = new WindowInteropHelper(this).Handle;
        if (window == IntPtr.Zero || !GetWindowRect(window, out var rect)) return;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var cornerDiameter = Math.Max(1, (int)Math.Round(36 * scale));
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, cornerDiameter, cornerDiameter);
        if (region == IntPtr.Zero) return;
        if (SetWindowRgn(window, region, true) == 0)
            DeleteObject(region);
    }

    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindowNative(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int valueSize);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

internal static class CustomCommandLine
{
    public static string JoinArguments(IEnumerable<string> arguments)
        => string.Join(" ", arguments.Select(QuoteWindowsArgument));

    private static string QuoteWindowsArgument(string argument)
    {
        var result = new System.Text.StringBuilder(argument.Length + 2);
        result.Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
            }
            else
            {
                result.Append('\\', backslashes);
                result.Append(character);
            }
            backslashes = 0;
        }
        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    public static bool TryParse(string commandLine, out string executable, out IReadOnlyList<string> arguments)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var started = false;

        foreach (var character in commandLine)
        {
            if (character == '"')
            {
                quoted = !quoted;
                started = true;
            }
            else if (char.IsWhiteSpace(character) && !quoted)
            {
                if (!started) continue;
                parts.Add(current.ToString());
                current.Clear();
                started = false;
            }
            else
            {
                current.Append(character);
                started = true;
            }
        }

        if (quoted)
        {
            executable = string.Empty;
            arguments = [];
            return false;
        }
        if (started) parts.Add(current.ToString());
        if (parts.Count == 0 || string.IsNullOrWhiteSpace(parts[0]))
        {
            executable = string.Empty;
            arguments = [];
            return false;
        }

        executable = parts[0];
        arguments = parts.Skip(1).ToArray();
        return true;
    }
}

internal sealed class CommandRow(string id, string name, string description, string shortcut) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    private int _searchRank;
    public int SearchRank { get => _searchRank; set { _searchRank = value; Changed(nameof(SearchRank)); } }
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
