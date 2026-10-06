using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows;
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

    public Dictionary<string, string> Shortcuts { get; set; } = new(StringComparer.Ordinal)
    {
        ["palette"] = "Alt+J",
        ["clipboard"] = "Ctrl+Alt+I",
        ["selection"] = "Ctrl+Shift+I"
    };
    public HashSet<string> DisabledCommands { get; set; } = new(StringComparer.Ordinal);
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
                    if (loaded.Shortcuts.TryGetValue("palette", out var paletteShortcut) &&
                        string.Equals(paletteShortcut, "Win+J", StringComparison.Ordinal))
                        loaded.Shortcuts["palette"] = "Alt+J";
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
