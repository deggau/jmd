using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FormsScreen = System.Windows.Forms.Screen;
using JMD.Core;
using JMD.Windows;
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
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
    private readonly TextBox _shortcut = CreateBox(36);
    private readonly TextBox _phrases = CreateBox(64, multiline: true);
    private readonly CheckBox _focusIfAlreadyOpen = new() { Content = "Se já estiver aberto, trazer para frente", Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(2, 8, 0, 0) };
    private readonly StackPanel _stepsPanel = new();
    private readonly List<StepDraft> _steps = [];
    private readonly Func<CustomShortcutEditorWindow, string?>? _validate;
    private readonly Func<CustomShortcutEditorWindow, Task<bool>>? _testWorkflow;
    private ControlInspectorWindow? _controlInspector;
    private bool _closed;
    private bool _accepted;
    private readonly TextBlock _testStatus = new()
    {
        Text = "O teste executa as etapas no programa selecionado.",
        Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)),
        FontSize = 11,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(2, 8, 2, 0)
    };

    public string CommandName => _name.Text.Trim();
    public string Shortcut => _shortcut.Text.Trim();
    public string[] VoicePhrases => _phrases.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public bool FocusIfAlreadyOpen => _focusIfAlreadyOpen.IsChecked == true;
    public bool ShowEditorDialog()
    {
        _accepted = false;
        ShowDialog();
        return _accepted;
    }

    private void CompleteDialog(bool accepted)
        => WorkflowDialogClosePolicy.Close(accepted, result => _accepted = result, Close);

    public List<CustomWorkflowStep> Steps
    {
        get
        {
            SyncSteps();
            return _steps.Select(step => new CustomWorkflowStep
            {
                Action = step.Action,
                Value = step.Value,
                DelayMilliseconds = step.DelayMilliseconds,
                RepeatCount = step.RepeatCount,
                TimeoutMilliseconds = step.TimeoutMilliseconds,
                MonitorDeviceName = step.MonitorDeviceName,
                ControlCondition = step.ControlCondition,
                SkipStepsWhenFalse = step.SkipStepsWhenFalse,
                MouseAction = step.MouseAction
            }).ToList();
        }
    }

    public CustomShortcutEditorWindow(CustomShortcutEntry? existing = null, IEnumerable<string>? existingPhrases = null,
        IEnumerable<string>? variableNames = null, Func<CustomShortcutEditorWindow, string?>? validate = null,
        Func<CustomShortcutEditorWindow, Task<bool>>? testWorkflow = null)
    {
        _validate = validate;
        _testWorkflow = testWorkflow;
        if (existing is not null)
        {
            _name.Text = existing.Name;
            _shortcut.Text = existing.Shortcut;
            _focusIfAlreadyOpen.IsChecked = existing.FocusIfAlreadyOpen;
            _phrases.Text = string.Join(Environment.NewLine, existingPhrases ?? []);
            if (existing.Steps.Count > 0)
                _steps.AddRange(existing.Steps.Select(step => new StepDraft(step.Action, step.Value,
                    step.DelayMilliseconds, step.RepeatCount, step.TimeoutMilliseconds, step.MonitorDeviceName,
                    step.ControlCondition, step.SkipStepsWhenFalse, step.MouseAction)));
            else if (!string.IsNullOrWhiteSpace(existing.Target))
                _steps.Add(new StepDraft("Open", existing.Target, 0));
        }
        if (_steps.Count == 0) _steps.Add(new StepDraft("Open", "", 0));

        Title = existing is null ? "Criar comando personalizado" : "Editar comando personalizado";
        Width = 590;
        Height = 740;
        MinWidth = 500;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        Background = new SolidColorBrush(Color.FromRgb(23, 25, 31));
        Topmost = true;

        var outer = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(23, 25, 31)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 62, 72)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(18)
        };
        Content = outer;
        Closed += (_, _) =>
        {
            _closed = true;
            _controlInspector?.Close();
            _controlInspector = null;
        };
        var layout = new DockPanel();
        outer.Child = layout;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        var cancel = MakeButton("Cancelar", false);
        cancel.Click += (_, _) => CompleteDialog(false);
        var save = MakeButton("Salvar comando", true);
        save.Margin = new Thickness(8, 0, 0, 0);
        save.Click += (_, _) =>
        {
            var error = _validate?.Invoke(this);
            if (error is not null)
            {
                MessageBox.Show(this, error, "Workflow inválido", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            CompleteDialog(true);
        };
        footer.Children.Add(cancel);
        footer.Children.Add(save);

        var heading = new TextBlock { Text = existing is null ? "Novo workflow" : "Editar workflow", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(heading, Dock.Top);
        layout.Children.Add(heading);

        var form = new StackPanel();
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = form };
        layout.Children.Add(scroll);
        AddField(form, "Nome do comando", _name, "Nome exibido na lista de comandos");
        AddField(form, "Atalho de teclado (opcional)", _shortcut, "Clique no campo e pressione Ctrl, Alt, Shift ou Win + outra tecla");
        AddField(form, "Frases do Jey (opcional)", _phrases, "Uma frase por linha");
        form.Children.Add(new TextBlock
        {
            Text = "Use {{nome}} para capturar um trecho da frase, por exemplo: Abra a tarefa {{tarefaId}}. Você pode falar a frase ao Jey ou digitá-la na busca e pressionar Enter. Use o mesmo marcador na URL ou nos parâmetros do programa; coloque o parâmetro entre aspas se puder conter espaços.",
            Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 10, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, -4, 2, 8)
        });
        _shortcut.PreviewKeyDown += CaptureShortcut;

        var workflowHeading = new DockPanel { Margin = new Thickness(2, 14, 2, 3) };
        var inspectControl = MakeButton("Inspecionar controle", false);
        inspectControl.Padding = new Thickness(9, 5, 9, 5);
        inspectControl.ToolTip = "Esconda esta janela e mostre o nome acessível do controle sob o cursor.";
        inspectControl.Click += (_, _) =>
        {
            var inspector = new ControlInspectorWindow();
            _controlInspector = inspector;
            if (Owner is not null) inspector.Owner = Owner;
            Hide();
            inspector.Closed += (_, _) =>
            {
                if (ReferenceEquals(_controlInspector, inspector)) _controlInspector = null;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closed) return;
                    if (!IsVisible) Show();
                    Activate();
                }));
            };
            inspector.Show();
        };
        var workflowActions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var testButton = MakeButton("Testar workflow", false);
        testButton.ToolTip = "Executa as etapas configuradas no programa selecionado. O teste pode interagir com esse programa.";
        testButton.Click += async (_, _) =>
        {
            var runTest = _testWorkflow;
            var error = _validate?.Invoke(this);
            if (error is not null)
            {
                _testStatus.Text = error;
                _testStatus.Foreground = Brushes.OrangeRed;
                return;
            }
            if (runTest is null)
            {
                _testStatus.Text = "Não foi possível iniciar o teste do workflow.";
                _testStatus.Foreground = Brushes.OrangeRed;
                return;
            }

            testButton.IsEnabled = false;
            _testStatus.Text = "Executando o workflow…";
            _testStatus.Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167));
            try
            {
                SyncSteps();
                Hide();
                await Task.Delay(120);
                var succeeded = await runTest(this);
                if (!_closed)
                {
                    _testStatus.Text = succeeded
                        ? "Teste concluído. Confira o programa; você pode continuar editando o workflow."
                        : "O teste falhou. Confira o detalhe exibido no JMD ou no Histórico.";
                    _testStatus.Foreground = succeeded
                        ? new SolidColorBrush(Color.FromRgb(180, 243, 106))
                        : Brushes.OrangeRed;
                }
            }
            catch (Exception exception)
            {
                CriticalErrorLog.Write("Testar workflow pelo editor", exception);
                if (!_closed)
                {
                    _testStatus.Text = $"O teste falhou: {exception.Message}";
                    _testStatus.Foreground = Brushes.OrangeRed;
                }
            }
            finally
            {
                if (!_closed)
                {
                    if (!IsVisible) Show();
                    Activate();
                    testButton.IsEnabled = true;
                }
            }
        };
        testButton.Margin = new Thickness(0, 0, 6, 0);
        workflowActions.Children.Add(testButton);
        workflowActions.Children.Add(inspectControl);
        DockPanel.SetDock(workflowActions, Dock.Right);
        workflowHeading.Children.Add(workflowActions);
        workflowHeading.Children.Add(new TextBlock { Text = "Etapas do workflow", Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        form.Children.Add(workflowHeading);
        form.Children.Add(_testStatus);
        form.Children.Add(new TextBlock { Text = "As etapas são executadas na ordem abaixo. Após abrir um programa, o JMD aguarda um instante antes de continuar.", Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 8) });
        var availableVariables = variableNames?.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        form.Children.Add(new TextBlock { Text = availableVariables.Length == 0
                ? "Para preencher dados reutilizáveis, cadastre variáveis globais em Gerenciar atalhos e use {{nome}} no texto."
                : $"Variáveis disponíveis no texto: {string.Join(", ", availableVariables.Select(name => $"{{{{{name}}}}}"))}",
            Foreground = new SolidColorBrush(Color.FromRgb(180, 243, 106)), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 2, 8) });
        form.Children.Add(_stepsPanel);
        var addStep = MakeButton("+ Adicionar etapa", false);
        addStep.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        addStep.Margin = new Thickness(0, 6, 0, 2);
        addStep.Click += (_, _) => { SyncSteps(); _steps.Add(new StepDraft("Type", "", 0)); RenderSteps(); };
        form.Children.Add(addStep);
        _focusIfAlreadyOpen.IsChecked = existing?.FocusIfAlreadyOpen == true;
        form.Children.Add(_focusIfAlreadyOpen);
        RenderSteps();
        Loaded += (_, _) => _name.Focus();
    }

    private void RenderSteps()
    {
        _stepsPanel.Children.Clear();
        for (var index = 0; index < _steps.Count; index++)
        {
            var stepIndex = index;
            var step = _steps[index];
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(34, 37, 45)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(49, 52, 61)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var body = new StackPanel();
            card.Child = body;
            var heading = new DockPanel();
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            DockPanel.SetDock(actions, Dock.Right);
            heading.Children.Add(actions);
            heading.Children.Add(new TextBlock { Text = $"Etapa {index + 1}", Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            body.Children.Add(heading);
            AddSmallAction("↑", "Mover para cima", () => MoveStep(stepIndex, -1));
            AddSmallAction("↓", "Mover para baixo", () => MoveStep(stepIndex, 1));
            AddSmallAction("Remover", "Remover etapa", () => RemoveStep(stepIndex));
            void AddSmallAction(string text, string tooltip, Action action)
            {
                var button = MakeButton(text, false);
                button.Padding = new Thickness(7, 3, 7, 3);
                button.Margin = new Thickness(4, 0, 0, 0);
                button.ToolTip = tooltip;
                button.Click += (_, _) => action();
                actions.Children.Add(button);
            }

            var type = new ComboBox { Margin = new Thickness(0, 8, 0, 5), Padding = new Thickness(6, 4, 6, 4), ItemsSource = new[]
            {
                "Abrir programa ou URL", "Pressionar tecla", "Digitar texto", "Aguardar", "Focar campo ou botão",
                "Aguardar janela", "Aguardar controle", "Se controle corresponder", "Clique na janela", "Reposicionar janela"
            } };
            type.SelectedIndex = step.Action switch { "Open" => 0, "Key" => 1, "Type" => 2, "Wait" => 3, "Focus" => 4,
                "WaitWindow" => 5, "WaitControl" => 6, "IfControl" => 7, "Click" => 8, "PositionWindow" => 9, _ => 2 };
            body.Children.Add(type);
            var value = CreateBox(step.Action == "Type" ? 68 : 36, multiline: step.Action == "Type");
            value.Text = step.Value;
            var valueLabel = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 10, Margin = new Thickness(2, 0, 2, 3) };
            var delay = CreateBox(34);
            delay.Text = step.DelayMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var repeatCount = CreateBox(34);
            repeatCount.Width = 84;
            repeatCount.Text = step.RepeatCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var repeatPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 6, 2, 0) };
            repeatPanel.Children.Add(new TextBlock { Text = "Repetir a tecla (1–100 vezes)", Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            repeatPanel.Children.Add(repeatCount);
            var timeout = CreateBox(34);
            timeout.Width = 100;
            timeout.Text = step.TimeoutMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var timeoutPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 6, 2, 0) };
            timeoutPanel.Children.Add(new TextBlock { Text = "Tempo limite (ms)", Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            timeoutPanel.Children.Add(timeout);
            var monitorOptions = new[] { new MonitorOption("Padrão do Windows", string.Empty) }
                .Concat(FormsScreen.AllScreens.Select(screen => new MonitorOption(
                    $"{screen.DeviceName} · {screen.Bounds.Width} × {screen.Bounds.Height}{(screen.Primary ? " · principal" : string.Empty)}",
                    screen.DeviceName)));
            if (!string.IsNullOrWhiteSpace(step.MonitorDeviceName) &&
                !monitorOptions.Any(option => option.DeviceName.Equals(step.MonitorDeviceName, StringComparison.OrdinalIgnoreCase)))
                monitorOptions = monitorOptions.Append(new MonitorOption($"{step.MonitorDeviceName} · não conectado", step.MonitorDeviceName));
            var monitorOptionList = monitorOptions.ToArray();
            var monitor = new ComboBox { ItemsSource = monitorOptionList, DisplayMemberPath = nameof(MonitorOption.Display),
                Margin = new Thickness(2, 4, 2, 0), Padding = new Thickness(6, 4, 6, 4) };
            monitor.SelectedItem = monitorOptionList.FirstOrDefault(option => option.DeviceName.Equals(step.MonitorDeviceName, StringComparison.OrdinalIgnoreCase)) ?? monitorOptionList[0];
            var monitorPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            monitorPanel.Children.Add(new TextBlock { Text = "Monitor de destino", Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 10, Margin = new Thickness(2, 0, 2, 2) });
            monitorPanel.Children.Add(monitor);
            var condition = new ComboBox { ItemsSource = new[] { "Existe", "Não existe", "Habilitado", "Desabilitado", "Visível", "Marcado", "Desmarcado" },
                Margin = new Thickness(2, 4, 2, 0), Padding = new Thickness(6, 4, 6, 4), SelectedIndex = step.ControlCondition switch
                { "Missing" => 1, "Enabled" => 2, "Disabled" => 3, "Visible" => 4, "Checked" => 5, "Unchecked" => 6, _ => 0 } };
            var conditionPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            conditionPanel.Children.Add(new TextBlock { Text = "Condição", Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 10, Margin = new Thickness(2, 0, 2, 2) });
            conditionPanel.Children.Add(condition);
            var skipCount = CreateBox(34);
            skipCount.Width = 84;
            skipCount.Text = step.SkipStepsWhenFalse.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var skipPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 6, 2, 0) };
            skipPanel.Children.Add(new TextBlock { Text = "Se não corresponder, pular próximas etapas", Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            skipPanel.Children.Add(skipCount);
            var mouseAction = new ComboBox { ItemsSource = new[] { "Clique", "Clique duplo", "Botão direito", "Rolar para cima", "Rolar para baixo" },
                Margin = new Thickness(2, 4, 2, 0), Padding = new Thickness(6, 4, 6, 4), SelectedIndex = step.MouseAction switch
                { "DoubleClick" => 1, "RightClick" => 2, "ScrollUp" => 3, "ScrollDown" => 4, _ => 0 } };
            var mousePanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
            mousePanel.Children.Add(new TextBlock { Text = "Ação do mouse", Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 10, Margin = new Thickness(2, 0, 2, 2) });
            mousePanel.Children.Add(mouseAction);
            var delayLabel = new TextBlock { Text = "Pausa após a etapa (ms, opcional)", Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 10, Margin = new Thickness(2, 6, 2, 3) };
            body.Children.Add(valueLabel);
            body.Children.Add(value);
            body.Children.Add(repeatPanel);
            body.Children.Add(timeoutPanel);
            body.Children.Add(monitorPanel);
            body.Children.Add(conditionPanel);
            body.Children.Add(skipPanel);
            body.Children.Add(mousePanel);
            body.Children.Add(delayLabel);
            body.Children.Add(delay);
            void UpdateKind()
            {
                switch (type.SelectedIndex)
                {
                    case 0:
                        valueLabel.Text = "Programa, parâmetros ou URL";
                        value.ToolTip = "Ex.: notepad.exe, explorer.exe \"C:\\Pasta com espaços\" ou https://example.com";
                        value.Height = 36; value.AcceptsReturn = false;
                        value.TextWrapping = TextWrapping.NoWrap;
                        delayLabel.Text = "Aguardar após abrir (ms, opcional; padrão 1000 ms)";
                        break;
                    case 1:
                        valueLabel.Text = "Tecla";
                        value.ToolTip = "Ex.: Tab, Enter, Escape, Backspace, F1, seta para baixo";
                        value.Height = 36; value.AcceptsReturn = false;
                        value.TextWrapping = TextWrapping.NoWrap;
                        delayLabel.Text = "Pausa após a tecla (ms, opcional)";
                        break;
                    case 2:
                        valueLabel.Text = "Texto literal para digitar";
                        value.ToolTip = "O texto será digitado no campo que estiver com foco";
                        value.Height = 68; value.AcceptsReturn = true;
                        value.TextWrapping = TextWrapping.Wrap;
                        delayLabel.Text = "Pausa após digitar (ms, opcional)";
                        break;
                    case 3:
                        valueLabel.Text = "Tempo de espera (ms)";
                        value.ToolTip = "Informe um valor entre 1 e 60000 ms";
                        value.Height = 36; value.AcceptsReturn = false;
                        value.TextWrapping = TextWrapping.NoWrap;
                        delayLabel.Text = "";
                        break;
                    case 4:
                        valueLabel.Text = "Nome acessível ou AutomationId do campo ou botão";
                        value.ToolTip = "Use Copiar ID no inspetor (id:Salvar) ou informe name:Salvar. Busca restrita à janela ativa.";
                        value.Height = 36; value.AcceptsReturn = false;
                        value.TextWrapping = TextWrapping.NoWrap;
                        delayLabel.Text = "Pausa após focar (ms, opcional)";
                        break;
                    case 5:
                        valueLabel.Text = "Título da janela ou process:nome-do-processo";
                        value.ToolTip = "Ex.: TeamViewer ou process:TeamViewer. Aguarda até o tempo limite.";
                        value.Height = 36; value.AcceptsReturn = false; value.TextWrapping = TextWrapping.NoWrap;
                        delayLabel.Text = "Pausa após encontrar (ms, opcional)";
                        break;
                    case 6:
                    case 7:
                        valueLabel.Text = "Controle: id:AutomationId ou name:nome acessível";
                        value.ToolTip = "Use Copiar ID no inspetor para copiar um identificador inequívoco.";
                        value.Height = 36; value.AcceptsReturn = false; value.TextWrapping = TextWrapping.NoWrap;
                        delayLabel.Text = "Pausa após verificar (ms, opcional)";
                        break;
                    case 8:
                        valueLabel.Text = "Posição relativa à janela (copie com Shift + clique no inspetor)";
                        value.ToolTip = "Ex.: 0.42,0.68. As coordenadas acompanham a janela ao mover para outro monitor.";
                        value.Height = 36; value.AcceptsReturn = false; value.TextWrapping = TextWrapping.NoWrap;
                        delayLabel.Text = "Pausa após clicar (ms, opcional)";
                        break;
                    default:
                        valueLabel.Text = "Título da janela ou process:nome-do-processo (vazio = janela ativa)";
                        value.ToolTip = "Move e centraliza a janela no monitor selecionado, mantendo seu tamanho.";
                        value.Height = 36; value.AcceptsReturn = false; value.TextWrapping = TextWrapping.NoWrap;
                        delayLabel.Text = "Pausa após mover (ms, opcional)";
                        break;
                }
                delayLabel.Visibility = type.SelectedIndex == 3 ? Visibility.Collapsed : Visibility.Visible;
                delay.Visibility = type.SelectedIndex == 3 ? Visibility.Collapsed : Visibility.Visible;
                repeatPanel.Visibility = type.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
                timeoutPanel.Visibility = type.SelectedIndex is 5 or 6 ? Visibility.Visible : Visibility.Collapsed;
                monitorPanel.Visibility = type.SelectedIndex is 0 or 9 ? Visibility.Visible : Visibility.Collapsed;
                conditionPanel.Visibility = type.SelectedIndex == 7 ? Visibility.Visible : Visibility.Collapsed;
                skipPanel.Visibility = type.SelectedIndex == 7 ? Visibility.Visible : Visibility.Collapsed;
                mousePanel.Visibility = type.SelectedIndex == 8 ? Visibility.Visible : Visibility.Collapsed;
            }
            type.SelectionChanged += (_, _) => UpdateKind();
            UpdateKind();
            _stepsPanel.Children.Add(card);
            step.TypeBox = type;
            step.ValueBox = value;
            step.DelayBox = delay;
            step.RepeatCountBox = repeatCount;
            step.TimeoutBox = timeout;
            step.MonitorBox = monitor;
            step.ConditionBox = condition;
            step.SkipCountBox = skipCount;
            step.MouseActionBox = mouseAction;
        }
    }

    private void MoveStep(int index, int delta)
    {
        SyncSteps();
        var destination = index + delta;
        if (destination < 0 || destination >= _steps.Count) return;
        (_steps[index], _steps[destination]) = (_steps[destination], _steps[index]);
        RenderSteps();
    }

    private void RemoveStep(int index)
    {
        SyncSteps();
        _steps.RemoveAt(index);
        RenderSteps();
    }

    private void SyncSteps()
    {
        foreach (var step in _steps)
        {
            if (step.TypeBox is not null)
                step.Action = step.TypeBox.SelectedIndex switch
                {
                    0 => "Open", 1 => "Key", 2 => "Type", 3 => "Wait", 4 => "Focus", 5 => "WaitWindow",
                    6 => "WaitControl", 7 => "IfControl", 8 => "Click", 9 => "PositionWindow", _ => "Type"
                };
            if (step.ValueBox is not null) step.Value = step.ValueBox.Text;
            if (step.DelayBox is not null)
                step.DelayMilliseconds = int.TryParse(step.DelayBox.Text, out var milliseconds) ? milliseconds : -1;
            if (step.RepeatCountBox is not null)
                step.RepeatCount = int.TryParse(step.RepeatCountBox.Text, out var count) ? count : -1;
            if (step.TimeoutBox is not null)
                step.TimeoutMilliseconds = int.TryParse(step.TimeoutBox.Text, out var timeout) ? timeout : -1;
            if (step.MonitorBox?.SelectedItem is MonitorOption monitor) step.MonitorDeviceName = monitor.DeviceName;
            if (step.ConditionBox is not null)
                step.ControlCondition = step.ConditionBox.SelectedIndex switch
                {
                    1 => "Missing", 2 => "Enabled", 3 => "Disabled", 4 => "Visible", 5 => "Checked", 6 => "Unchecked", _ => "Exists"
                };
            if (step.SkipCountBox is not null)
                step.SkipStepsWhenFalse = int.TryParse(step.SkipCountBox.Text, out var skipCount) ? skipCount : -1;
            if (step.MouseActionBox is not null)
                step.MouseAction = step.MouseActionBox.SelectedIndex switch
                {
                    1 => "DoubleClick", 2 => "RightClick", 3 => "ScrollUp", 4 => "ScrollDown", _ => "LeftClick"
                };
        }
    }

    private sealed record MonitorOption(string Display, string DeviceName);

    private sealed class StepDraft(string action, string value, int delayMilliseconds, int repeatCount = 1,
        int timeoutMilliseconds = 10000, string monitorDeviceName = "", string controlCondition = "Exists",
        int skipStepsWhenFalse = 1, string mouseAction = "LeftClick")
    {
        public string Action { get; set; } = action;
        public string Value { get; set; } = value;
        public int DelayMilliseconds { get; set; } = delayMilliseconds;
        public int RepeatCount { get; set; } = repeatCount;
        public int TimeoutMilliseconds { get; set; } = timeoutMilliseconds;
        public string MonitorDeviceName { get; set; } = monitorDeviceName;
        public string ControlCondition { get; set; } = controlCondition;
        public int SkipStepsWhenFalse { get; set; } = skipStepsWhenFalse;
        public string MouseAction { get; set; } = mouseAction;
        public ComboBox? TypeBox { get; set; }
        public TextBox? ValueBox { get; set; }
        public TextBox? DelayBox { get; set; }
        public TextBox? RepeatCountBox { get; set; }
        public TextBox? TimeoutBox { get; set; }
        public ComboBox? MonitorBox { get; set; }
        public ComboBox? ConditionBox { get; set; }
        public TextBox? SkipCountBox { get; set; }
        public ComboBox? MouseActionBox { get; set; }
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
