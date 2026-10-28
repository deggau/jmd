using System.Runtime.InteropServices;
using DrawingPoint = System.Drawing.Point;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JMD.Core;
using JMD.Windows;
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
    private readonly Dispatcher _uiDispatcher = Dispatcher.CurrentDispatcher;
    private readonly AsyncInspectionCoordinator<System.Windows.Point, InspectionResult> _inspection;
    private string _currentName = string.Empty;
    private string _currentAutomationId = string.Empty;
    private readonly InspectionRequestGate<System.Windows.Point> _requestGate = new();
    private readonly LowLevelMouseHookProc _mouseHookProc;
    private IntPtr _mouseHook;
    private bool _captureInProgress;
    private volatile bool _isClosed;

    public ControlInspectorWindow()
    {
        _mouseHookProc = OnMouseHook;
        _inspection = new AsyncInspectionCoordinator<System.Windows.Point, InspectionResult>(
            ReadControlAt,
            () => new InspectionResult(string.Empty, "Não foi possível consultar este controle. Tente outro."),
            action => { _uiDispatcher.BeginInvoke(action); },
            result => UpdateInspection(result));
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
        var titleBar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var close = new WpfButton { Content = "×", Padding = new Thickness(8, 1, 8, 2), FontSize = 16, Background = new SolidColorBrush(Color.FromRgb(48, 51, 60)), Foreground = Brushes.White, BorderThickness = new Thickness(0), ToolTip = "Fechar inspetor" };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        titleBar.Children.Add(close);
        var title = new TextBlock { Text = "Inspecionar · Ctrl+clique copia ID · Shift+clique copia posição", Foreground = new SolidColorBrush(Color.FromRgb(180, 243, 106)), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Cursor = WpfCursors.SizeAll };
        title.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        titleBar.Children.Add(title);
        content.Children.Add(titleBar);
        var row = new StackPanel { Orientation = WpfOrientation.Horizontal };
        row.Children.Add(_name);
        var copyButtons = new StackPanel { Orientation = WpfOrientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var copyId = new WpfButton { Content = "Copiar ID", Padding = new Thickness(7, 6, 7, 6),
            ToolTip = "Copia o AutomationId com o prefixo id: para usar nas etapas Focar, Aguardar controle ou Se controle corresponder." };
        copyId.Click += (_, _) =>
        {
            if (_currentAutomationId.Length == 0) return;
            try
            {
                CopySelectedControl($"id:{_currentAutomationId}", copyId);
            }
            catch (Exception exception) when (exception is ExternalException or InvalidOperationException or ArgumentException)
            {
                copyId.Content = "Falhou";
            }
        };
        copyButtons.Children.Add(copyId);
        var copyName = new WpfButton { Content = "Copiar nome", Padding = new Thickness(7, 6, 7, 6), Margin = new Thickness(4, 0, 0, 0),
            ToolTip = "Copia o nome acessível com o prefixo name: para usar nas etapas Focar, Aguardar controle ou Se controle corresponder." };
        copyName.Click += (_, _) =>
        {
            if (_currentName.Length == 0) return;
            try { CopySelectedControl($"name:{_currentName}", copyName); }
            catch (Exception exception) when (exception is ExternalException or InvalidOperationException or ArgumentException) { copyName.Content = "Falhou"; }
        };
        copyButtons.Children.Add(copyName);
        row.Children.Add(copyButtons);
        content.Children.Add(row);
        content.Children.Add(_details);
        Content = border;
        Loaded += (_, _) =>
        {
            Left = SystemParameters.WorkArea.Right - ActualWidth - 18;
            Top = SystemParameters.WorkArea.Bottom - ActualHeight - 18;
            _timer.Start();
            _mouseHook = SetWindowsHookEx(14, _mouseHookProc, IntPtr.Zero, 0);
        };
        Closed += (_, _) =>
        {
            _isClosed = true;
            _timer.Stop();
            _inspection.Close();
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        };
        _timer.Tick += (_, _) => InspectAtCursor();
    }

    private void InspectAtCursor()
    {
        if (_isClosed) return;
        if (!GetCursorPos(out var cursor)) return;
        var own = new Rect(Left, Top, ActualWidth, ActualHeight);
        var point = new System.Windows.Point(cursor.X, cursor.Y);
        if (own.Contains(point))
        {
            _requestGate.Reset();
            return;
        }
        var now = Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp());
        if (!_requestGate.TryAcceptAfterDelay(point, now, TimeSpan.FromMilliseconds(300))) return;
        _ = _inspection.Request(point);
    }

    private static InspectionResult ReadControlAt(System.Windows.Point point)
    {
        try
        {
            var element = AutomationElement.FromPoint(point);
            if (element is null) return new InspectionResult(string.Empty, "Nenhum controle acessível sob o cursor.");
            var wrapperOnly = false;
            if (element.Current.ControlType == ControlType.Window &&
                (element.Current.Name ?? string.Empty).Contains("Chrome Legacy Window", StringComparison.OrdinalIgnoreCase))
            {
                // Chromium based apps sometimes return the legacy host window from FromPoint
                // even though UIA exposes useful children below that host.
                var descendants = element.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
                var underPointer = descendants.Cast<AutomationElement>()
                    .Select(candidate => (Element: candidate, Bounds: candidate.Current.BoundingRectangle))
                    .Where(candidate => !candidate.Bounds.IsEmpty && candidate.Bounds.Contains(point))
                    .OrderBy(candidate => candidate.Element.Current.ControlType == ControlType.Edit ? 0 : 1)
                    .ThenBy(candidate => candidate.Bounds.Width * candidate.Bounds.Height)
                    .ToArray();
                if (underPointer.Length > 0) element = underPointer[0].Element;
                else wrapperOnly = true;
            }
            // Keep the hit element's type and id. Walking to a named ancestor
            // and using its metadata made browser pages look like RootWebArea
            // even when the pointer was over a textbox.
            var hitType = element.Current.ControlType;
            var hitTypeName = hitType?.ProgrammaticName?.Replace("ControlType.", "", StringComparison.Ordinal) ?? "Controle";
            var automationId = element.Current.AutomationId;
            var nearest = element;
            var name = string.Empty;
            for (var depth = 0; depth < 10 && nearest is not null; depth++)
            {
                var candidateType = nearest.Current.ControlType;
                if (!ReferenceEquals(nearest, element) &&
                    (candidateType == ControlType.Document || candidateType == ControlType.Pane))
                    break;
                var candidateName = nearest.Current.Name?.Trim() ?? string.Empty;
                if (candidateName.Length > 0)
                {
                    name = candidateName;
                    break;
                }
                nearest = TreeWalker.ControlViewWalker.GetParent(nearest);
            }

            var details = $"Tipo: {hitTypeName}" + (string.IsNullOrWhiteSpace(automationId) ? string.Empty : $" · AutomationId: {automationId}");
            if (wrapperOnly)
            {
                name = string.Empty;
                details += " · Chromium não expôs controles filhos; Ctrl+clique não consegue identificar este textbox. Tente navegar até ele com Tab e Shift+Tab.";
            }
            return new InspectionResult(name, details, wrapperOnly ? string.Empty : automationId ?? string.Empty);
        }
        catch (Exception)
        {
            return new InspectionResult(string.Empty, "Não foi possível consultar este controle. Tente outro.");
        }
    }

    private void UpdateInspection(InspectionResult result, bool fromCtrlClick = false)
    {
        if (_isClosed || !IsLoaded) return;
        if (_captureInProgress && !fromCtrlClick) return;
        _currentName = result.Name;
        _currentAutomationId = result.AutomationId;
        _name.Text = result.Name.Length == 0 ? "(sem nome acessível)" : result.Name;
        _details.Text = result.Details;
        if (!fromCtrlClick) return;

        var valueToCopy = _currentAutomationId.Length > 0
            ? $"id:{_currentAutomationId}"
            : _currentName.Length > 0 ? $"name:{_currentName}" : string.Empty;
        _captureInProgress = false;
        if (valueToCopy.Length > 0)
        {
            try
            {
                WpfClipboard.SetText(valueToCopy);
                Close();
                return;
            }
            catch (Exception exception) when (exception is ExternalException or InvalidOperationException or ArgumentException)
            {
                _details.Text += " Não foi possível copiar para a área de transferência.";
            }
        }
        _timer.Start();
    }

    private void CopySelectedControl(string value, WpfButton button)
    {
        try
        {
            WpfClipboard.SetText(value);
            Close();
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException or ArgumentException)
        {
            button.Content = "Falhou";
        }
    }

    private IntPtr OnMouseHook(int code, IntPtr message, IntPtr data)
    {
        if (_isClosed) return CallNextHookEx(_mouseHook, code, message, data);
        if (code >= 0 && message == (IntPtr)0x0201)
        {
            var controlPressed = (GetAsyncKeyState(0x11) & 0x8000) != 0;
            var positionPressed = (GetAsyncKeyState(0x10) & 0x8000) != 0;
            if (controlPressed || positionPressed)
            {
                var mouse = Marshal.PtrToStructure<LowLevelMouseHookData>(data);
                var point = new System.Windows.Point(mouse.Point.X, mouse.Point.Y);
                Dispatcher.BeginInvoke(() => CaptureControlAtClick(point, positionPressed && !controlPressed));
            }
        }
        return CallNextHookEx(_mouseHook, code, message, data);
    }

    private async void CaptureControlAtClick(System.Windows.Point point, bool capturePosition)
    {
        if (_isClosed || !IsLoaded || _captureInProgress) return;
        var own = new Rect(Left, Top, ActualWidth, ActualHeight);
        if (own.Contains(point)) return;
        if (capturePosition)
        {
            if (!WorkflowWindowAutomation.TryCaptureRelativePoint(new DrawingPoint((int)point.X, (int)point.Y), out _, out var relativePoint))
            {
                _details.Text = "Não encontrei uma janela sob o ponto selecionado.";
                return;
            }
            try
            {
                WpfClipboard.SetText(relativePoint.ToString());
                _name.Text = "Posição copiada";
                _details.Text = $"Coordenadas relativas à janela: {relativePoint}. Cole em uma etapa Clique.";
                Close();
            }
            catch (Exception exception) when (exception is ExternalException or InvalidOperationException or ArgumentException)
            {
                _details.Text = "Não foi possível copiar a posição para a área de transferência.";
            }
            return;
        }
        _captureInProgress = true;
        _timer.Stop();
        _name.Text = "Lendo controle…";
        _details.Text = "Ctrl+clique detectado.";
        var result = await Task.Run(() => ReadControlAt(point));
        if (_isClosed || !IsLoaded) return;
        UpdateInspection(result, fromCtrlClick: true);
    }

    private sealed record InspectionResult(string Name, string Details, string AutomationId = "");

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    private delegate IntPtr LowLevelMouseHookProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouseHookData
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelMouseHookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
