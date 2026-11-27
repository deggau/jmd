using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using JMD.Core;

namespace JMD.App;

public partial class JmdNotificationWindow : Window
{
    private readonly DispatcherTimer _dismissTimer;
    private readonly NotificationClickTarget _clickTarget;
    private readonly bool _isProcessing;
    private readonly Action? _stopAudioDebug;
    private readonly Action? _postponeAudioDebug;
    private readonly Func<bool, bool>? _resolvePairing;

    public event Action<NotificationClickTarget>? NotificationRequested;

    public JmdNotificationWindow(string message, long? historyId, bool isProcessing = false,
        TimeSpan? displayDuration = null, Action? stopAudioDebug = null, Action? postponeAudioDebug = null,
        NotificationClickTarget? clickTarget = null, Func<bool, bool>? resolvePairing = null, int stackIndex = 0)
    {
        InitializeComponent();
        _dismissTimer = new DispatcherTimer
        {
            Interval = displayDuration ?? TimeSpan.FromSeconds(4)
        };
        _clickTarget = clickTarget ?? NotificationClickTarget.ForHistory(historyId);
        _isProcessing = isProcessing;
        _stopAudioDebug = stopAudioDebug;
        _postponeAudioDebug = postponeAudioDebug;
        _resolvePairing = resolvePairing;
        AudioDebugActions.Visibility = stopAudioDebug is not null ? Visibility.Visible : Visibility.Collapsed;
        PairingActions.Visibility = resolvePairing is not null ? Visibility.Visible : Visibility.Collapsed;
        if (stopAudioDebug is not null)
        {
            Height = 164;
            DismissButton.Visibility = Visibility.Collapsed;
        }
        if (resolvePairing is not null)
        {
            Height = 176;
            NotificationMessage.MaxHeight = 84;
        }
        NotificationMessage.Text = message;
        ProcessingProgress.Visibility = isProcessing ? Visibility.Visible : Visibility.Collapsed;

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 18;
        Top = workArea.Bottom - Height - 18 - stackIndex * (Height + 8);

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

    private void ApprovePairing_Click(object sender, RoutedEventArgs e) => ResolvePairing(true);

    private void DenyPairing_Click(object sender, RoutedEventArgs e) => ResolvePairing(false);

    private void ResolvePairing(bool approve)
    {
        if (_resolvePairing is null) return;
        if (_resolvePairing(approve))
        {
            Close();
            return;
        }

        NotificationMessage.Text = "Este pedido expirou ou já foi respondido.";
        PairingActions.Visibility = Visibility.Collapsed;
        _dismissTimer.Stop();
        _dismissTimer.Interval = TimeSpan.FromSeconds(4);
        _dismissTimer.Start();
    }

    private void NotificationBorder_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_isProcessing || _resolvePairing is not null) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;
        NotificationRequested?.Invoke(_clickTarget);
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
