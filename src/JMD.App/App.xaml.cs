using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;
using System.IO.Pipes;
using System.Diagnostics;
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
    private CancellationTokenSource? _cliServerCancellation;
    private const string CliPipeName = "JMD.CLI.v1";
    private const string CliFlag = "--cli";
    private const string BackgroundFlag = "--background";

    protected override async void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) => CriticalErrorLog.Write("Dispatcher WPF", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => CriticalErrorLog.Write(
            $"Exceção não tratada do processo (terminando: {args.IsTerminating})",
            args.ExceptionObject as Exception ?? new Exception(Convert.ToString(args.ExceptionObject) ?? "Exceção desconhecida."));
        TaskScheduler.UnobservedTaskException += (_, args) => CriticalErrorLog.Write("Task sem exceção observada", args.Exception);
        base.OnStartup(e);
        if (e.Args.FirstOrDefault() == CliFlag)
        {
            AttachParentConsole();
            var exitCode = await RunCliAsync(e.Args.Skip(1).ToArray());
            Shutdown(exitCode);
            return;
        }
        var backgroundOnStartup = e.Args.Contains(BackgroundFlag, StringComparer.OrdinalIgnoreCase);
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
        StartCliServer();
        if (updateOnStartup)
            _ = _mainWindow.Dispatcher.BeginInvoke(new Action(async () => await _mainWindow.UpdateFromCommandLineAsync()));
        else if (openSettingsOnStartup)
            _ = _mainWindow.Dispatcher.BeginInvoke(new Action(_mainWindow.ShowShortcutSettings));
        else if (!backgroundOnStartup)
            _ = _mainWindow.Dispatcher.BeginInvoke(new Action(_mainWindow.ShowPalette));
    }

    private async Task<int> RunCliAsync(string[] arguments)
    {
        var parsed = JmdCommandLinePolicy.Parse(arguments);
        if (parsed.Operation == "help")
        {
            Console.WriteLine("Uso: jmd <comando> | jmd -ls | jmd -help");
            Console.WriteLine("  jmd -ls --verbose             Lista descrições, atalhos e comandos desativados.");
            Console.WriteLine("  jmd -search <texto>           Mostra resultados numerados sem executar.");
            Console.WriteLine("  jmd -s <texto>                Alias de -search.");
            Console.WriteLine("  jmd -search <texto> --verbose Mostra descrições, atalhos e estado dos resultados.");
            Console.WriteLine("  jmd -run <n> [texto]          Executa o item n da lista ou dos resultados de busca.");
            Console.WriteLine("  jmd <texto>  Procura comandos com as regras da busca da paleta e executa o primeiro resultado.");
            Console.WriteLine("  jmd -ls      Lista os comandos disponíveis.");
            Console.WriteLine("  jmd -install-completion       Ativa autocomplete no perfil do PowerShell.");
            Console.WriteLine("  jmd -uninstall-completion     Remove o autocomplete do perfil do PowerShell.");
            Console.WriteLine("  jmd -help    Exibe estas dicas.");
            return 0;
        }

        if (parsed.Operation is "installCompletion" or "uninstallCompletion")
        {
            var result = UpdatePowerShellCompletion(parsed.Operation == "installCompletion");
            Console.WriteLine(result.Output);
            return result.ExitCode;
        }

        var request = new JmdCliRequest(parsed.Operation, parsed.Query, parsed.Index, parsed.Verbose);
        try
        {
            var response = await SendCliRequestAsync(request, TimeSpan.FromMinutes(5));
            Console.WriteLine(response.Output);
            return response.ExitCode;
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception or JsonException)
        {
            Console.Error.WriteLine($"Não foi possível iniciar ou contatar o JMD: {exception.Message}");
            return 2;
        }
    }

    private static JmdCliResponse UpdatePowerShellCompletion(bool install)
    {
        var completionScript = Path.Combine(AppContext.BaseDirectory, "Assets", "JMD.PowerShellCompletion.ps1");
        if (install && !File.Exists(completionScript))
            return new JmdCliResponse(false, "O script de autocomplete não foi instalado junto com o JMD.", 2);
        var escapedScript = completionScript.Replace("'", "''", StringComparison.Ordinal);
        var profileLine = $"if (Test-Path -LiteralPath '{escapedScript}') {{ . '{escapedScript}' }}";
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var profiles = new[]
        {
            Path.Combine(documents, "PowerShell", "Microsoft.PowerShell_profile.ps1"),
            Path.Combine(documents, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1")
        };
        try
        {
            foreach (var profile in profiles)
            {
                if (install)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
                    var lines = File.Exists(profile) ? File.ReadAllLines(profile) : [];
                    if (!lines.Contains(profileLine, StringComparer.OrdinalIgnoreCase))
                    {
                        var updatedLines = lines.Append(profileLine);
                        File.WriteAllLines(profile, updatedLines, new UTF8Encoding(true));
                    }
                }
                else if (File.Exists(profile))
                {
                    var lines = File.ReadAllLines(profile).Where(line => !string.Equals(line.Trim(), profileLine, StringComparison.OrdinalIgnoreCase));
                    File.WriteAllLines(profile, lines, new UTF8Encoding(true));
                }
            }
            return new JmdCliResponse(true, install
                ? "Autocomplete do JMD instalado nos perfis do PowerShell. Abra um novo terminal para usar Tab."
                : "Autocomplete do JMD removido dos perfis do PowerShell.", 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new JmdCliResponse(false, $"Não foi possível atualizar o perfil do PowerShell: {exception.Message}", 2);
        }
    }

    private static async Task<JmdCliResponse> SendCliRequestAsync(JmdCliRequest request, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        var response = await TrySendCliRequestAsync(request, cancellation.Token);
        if (response is null)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Não foi possível localizar o executável do JMD.");
            var startInfo = new ProcessStartInfo(executable) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory, Arguments = BackgroundFlag };
            _ = Process.Start(startInfo) ?? throw new InvalidOperationException("Não foi possível iniciar o JMD em segundo plano.");

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < timeout)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                response = await TrySendCliRequestAsync(request, cancellation.Token);
                if (response is not null) return response;
                await Task.Delay(150, cancellation.Token);
            }
            throw new TimeoutException("O JMD não respondeu dentro do tempo esperado.");
        }
        return response!;
    }

    private static async Task<JmdCliResponse?> TrySendCliRequestAsync(JmdCliRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", CliPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(250, cancellationToken);
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(), cancellationToken);
            var responseLine = await reader.ReadLineAsync(cancellationToken);
            return responseLine is null
                ? new JmdCliResponse(false, "O JMD encerrou a conexão sem responder.", 1)
                : JsonSerializer.Deserialize<JmdCliResponse>(responseLine) ?? new JmdCliResponse(false, "Resposta inválida do JMD.", 1);
        }
        catch (TimeoutException) { return null; }
        catch (IOException) { return null; }
        catch (JsonException) { return new JmdCliResponse(false, "Resposta inválida do JMD.", 1); }
    }

    private void StartCliServer()
    {
        _cliServerCancellation = new CancellationTokenSource();
        var cancellationToken = _cliServerCancellation.Token;
        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(CliPipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(cancellationToken);
                    using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                    await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                    var line = await reader.ReadLineAsync(cancellationToken);
                    var request = line is null ? null : JsonSerializer.Deserialize<JmdCliRequest>(line);
                    var response = request is null
                        ? new JmdCliResponse(false, "Solicitação inválida.", 1)
                        : await _mainWindow!.ExecuteCliRequestAsync(request);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response).AsMemory(), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Keep the local command endpoint available after a malformed request.
                }
            }
        }, cancellationToken);
    }

    private static void AttachParentConsole()
    {
        if (!AttachConsole(uint.MaxValue)) return;
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

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
        _cliServerCancellation?.Cancel();
        _cliServerCancellation?.Dispose();
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

internal sealed record JmdCliRequest(string Operation, string Query, int? Index = null, bool Verbose = false);
internal sealed record JmdCliResponse(bool Success, string Output, int ExitCode);

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
    public int TimeoutMilliseconds { get; set; } = 10000;
    public string MonitorDeviceName { get; set; } = string.Empty;
    public string ControlCondition { get; set; } = "Exists";
    public int SkipStepsWhenFalse { get; set; } = 1;
    public string MouseAction { get; set; } = "LeftClick";
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
