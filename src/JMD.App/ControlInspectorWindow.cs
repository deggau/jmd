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
        Closed += (_, _) => _timer.Stop();
        _timer.Tick += (_, _) => InspectAtCursor();
    }

    private void InspectAtCursor()
    {
        if (!GetCursorPos(out var cursor)) return;
        var own = new Rect(Left, Top, ActualWidth, ActualHeight);
        if (own.Contains(new System.Windows.Point(cursor.X, cursor.Y))) return;
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(cursor.X, cursor.Y));
            if (element is null) return;
            var name = element.Current.Name?.Trim() ?? string.Empty;
            if (name == _currentName && _details.Text.Length > 0) return;
            _currentName = name;
            _name.Text = name.Length == 0 ? "(sem nome acessível)" : name;
            var type = element.Current.ControlType?.ProgrammaticName?.Replace("ControlType.", "", StringComparison.Ordinal) ?? "Controle";
            var automationId = element.Current.AutomationId;
            _details.Text = $"Tipo: {type}" + (string.IsNullOrWhiteSpace(automationId) ? string.Empty : $" · AutomationId: {automationId}");
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            _currentName = string.Empty;
            _name.Text = "Não foi possível ler este controle";
            _details.Text = "Tente apontar para outro campo ou botão.";
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
}
