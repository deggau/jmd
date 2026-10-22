using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using JMD.Core;
using Forms = System.Windows.Forms;

namespace JMD.App;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private Forms.NotifyIcon? _trayIcon;
    private Icon? _appIcon;
    private MainWindow? _mainWindow;
    private EventWaitHandle? _updateSignal;
    private CancellationTokenSource? _updateListenerCancellation;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var updateOnStartup = AppStartupOptions.StartsUpdate(e.Args);
        _updateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\JMD.UpdateRequested");
        _instanceMutex = new Mutex(true, @"Local\JMD.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            if (updateOnStartup) _updateSignal.Set();
            ShowExistingWindow();
            _updateSignal.Dispose();
            _updateSignal = null;
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }
        var openSettingsOnStartup = AppStartupOptions.StartsInShortcutSettings(e.Args);
        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _mainWindow.Show();
        _mainWindow.Hide();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir JMD", null, (_, _) => _mainWindow.ShowPalette());
        var jeyToggleItem = new Forms.ToolStripMenuItem("Ativar Jey");
        jeyToggleItem.Click += (_, _) => _mainWindow.ToggleVoiceWakeWordFromTray();
        menu.Items.Add(jeyToggleItem);
        menu.Opening += (_, _) =>
            jeyToggleItem.Text = _mainWindow.IsVoiceWakeWordEnabled ? "Desativar Jey" : "Ativar Jey";
        menu.Items.Add("Sair", null, (_, _) =>
        {
            _mainWindow.PrepareForExit();
            Shutdown();
        });
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "JMD",
            Icon = _appIcon = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "JMD.ico")),
            ContextMenuStrip = menu,
            Visible = true
        };
        long? activeBalloonHistoryId = null;
        _mainWindow.TrayNotification += (title, message, useJmdNotification, historyId, displayDuration) =>
        {
            if (useJmdNotification)
            {
                var window = _mainWindow;
                if (window is not null)
                    window.Dispatcher.BeginInvoke(() =>
                    {
                        var notification = new JmdNotificationWindow(message, historyId,
                            displayDuration: displayDuration);
                        window.PrepareWindowForCaptureExclusion(notification);
                        notification.HistoryRequested += selectedHistoryId => window.Dispatcher.BeginInvoke(
                            new Action(() => window.ShowHistory(selectedHistoryId)));
                        notification.Show();
                    });
                return;
            }

            activeBalloonHistoryId = historyId;
            _trayIcon.BalloonTipTitle = title;
            _trayIcon.BalloonTipText = message;
            _trayIcon.ShowBalloonTip((int)displayDuration.TotalMilliseconds);
        };
        _trayIcon.BalloonTipClicked += (_, _) =>
            _mainWindow.Dispatcher.BeginInvoke(new Action(() => _mainWindow.ShowHistory(activeBalloonHistoryId)));
        StartUpdateSignalListener();
        if (updateOnStartup)
            _mainWindow.Dispatcher.BeginInvoke(new Action(async () => await _mainWindow.UpdateFromCommandLineAsync()));
        else if (openSettingsOnStartup)
            _mainWindow.Dispatcher.BeginInvoke(new Action(_mainWindow.ShowShortcutSettings));
        else
            _mainWindow.Dispatcher.BeginInvoke(new Action(_mainWindow.ShowPalette));
    }

    private void StartUpdateSignalListener()
    {
        if (_updateSignal is null) return;
        _updateListenerCancellation = new CancellationTokenSource();
        var signal = _updateSignal;
        var cancellationToken = _updateListenerCancellation.Token;
        _ = Task.Run(() =>
        {
            var handles = new WaitHandle[] { signal, cancellationToken.WaitHandle };
            while (WaitHandle.WaitAny(handles) == 0)
            {
                var window = _mainWindow;
                if (window is not null)
                    window.Dispatcher.BeginInvoke(new Action(async () => await window.UpdateFromCommandLineAsync()));
            }
        });
    }

    private static void ShowExistingWindow()
    {
        var window = FindWindow(null, "JMD");
        if (window == IntPtr.Zero) return;
        ShowWindow(window, 9);
        SetForegroundWindow(window);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    protected override void OnExit(ExitEventArgs e)
    {
        _updateListenerCancellation?.Cancel();
        _updateListenerCancellation?.Dispose();
        _updateSignal?.Dispose();
        if (_mainWindow is not null) _mainWindow.DisposeServices();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _appIcon?.Dispose();
        _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

}

internal sealed class UserSettings
{
    public UserSettings() { }

    public string? PreferredDelimiter { get; set; }
    public int HistoryRetentionDays { get; set; } = 15;
    public bool KeepAwakeEnabled { get; set; }
    public bool HideFromScreenCapture { get; set; }
    public bool UseJmdNotifications { get; set; }
    public string ShortcutActivatedSound { get; set; } = ActivationSoundService.Classic;
    public string VoiceCommandFoundSound { get; set; } = ActivationSoundService.Classic;
    public string JeyRecognizedSound { get; set; } = ActivationSoundService.Classic;
    public string TaskReminderSound { get; set; } = ActivationSoundService.Soft;
    public bool SilentUpdatesEnabled { get; set; }
    public bool VoiceWakeWordEnabled { get; set; } = true;
    public bool VoiceDebugEnabled { get; set; }
    public bool VoiceWakeRecognitionDebugEnabled { get; set; }
    public bool JeyWakeWordClosedGrammarEnabled { get; set; }
    public bool JeyCommandClosedGrammarEnabled { get; set; }
    public int JeyCommandMinimumConfidencePercent { get; set; }
    public int JeyWakeWordMinimumConfidencePercent { get; set; }
    public string JeyPortugueseModel { get; set; } = "vosk";
    public string JeyEnglishModel { get; set; } = "vosk";
    public List<SnippetEntry> Snippets { get; set; } = [];
    public List<CustomShortcutEntry> CustomShortcuts { get; set; } = [];
    public List<WorkflowVariableEntry> WorkflowVariables { get; set; } = [];

    public Dictionary<string, string> Shortcuts { get; set; } = new(StringComparer.Ordinal)
    {
        ["palette"] = ShortcutDefaults.ForCommand("palette"),
        ["clipboard"] = ShortcutDefaults.ForCommand("clipboard"),
        ["selection"] = ShortcutDefaults.ForCommand("selection"),
        ["upperCase"] = ShortcutDefaults.ForCommand("upperCase"),
        ["lowerCase"] = ShortcutDefaults.ForCommand("lowerCase"),
        ["titleCase"] = ShortcutDefaults.ForCommand("titleCase"),
        ["jsonPretty"] = ShortcutDefaults.ForCommand("jsonPretty"),
        ["jsonCompact"] = ShortcutDefaults.ForCommand("jsonCompact"),
        ["snippets"] = ShortcutDefaults.ForCommand("snippets"),
        ["saveSnippet"] = ShortcutDefaults.ForCommand("saveSnippet"),
        ["draftSave"] = ShortcutDefaults.ForCommand("draftSave"),
        ["draftRestore"] = ShortcutDefaults.ForCommand("draftRestore"),
        ["todoCreate"] = "Ctrl+Alt+T",
        ["todoPanel"] = "Ctrl+Alt+Left",
        ["todoPanelClose"] = "Ctrl+Alt+Right",
        ["about"] = string.Empty,
        ["checkUpdates"] = string.Empty
    };
    public Dictionary<string, List<string>> VoicePhrases { get; set; } = new(StringComparer.Ordinal);
    public HashSet<string> DisabledCommands { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class SnippetEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string? Rtf { get; set; }
    public string? Html { get; set; }
}

internal sealed class CustomShortcutEntry
{
    public string Id { get; set; } = $"custom:{Guid.NewGuid():N}";
    public string Name { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Shortcut { get; set; } = string.Empty;
    public bool FocusIfAlreadyOpen { get; set; }
    public List<CustomWorkflowStep> Steps { get; set; } = [];
}

internal sealed class CustomWorkflowStep
{
    public string Action { get; set; } = "";
    public string Value { get; set; } = "";
    public int DelayMilliseconds { get; set; }
    public int RepeatCount { get; set; } = 1;
}

internal sealed class WorkflowVariableEntry
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string ProtectedValue { get; set; } = string.Empty;
    public bool IsSecret { get; set; }
}

internal sealed class SettingsStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JMD", "settings.json");

    public UserSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_path));
                if (loaded is not null)
                {
                    loaded.Shortcuts ??= new Dictionary<string, string>(StringComparer.Ordinal);
                    loaded.Shortcuts.TryAdd("todoCreate", "Ctrl+Alt+T");
                    loaded.Shortcuts.TryAdd("todoPanel", "Ctrl+Alt+Left");
                    loaded.Shortcuts.TryAdd("todoPanelClose", "Ctrl+Alt+Right");
                    loaded.Shortcuts.TryAdd("upperCase", ShortcutDefaults.ForCommand("upperCase"));
                    loaded.Shortcuts.TryAdd("lowerCase", ShortcutDefaults.ForCommand("lowerCase"));
                    loaded.Shortcuts.TryAdd("titleCase", ShortcutDefaults.ForCommand("titleCase"));
                    loaded.VoicePhrases ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    loaded.DisabledCommands ??= new HashSet<string>(StringComparer.Ordinal);
                    loaded.Snippets ??= [];
                    loaded.CustomShortcuts ??= [];
                    loaded.WorkflowVariables ??= [];
                    foreach (var customShortcut in loaded.CustomShortcuts)
                    {
                        customShortcut.Steps ??= [];
                        if (customShortcut.Steps.Count == 0 && !string.IsNullOrWhiteSpace(customShortcut.Target))
                            customShortcut.Steps.Add(new CustomWorkflowStep { Action = "Open", Value = customShortcut.Target });
                    }
                    loaded.JeyPortugueseModel = VoiceModelOptions.NormalizePersisted(loaded.JeyPortugueseModel);
                    loaded.JeyEnglishModel = VoiceModelOptions.NormalizePersisted(loaded.JeyEnglishModel);
                    loaded.JeyWakeWordMinimumConfidencePercent = Math.Clamp(loaded.JeyWakeWordMinimumConfidencePercent, 0, 100);
                    loaded.JeyCommandMinimumConfidencePercent = Math.Clamp(loaded.JeyCommandMinimumConfidencePercent, 0, 100);
                    ShortcutDefaults.MigratePreviousDefaults(loaded.Shortcuts);
                    return loaded;
                }
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new UserSettings();
    }

    public void Save(UserSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

}
