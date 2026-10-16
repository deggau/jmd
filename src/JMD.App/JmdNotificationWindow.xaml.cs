using System.Windows;
using System.Windows.Threading;

namespace JMD.App;

public partial class JmdNotificationWindow : Window
{
    private readonly DispatcherTimer _dismissTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    public JmdNotificationWindow(string title, string message)
    {
        InitializeComponent();
        NotificationTitle.Text = title;
        NotificationMessage.Text = message;

        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 18;
        Top = workArea.Bottom - Height - 18;

        _dismissTimer.Tick += (_, _) => Close();
        Loaded += (_, _) => _dismissTimer.Start();
        Closed += (_, _) => _dismissTimer.Stop();
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e) => Close();
}
