using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using WpfButton = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfCursors = System.Windows.Input.Cursors;
using WpfClipboard = System.Windows.Clipboard;

namespace JMD.App;

/// <summary>Small always-on-top inspector for the UI Automation name under the mouse.</summary>
internal sealed class ControlInspectorWindow : Window
{
    private readonly TextBlock _name = new() { Text = "Aponte para um campo ou botão…", Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, MinWidth = 230, MaxWidth = 330, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _details = new() { Foreground = Brushes.LightGray, FontSize = 10, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private string _currentName = string.Empty;
    private int _inspectionInProgress;
    private int _requestId;

    public ControlInspectorWindow()
    {
        Title = "Inspecionar controle";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = new SolidColorBrush(Color.FromRgb(28, 30, 37));
        var border = new Border { Background = Background, BorderBrush = new SolidColorBrush(Color.FromRgb(90, 95, 108)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12) };
        var content = new StackPanel();
        border.Child = content;
        var titleBar = new TextBlock { Text = "Nome acessível do controle", Foreground = new SolidColorBrush(Color.FromRgb(180, 243, 106)), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), Cursor = WpfCursors.SizeAll };
        titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        content.Children.Add(titleBar);
        var row = new StackPanel { Orientation = WpfOrientation.Horizontal };
        row.Children.Add(_name);
        var copy = new WpfButton { Content = "Copiar", Padding = new Thickness(10, 6, 10, 6), VerticalAlignment = VerticalAlignment.Center };
        copy.Click += (_, _) =>
        {
            if (_currentName.Length == 0) return;
            try { WpfClipboard.SetText(_currentName); copy.Content = "Copiado"; }
            catch (System.Runtime.InteropServices.ExternalException) { copy.Content = "Falhou"; }
        };
        row.Children.Add(copy);
        content.Children.Add(row);
        content.Children.Add(_details);
        Content = border;
        Loaded += (_, _) => { Left = SystemParameters.WorkArea.Right - ActualWidth - 18; Top = SystemParameters.WorkArea.Bottom - ActualHeight - 18; _timer.Start(); };
        Closed += (_, _) => { _timer.Stop(); Interlocked.Increment(ref _requestId); };
        _timer.Tick += (_, _) => InspectAtCursor();
    }

    private void InspectAtCursor()
    {
        if (!GetCursorPos(out var cursor)) return;
        var own = new Rect(Left, Top, ActualWidth, ActualHeight);
        if (own.Contains(new System.Windows.Point(cursor.X, cursor.Y))) return;
        if (Interlocked.Exchange(ref _inspectionInProgress, 1) != 0) return;
        var requestId = Interlocked.Increment(ref _requestId);
        var point = new System.Windows.Point(cursor.X, cursor.Y);
        _ = Task.Run(() => ReadControlAt(point)).ContinueWith(task =>
        {
            Interlocked.Exchange(ref _inspectionInProgress, 0);
            if (task.IsCanceled || requestId != Volatile.Read(ref _requestId) || !IsLoaded) return;
            var result = task.Status == TaskStatus.RanToCompletion
                ? task.Result
                : new InspectionResult(string.Empty, "Não foi possível consultar este controle. Tente outro.");
            Dispatcher.BeginInvoke(new Action(() => UpdateInspection(result)));
        }, TaskScheduler.Default);
    }

    private static InspectionResult ReadControlAt(System.Windows.Point point)
    {
        try
        {
            var element = AutomationElement.FromPoint(point);
            if (element is null) return new InspectionResult(string.Empty, "Nenhum controle acessível sob o cursor.");
            var nearest = element;
            var name = string.Empty;
            for (var depth = 0; depth < 10 && nearest is not null; depth++)
            {
                var candidateName = nearest.Current.Name?.Trim() ?? string.Empty;
                if (candidateName.Length > 0)
                {
                    name = candidateName;
                    break;
                }
                nearest = TreeWalker.ControlViewWalker.GetParent(nearest);
            }

            var type = nearest?.Current.ControlType?.ProgrammaticName?.Replace("ControlType.", "", StringComparison.Ordinal) ??
                       element.Current.ControlType?.ProgrammaticName?.Replace("ControlType.", "", StringComparison.Ordinal) ?? "Controle";
            var automationId = nearest?.Current.AutomationId;
            var details = $"Tipo: {type}" + (string.IsNullOrWhiteSpace(automationId) ? string.Empty : $" · AutomationId: {automationId}");
            return new InspectionResult(name, details);
        }
        catch (Exception)
        {
            return new InspectionResult(string.Empty, "Não foi possível consultar este controle. Tente outro.");
        }
    }

    private void UpdateInspection(InspectionResult result)
    {
        if (!IsLoaded) return;
        _currentName = result.Name;
        _name.Text = result.Name.Length == 0 ? "(sem nome acessível)" : result.Name;
        _details.Text = result.Details;
    }

    private sealed record InspectionResult(string Name, string Details);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
}
