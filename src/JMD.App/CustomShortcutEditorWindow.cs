using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    public string CommandName => _name.Text.Trim();
    public string Shortcut => _shortcut.Text.Trim();
    public string[] VoicePhrases => _phrases.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public bool FocusIfAlreadyOpen => _focusIfAlreadyOpen.IsChecked == true;
    public List<CustomWorkflowStep> Steps
    {
        get
        {
            SyncSteps();
            return _steps.Select(step => new CustomWorkflowStep
            {
                Action = step.Action,
                Value = step.Value,
                DelayMilliseconds = step.DelayMilliseconds
            }).ToList();
        }
    }

    public CustomShortcutEditorWindow(CustomShortcutEntry? existing = null, IEnumerable<string>? existingPhrases = null,
        IEnumerable<string>? variableNames = null, Func<CustomShortcutEditorWindow, string?>? validate = null)
    {
        _validate = validate;
        if (existing is not null)
        {
            _name.Text = existing.Name;
            _shortcut.Text = existing.Shortcut;
            _focusIfAlreadyOpen.IsChecked = existing.FocusIfAlreadyOpen;
            _phrases.Text = string.Join(Environment.NewLine, existingPhrases ?? []);
            if (existing.Steps.Count > 0)
                _steps.AddRange(existing.Steps.Select(step => new StepDraft(step.Action, step.Value, step.DelayMilliseconds)));
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
        var layout = new DockPanel();
        outer.Child = layout;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        var cancel = MakeButton("Cancelar", false);
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
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
            DialogResult = true;
            Close();
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

        form.Children.Add(new TextBlock { Text = "Etapas do workflow", Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 14, 2, 3) });
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

            var type = new ComboBox { Margin = new Thickness(0, 8, 0, 5), Padding = new Thickness(6, 4, 6, 4), ItemsSource = new[] { "Abrir programa ou URL", "Pressionar tecla", "Digitar texto", "Aguardar" } };
            type.SelectedIndex = step.Action switch { "Open" => 0, "Key" => 1, "Type" => 2, "Wait" => 3, _ => 2 };
            body.Children.Add(type);
            var value = CreateBox(step.Action == "Type" ? 68 : 36, multiline: step.Action == "Type");
            value.Text = step.Value;
            var valueLabel = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 10, Margin = new Thickness(2, 0, 2, 3) };
            var delay = CreateBox(34);
            delay.Text = step.DelayMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var delayLabel = new TextBlock { Text = "Pausa após a etapa (ms, opcional)", Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 10, Margin = new Thickness(2, 6, 2, 3) };
            body.Children.Add(valueLabel);
            body.Children.Add(value);
            body.Children.Add(delayLabel);
            body.Children.Add(delay);
            void UpdateKind()
            {
                switch (type.SelectedIndex)
                {
                    case 0:
                        valueLabel.Text = "Programa, parâmetros ou URL";
                        value.ToolTip = "Ex.: notepad.exe, explorer.exe C:\\Pasta ou https://example.com";
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
                    default:
                        valueLabel.Text = "Tempo de espera (ms)";
                        value.ToolTip = "Informe um valor entre 1 e 60000 ms";
                        value.Height = 36; value.AcceptsReturn = false;
                        value.TextWrapping = TextWrapping.NoWrap;
                        delayLabel.Text = "";
                        break;
                }
                delayLabel.Visibility = type.SelectedIndex == 3 ? Visibility.Collapsed : Visibility.Visible;
                delay.Visibility = type.SelectedIndex == 3 ? Visibility.Collapsed : Visibility.Visible;
            }
            type.SelectionChanged += (_, _) => UpdateKind();
            UpdateKind();
            _stepsPanel.Children.Add(card);
            step.TypeBox = type;
            step.ValueBox = value;
            step.DelayBox = delay;
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
                step.Action = step.TypeBox.SelectedIndex switch { 0 => "Open", 1 => "Key", 2 => "Type", 3 => "Wait", _ => "Type" };
            if (step.ValueBox is not null) step.Value = step.ValueBox.Text;
            if (step.DelayBox is not null)
                step.DelayMilliseconds = int.TryParse(step.DelayBox.Text, out var milliseconds) ? milliseconds : -1;
        }
    }

    private sealed class StepDraft(string action, string value, int delayMilliseconds)
    {
        public string Action { get; set; } = action;
        public string Value { get; set; } = value;
        public int DelayMilliseconds { get; set; } = delayMilliseconds;
        public ComboBox? TypeBox { get; set; }
        public TextBox? ValueBox { get; set; }
        public TextBox? DelayBox { get; set; }
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
