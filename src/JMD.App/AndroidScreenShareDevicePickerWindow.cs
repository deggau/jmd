using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace JMD.App;

internal sealed class AndroidScreenShareDevicePickerWindow : Window
{
    private readonly System.Windows.Controls.ListBox _devices;
    public JmdroidPairedDevice? SelectedDevice => _devices.SelectedItem as JmdroidPairedDevice;

    public AndroidScreenShareDevicePickerWindow(IReadOnlyList<JmdroidPairedDevice> devices)
    {
        Title = "Selecionar celular";
        Width = 360;
        Height = 330;
        MinWidth = 300;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(23, 25, 31));

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var title = new TextBlock
        {
            Text = "Qual tela você quer visualizar?",
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        root.Children.Add(title);

        _devices = new System.Windows.Controls.ListBox
        {
            ItemsSource = devices,
            DisplayMemberPath = nameof(JmdroidPairedDevice.Name),
            SelectedIndex = devices.Count == 1 ? 0 : -1,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(_devices, 1);
        root.Children.Add(_devices);

        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        var cancel = new System.Windows.Controls.Button { Content = "Cancelar", MinWidth = 86, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        var start = new System.Windows.Controls.Button { Content = "Iniciar", MinWidth = 86, Padding = new Thickness(10, 6, 10, 6), IsDefault = true };
        start.Click += (_, _) =>
        {
            if (SelectedDevice is not null) DialogResult = true;
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(start);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        Content = root;
    }
}
