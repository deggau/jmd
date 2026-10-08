using System.Drawing;
using System.IO;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _instanceMutex = new Mutex(true, @"Local\JMD.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
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
        _mainWindow.TrayNotification += (title, message) =>
        {
            _trayIcon.BalloonTipTitle = title;
            _trayIcon.BalloonTipText = message;
            _trayIcon.ShowBalloonTip(2200);
        };
        _trayIcon.BalloonTipClicked += (_, _) =>
            _mainWindow.Dispatcher.BeginInvoke(_mainWindow.ShowHistory);
        if (openSettingsOnStartup)
            _mainWindow.Dispatcher.BeginInvoke(new Action(_mainWindow.ShowShortcutSettings));
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
    public List<SnippetEntry> Snippets { get; set; } = [];

    public Dictionary<string, string> Shortcuts { get; set; } = new(StringComparer.Ordinal)
    {
        ["palette"] = ShortcutDefaults.ForCommand("palette"),
        ["clipboard"] = ShortcutDefaults.ForCommand("clipboard"),
        ["selection"] = ShortcutDefaults.ForCommand("selection"),
        ["jsonPretty"] = ShortcutDefaults.ForCommand("jsonPretty"),
        ["jsonCompact"] = ShortcutDefaults.ForCommand("jsonCompact"),
        ["snippets"] = ShortcutDefaults.ForCommand("snippets"),
        ["saveSnippet"] = ShortcutDefaults.ForCommand("saveSnippet"),
        ["draftSave"] = ShortcutDefaults.ForCommand("draftSave"),
        ["draftRestore"] = ShortcutDefaults.ForCommand("draftRestore")
    };
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
                    loaded.DisabledCommands ??= new HashSet<string>(StringComparer.Ordinal);
                    loaded.Snippets ??= [];
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
