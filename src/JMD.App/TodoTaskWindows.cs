using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using JMD.Core;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;
using ComboBox = System.Windows.Controls.ComboBox;

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
    public string Urls { get; set; } = "";
    public string Attachments { get; set; } = "";
    public DateTimeOffset? PausedAt { get; set; }
    public string PauseReason { get; set; } = "";
    public string PausedBy { get; set; } = "";
    public List<TodoTaskChecklistEntry> Checklist { get; set; } = [];
}

internal sealed class TodoTaskReportWindow : Window
{
    private const string AllComputersFilter = "__all__";
    private readonly IReadOnlyList<TodoTaskReportComputer> _computers;
    private readonly Grid _calendar = new() { Width = 300, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly WrapPanel _calendarLegend = new() { Width = 300, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 5, 0, 0) };
    private readonly StackPanel _report = new();
    private readonly TextBlock _rangeLabel = new();
    private readonly Button _showMonthButton;
    private readonly Button _calendarToggleButton;
    private readonly System.Windows.Controls.CheckBox _executedOnly = new() { Content = "Executado", VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _computerFilter = new() { Width = 220, Height = 34, Margin = new Thickness(0, 10, 0, 0) };
    private string _selectedComputerId = "__local__";
    private bool _calendarExpanded = true;
    private DateTime _displayMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private TodoTaskDateFilter _dateFilter = new();

    public TodoTaskReportWindow(IReadOnlyList<TodoTaskReportComputer> computers)
    {
        _computers = computers;
        Title = "Relatório de tarefas"; Width = Math.Min(1100, SystemParameters.WorkArea.Width - 80);
        Height = Math.Min(820, SystemParameters.WorkArea.Height - 80); MinWidth = 680; MinHeight = 480;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResize; ShowInTaskbar = false; Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(23, 25, 31)); WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new DockPanel { Margin = new Thickness(24) };
        var header = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleBar = new Border { Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.SizeAll, VerticalAlignment = VerticalAlignment.Center };
        titleBar.Child = new TextBlock { Text = "Relatório de tarefas", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        header.Children.Add(titleBar);
        var close = ReportButton("×"); close.Width = 36; close.Height = 36; close.FontSize = 20; close.ToolTip = "Fechar relatório";
        close.Click += (_, _) => Close(); Grid.SetColumn(close, 1); header.Children.Add(close);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        var calendarPanel = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        calendarPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        calendarPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _executedOnly.Style = (Style)System.Windows.Application.Current.FindResource("CompactToggleCheckBoxStyle");
        _executedOnly.HorizontalAlignment = HorizontalAlignment.Left;
        _executedOnly.Margin = new Thickness(0, 4, 12, 0);
        _executedOnly.ToolTip = "Mostrar somente tarefas e etapas concluídas";
        _executedOnly.Checked += (_, _) => RefreshReport();
        _executedOnly.Unchecked += (_, _) => RefreshReport();
        var filters = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        filters.Children.Add(_executedOnly);
        if (_computers.Count > 1)
        {
            _computerFilter.Style = (Style)System.Windows.Application.Current.FindResource("JeyModelComboBoxStyle");
            _computerFilter.ToolTip = "Filtrar relatório e calendário por computador";
            foreach (var computer in _computers)
                _computerFilter.Items.Add(CreateComputerFilterItem(computer));
            _computerFilter.Items.Add(new ComboBoxItem
            {
                Tag = AllComputersFilter,
                Content = new TextBlock { Text = "Todos os computadores", VerticalAlignment = VerticalAlignment.Center }
            });
            _computerFilter.SelectionChanged += (_, _) =>
            {
                if (_computerFilter.SelectedItem is ComboBoxItem option && option.Tag is string id)
                {
                    _selectedComputerId = id;
                    RefreshReport();
                }
            };
            filters.Children.Add(_computerFilter);
        }
        Grid.SetColumn(filters, 0);
        calendarPanel.Children.Add(filters);
        var calendarControlsPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var calendarControls = new DockPanel { Width = 340, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 8) };
        var nextMonth = ReportButton("›"); nextMonth.ToolTip = "Próximo mês";
        nextMonth.Click += (_, _) => { _displayMonth = _displayMonth.AddMonths(1); _dateFilter = new(); RefreshReport(); };
        DockPanel.SetDock(nextMonth, Dock.Right); calendarControls.Children.Add(nextMonth);
        _calendarToggleButton = ReportButton("⌄"); _calendarToggleButton.ToolTip = "Recolher calendário";
        _calendarToggleButton.Click += (_, _) =>
        {
            _calendarExpanded = !_calendarExpanded;
            _calendar.Visibility = _calendarExpanded ? Visibility.Visible : Visibility.Collapsed;
            _calendarLegend.Visibility = _calendarExpanded ? Visibility.Visible : Visibility.Collapsed;
            _calendarToggleButton.Content = _calendarExpanded ? "⌄" : "▦";
            _calendarToggleButton.ToolTip = _calendarExpanded ? "Recolher calendário" : "Expandir calendário";
        };
        DockPanel.SetDock(_calendarToggleButton, Dock.Right); calendarControls.Children.Add(_calendarToggleButton);
        var previousMonth = ReportButton("‹"); previousMonth.ToolTip = "Mês anterior";
        previousMonth.Click += (_, _) => { _displayMonth = _displayMonth.AddMonths(-1); _dateFilter = new(); RefreshReport(); };
        DockPanel.SetDock(previousMonth, Dock.Left); calendarControls.Children.Add(previousMonth);
        var monthControls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        _rangeLabel.Foreground = new SolidColorBrush(Color.FromRgb(220, 223, 230));
        _rangeLabel.VerticalAlignment = VerticalAlignment.Center; _rangeLabel.FontSize = 14; _rangeLabel.FontWeight = FontWeights.SemiBold;
        monthControls.Children.Add(_rangeLabel);
        _showMonthButton = ReportButton("Mês"); _showMonthButton.Margin = new Thickness(10, 0, 0, 0); _showMonthButton.ToolTip = "Mostrar todas as atividades do mês";
        _showMonthButton.Click += (_, _) => { _dateFilter = new(); RefreshReport(); };
        monthControls.Children.Add(_showMonthButton); DockPanel.SetDock(monthControls, Dock.Left); calendarControls.Children.Add(monthControls);
        calendarControlsPanel.Children.Add(calendarControls);
        calendarControlsPanel.Children.Add(_calendar);
        calendarControlsPanel.Children.Add(_calendarLegend);
        Grid.SetColumn(calendarControlsPanel, 1);
        calendarPanel.Children.Add(calendarControlsPanel);
        DockPanel.SetDock(calendarPanel, Dock.Top); root.Children.Add(calendarPanel);
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _report });
        Content = root;
        if (_computerFilter.Items.Count > 0) _computerFilter.SelectedIndex = 0;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        RefreshReport();
    }

    private void RefreshReport()
    {
        var (startDate, endDate) = TodoTaskDateFilterPolicy.Resolve(_dateFilter, _displayMonth);
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        _rangeLabel.Text = _dateFilter.StartDate is { } start
            ? _dateFilter.EndDate is { } end && end.Date != start.Date
                ? $"{start:dd/MM/yyyy} – {end:dd/MM/yyyy}"
                : start.ToString("dd 'de' MMMM yyyy", culture)
            : culture.TextInfo.ToTitleCase(_displayMonth.ToString("MMMM yyyy", culture));
        _showMonthButton.Visibility = _dateFilter.HasSelection ? Visibility.Visible : Visibility.Collapsed;
        var selectedComputers = _selectedComputerId == AllComputersFilter
            ? _computers
            : _computers.Where(computer => computer.Id == _selectedComputerId).ToList();
        var visible = selectedComputers.SelectMany(computer => computer.Events
            .Select(entry => (Entry: entry, LocalTime: entry.OccurredAt.ToLocalTime(), Computer: computer)))
            .Where(item => item.LocalTime.Date >= startDate && item.LocalTime.Date <= endDate)
            .Where(item => !_executedOnly.IsChecked.GetValueOrDefault() || IsCompletionEvent(item.Entry)).ToList();
        RenderCalendar(selectedComputers);

        _report.Children.Clear();
        foreach (var dayGroup in visible.GroupBy(item => item.LocalTime.Date).OrderByDescending(group => group.Key))
        {
            _report.Children.Add(new TextBlock { Text = dayGroup.Key.ToString("dddd, dd 'de' MMMM 'de' yyyy", culture), Foreground = new SolidColorBrush(Color.FromRgb(180, 243, 106)), FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 8) });
            var periods = new[] { "Manhã", "Tarde", "Noite" };
            var columns = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            foreach (var _ in periods) columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var periodIndex = 0; periodIndex < periods.Length; periodIndex++)
            {
                var periodName = periods[periodIndex];
                var periodPanel = new StackPanel { Margin = new Thickness(periodIndex == 0 ? 0 : 5, 0, periodIndex == periods.Length - 1 ? 0 : 5, 0) };
                periodPanel.Children.Add(new TextBlock { Text = periodName, Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4) });
                var periodEvents = dayGroup.Where(item =>
                {
                    var hour = item.LocalTime.Hour;
                    var itemPeriod = hour >= 5 && hour < 12 ? "Manhã" : hour >= 12 && hour < 18 ? "Tarde" : "Noite";
                    return itemPeriod == periodName;
                }).OrderByDescending(item => item.LocalTime).ToList();
                if (_executedOnly.IsChecked == true)
                {
                    var taskGroups = periodEvents.GroupBy(item => item.Computer.Id + ":" + GetExecutedTaskGrouping(item.Entry), StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(group => group.Max(item => item.LocalTime));
                    foreach (var taskGroup in taskGroups)
                    {
                        var orderedActions = taskGroup.OrderBy(item => item.LocalTime).ToList();
                        var latest = orderedActions[^1];
                        var card = new Border { Background = new SolidColorBrush(Color.FromRgb(34, 37, 45)), CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 5) };
                        var content = new StackPanel();
                        content.Children.Add(ComputerBadge(latest.Computer));
                        content.Children.Add(new TextBlock { Text = GetExecutedTaskLabel(latest.Entry), Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                        foreach (var action in orderedActions)
                        {
                            var actionText = GetExecutedActionLabel(action.Entry);
                            content.Children.Add(new TextBlock
                            {
                                Text = $"{action.LocalTime:HH:mm}  ·  {actionText}",
                                Foreground = new SolidColorBrush(Color.FromRgb(190, 194, 204)),
                                FontSize = 11,
                                TextWrapping = TextWrapping.Wrap,
                                Margin = new Thickness(0, 5, 0, 0)
                            });
                        }
                        card.Child = content;
                        periodPanel.Children.Add(card);
                    }
                }
                else
                {
                    foreach (var item in periodEvents)
                    {
                        var card = new Border { Background = new SolidColorBrush(Color.FromRgb(34, 37, 45)), CornerRadius = new CornerRadius(6), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 5) };
                        var content = new StackPanel();
                        content.Children.Add(ComputerBadge(item.Computer));
                        content.Children.Add(new TextBlock { Text = $"{item.LocalTime:HH:mm}  ·  {item.Entry.CommandName}", Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                        if (!string.IsNullOrWhiteSpace(item.Entry.AfterValue))
                            content.Children.Add(new TextBlock { Text = item.Entry.AfterValue, Foreground = new SolidColorBrush(Color.FromRgb(190, 194, 204)), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
                        card.Child = content; periodPanel.Children.Add(card);
                    }
                }
                if (periodEvents.Count == 0)
                    periodPanel.Children.Add(new TextBlock { Text = "—", Foreground = new SolidColorBrush(Color.FromRgb(100, 104, 114)), FontSize = 12, Margin = new Thickness(2, 5, 0, 0) });
                Grid.SetColumn(periodPanel, periodIndex); columns.Children.Add(periodPanel);
            }
            _report.Children.Add(columns);
        }
        if (visible.Count == 0) _report.Children.Add(new TextBlock { Text = "Nenhuma atividade de tarefa neste período.", Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 13, Margin = new Thickness(0, 16, 0, 0) });
    }

    private static bool IsCompletionEvent(ConversionHistoryEntry entry)
    {
        if (entry.CommandName.StartsWith("Tarefa concluída", StringComparison.OrdinalIgnoreCase)) return true;
        if (!entry.CommandName.StartsWith("Tarefa checklist", StringComparison.OrdinalIgnoreCase)) return false;
        return entry.AfterValue.Contains("concluído", StringComparison.OrdinalIgnoreCase)
            || entry.AfterValue.Contains("concluída", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetExecutedTaskGrouping(ConversionHistoryEntry entry)
    {
        var label = GetExecutedTaskLabel(entry);
        var closingParenthesis = label.IndexOf(')');
        if (label.StartsWith('(') && closingParenthesis > 0)
            return $"reference:{label[..(closingParenthesis + 1)]}";
        return $"title:{label.Trim()}";
    }

    private static string GetExecutedTaskLabel(ConversionHistoryEntry entry)
    {
        var separator = entry.CommandName.IndexOf(" — ", StringComparison.Ordinal);
        if (separator >= 0 && separator + 3 < entry.CommandName.Length)
            return entry.CommandName[(separator + 3)..].Trim();
        return entry.AfterValue.Trim();
    }

    private static string GetExecutedActionLabel(ConversionHistoryEntry entry)
    {
        if (!entry.CommandName.StartsWith("Tarefa checklist", StringComparison.OrdinalIgnoreCase))
            return "Tarefa concluída";
        return string.IsNullOrWhiteSpace(entry.AfterValue) ? "Etapa concluída" : entry.AfterValue.Trim();
    }

    private void RenderCalendar(IReadOnlyList<TodoTaskReportComputer> computers)
    {
        _calendar.Children.Clear();
        _calendar.RowDefinitions.Clear();
        _calendar.ColumnDefinitions.Clear();
        for (var column = 0; column < 7; column++)
            _calendar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _calendar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var weekdays = new[] { "Dom", "Seg", "Ter", "Qua", "Qui", "Sex", "Sáb" };
        for (var column = 0; column < weekdays.Length; column++)
        {
            var label = new TextBlock { Text = weekdays[column], Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 5) };
            Grid.SetColumn(label, column); _calendar.Children.Add(label);
        }

        var dayComputers = computers.SelectMany(computer => computer.Events
                .Select(entry => (Date: entry.OccurredAt.ToLocalTime().Date, Computer: computer)))
            .Where(item => item.Date.Year == _displayMonth.Year && item.Date.Month == _displayMonth.Month)
            .GroupBy(item => item.Date)
            .ToDictionary(group => group.Key, group => group.GroupBy(item => item.Computer.Id)
                .Select(computerGroup => (Computer: computerGroup.First().Computer, Count: computerGroup.Count())).ToList());
        _calendarLegend.Children.Clear();
        foreach (var computer in dayComputers.Values.SelectMany(day => day).Select(item => item.Computer).DistinctBy(computer => computer.Id))
            _calendarLegend.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(34, 37, 45)), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(0, 0, 5, 5),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(computer.Color), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = computer.Name, Foreground = new SolidColorBrush(Color.FromRgb(190, 194, 204)), FontSize = 9, VerticalAlignment = VerticalAlignment.Center }
                    }
                }
            });
        var firstDayOffset = TodoTaskDateFilterPolicy.GetWeekdayColumn(_displayMonth.DayOfWeek);
        var daysInMonth = DateTime.DaysInMonth(_displayMonth.Year, _displayMonth.Month);
        var weekRows = (firstDayOffset + daysInMonth + 6) / 7;
        for (var row = 0; row < weekRows; row++) _calendar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });

        for (var dayNumber = 1; dayNumber <= daysInMonth; dayNumber++)
        {
            var index = firstDayOffset + dayNumber - 1;
            var row = index / 7 + 1;
            var column = index % 7;
            var date = new DateTime(_displayMonth.Year, _displayMonth.Month, dayNumber);
            var hasEvents = dayComputers.TryGetValue(date, out var computersForDay);
            var inSelectedRange = TodoTaskDateFilterPolicy.Contains(_dateFilter, date);
            var selectedEndpoint = _dateFilter.StartDate?.Date == date || _dateFilter.EndDate?.Date == date;
            var dayButton = new Button
            {
                Content = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = dayNumber.ToString(System.Globalization.CultureInfo.CurrentCulture), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Margin = new Thickness(0, 2, 0, 0),
                            Children = { }
                        }
                    }
                },
                Margin = new Thickness(2), Padding = new Thickness(2), BorderThickness = selectedEndpoint ? new Thickness(1) : new Thickness(0),
                BorderBrush = new SolidColorBrush(Color.FromRgb(180, 243, 106)),
                Background = inSelectedRange ? new SolidColorBrush(Color.FromRgb(61, 77, 53)) : new SolidColorBrush(Color.FromRgb(34, 37, 45)),
                Foreground = Brushes.White, Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = hasEvents
                    ? $"{date:dd/MM/yyyy}\n{string.Join("\n", computersForDay!.Select(item => $"{item.Computer.Name}: {item.Count} {(item.Count == 1 ? "evento" : "eventos")}"))}\nClique para filtrar; Shift+clique para selecionar o fim do intervalo"
                    : $"{date:dd/MM/yyyy} · sem eventos\nClique para filtrar; Shift+clique para selecionar o fim do intervalo"
            };
            var computerDots = (StackPanel)((StackPanel)dayButton.Content).Children[1];
            if (hasEvents)
                foreach (var item in computersForDay!)
                    computerDots.Children.Add(new System.Windows.Shapes.Ellipse
                    {
                        Width = 5, Height = 5, Margin = new Thickness(1, 0, 1, 0),
                        Fill = new SolidColorBrush(item.Computer.Color), ToolTip = item.Computer.Name
                    });
            dayButton.Click += (_, _) =>
            {
                _dateFilter = TodoTaskDateFilterPolicy.SelectDay(_dateFilter, date, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                RefreshReport();
            };
            Grid.SetRow(dayButton, row); Grid.SetColumn(dayButton, column); _calendar.Children.Add(dayButton);
        }
    }

    private static ComboBoxItem CreateComputerFilterItem(TodoTaskReportComputer computer)
        => new()
        {
            Tag = computer.Id,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(computer.Color), Margin = new Thickness(0, 0, 8, 0) },
                    new TextBlock { Text = computer.Name, VerticalAlignment = VerticalAlignment.Center }
                }
            }
        };

    private static Border ComputerBadge(TodoTaskReportComputer computer)
        => new()
        {
            Background = new SolidColorBrush(Color.FromArgb(38, computer.Color.R, computer.Color.G, computer.Color.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(110, computer.Color.R, computer.Color.G, computer.Color.B)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1, 6, 1),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 5),
            Child = new TextBlock { Text = computer.Name, Foreground = new SolidColorBrush(computer.Color), FontSize = 9 }
        };

    private static Button ReportButton(string text) => new() { Content = text, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(4, 0, 0, 0), Background = new SolidColorBrush(Color.FromRgb(49, 53, 63)), Foreground = Brushes.White, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
}

internal sealed record TodoTaskReportComputer(string Id, string Name, Color Color, IReadOnlyList<ConversionHistoryEntry> Events);

internal static class TodoTaskComputerPalette
{
    private static readonly Color[] RemoteColors =
    [
        Color.FromRgb(157, 127, 255), Color.FromRgb(89, 190, 255), Color.FromRgb(255, 174, 92),
        Color.FromRgb(255, 121, 164), Color.FromRgb(88, 214, 173), Color.FromRgb(255, 220, 98)
    ];

    public static Color Local => Color.FromRgb(180, 243, 106);
    public static Color Remote(int index) => RemoteColors[Math.Abs(index) % RemoteColors.Length];
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
        => task.ReminderIntervalMinutes is { } interval
            ? task.PausedAt is null ? $"Lembrete a cada {interval} min" : $"Lembrete suspenso · a cada {interval} min"
            : "";
}

internal sealed class TodoTaskEditWindow : Window
{
    private readonly TextBlock _validation = new();
    private readonly TextBox _title = new();
    private readonly TextBox _description = new();
    private readonly TextBox _reminderInterval = new();
    private readonly TextBox _urls = new();
    private readonly TextBox _attachments = new();
    private readonly StackPanel _checklistPanel = new();
    private readonly ComboBox? _destinationPicker;
    private readonly List<(TextBox Title, TextBox Weight, bool Completed)> _checklistRows = [];
    private readonly Dictionary<string, Button> _categoryButtons = new(StringComparer.Ordinal);
    private string _categoryColor = "Verde";
    public string TaskTitle => _title.Text.Trim();
    public string Description => _description.Text.Trim();
    public int? ReminderIntervalMinutes => int.TryParse(_reminderInterval.Text.Trim(), out var interval) ? interval : null;
    public string CategoryColor => _categoryColor;
    public string Urls => _urls.Text.Trim();
    public string Attachments => _attachments.Text.Trim();
    public IReadOnlyList<TodoTaskChecklistEntry> Checklist => _checklistRows
        .Where(row => !string.IsNullOrWhiteSpace(row.Title.Text))
        .Select(row => new TodoTaskChecklistEntry(row.Title.Text.Trim(),
            decimal.TryParse(row.Weight.Text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.CurrentCulture, out var weight) ? weight : -1m,
            row.Completed)).ToList();
    public string? TargetPeerId => (_destinationPicker?.SelectedItem as ComboBoxItem)?.Tag as string;

    public TodoTaskEditWindow(TodoTask? task = null, string initialDescription = "", string initialUrls = "", string initialAttachments = "",
        IReadOnlyList<DesktopPairedPeer>? connectedPeers = null)
    {
        Title = task is null ? "Nova tarefa" : "Editar tarefa";
        Width = 440; SizeToContent = SizeToContent.Height; WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(23, 25, 31)); WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(18) };
        var titleBar = new Border
        {
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.SizeAll,
            Margin = new Thickness(0, 0, 0, 14),
            Child = new TextBlock { Text = Title, FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White }
        };
        titleBar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        };
        panel.Children.Add(titleBar);
        panel.Children.Add(Label("Título"));
        StyleBox(_title); _title.Text = task?.Title ?? ""; _title.Margin = new Thickness(0, 4, 0, 12); panel.Children.Add(_title);
        panel.Children.Add(Label("Descrição"));
        StyleBox(_description); _description.AcceptsReturn = true; _description.TextWrapping = TextWrapping.Wrap; _description.Height = 112; _description.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _description.Text = task?.Description ?? initialDescription; _description.Margin = new Thickness(0, 4, 0, 14); panel.Children.Add(_description);
        if (connectedPeers is { Count: > 0 })
        {
            panel.Children.Add(Label(task is null ? "Criar em" : "Mover para"));
            _destinationPicker = new ComboBox
            {
                Height = 36, Margin = new Thickness(0, 4, 0, 12),
                Style = (Style)System.Windows.Application.Current.FindResource("JeyModelComboBoxStyle"),
                ToolTip = task is null ? "Escolha em qual computador a nova tarefa será criada" : "Escolha o computador para onde esta tarefa será movida"
            };
            _destinationPicker.Items.Add(CreateDestinationItem("Este computador", null, TodoTaskComputerPalette.Local));
            for (var index = 0; index < connectedPeers.Count; index++)
            {
                var peer = connectedPeers[index];
                _destinationPicker.Items.Add(CreateDestinationItem(peer.Name, peer.Id, TodoTaskComputerPalette.Remote(index)));
            }
            _destinationPicker.SelectedIndex = 0;
            panel.Children.Add(_destinationPicker);
        }
        panel.Children.Add(Label("Checklist (peso de cada etapa no progresso)"));
        _checklistPanel.Margin = new Thickness(0, 5, 0, 12); panel.Children.Add(_checklistPanel);
        foreach (var entry in task?.Checklist ?? []) AddChecklistRow(entry.Title, entry.Weight, entry.IsCompleted);
        AddChecklistRow("", 0, false);
        var resourcesPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var resources = new Expander
        {
            Header = "Links e anexos (opcional)",
            IsExpanded = !string.IsNullOrWhiteSpace(task?.Urls) || !string.IsNullOrWhiteSpace(task?.Attachments) ||
                         !string.IsNullOrWhiteSpace(initialUrls) || !string.IsNullOrWhiteSpace(initialAttachments),
            Foreground = new SolidColorBrush(Color.FromRgb(190, 194, 204)),
            FontSize = 12
        };
        var resourceFields = new StackPanel { Margin = new Thickness(16, 8, 0, 0) };
        resourceFields.Children.Add(Label("URLs (uma por linha)"));
        StyleBox(_urls); _urls.Text = task?.Urls ?? initialUrls; _urls.Height = 36; _urls.AcceptsReturn = true; _urls.TextWrapping = TextWrapping.Wrap; _urls.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _urls.Margin = new Thickness(0, 4, 0, 8); resourceFields.Children.Add(_urls);
        resourceFields.Children.Add(Label("Anexos"));
        StyleBox(_attachments); _attachments.Height = 32; _attachments.AcceptsReturn = true; _attachments.TextWrapping = TextWrapping.Wrap; _attachments.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _attachments.ToolTip = "Copie arquivos no Explorador e pressione Ctrl+V aqui para adicionar os caminhos.";
        _attachments.Text = task?.Attachments ?? initialAttachments; _attachments.Margin = new Thickness(0, 4, 0, 5); resourceFields.Children.Add(_attachments);
        _attachments.PreviewKeyDown += Attachments_PreviewKeyDown;
        var addAttachment = CreateButton("Adicionar arquivo"); addAttachment.Height = 28; addAttachment.Margin = new Thickness(0, 0, 0, 4); addAttachment.HorizontalAlignment = HorizontalAlignment.Left;
        addAttachment.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Selecionar anexos" };
            if (picker.ShowDialog(this) != true) return;
            var paths = _attachments.Text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ToList();
            paths.AddRange(picker.FileNames.Where(path => !paths.Contains(path, StringComparer.OrdinalIgnoreCase)));
            _attachments.Text = string.Join(Environment.NewLine, paths);
        };
        resourceFields.Children.Add(addAttachment);
        resources.Content = resourceFields;
        resourcesPanel.Children.Add(resources);
        panel.Children.Add(resourcesPanel);
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
            var checklist = Checklist;
            if (checklist.Count > 0 && (checklist.Any(entry => entry.Weight < 0 || entry.Weight > 100) || checklist.Sum(entry => entry.Weight) != 100m))
            {
                _validation.Text = "Os pesos das etapas precisam somar 100%.";
                return;
            }
            DialogResult = true; Close();
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons); Content = panel;
        SelectCategory(task?.CategoryColor ?? "Verde");
        Loaded += (_, _) =>
        {
            FocusInitialField();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(FocusInitialField));
        };
        PreviewKeyDown += TodoTaskEditWindow_PreviewKeyDown;
        _title.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _description.Focus(); } };
        _description.KeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); } };
    }

    private void FocusInitialField()
    {
        if (!IsVisible) return;
        if (TodoTaskWindowFocusPolicy.ShouldActivateWindow(IsVisible, IsActive)) Activate();
        _title.Focus();
        Keyboard.Focus(_title);
    }

    private static ComboBoxItem CreateDestinationItem(string name, string? peerId, Color color)
        => new()
        {
            Tag = peerId,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(color), Margin = new Thickness(0, 0, 9, 0) },
                    new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }
                }
            }
        };

    private void Attachments_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.V || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;

        try
        {
            if (!System.Windows.Clipboard.ContainsFileDropList()) return;
            var copiedPaths = System.Windows.Clipboard.GetFileDropList().Cast<string>().ToList();
            if (copiedPaths.Count == 0) return;

            var paths = _attachments.Text.Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            foreach (var path in copiedPaths)
                if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase)) paths.Add(path);

            _attachments.Text = string.Join(Environment.NewLine, paths);
            _attachments.CaretIndex = _attachments.Text.Length;
            _attachments.ScrollToEnd();
            e.Handled = true;
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException)
        {
            // Keep the normal text paste behavior if the clipboard is temporarily unavailable.
        }
    }

    private void AddChecklistRow(string title, decimal weight, bool completed)
    {
        var titleBox = new TextBox
        {
            MinWidth = 0, MinHeight = 31, MaxHeight = 64, VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 12, Padding = new Thickness(6), TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        StyleBox(titleBox); titleBox.FontSize = 12; titleBox.Padding = new Thickness(6); titleBox.Text = title;
        var weightBox = new TextBox { Width = 66, Height = 31, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0), ToolTip = "Peso percentual" };
        StyleBox(weightBox); weightBox.FontSize = 12; weightBox.Padding = new Thickness(5, 4, 5, 4);
        weightBox.Text = weight.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
        (TextBox Title, TextBox Weight, bool Completed) row = (titleBox, weightBox, completed);
        _checklistRows.Add(row);
        var line = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleBox.Margin = new Thickness(0, 0, 4, 0);
        line.Children.Add(titleBox);
        Grid.SetColumn(weightBox, 1); line.Children.Add(weightBox);
        var percentLabel = new TextBlock { Text = "%", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        Grid.SetColumn(percentLabel, 2); line.Children.Add(percentLabel);
        _checklistPanel.Children.Add(line);
        titleBox.TextChanged += (_, _) =>
        {
            if (_updatingChecklistRows) return;
            if (ReferenceEquals(titleBox, _checklistRows.LastOrDefault().Title) && !string.IsNullOrWhiteSpace(titleBox.Text))
            {
                RedistributeChecklistWeights(); AddChecklistRow("", 0, false);
            }
            else if (string.IsNullOrWhiteSpace(titleBox.Text) && _checklistRows.Count > 1 && !ReferenceEquals(titleBox, _checklistRows.Last().Title))
            {
                _checklistRows.Remove(row); _checklistPanel.Children.Remove(line); RedistributeChecklistWeights();
            }
        };
        weightBox.LostFocus += (_, _) => CommitChecklistWeight(weightBox);
        weightBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            CommitChecklistWeight(weightBox);
        };
    }

    private bool _updatingChecklistRows;
    private void RedistributeChecklistWeights()
    {
        var active = _checklistRows.Where(row => !string.IsNullOrWhiteSpace(row.Title.Text)).ToList();
        if (active.Count == 0) return;
        var baseWeight = decimal.Floor(100m / active.Count * 100m) / 100m;
        _updatingChecklistRows = true;
        for (var i = 0; i < active.Count; i++) active[i].Weight.Text = (i == active.Count - 1 ? 100m - baseWeight * (active.Count - 1) : baseWeight).ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
        _updatingChecklistRows = false;
    }

    private void CommitChecklistWeight(TextBox changedBox)
    {
        if (_updatingChecklistRows || !decimal.TryParse(changedBox.Text, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.CurrentCulture, out var requestedWeight)) return;
        var active = _checklistRows.Where(row => !string.IsNullOrWhiteSpace(row.Title.Text)).ToList();
        var changedIndex = active.FindIndex(row => ReferenceEquals(row.Weight, changedBox));
        if (changedIndex < 0) return;
        var weights = TodoTaskChecklistWeightPolicy.RebalanceAfterEdit(active.Select(row =>
            decimal.TryParse(row.Weight.Text, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.CurrentCulture, out var currentWeight) ? currentWeight : 0m).ToArray(),
            changedIndex, requestedWeight);
        _updatingChecklistRows = true;
        for (var i = 0; i < active.Count; i++)
            active[i].Weight.Text = weights[i].ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
        _updatingChecklistRows = false;
    }

    private void TodoTaskEditWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
            return;
        }

        if (e.Key is not (Key.Up or Key.Down)) return;
        if (Keyboard.FocusedElement is TextBox { AcceptsReturn: true } textBox)
        {
            var currentLine = textBox.GetLineIndexFromCharacterIndex(textBox.CaretIndex);
            var atBoundary = e.Key == Key.Up ? currentLine == 0 : currentLine >= textBox.LineCount - 1;
            if (!atBoundary) return;
        }

        var direction = e.Key == Key.Up ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next;
        if (MoveFocus(new TraversalRequest(direction))) e.Handled = true;
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

internal sealed class TodoTaskPauseWindow : Window
{
    private readonly TextBox _reason = new();
    private readonly TextBox _responsible = new();

    public string Reason => _reason.Text.Trim();
    public string Responsible => _responsible.Text.Trim();

    public TodoTaskPauseWindow(string taskTitle)
    {
        Title = "Pausar tarefa";
        Width = 400; SizeToContent = SizeToContent.Height; WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        Background = new SolidColorBrush(Color.FromRgb(23, 25, 31)); WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "Pausar tarefa", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = taskTitle, Foreground = new SolidColorBrush(Color.FromRgb(180, 243, 106)), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });

        panel.Children.Add(Label("Motivo da pausa"));
        StyleBox(_reason); _reason.AcceptsReturn = true; _reason.TextWrapping = TextWrapping.Wrap; _reason.Height = 68; _reason.Margin = new Thickness(0, 4, 0, 12); panel.Children.Add(_reason);
        panel.Children.Add(Label("Responsável"));
        StyleBox(_responsible); _responsible.Margin = new Thickness(0, 4, 0, 8); panel.Children.Add(_responsible);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = CreateDialogButton("Cancelar");
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = CreateDialogButton("Pausar");
        save.IsDefault = true;
        save.Click += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => { _reason.Focus(); Keyboard.Focus(_reason); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                DialogResult = false;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                e.Handled = true;
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        };
    }

    private static TextBlock Label(string text) => new() { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(190, 194, 204)), FontSize = 12 };
    private static void StyleBox(TextBox box) { box.Padding = new Thickness(9); box.FontSize = 13; box.Foreground = Brushes.White; box.Background = new SolidColorBrush(Color.FromRgb(36, 39, 48)); box.BorderBrush = new SolidColorBrush(Color.FromRgb(58, 62, 72)); }
    private static Button CreateDialogButton(string text) => new() { Content = text, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(8, 0, 0, 0), Background = new SolidColorBrush(Color.FromRgb(180, 243, 106)), Foreground = new SolidColorBrush(Color.FromRgb(23, 25, 31)), BorderThickness = new Thickness(0) };
}

internal sealed class TodoTaskPanelWindow : Window
{
    private const string LocalComputerFilter = "__local__";
    private const string AllComputersFilter = "__all__";
    private readonly ObservableCollection<TodoTask> _tasks;
    private readonly UserSettings _settings;
    private readonly DesktopTaskApiClient _desktopTaskApi;
    private readonly Action<TodoTask> _complete;
    private readonly Action<TodoTask> _edit;
    private readonly Action<TodoTask> _delete;
    private readonly Action<TodoTask> _reopen;
    private readonly Action<TodoTask> _pause;
    private readonly Action<TodoTask> _unpause;
    private readonly Action<TodoTask, int> _toggleChecklist;
    private readonly Func<Task> _create;
    private readonly Action _showReport;
    private readonly Action<IReadOnlyList<TodoTaskEntry>> _undo;
    private readonly Button _historyButton;
    private readonly Button _undoButton;
    private readonly Button _reportButton;
    private readonly Button _newTaskButton;
    private readonly Button _businessmapButton;
    private readonly Func<Task<IReadOnlyList<BusinessmapTaskCard>>> _loadBusinessmapTasks;
    private readonly System.Windows.Threading.DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly TextBlock _title;
    private readonly StackPanel _list = new();
    private readonly ComboBox _computerFilter = new() { Height = 36, Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
    private readonly Button _refreshRemoteButton = new() { Content = "Atualizar", Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(8, 0, 0, 8), Visibility = Visibility.Collapsed, Background = new SolidColorBrush(Color.FromRgb(49, 53, 63)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
    private readonly System.Windows.Threading.DispatcherTimer _remoteRefreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly List<RemoteTodoTask> _remoteTasks = [];
    private readonly List<DesktopPairedPeer> _onlinePeers = [];
    private string _selectedComputerId = LocalComputerFilter;
    private bool _loadingRemoteTasks;
    private string? _remoteTaskError;
    private List<TodoTaskEntry>? _pendingUndoSnapshot;
    private List<TodoTaskEntry>? _undoSnapshot;
    private bool _showHistory;
    private bool _modalChildActive;
    private bool _businessmapEnabled;
    private bool _showBusinessmap;
    private bool _loadingBusinessmap;
    private string? _businessmapError;
    private List<BusinessmapTaskCard> _businessmapTasks = [];

    public TodoTaskPanelWindow(ObservableCollection<TodoTask> tasks, Action<TodoTask> complete, Action<TodoTask> edit,
        Action<TodoTask> delete, Action<TodoTask> reopen, Action<TodoTask> pause, Action<TodoTask> unpause, Action<TodoTask, int> toggleChecklist,
        Func<Task> onCreate, Action showReport, Action<IReadOnlyList<TodoTaskEntry>> undo,
        Func<Task<IReadOnlyList<BusinessmapTaskCard>>> loadBusinessmapTasks, bool businessmapEnabled,
        UserSettings settings, SettingsStore settingsStore)
    {
        _settings = settings;
        _desktopTaskApi = new DesktopTaskApiClient(settings, settingsStore);
        _computerFilter.Style = (Style)System.Windows.Application.Current.FindResource("JeyModelComboBoxStyle");
        _computerFilter.ToolTip = "Filtrar tarefas por computador";
        _tasks = tasks; _complete = complete; _edit = edit; _delete = delete; _reopen = reopen;
        _pause = pause; _unpause = unpause; _toggleChecklist = toggleChecklist; _create = onCreate; _showReport = showReport; _undo = undo;
        _loadBusinessmapTasks = loadBusinessmapTasks; _businessmapEnabled = businessmapEnabled;
        Width = 390; Height = SystemParameters.WorkArea.Height; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false; Topmost = true; Background = new SolidColorBrush(Color.FromRgb(23, 25, 31));
        WindowStartupLocation = WindowStartupLocation.Manual; Left = SystemParameters.WorkArea.Right - Width; Top = SystemParameters.WorkArea.Top;
        var root = new DockPanel { Margin = new Thickness(18) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        _title = new TextBlock { Text = "Tarefas", FontSize = 21, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_title, Dock.Left); header.Children.Add(_title);
        _businessmapButton = IconButton("\uE71B", "Mostrar tarefas do Businessmap");
        _businessmapButton.Click += async (_, _) => await ToggleBusinessmapAsync();
        DockPanel.SetDock(_businessmapButton, Dock.Right); header.Children.Add(_businessmapButton);
        var collapse = IconButton("\uE76C", "Recolher painel"); collapse.Click += (_, _) => Hide(); DockPanel.SetDock(collapse, Dock.Right); header.Children.Add(collapse);
        _historyButton = IconButton("\uE81C", "Ver tarefas concluídas");
        _historyButton.Click += async (_, _) => { _showHistory = !_showHistory; _historyButton.ToolTip = _showHistory ? "Ver tarefas em aberto" : "Ver tarefas concluídas"; Refresh(); await RefreshRemoteTasksAsync(); };
        DockPanel.SetDock(_historyButton, Dock.Right); header.Children.Add(_historyButton); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        _reportButton = IconButton("\uE9D2", "Relatório de tarefas"); _reportButton.Click += (_, _) => _showReport(); DockPanel.SetDock(_reportButton, Dock.Right); header.Children.Add(_reportButton);
        var content = new DockPanel();
        _newTaskButton = new Button { Content = "＋  Nova tarefa", Height = 40, Margin = new Thickness(0, 12, 0, 0), Background = new SolidColorBrush(Color.FromRgb(180, 243, 106)), Foreground = new SolidColorBrush(Color.FromRgb(23, 25, 31)), FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
        _newTaskButton.Click += async (_, _) => await RunUndoableCreateAsync();
        _undoButton = new Button
        {
            Content = "Desfazer", Height = 34, Margin = new Thickness(0, 10, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(49, 53, 63)), Foreground = Brushes.White,
            BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Desfaz a última ação no painel de tarefas", Visibility = Visibility.Collapsed
        };
        _undoButton.Click += (_, _) => UndoLastAction();
        _undoTimer.Tick += (_, _) => ClearUndo();
        _remoteRefreshTimer.Tick += async (_, _) => await RefreshRemoteTasksAsync();
        var bottomActions = new StackPanel();
        bottomActions.Children.Add(_undoButton);
        bottomActions.Children.Add(_newTaskButton);
        DockPanel.SetDock(bottomActions, Dock.Bottom); content.Children.Add(bottomActions);
        var filterRow = new DockPanel();
        _refreshRemoteButton.Click += async (_, _) => await RefreshRemoteTasksAsync();
        DockPanel.SetDock(_refreshRemoteButton, Dock.Right); filterRow.Children.Add(_refreshRemoteButton);
        _computerFilter.SelectionChanged += (_, _) =>
        {
            if (_computerFilter.SelectedItem is ComboBoxItem option && option.Tag is string id)
            {
                _selectedComputerId = id;
                Refresh();
            }
        };
        filterRow.Children.Add(_computerFilter);
        DockPanel.SetDock(filterRow, Dock.Top); content.Children.Add(filterRow);
        content.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list }); root.Children.Add(content);
        Content = root; UpdateComputerFilter(); Refresh();
        IsVisibleChanged += async (_, _) =>
        {
            if (!IsVisible) { ClearUndo(); _remoteRefreshTimer.Stop(); }
            else { _remoteRefreshTimer.Start(); await RefreshRemoteTasksAsync(); }
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape || (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Left)) { e.Handled = true; Hide(); } };
        Deactivated += (_, _) => { if (!_modalChildActive) Hide(); };
    }

    public void Refresh()
    {
        if (_showBusinessmap)
        {
            RenderBusinessmapTasks();
            return;
        }
        TryPromotePendingUndo();
        _list.Children.Clear();
        _title.Text = _showHistory ? "Histórico" : "Tarefas";
        var localTasks = _tasks.Where(task => _showHistory ? task.CompletedAt is not null : task.CompletedAt is null)
            .Select(task => new RemoteTodoTask(task, Environment.MachineName, false, string.Empty));
        var remoteTasks = _remoteTasks.Where(item => _showHistory ? item.Task.CompletedAt is not null : item.Task.CompletedAt is null);
        var entries = _selectedComputerId switch
        {
            AllComputersFilter => localTasks.Concat(remoteTasks).ToList(),
            LocalComputerFilter => localTasks.ToList(),
            _ => remoteTasks.Where(item => item.PeerId == _selectedComputerId).ToList()
        };
        if (_loadingRemoteTasks && _selectedComputerId != LocalComputerFilter)
            _list.Children.Add(new TextBlock { Text = "Consultando computadores pareados…", Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 12, Margin = new Thickness(0, 0, 0, 8) });
        if (!string.IsNullOrWhiteSpace(_remoteTaskError) && _selectedComputerId != LocalComputerFilter)
            _list.Children.Add(new TextBlock { Text = _remoteTaskError, Foreground = new SolidColorBrush(Color.FromRgb(255, 190, 120)), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        foreach (var dayGroup in entries.GroupBy(item => item.Task.CreatedAt.ToLocalTime().Date).OrderByDescending(group => group.Key))
        {
            var day = dayGroup.Key;
            var dayLabel = day == DateTime.Today ? "Hoje" : day == DateTime.Today.AddDays(-1) ? "Ontem" : day.ToString("dddd, dd/MM/yyyy", System.Globalization.CultureInfo.CurrentCulture);
            _list.Children.Add(new TextBlock { Text = dayLabel, Foreground = new SolidColorBrush(Color.FromRgb(180, 243, 106)), FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 3, 0, 7) });
            foreach (var item in dayGroup.OrderBy(item => _showHistory ? item.Task.CompletedAt : item.Task.CreatedAt))
            {
            var task = item.Task;
            var card = new Border { Background = new SolidColorBrush(Color.FromRgb(34, 37, 45)), CornerRadius = new CornerRadius(9), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 9) };
            var layout = new StackPanel();
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 0, 4) };
            if (!item.IsRemote && !_showHistory)
            {
                var done = IconButton("\uE73E", "Concluir"); done.Click += (_, _) => RunUndoableAction(() => _complete(task));
                actions.Children.Add(done);
                if (task.PausedAt is null)
                {
                    var pause = IconButton("", "Pausar tarefa");
                    var pauseGlyph = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    pauseGlyph.Children.Add(new System.Windows.Shapes.Rectangle { Width = 3, Height = 14, Fill = Brushes.White, Margin = new Thickness(1, 0, 1, 0) });
                    pauseGlyph.Children.Add(new System.Windows.Shapes.Rectangle { Width = 3, Height = 14, Fill = Brushes.White, Margin = new Thickness(1, 0, 1, 0) });
                    pause.Content = pauseGlyph;
                    pause.Click += (_, _) => RunUndoableAction(() => _pause(task));
                    actions.Children.Add(pause);
                }
                else
                {
                    var unpause = IconButton("\uE768", "Destravar tarefa"); unpause.Click += (_, _) => RunUndoableAction(() => _unpause(task));
                    actions.Children.Add(unpause);
                }
            }
            else if (!item.IsRemote)
            {
                var reopen = IconButton("\uE777", "Reabrir tarefa"); reopen.Click += (_, _) => RunUndoableAction(() => _reopen(task)); actions.Children.Add(reopen);
            }
            if (!item.IsRemote)
            {
                var edit = IconButton("\uE70F", "Editar"); edit.Click += (_, _) => RunUndoableAction(() => _edit(task)); actions.Children.Add(edit);
                var remove = IconButton("\uE74D", "Excluir"); remove.Click += (_, _) => RunUndoableAction(() => _delete(task)); actions.Children.Add(remove);
                layout.Children.Add(actions);
            }
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = TodoTaskDisplay.Reference(task), Foreground = new SolidColorBrush(Color.FromRgb(180, 243, 106)), FontSize = 10, Margin = new Thickness(0, 0, 0, 3) });
            body.Children.Add(new Border { Background = item.IsRemote ? new SolidColorBrush(Color.FromRgb(75, 65, 105)) : new SolidColorBrush(Color.FromRgb(53, 70, 48)), CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 2, 6, 2), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 5), Child = new TextBlock { Text = item.ComputerName, Foreground = Brushes.White, FontSize = 9 }, ToolTip = item.IsRemote ? $"Tarefa do computador {item.ComputerName}" : "Tarefa deste computador" });
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new Border { Width = 10, Height = 10, Background = TodoTaskEditWindow.CategoryBrush(task.CategoryColor), CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 4, 7, 0), ToolTip = $"Prioridade {task.CategoryColor}" });
            var selectableTitle = CreateSelectableTaskText(task.Title, Brushes.White, 14, FontWeights.SemiBold);
            selectableTitle.MaxWidth = 280;
            titleRow.Children.Add(selectableTitle);
            body.Children.Add(titleRow);
            if (task.Checklist.Count > 0)
            {
                var progress = TodoTaskChecklistWeightPolicy.CompletedPercent(task.Checklist);
                var progressRow = new DockPanel { Margin = new Thickness(0, 7, 0, 4) };
                var percent = new TextBlock { Text = $"{progress:0.#}%", Foreground = new SolidColorBrush(Color.FromRgb(180, 243, 106)), FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(percent, Dock.Right); progressRow.Children.Add(percent);
                var bar = new System.Windows.Controls.ProgressBar { Minimum = 0, Maximum = 100, Value = (double)progress, Height = 6, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(180, 243, 106)), Background = new SolidColorBrush(Color.FromRgb(58, 62, 72)) };
                progressRow.Children.Add(bar); body.Children.Add(progressRow);
                for (var index = 0; index < task.Checklist.Count; index++)
                {
                    var entry = task.Checklist[index];
                    var checklistRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                    var check = new System.Windows.Controls.CheckBox
                    {
                        IsChecked = entry.IsCompleted,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 1, 5, 0)
                    };
                    var checklistText = CreateSelectableTaskText($"{entry.Title} ({entry.Weight:0.#}%)",
                        new SolidColorBrush(Color.FromRgb(190, 194, 204)), 11, FontWeights.Normal);
                    checklistText.MaxWidth = 240;
                    var itemIndex = index;
                    check.Click += (_, _) => RunUndoableAction(() => _toggleChecklist(task, itemIndex));
                    checklistRow.Children.Add(check);
                    checklistRow.Children.Add(checklistText);
                    body.Children.Add(checklistRow);
                }
            }
            if (!string.IsNullOrWhiteSpace(task.Description))
            {
                var description = CreateSelectableTaskText(task.Description, new SolidColorBrush(Color.FromRgb(190, 194, 204)), 12);
                description.MaxHeight = 130;
                description.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                description.Margin = new Thickness(0, 5, 0, 5);
                body.Children.Add(description);
            }
            body.Children.Add(new TextBlock { Text = $"Criada {task.CreatedAt.LocalDateTime:g}" + (task.CompletedAt is null ? "" : $" · Concluída {task.CompletedAt.Value.LocalDateTime:g}") + (task.PausedAt is null ? "" : $" · Pausada {task.PausedAt.Value.LocalDateTime:g}"), Foreground = new SolidColorBrush(Color.FromRgb(135, 140, 151)), FontSize = 10 });
            if (task.PausedAt is not null && (!string.IsNullOrWhiteSpace(task.PausedBy) || !string.IsNullOrWhiteSpace(task.PauseReason)))
                body.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { string.IsNullOrWhiteSpace(task.PausedBy) ? null : $"Responsável: {task.PausedBy}", string.IsNullOrWhiteSpace(task.PauseReason) ? null : $"Motivo: {task.PauseReason}" }.Where(value => value is not null)), Foreground = new SolidColorBrush(Color.FromRgb(255, 204, 0)), FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });
            if (task.ReminderIntervalMinutes is not null) body.Children.Add(new TextBlock { Text = TodoTaskDisplay.ReminderLabel(task), Foreground = new SolidColorBrush(Color.FromRgb(180, 243, 106)), FontSize = 10, Margin = new Thickness(0, 3, 0, 0) });
            var resourceLinks = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
            foreach (var url in task.Urls.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var label = Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.Host : "URL";
                resourceLinks.Children.Add(ResourceLinkButton($"↗ {label}", url));
            }
            foreach (var path in task.Attachments.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (item.IsRemote)
                    resourceLinks.Children.Add(new TextBlock { Text = $"📎 {System.IO.Path.GetFileName(path)}", Foreground = new SolidColorBrush(Color.FromRgb(180, 243, 106)), FontSize = 10, Margin = new Thickness(0, 0, 10, 2), ToolTip = $"Anexo no computador {item.ComputerName}" });
                else resourceLinks.Children.Add(ResourceLinkButton($"📎 {System.IO.Path.GetFileName(path)}", path));
            }
            if (resourceLinks.Children.Count > 0) body.Children.Add(resourceLinks);
            layout.Children.Add(body); card.Child = layout; _list.Children.Add(card);
            }
        }
        if (entries.Count == 0 && !_loadingRemoteTasks) _list.Children.Add(new TextBlock { Text = _showHistory ? "Nenhuma tarefa concluída." : "Nenhuma tarefa em aberto.", Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 13, Margin = new Thickness(0, 10, 0, 0) });
    }

    public IReadOnlyList<DesktopPairedPeer> OnlineDesktopPeers => _onlinePeers.ToArray();

    public async Task RefreshRemoteTasksAsync()
    {
        if (_loadingRemoteTasks || _settings.DesktopPeers.Count == 0) return;
        _loadingRemoteTasks = true;
        _remoteTaskError = null;
        Refresh();
        try
        {
            var status = _showHistory ? "completed" : "open";
            var results = await Task.WhenAll(_settings.DesktopPeers.Select(async peer =>
            {
                try { return (Peer: peer, Tasks: await _desktopTaskApi.GetTasksAsync(peer, status), Error: (string?)null); }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or CryptographicException or System.Net.Sockets.SocketException or System.Net.NetworkInformation.NetworkInformationException or InvalidOperationException)
                { return (Peer: peer, Tasks: (IReadOnlyList<TodoTaskEntry>)[], Error: $"{peer.Name}: {exception.Message}"); }
            }));
            _onlinePeers.Clear();
            _onlinePeers.AddRange(results.Where(result => result.Error is null).Select(result => result.Peer));
            _remoteTasks.Clear();
            foreach (var result in results.Where(result => result.Error is null))
                _remoteTasks.AddRange(result.Tasks.Select(task => new RemoteTodoTask(FromRemoteEntry(task), result.Peer.Name, true, result.Peer.Id)));
            _remoteTaskError = string.Join(" · ", results.Where(result => result.Error is not null).Select(result => result.Error!));
            UpdateComputerFilter();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or CryptographicException or System.Net.Sockets.SocketException or System.Net.NetworkInformation.NetworkInformationException)
        {
            _remoteTaskError = exception.Message;
            _onlinePeers.Clear();
            _remoteTasks.Clear();
            UpdateComputerFilter();
        }
        finally
        {
            _loadingRemoteTasks = false;
            Refresh();
        }
    }

    private void UpdateComputerFilter()
    {
        var previous = _selectedComputerId;
        _computerFilter.Items.Clear();
        _computerFilter.Items.Add(CreateComputerFilterItem("Este computador", LocalComputerFilter, TodoTaskComputerPalette.Local, "Este computador"));
        foreach (var peer in _onlinePeers.OrderBy(peer => peer.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var peerIndex = Math.Max(0, _settings.DesktopPeers.FindIndex(candidate => candidate.Id == peer.Id));
            _computerFilter.Items.Add(CreateComputerFilterItem(peer.Name, peer.Id, TodoTaskComputerPalette.Remote(peerIndex), "Computador pareado"));
        }
        if (_onlinePeers.Count > 0)
            _computerFilter.Items.Add(new ComboBoxItem
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock { Text = "▦", Foreground = new SolidColorBrush(Color.FromRgb(180, 243, 106)), FontSize = 14, Margin = new Thickness(0, 0, 8, 0) },
                        new TextBlock { Text = "Todos os computadores", VerticalAlignment = VerticalAlignment.Center }
                    }
                },
                Tag = AllComputersFilter
            });
        var selected = _computerFilter.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, previous));
        if (selected is null) { _selectedComputerId = LocalComputerFilter; selected = (ComboBoxItem)_computerFilter.Items[0]; }
        _computerFilter.SelectedItem = selected;
        _computerFilter.Visibility = _onlinePeers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _refreshRemoteButton.Visibility = _onlinePeers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ComboBoxItem CreateComputerFilterItem(string name, string id, Color color, string description)
        => new()
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(color), Margin = new Thickness(0, 0, 8, 0) },
                    new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }
                }
            },
            Tag = id,
            ToolTip = description
        };

    private static TodoTask FromRemoteEntry(TodoTaskEntry task)
        => new() { Id = task.Id, Title = task.Title, Description = task.Description, CreatedAt = task.CreatedAt, CompletedAt = task.CompletedAt,
            TaskNumber = task.TaskNumber, ReminderIntervalMinutes = task.ReminderIntervalMinutes, CategoryColor = task.CategoryColor,
            LastReminderAt = task.LastReminderAt, Urls = task.Urls, Attachments = task.Attachments, PausedAt = task.PausedAt,
            PauseReason = task.PauseReason, PausedBy = task.PausedBy, Checklist = task.Checklist ?? [] };

    private sealed record RemoteTodoTask(TodoTask Task, string ComputerName, bool IsRemote, string PeerId);

    public void SetBusinessmapEnabled(bool enabled)
    {
        _businessmapEnabled = enabled;
        _businessmapButton.ToolTip = enabled ? "Mostrar tarefas do Businessmap" : "Configure a integração do Businessmap";
    }

    private async Task ToggleBusinessmapAsync()
    {
        if (!_showBusinessmap && !_businessmapEnabled)
        {
            MessageBox.Show("Configure e ative a sincronização do Businessmap em … > Configurações de tarefas.", "Tarefas do Businessmap", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _showBusinessmap = !_showBusinessmap;
        _showHistory = false;
        _historyButton.Visibility = _showBusinessmap ? Visibility.Collapsed : Visibility.Visible;
        _reportButton.Visibility = _showBusinessmap ? Visibility.Collapsed : Visibility.Visible;
        _newTaskButton.Visibility = _showBusinessmap ? Visibility.Collapsed : Visibility.Visible;
        _undoButton.Visibility = Visibility.Collapsed;
        _businessmapButton.ToolTip = _showBusinessmap ? "Voltar às tarefas do JMD" : "Mostrar tarefas do Businessmap";
        if (_showBusinessmap) await ReloadBusinessmapAsync();
        else Refresh();
    }

    private async Task ReloadBusinessmapAsync()
    {
        if (_loadingBusinessmap) return;
        _loadingBusinessmap = true;
        _businessmapError = null;
        _businessmapTasks = [];
        Refresh();
        try { _businessmapTasks = (await _loadBusinessmapTasks()).ToList(); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or ArgumentException or JsonException or CryptographicException)
        { _businessmapError = exception.Message; }
        finally { _loadingBusinessmap = false; Refresh(); }
    }

    private void RenderBusinessmapTasks()
    {
        _list.Children.Clear();
        _title.Text = "Businessmap";
        var refresh = new Button { Content = "Atualizar cartões", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 0, 10), Background = new SolidColorBrush(Color.FromRgb(49, 53, 63)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
        refresh.Click += async (_, _) => await ReloadBusinessmapAsync();
        _list.Children.Add(refresh);
        if (_loadingBusinessmap)
        {
            _list.Children.Add(new TextBlock { Text = "Carregando cartões…", Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 12 });
            return;
        }
        if (_businessmapError is not null)
        {
            _list.Children.Add(new TextBlock { Text = _businessmapError, Foreground = new SolidColorBrush(Color.FromRgb(255, 145, 135)), FontSize = 12, TextWrapping = TextWrapping.Wrap });
            return;
        }
        foreach (var task in _businessmapTasks)
        {
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = $"{task.BoardName} · {task.LaneName}", Foreground = new SolidColorBrush(Color.FromRgb(180, 243, 106)), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5) });
            body.Children.Add(new TextBlock { Text = task.Title, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(new TextBlock { Text = string.Join(" · ", new[] { task.OwnerName, task.ColumnName, task.Deadline }.Where(value => !string.IsNullOrWhiteSpace(value))), Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });
            if (!string.IsNullOrWhiteSpace(task.Description)) body.Children.Add(new TextBlock { Text = task.Description, Foreground = new SolidColorBrush(Color.FromRgb(190, 194, 204)), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0), MaxHeight = 100 });
            var card = new Border { Background = new SolidColorBrush(Color.FromRgb(34, 37, 45)), CornerRadius = new CornerRadius(9), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 9), Child = body, ToolTip = $"Cartão #{task.Id}" };
            _list.Children.Add(card);
        }
        if (_businessmapTasks.Count == 0) _list.Children.Add(new TextBlock { Text = "Nenhum cartão corresponde aos quadros e filtros selecionados.", Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167)), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
    }

    public void ShowOpenTasks()
    {
        _showHistory = false;
        _historyButton.ToolTip = "Ver tarefas concluídas";
        Refresh();
    }

    public void SetModalChildActive(bool active)
    {
        _modalChildActive = active;
        if (!active && IsVisible) Activate();
    }

    public void FocusFirstAction()
    {
        var firstAction = _list.Children.OfType<Border>()
            .Select(card => card.Child as StackPanel)
            .Where(layout => layout is not null)
            .SelectMany(layout => layout!.Children.OfType<StackPanel>().Take(1))
            .SelectMany(actions => actions.Children.OfType<Button>())
            .FirstOrDefault();
        if (firstAction is not null) Keyboard.Focus(firstAction);
    }

    private static Button IconButton(string glyph, string hint) => new() { Content = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, Width = 28, Height = 28, Margin = new Thickness(1), ToolTip = hint, Focusable = true, Background = new SolidColorBrush(Color.FromRgb(49, 53, 63)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };

    private static TextBox CreateSelectableTaskText(string text, System.Windows.Media.Brush foreground, double fontSize, FontWeight? fontWeight = null)
        => new()
        {
            Text = text,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Foreground = foreground,
            FontSize = fontSize,
            FontWeight = fontWeight ?? FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            CaretBrush = foreground
        };

    private static Button ResourceLinkButton(string label, string target)
    {
        var button = new Button
        {
            Content = label,
            ToolTip = target,
            MaxWidth = 220,
            Margin = new Thickness(0, 0, 10, 2),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = new SolidColorBrush(Color.FromRgb(180, 243, 106)),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 10
        };
        button.Click += (_, _) =>
        {
            try
            {
                var isUrl = Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
                if (!isUrl && !System.IO.File.Exists(target) && !System.IO.Directory.Exists(target))
                {
                    System.Windows.MessageBox.Show($"O arquivo anexado não foi encontrado:\n{target}", "Anexo indisponível", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
            {
                System.Windows.MessageBox.Show($"Não foi possível abrir o item:\n{exception.Message}", "Não foi possível abrir", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        return button;
    }

    private void RunUndoableAction(Action action)
    {
        _pendingUndoSnapshot = CaptureTaskSnapshot();
        try
        {
            action();
            TryPromotePendingUndo();
        }
        finally
        {
            if (_pendingUndoSnapshot is { } pending && TaskSnapshotsEqual(pending, CaptureTaskSnapshot()))
                _pendingUndoSnapshot = null;
        }
    }

    private async Task RunUndoableCreateAsync()
    {
        _pendingUndoSnapshot = CaptureTaskSnapshot();
        try
        {
            await _create();
            TryPromotePendingUndo();
        }
        finally
        {
            if (_pendingUndoSnapshot is { } pending && TaskSnapshotsEqual(pending, CaptureTaskSnapshot()))
                _pendingUndoSnapshot = null;
        }
    }

    private void TryPromotePendingUndo()
    {
        if (_pendingUndoSnapshot is not { } pending || TaskSnapshotsEqual(pending, CaptureTaskSnapshot())) return;
        _undoSnapshot = pending;
        _pendingUndoSnapshot = null;
        _undoButton.Visibility = Visibility.Visible;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    private void UndoLastAction()
    {
        if (_undoSnapshot is not { } snapshot) return;
        _undoTimer.Stop();
        _undoSnapshot = null;
        _pendingUndoSnapshot = null;
        _undoButton.Visibility = Visibility.Collapsed;
        _undo(snapshot);
    }

    private void ClearUndo()
    {
        _undoTimer.Stop();
        _undoSnapshot = null;
        _pendingUndoSnapshot = null;
        _undoButton.Visibility = Visibility.Collapsed;
    }

    private List<TodoTaskEntry> CaptureTaskSnapshot()
        => _tasks.Select(task => new TodoTaskEntry(task.Id, task.Title, task.Description, task.CreatedAt, task.CompletedAt,
            task.TaskNumber, task.ReminderIntervalMinutes, task.CategoryColor, task.LastReminderAt, task.Urls, task.Attachments,
            task.PausedAt, task.PauseReason, task.PausedBy, [.. task.Checklist])).ToList();

    private static bool TaskSnapshotsEqual(IReadOnlyList<TodoTaskEntry> left, IReadOnlyList<TodoTaskEntry> right)
        => left.Count == right.Count && left.Zip(right).All(pair =>
            (pair.First with { Checklist = null }) == (pair.Second with { Checklist = null }) &&
            (pair.First.Checklist ?? []).SequenceEqual(pair.Second.Checklist ?? []));
}
