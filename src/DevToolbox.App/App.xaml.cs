using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows;
using Forms = System.Windows.Forms;

namespace DevToolbox.App;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _mainWindow.Show();
        _mainWindow.Hide();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir DevToolbox", null, (_, _) => _mainWindow.ShowPalette());
        menu.Items.Add("Sair", null, (_, _) =>
        {
            _mainWindow.PrepareForExit();
            Shutdown();
        });
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "DevToolbox",
            Icon = Icon,
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
        base.OnExit(e);
    }

    private static Icon Icon => SystemIcons.Application;
}

internal sealed class UserSettings
{
    public UserSettings() { }

    public string? PreferredDelimiter { get; set; }

    public Dictionary<string, string> Shortcuts { get; set; } = new(StringComparer.Ordinal)
    {
        ["palette"] = "Win+J",
        ["clipboard"] = "Ctrl+Alt+I",
        ["selection"] = "Ctrl+Shift+I"
    };
    public HashSet<string> DisabledCommands { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class SettingsStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevToolbox", "settings.json");

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
