using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;

namespace JMD.App;

internal sealed class AndroidPairingWindow : Window
{
    private static readonly Color SurfaceColor = Color.FromRgb(23, 25, 31);
    private static readonly Color MutedColor = Color.FromRgb(150, 155, 167);
    private readonly JmdroidApiServer _server;
    private readonly Image _qrImage = new() { Width = 250, Height = 250, Stretch = Stretch.Uniform };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly StackPanel _requestsPanel = new();
    private readonly StackPanel _devicesPanel = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private bool _closing;

    public AndroidPairingWindow(JmdroidApiServer server)
    {
        _server = server;
        Title = "Conectar JMDroid";
        Width = 610;
        Height = 780;
        MinWidth = 560;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(SurfaceColor);
        Foreground = Brushes.White;

        var root = new DockPanel { Margin = new Thickness(24) };
        var title = new TextBlock
        {
            Text = "Conectar um aparelho",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        };
        DockPanel.SetDock(title, Dock.Top);
        root.Children.Add(title);

        var footer = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        footer.Children.Add(_status);
        var newQrButton = new Button
        {
            Content = "Gerar um novo QR code",
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 10, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(180, 243, 106)),
            Foreground = new SolidColorBrush(SurfaceColor),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        newQrButton.Click += async (_, _) => await RefreshQrAsync();
        footer.Children.Add(newQrButton);
        root.Children.Add(footer);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var content = new StackPanel();
        content.Children.Add(new TextBlock
        {
            Text = "No JMDroid, escolha Conectar e leia este QR code. O JMD só libera o acesso depois que você aprovar o aparelho nesta tela.",
            Foreground = new SolidColorBrush(MutedColor),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });
        content.Children.Add(new Border
        {
            Background = Brushes.White,
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = _qrImage
        });
        content.Children.Add(SectionTitle("Pedidos aguardando aprovação"));
        content.Children.Add(_requestsPanel);
        content.Children.Add(SectionTitle("Aparelhos autorizados"));
        content.Children.Add(_devicesPanel);
        scroll.Content = content;
        root.Children.Add(scroll);
        Content = root;

        Loaded += async (_, _) =>
        {
            await RefreshQrAsync();
            _refreshTimer.Tick += (_, _) => RefreshLists();
            _refreshTimer.Start();
        };
        Closing += async (_, _) =>
        {
            if (_closing) return;
            _closing = true;
            _refreshTimer.Stop();
            await _server.ClosePairingScreenAsync();
        };
    }

    private async Task RefreshQrAsync()
    {
        _status.Text = "Iniciando conexão HTTPS local…";
        try
        {
            var pairing = await _server.OpenPairingScreenAsync();
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(pairing.QrPng))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
            }
            _qrImage.Source = bitmap;
            _status.Text = $"API ativa em {_server.BaseUrl}. QR code válido até {pairing.ExpiresAt.ToLocalTime():HH:mm:ss}.\n" +
                           "O Windows pode pedir permissão de rede privada para o JMD.";
            RefreshLists();
        }
        catch (Exception exception)
        {
            _qrImage.Source = null;
            _status.Text = $"Não foi possível iniciar a API: {exception.Message}";
        }
    }

    private void RefreshLists()
    {
        _requestsPanel.Children.Clear();
        var requests = _server.GetPendingRequests();
        if (requests.Count == 0)
            _requestsPanel.Children.Add(EmptyLabel("Nenhum pedido aguardando."));
        foreach (var request in requests)
        {
            var row = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(34, 37, 45)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(58, 62, 72)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 7)
            };
            var layout = new DockPanel();
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var approve = SmallButton("Aprovar", true);
            approve.Click += (_, _) =>
            {
                if (!_server.ApprovePairing(request.Id))
                    MessageBox.Show(this, "O pedido expirou. Gere um novo QR code e tente novamente.", "Pareamento", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshLists();
            };
            var deny = SmallButton("Negar", false);
            deny.Click += (_, _) => { _server.DenyPairing(request.Id); RefreshLists(); };
            actions.Children.Add(approve);
            actions.Children.Add(deny);
            DockPanel.SetDock(actions, Dock.Right);
            layout.Children.Add(actions);
            layout.Children.Add(new TextBlock
            {
                Text = $"{request.DeviceName}\nJMDroid {request.AppVersion}",
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Child = layout;
            _requestsPanel.Children.Add(row);
        }

        _devicesPanel.Children.Clear();
        var devices = _server.PairedDevices;
        if (devices.Count == 0)
            _devicesPanel.Children.Add(EmptyLabel("Nenhum aparelho autorizado."));
        foreach (var device in devices)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
            var remove = SmallButton("Revogar", false);
            remove.Click += async (_, _) =>
            {
                if (MessageBox.Show(this, $"Revogar o acesso de {device.Name}?", "Remover aparelho", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    await _server.RevokeDeviceAsync(device.Id);
                RefreshLists();
            };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var connected = _server.IsDeviceConnected(device.Id) ? "Conectado" : "Desconectado";
            row.Children.Add(new TextBlock
            {
                Text = $"{device.Name}  ·  {connected}",
                Foreground = _server.IsDeviceConnected(device.Id) ? new SolidColorBrush(Color.FromRgb(180, 243, 106)) : new SolidColorBrush(MutedColor),
                VerticalAlignment = VerticalAlignment.Center
            });
            _devicesPanel.Children.Add(row);
        }
    }

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 18, 0, 9)
    };

    private static TextBlock EmptyLabel(string text) => new()
    {
        Text = text,
        Foreground = new SolidColorBrush(MutedColor),
        Margin = new Thickness(0, 0, 0, 6)
    };

    private static Button SmallButton(string text, bool primary) => new()
    {
        Content = text,
        Padding = new Thickness(9, 5, 9, 5),
        Margin = new Thickness(5, 0, 0, 0),
        Background = new SolidColorBrush(primary ? Color.FromRgb(180, 243, 106) : Color.FromRgb(48, 52, 62)),
        Foreground = primary ? new SolidColorBrush(SurfaceColor) : Brushes.White,
        BorderThickness = new Thickness(0)
    };
}
