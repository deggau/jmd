using System.ComponentModel;
using System.Diagnostics;
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
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using UserControl = System.Windows.Controls.UserControl;

namespace JMD.App;

internal sealed class AndroidPairingPanel : UserControl
{
    private static readonly Color SurfaceColor = Color.FromRgb(23, 25, 31);
    private static readonly Color MutedColor = Color.FromRgb(150, 155, 167);
    private readonly JmdroidApiServer _server;
    private readonly ComboBox _networkSelector = new() { Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(6) };
    private readonly Image _qrImage = new() { Width = 184, Height = 184, Stretch = Stretch.Uniform };
    private readonly TextBox _addressBox = new()
    {
        IsReadOnly = true,
        Padding = new Thickness(9, 7, 9, 7),
        Background = new SolidColorBrush(Color.FromRgb(34, 37, 45)),
        Foreground = Brushes.White,
        BorderBrush = new SolidColorBrush(Color.FromRgb(58, 62, 72)),
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly StackPanel _requestsPanel = new();
    private readonly StackPanel _devicesPanel = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private bool _active;
    private bool _tickHandlerAdded;

    public AndroidPairingPanel(JmdroidApiServer server)
    {
        _server = server;
        Foreground = Brushes.White;
        var root = new Grid { Margin = new Thickness(10, 8, 10, 8) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var title = new TextBlock
        {
            Text = "Conectar um aparelho",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        var connectionCard = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(34, 37, 45)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(58, 62, 72)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8)
        };
        var connection = new Grid();
        connection.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        connection.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var qrBorder = new Border
        {
            Background = Brushes.White,
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(8),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _qrImage
        };
        Grid.SetColumn(qrBorder, 0);
        connection.Children.Add(qrBorder);
        var details = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        details.Children.Add(new TextBlock
        {
            Text = "No JMDroid, toque em Conectar e leia o QR code.",
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        details.Children.Add(new TextBlock { Text = "Interface de rede", Foreground = new SolidColorBrush(MutedColor), FontSize = 11 });
        _networkSelector.Margin = new Thickness(0, 3, 0, 8);
        _networkSelector.Padding = new Thickness(6);
        details.Children.Add(_networkSelector);
        details.Children.Add(new TextBlock
        {
            Text = "Endereço da API",
            Foreground = new SolidColorBrush(MutedColor),
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 3)
        });
        var addressRow = new DockPanel();
        var openBrowserButton = new Button
        {
            Content = "Testar no navegador",
            Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(7, 0, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(49, 53, 63)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(openBrowserButton, Dock.Right);
        openBrowserButton.Click += (_, _) => OpenApiAddress();
        addressRow.Children.Add(openBrowserButton);
        addressRow.Children.Add(_addressBox);
        details.Children.Add(addressRow);
        _status.TextAlignment = TextAlignment.Left;
        _status.Margin = new Thickness(0, 7, 0, 0);
        details.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var firewallButton = new Button
        {
            Content = "Configurar Firewall",
            Padding = new Thickness(11, 7, 11, 7),
            Margin = new Thickness(0, 0, 7, 0),
            Background = new SolidColorBrush(Color.FromRgb(49, 53, 63)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            ToolTip = "O Windows solicitará permissão de administrador. A regra libera somente a porta ativa do JMD na rede local privada."
        };
        firewallButton.Click += async (_, _) => await ConfigureFirewallAsync(firewallButton);
        actions.Children.Add(firewallButton);
        var newQrButton = new Button
        {
            Content = "Gerar novo QR code",
            Padding = new Thickness(11, 7, 11, 7),
            Background = new SolidColorBrush(Color.FromRgb(180, 243, 106)),
            Foreground = new SolidColorBrush(SurfaceColor),
            BorderThickness = new Thickness(0)
        };
        newQrButton.Click += async (_, _) => await RefreshQrAsync();
        actions.Children.Add(newQrButton);
        details.Children.Add(actions);
        Grid.SetColumn(details, 1);
        connection.Children.Add(details);
        connectionCard.Child = connection;
        Grid.SetRow(connectionCard, 1);
        root.Children.Add(connectionCard);

        var lists = new Grid();
        lists.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lists.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lists.ColumnDefinitions[0].MinWidth = 180;
        lists.ColumnDefinitions[1].MinWidth = 180;
        var requestsColumn = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        var devicesColumn = new StackPanel { Margin = new Thickness(8, 0, 0, 0) };
        requestsColumn.Children.Add(SectionTitle("Pedidos de aprovação"));
        requestsColumn.Children.Add(_requestsPanel);
        devicesColumn.Children.Add(SectionTitle("Aparelhos autorizados"));
        devicesColumn.Children.Add(_devicesPanel);
        Grid.SetColumn(devicesColumn, 1);
        lists.Children.Add(requestsColumn);
        lists.Children.Add(devicesColumn);
        var listsScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = lists };
        Grid.SetRow(listsScroll, 2);
        root.Children.Add(listsScroll);
        _networkSelector.ItemsSource = _server.AvailableNetworkAddresses;
        _networkSelector.DisplayMemberPath = nameof(JmdroidNetworkAddress.DisplayName);
        if (_networkSelector.Items.Count > 0) _networkSelector.SelectedIndex = 0;
        _networkSelector.SelectionChanged += async (_, _) =>
        {
            if (_active) await RefreshQrAsync();
        };
        Content = root;

    }

    public bool IsActive => _active;

    public async Task ActivateAsync()
    {
        if (_active) return;
        _active = true;
        if (!_tickHandlerAdded)
        {
            _refreshTimer.Tick += (_, _) => RefreshLists();
            _tickHandlerAdded = true;
        }
        await RefreshQrAsync();
        if (_active) _refreshTimer.Start();
    }

    public async Task DeactivateAsync()
    {
        if (!_active) return;
        _active = false;
        _refreshTimer.Stop();
        await _server.ClosePairingScreenAsync();
    }

    private async Task RefreshQrAsync()
    {
        _status.Text = "Iniciando conexão HTTPS local…";
        try
        {
            var selectedAddress = (_networkSelector.SelectedItem as JmdroidNetworkAddress)?.Address;
            var pairing = await _server.OpenPairingScreenAsync(selectedAddress);
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
            _addressBox.Text = pairing.BaseUrl;
            _status.Text = $"HTTPS validado neste PC · QR válido até {pairing.ExpiresAt.ToLocalTime():HH:mm:ss}. " +
                           "No navegador, avance pelo aviso do certificado local. No Android, use o QR pelo JMDroid. " +
                           "Para celular/VPN, permita acesso à LAN nas opções da VPN.";
            RefreshLists();
        }
        catch (Exception exception)
        {
            CriticalErrorLog.Write("Iniciar API local do JMDroid", exception);
            _qrImage.Source = null;
            _addressBox.Clear();
            _status.Text = $"Não foi possível iniciar a API: {exception.GetBaseException().Message}";
        }
    }

    private void OpenApiAddress()
    {
        if (string.IsNullOrWhiteSpace(_addressBox.Text)) return;
        try
        {
            Process.Start(new ProcessStartInfo(_addressBox.Text) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _status.Text = $"Não foi possível abrir o navegador: {exception.Message}";
        }
    }

    private async Task ConfigureFirewallAsync(Button button)
    {
        button.IsEnabled = false;
        _status.Text = "Aguardando autorização do Windows para configurar o Firewall…";
        try
        {
            var listeningAddress = _server.ListeningAddress ?? throw new InvalidOperationException("A API ainda não está ouvindo em um endereço de rede.");
            await JmdroidFirewallRule.ConfigurePrivateNetworkAccessAsync(listeningAddress, _server.ListeningPort);
            _status.Text = $"Firewall configurado para a porta {_server.ListeningPort} na rede local. O celular e o computador precisam estar na mesma rede.";
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            _status.Text = "A configuração do Firewall foi cancelada no aviso do Windows.";
        }
        catch (Exception exception)
        {
            _status.Text = $"Não foi possível configurar o Firewall: {exception.Message}";
        }
        finally
        {
            button.IsEnabled = true;
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
                    MessageBox.Show(Window.GetWindow(this), "O pedido expirou. Gere um novo QR code e tente novamente.", "Pareamento", MessageBoxButton.OK, MessageBoxImage.Information);
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
                if (MessageBox.Show(Window.GetWindow(this), $"Revogar o acesso de {device.Name}?", "Remover aparelho", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
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
