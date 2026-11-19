using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using JMD.Core;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using UserControl = System.Windows.Controls.UserControl;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;

namespace JMD.App;

internal sealed class AndroidSettingsPanel : UserControl
{
    private static readonly Color CardColor = Color.FromRgb(34, 37, 45);
    private static readonly Color BorderColor = Color.FromRgb(58, 62, 72);
    private readonly AndroidInstallPanel _installPanel = new();
    private readonly ScrollViewer _installScroll;
    private readonly AndroidPairingPanel _pairingPanel;
    private readonly JmdroidApiServer _server;
    private readonly Func<int, string?> _saveScreenShareFpsPreference;
    private readonly ContentControl _content = new();
    private UIElement _dashboardView = null!;
    private readonly StackPanel _connectedDevices = new();
    private readonly System.Windows.Shapes.Ellipse _serviceIndicator = new() { Width = 11, Height = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _serviceStatus = new() { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _screenShareFpsSelector = new() { Width = 90, Padding = new Thickness(5), Margin = new Thickness(8, 0, 0, 0) };
    private readonly DispatcherTimer _dashboardTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _connectSelected;
    private bool _dashboardSelected = true;

    public AndroidSettingsPanel(JmdroidApiServer server, bool useHttp, int screenShareMaxFps, Func<bool, string?> saveTransportPreference, Func<int, string?> saveScreenShareFpsPreference)
    {
        _server = server;
        _saveScreenShareFpsPreference = saveScreenShareFpsPreference;
        _pairingPanel = new AndroidPairingPanel(server, useHttp, saveTransportPreference);
        _installScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _installPanel
        };
        Foreground = Brushes.White;

        var root = new DockPanel { Margin = new Thickness(10, 5, 10, 8) };
        _screenShareFpsSelector.ItemsSource = Enumerable.Range(JmdroidScreenSharePolicy.DefaultMaxFramesPerSecond, JmdroidScreenSharePolicy.MaxFramesPerSecond - JmdroidScreenSharePolicy.DefaultMaxFramesPerSecond + 1);
        _screenShareFpsSelector.SelectedItem = JmdroidScreenSharePolicy.NormalizeFramesPerSecond(screenShareMaxFps);
        _screenShareFpsSelector.ToolTip = "Limite máximo de quadros por segundo nas transmissões entre o computador e o celular.";
        _screenShareFpsSelector.SelectionChanged += (_, _) =>
        {
            if (_screenShareFpsSelector.SelectedItem is not int fps) return;
            var error = _saveScreenShareFpsPreference(fps);
            if (error is null) _server.ScreenShareMaxFps = fps;
            else MessageBox.Show(error, "Configuração de transmissão", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        _dashboardView = BuildDashboard();
        _content.Content = _dashboardView;
        root.Children.Add(_content);
        Content = root;
        RefreshDashboard();
        _dashboardTimer.Tick += (_, _) => RefreshDashboard();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                _dashboardTimer.Stop();
                _ = _pairingPanel.DeactivateAsync();
            }
            else
            {
                RefreshDashboard();
                if (_dashboardSelected) _dashboardTimer.Start();
                if (_connectSelected) _ = _pairingPanel.ActivateAsync();
            }
        };
    }

    private async Task SelectInstallAsync()
    {
        _dashboardSelected = false;
        _connectSelected = false;
        _dashboardTimer.Stop();
        await _pairingPanel.DeactivateAsync();
        _content.Content = BuildDetailPage("Instalar app", _installScroll);
    }

    private async Task SelectConnectAsync()
    {
        _dashboardSelected = false;
        _connectSelected = true;
        _dashboardTimer.Stop();
        _content.Content = BuildDetailPage("Conectar JMDroid", _pairingPanel);
        await _pairingPanel.ActivateAsync();
    }

    private void ReturnToDashboard()
    {
        _dashboardSelected = true;
        _connectSelected = false;
        _content.Content = _dashboardView;
        RefreshDashboard();
        if (IsVisible) _dashboardTimer.Start();
        _ = _pairingPanel.DeactivateAsync();
    }

    private UIElement BuildDashboard()
    {
        var dashboard = new StackPanel { Margin = new Thickness(4, 5, 4, 4) };
        dashboard.Children.Add(new TextBlock
        {
            Text = "Aparelhos Android",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 0, 0, 8)
        });
        var devicesCard = CreateCard();
        var devicesContents = new StackPanel();
        devicesContents.Children.Add(new TextBlock
        {
            Text = "Aparelhos conectados",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        devicesContents.Children.Add(_connectedDevices);
        devicesCard.Child = devicesContents;
        dashboard.Children.Add(devicesCard);

        var statusCard = CreateCard();
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal };
        statusRow.Children.Add(_serviceIndicator);
        statusRow.Children.Add(new TextBlock { Text = "  " });
        statusRow.Children.Add(_serviceStatus);
        statusCard.Child = statusRow;
        dashboard.Children.Add(statusCard);

        var fpsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 9, 2, 10) };
        fpsRow.Children.Add(new TextBlock
        {
            Text = "Taxa máxima de atualização da tela (FPS)",
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        });
        fpsRow.Children.Add(_screenShareFpsSelector);
        dashboard.Children.Add(fpsRow);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        var installButton = CreateActionButton("Instalar app");
        installButton.Click += async (_, _) => await SelectInstallAsync();
        var connectButton = CreateActionButton("Conectar JMDroid");
        connectButton.Margin = new Thickness(8, 0, 0, 0);
        connectButton.Click += async (_, _) => await SelectConnectAsync();
        actions.Children.Add(installButton);
        actions.Children.Add(connectButton);
        dashboard.Children.Add(actions);
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = dashboard };
    }

    private UIElement BuildDetailPage(string title, UIElement page)
    {
        var layout = new DockPanel();
        var header = new DockPanel { Margin = new Thickness(2, 2, 2, 8) };
        var back = CreateActionButton("← Aparelhos");
        back.Margin = new Thickness(0, 0, 10, 0);
        back.Click += (_, _) => ReturnToDashboard();
        DockPanel.SetDock(back, Dock.Left);
        header.Children.Add(back);
        header.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        });
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(page);
        return layout;
    }

    private void RefreshDashboard()
    {
        var running = _server.IsRunning;
        _serviceIndicator.Fill = running ? Brushes.LimeGreen : Brushes.Red;
        _serviceStatus.Text = running ? "Serviço JMDroid: ON" : "Serviço JMDroid: OFF";
        _connectedDevices.Children.Clear();
        var devices = _server.PairedDevices.Where(device => _server.IsDeviceConnected(device.Id)).ToArray();
        if (devices.Length == 0)
        {
            _connectedDevices.Children.Add(new TextBlock
            {
                Text = "Nenhum aparelho conectado no momento.",
                Foreground = new SolidColorBrush(Color.FromRgb(150, 155, 167))
            });
            return;
        }

        foreach (var device in devices)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = Brushes.LimeGreen,
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock { Text = $"  {device.Name}", Foreground = Brushes.White });
            _connectedDevices.Children.Add(row);
        }
    }

    private static Border CreateCard() => new()
    {
        Background = new SolidColorBrush(CardColor),
        BorderBrush = new SolidColorBrush(BorderColor),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(12),
        Margin = new Thickness(0, 0, 0, 8)
    };

    private static Button CreateActionButton(string title) => new()
    {
        Content = title,
        Padding = new Thickness(10, 6, 10, 6),
        Background = new SolidColorBrush(CardColor),
        Foreground = Brushes.White,
        BorderBrush = new SolidColorBrush(BorderColor),
        BorderThickness = new Thickness(1),
        Cursor = System.Windows.Input.Cursors.Hand
    };

}
