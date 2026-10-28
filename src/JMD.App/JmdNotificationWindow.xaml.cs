using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace JMD.App;

public partial class JmdNotificationWindow : Window
{
    private readonly DispatcherTimer _dismissTimer;
    private readonly long? _historyId;
    private readonly bool _isProcessing;
    private readonly Action? _stopAudioDebug;
    private readonly Action? _postponeAudioDebug;

    public event Action<long?>? HistoryRequested;

    public JmdNotificationWindow(string message, long? historyId, bool isProcessing = false,
        TimeSpan? displayDuration = null, Action? stopAudioDebug = null, Action? postponeAudioDebug = null)
    {
        InitializeComponent();
        _dismissTimer = new DispatcherTimer
        {
            Interval = displayDuration ?? TimeSpan.FromSeconds(4)
        };
        _historyId = historyId;
        _isProcessing = isProcessing;
        _stopAudioDebug = stopAudioDebug;
        _postponeAudioDebug = postponeAudioDebug;
        AudioDebugActions.Visibility = stopAudioDebug is not null ? Visibility.Visible : Visibility.Collapsed;
        if (stopAudioDebug is not null)
        {
            Height = 164;
            DismissButton.Visibility = Visibility.Collapsed;
        }
        NotificationMessage.Text = message;
        ProcessingProgress.Visibility = isProcessing ? Visibility.Visible : Visibility.Collapsed;

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 18;
        Top = workArea.Bottom - Height - 18;

        _dismissTimer.Tick += (_, _) => Close();
        Loaded += (_, _) => { if (!_isProcessing) _dismissTimer.Start(); };
        Closed += (_, _) => _dismissTimer.Stop();
    }

    public void UpdateProcessingMessage(string message)
    {
        if (_isProcessing) NotificationMessage.Text = message;
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e) => Close();

    private void StopAudioDebug_Click(object sender, RoutedEventArgs e) => _stopAudioDebug?.Invoke();

    private void PostponeAudioDebug_Click(object sender, RoutedEventArgs e) => _postponeAudioDebug?.Invoke();

    private void NotificationBorder_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_isProcessing) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        HistoryRequested?.Invoke(_historyId);
        Close();
    }

    private static bool IsInsideButton(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is System.Windows.Controls.Primitives.ButtonBase) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }
}
