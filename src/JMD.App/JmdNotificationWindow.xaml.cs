using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace JMD.App;

public partial class JmdNotificationWindow : Window
{
    private readonly DispatcherTimer _dismissTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly long? _historyId;

    public event Action<long?>? HistoryRequested;

    public JmdNotificationWindow(string message, long? historyId)
    {
        InitializeComponent();
        _historyId = historyId;
        NotificationMessage.Text = message;

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 18;
        Top = workArea.Bottom - Height - 18;

        _dismissTimer.Tick += (_, _) => Close();
        Loaded += (_, _) => _dismissTimer.Start();
        Closed += (_, _) => _dismissTimer.Stop();
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e) => Close();

    private void NotificationBorder_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
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
