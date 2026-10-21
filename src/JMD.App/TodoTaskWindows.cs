using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JMD.Core;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace JMD.App;

internal sealed class TodoTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }
    public int TaskNumber { get; set; }
    public int? ReminderIntervalMinutes { get; set; }
    public string CategoryColor { get; set; } = "Verde";
    public DateTimeOffset? LastReminderAt { get; set; }
}

internal static class TodoTaskDisplay
{
    public static string Reference(TodoTask task)
    {
        var date = task.CreatedAt.ToLocalTime().Date;
        var relativeDate = date == DateTime.Today ? "Hoje"
            : date == DateTime.Today.AddDays(-1) ? "Ontem"
            : date.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.CurrentCulture);
        return $"({relativeDate} - Tarefa {task.TaskNumber})";
    }

    public static string ReminderLabel(TodoTask task)
        => task.ReminderIntervalMinutes is { } interval ? $"Lembrete a cada {interval} min" : "";
}

internal sealed class TodoTaskEditWindow : Window
{
    private readonly TextBox _title = new();
    private readonly TextBox _description = new();
    private readonly TextBox _reminderInterval = new();
    private readonly TextBlock _validation = new();
    private readonly Dictionary<string, Button> _categoryButtons = new(StringComparer.Ordinal);
    private string _categoryColor = "Verde";
    public string TaskTitle => _title.Text.Trim();
    public string Description => _description.Text.Trim();
    public int? ReminderIntervalMinutes => int.TryParse(_reminderInterval.Text.Trim(), out var interval) ? interval : null;
    public string CategoryColor => _categoryColor;

    public TodoTaskEditWindow(TodoTask? task = null, string initialDescription = "")
    {
        Title = task is null ? "Nova tarefa" : "Editar tarefa";
        Width = 440; SizeToContent = SizeToContent.Height; WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(23, 25, 31)); WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = Title, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 14) });
        panel.Children.Add(Label("Título"));
        StyleBox(_title); _title.Text = task?.Title ?? ""; _title.Margin = new Thickness(0, 4, 0, 12); panel.Children.Add(_title);
        panel.Children.Add(Label("Descrição"));
        StyleBox(_description); _description.AcceptsReturn = true; _description.TextWrapping = TextWrapping.Wrap; _description.Height = 112; _description.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _description.Text = task?.Description ?? initialDescription; _description.Margin = new Thickness(0, 4, 0, 14); panel.Children.Add(_description);
        panel.Children.Add(Label("Prioridade"));
        var categories = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 12) };
        foreach (var category in new[] { "Vermelho", "Laranja", "Amarelo", "Verde" })
        {
            var choice = new Button
            {
                Width = 30, Height = 30, Margin = new Thickness(0, 0, 9, 0), Padding = new Thickness(0),
                Background = CategoryBrush(category), BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Color.FromRgb(58, 62, 72)), ToolTip = category
            };
            System.Windows.Automation.AutomationProperties.SetName(choice, $"Prioridade {category}");
            choice.Click += (_, _) => SelectCategory(category);
            _categoryButtons.Add(category, choice);
            categories.Children.Add(choice);
        }
        panel.Children.Add(categories);
        panel.Children.Add(Label("Lembrete a cada X minutos (deixe vazio para desativar)"));
        StyleBox(_reminderInterval);
        _reminderInterval.Text = task?.ReminderIntervalMinutes?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "";
        _reminderInterval.Margin = new Thickness(0, 4, 0, 8);
        _reminderInterval.ToolTip = "Informe um intervalo entre 1 e 10080 minutos.";
        _reminderInterval.PreviewTextInput += (_, e) => e.Handled = e.Text.Any(character => !char.IsDigit(character));
        System.Windows.DataObject.AddPastingHandler(_reminderInterval, (_, e) =>
        {
            if (e.DataObject.GetDataPresent(System.Windows.DataFormats.Text) && e.DataObject.GetData(System.Windows.DataFormats.Text) is string value && value.All(char.IsDigit)) return;
            e.CancelCommand();
        });
        panel.Children.Add(_reminderInterval);
        _validation.Foreground = new SolidColorBrush(Color.FromRgb(255, 145, 135));
        _validation.FontSize = 11;
        _validation.Margin = new Thickness(0, 0, 0, 8);
        panel.Children.Add(_validation);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = CreateButton("Cancelar"); cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = CreateButton("Salvar"); save.IsDefault = true; save.Click += (_, _) =>
        {
            if (TaskTitle.Length == 0) { _validation.Text = "Informe um título."; _title.Focus(); return; }
            if (!string.IsNullOrWhiteSpace(_reminderInterval.Text) && (ReminderIntervalMinutes is null or < 1 or > 10080))
            {
                _validation.Text = "Informe um intervalo entre 1 e 10080 minutos.";
                _reminderInterval.Focus();
                return;
            }
            DialogResult = true; Close();
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons); Content = panel;
        SelectCategory(task?.CategoryColor ?? "Verde");
        Loaded += (_, _) => { _title.Focus(); Keyboard.Focus(_title); };
        _title.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _description.Focus(); } };
        _description.KeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); } };
    }

    private static TextBlock Label(string text) => new() { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(190, 194, 204)), FontSize = 12 };
    private static void StyleBox(TextBox box) { box.Padding = new Thickness(9); box.FontSize = 13; box.Foreground = Brushes.White; box.Background = new SolidColorBrush(Color.FromRgb(36, 39, 48)); box.BorderBrush = new SolidColorBrush(Color.FromRgb(58, 62, 72)); }
    private static Button CreateButton(string text) => new() { Content = text, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(8, 0, 0, 0), Background = new SolidColorBrush(Color.FromRgb(180, 243, 106)), Foreground = new SolidColorBrush(Color.FromRgb(23, 25, 31)), BorderThickness = new Thickness(0) };

    private void SelectCategory(string category)
    {
        _categoryColor = _categoryButtons.ContainsKey(category) ? category : "Verde";
        foreach (var (name, button) in _categoryButtons)
            button.BorderBrush = new SolidColorBrush(name == _categoryColor ? Color.FromRgb(255, 255, 255) : Color.FromRgb(58, 62, 72));
    }

    internal static SolidColorBrush CategoryBrush(string category) => new(category switch
    {
        "Vermelho" => Color.FromRgb(232, 72, 86),
        "Laranja" => Color.FromRgb(255, 140, 0),
        "Amarelo" => Color.FromRgb(255, 204, 0),
        _ => Color.FromRgb(76, 175, 80)
    });
}

internal sealed class TodoTaskPanelWindow : Window
{
    private readonly ObservableCollection<TodoTask> _tasks;
    private readonly Action<TodoTask> _complete;
    private readonly Action<TodoTask> _edit;
    private readonly Action<TodoTask> _delete;
    private readonly StackPanel _list = new();
    private bool _showHistory;

    public TodoTaskPanelWindow(ObservableCollection<TodoTask> tasks, Action<TodoTask> complete, Action<TodoTask> edit, Action<TodoTask> delete)
    {
        _tasks = tasks; _complete = complete; _edit = edit; _delete = delete;
        Width = 390; Height = SystemParameters.WorkArea.Height; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true; Background = new SolidColorBrush(Color.FromRgb(23, 25, 31));
        WindowStartupLocation = WindowStartupLocation.Manual; Left = SystemParameters.WorkArea.Right - Width; Top = SystemParameters.WorkArea.Top;
        var root = new DockPanel { Margin = new Thickness(18) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var title = new TextBlock { Text = "Tarefas", FontSize = 21, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(title, Dock.Left); header.Children.Add(title);
        var history = IconButton("\uE81C", "Ver tarefas concluídas"); history.Click += (_, _) => { _showHistory = !_showHistory; history.ToolTip = _showHistory ? "Ver tarefas em aberto" : "Ver tarefas concluídas"; Refresh(); };
        DockPanel.SetDock(history, Dock.Right); header.Children.Add(history); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list }; root.Children.Add(scroll);
        Content = root; Refresh();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape || (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Left)) { e.Handled = true; Hide(); } };
    }

    public void Refresh()
    {
        _list.Children.Clear();
        var entries = _tasks.Where(t => _showHistory ? t.CompletedAt is not null : t.CompletedAt is null)
            .OrderBy(t => _showHistory ? t.CompletedAt : t.CreatedAt);
        foreach (var task in entries)
        {
            var card = new Border { Background = new SolidColorBrush(Color.FromRgb(34, 37, 45)), CornerRadius = new CornerRadius(9), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 9) };
            var layout = new DockPanel(); var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            if (!_showHistory)
            {
                var done = IconButton("\uE73E", "Concluir"); done.Click += (_, _) => _complete(task);
                actions.Children.Add(done);
            }
            var edit = IconButton("\uE70F", "Editar"); edit.Click += (_, _) => _edit(task); actions.Children.Add(edit);
            var remove = IconButton("\uE74D", "Excluir"); remove.Click += (_, _) => _delete(task); actions.Children.Add(remove);
            DockPanel.SetDock(actions, Dock.Right); layout.Children.Add(actions);
            var body = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
            body.Children.Add(new TextBlock { Text = TodoTaskDisplay.Reference(task), Foreground = new SolidColorBrush(Color.FromRgb(180, 243, 106)), FontSize = 10, Margin = new Thickness(0, 0, 0, 3) });
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new Border { Width = 10, Height = 10, Background = TodoTaskEditWindow.CategoryBrush(task.CategoryColor), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 4, 7, 0), ToolTip = $"Prioridade {task.CategoryColor}" });
            titleRow.Children.Add(new TextBlock { Text = task.Title, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(titleRow);
            if (!string.IsNullOrWhiteSpace(task.Description)) body.Children.Add(new TextBlock { Text = task.Description.Length > 100 ? "…" + task.Description[^100..] : task.Description, Foreground = new SolidColorBrush(Color.FromRgb(190, 194, 204)), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 5) });
            body.Children.Add(new TextBlock { Text = $"Criada {task.CreatedAt.LocalDateTime:g}" + (task.CompletedAt is null ? "" : $" · Concluída {task.CompletedAt.Value.LocalDateTime:g}"), Foreground = new SolidColorBrush(Color.FromRgb(135, 140, 151)), FontSize = 10 });
            if (task.ReminderIntervalMinutes is not null) body.Children.Add(new TextBlock { Text = TodoTaskDisplay.ReminderLabel(task), Foreground = new SolidColorBrush(Color.FromRgb(180, 243, 106)), FontSize = 10, Margin = new Thickness(0, 3, 0, 0) });
            layout.Children.Add(body); card.Child = layout; _list.Children.Add(card);
        }
        if (_list.Children.Count == 0) _list.Children.Add(new TextBlock { Text = _showHistory ? "Nenhuma tarefa concluída." : "Nenhuma tarefa em aberto.", Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 13, Margin = new Thickness(0, 10, 0, 0) });
    }

    public void FocusFirstAction()
    {
        var firstAction = _list.Children.OfType<Border>()
            .Select(card => card.Child as DockPanel)
            .Where(layout => layout is not null)
            .SelectMany(layout => layout!.Children.OfType<StackPanel>())
            .SelectMany(actions => actions.Children.OfType<Button>())
            .FirstOrDefault();
        if (firstAction is not null) Keyboard.Focus(firstAction);
    }

    private static Button IconButton(string glyph, string hint) => new() { Content = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15, Width = 32, Height = 32, Margin = new Thickness(2), ToolTip = hint, Focusable = true, Background = new SolidColorBrush(Color.FromRgb(49, 53, 63)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
}
