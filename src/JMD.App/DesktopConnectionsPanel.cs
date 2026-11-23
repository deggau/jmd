using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using UserControl = System.Windows.Controls.UserControl;

namespace JMD.App;

internal sealed record DiscoveredJmd(string ComputerName, IPAddress Address, int Port, string Scheme, string? AppVersion, string PeerId, string PublicKey)
{
    public string BaseUrl => $"{Scheme}://{Address}:{Port}";
}
internal sealed record JmdNetworkDiscoveryResult(IReadOnlyList<DiscoveredJmd> Devices, int SkippedNetworks);

internal static class DesktopPeerIdentity
{
    public static void Ensure(UserSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.DesktopIdentityPublicKey) && !string.IsNullOrWhiteSpace(settings.ProtectedDesktopIdentityPrivateKey)) return;
        using var rsa = RSA.Create(3072);
        settings.DesktopIdentityPublicKey = rsa.ExportSubjectPublicKeyInfoPem();
        settings.ProtectedDesktopIdentityPrivateKey = WorkflowVariableProtection.Protect(Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()));
    }

    public static string PeerId(string publicKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(publicKey))).ToLowerInvariant();

    public static string Sign(UserSettings settings, string value)
    {
        using var rsa = RSA.Create();
        var privateKey = Convert.FromBase64String(WorkflowVariableProtection.Unprotect(settings.ProtectedDesktopIdentityPrivateKey));
        try { rsa.ImportPkcs8PrivateKey(privateKey, out _); }
        finally { CryptographicOperations.ZeroMemory(privateKey); }
        return Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(value), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
    }

    public static bool Verify(string publicKey, string value, string signature)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKey);
            return rsa.VerifyData(Encoding.UTF8.GetBytes(value), Convert.FromBase64String(signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (CryptographicException) { return false; }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }
}

internal static class JmdNetworkDiscovery
{
    private const int DiscoveryPort = 47831;
    private static readonly HttpClient Client = CreateClient();

    public static async Task<JmdNetworkDiscoveryResult> FindAsync(CancellationToken cancellationToken)
    {
        var addresses = new List<IPAddress>();
        var skippedNetworks = 0;
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up &&
                                       networkInterface.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel));

        foreach (var networkInterface in interfaces)
        foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
        {
            if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || !IsPrivateIpv4(unicast.Address)) continue;
            var prefixLength = unicast.PrefixLength;
            if (prefixLength is < 20 or > 30)
            {
                skippedNetworks++;
                continue;
            }

            var local = BinaryPrimitives.ReadUInt32BigEndian(unicast.Address.GetAddressBytes());
            var mask = uint.MaxValue << (32 - prefixLength);
            var network = local & mask;
            var broadcast = network | ~mask;
            for (var candidate = network + 1; candidate < broadcast; candidate++)
            {
                if (candidate == local) continue;
                var bytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(bytes, candidate);
                addresses.Add(new IPAddress(bytes));
            }
        }

        var uniqueAddresses = addresses.Distinct().ToArray();
        using var concurrency = new SemaphoreSlim(64);
        var devices = new System.Collections.Concurrent.ConcurrentBag<DiscoveredJmd>();
        await Task.WhenAll(uniqueAddresses.Select(async address =>
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                var device = await ProbeAsync(address, cancellationToken);
                if (device is not null) devices.Add(device);
            }
            finally { concurrency.Release(); }
        }));

        return new JmdNetworkDiscoveryResult(devices.OrderBy(device => device.ComputerName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(device => device.Address.ToString(), StringComparer.Ordinal).ToArray(), skippedNetworks);
    }

    private static async Task<DiscoveredJmd?> ProbeAsync(IPAddress address, CancellationToken cancellationToken)
    {
        var firstScheme = Uri.UriSchemeHttps;
        foreach (var scheme in new[] { firstScheme, Uri.UriSchemeHttp })
        {
            try
            {
                using var response = await Client.GetAsync($"{scheme}://{address}:{DiscoveryPort}/api/v1/discovery",
                    HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode) continue;
                var info = await response.Content.ReadFromJsonAsync<DiscoveryResponse>(cancellationToken: cancellationToken);
                if (info is { Service: "jmd", ApiVersion: 1 } && !string.IsNullOrWhiteSpace(info.ComputerName))
                    if (!string.IsNullOrWhiteSpace(info.PeerId) && !string.IsNullOrWhiteSpace(info.PublicKey) &&
                        DesktopPeerIdentity.PeerId(info.PublicKey) == info.PeerId)
                        return new DiscoveredJmd(info.ComputerName, address, info.Port is > 0 and <= 65535 ? info.Port : DiscoveryPort,
                            info.Scheme is "http" or "https" ? info.Scheme : scheme, info.AppVersion, info.PeerId, info.PublicKey);
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException)
            {
                if (cancellationToken.IsCancellationRequested) throw;
            }
        }
        return null;
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(450),
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(800) };
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168);
    }

    private sealed record DiscoveryResponse(string Service, string ComputerName, int ApiVersion, int Port, string? Scheme, string? AppVersion, string? PeerId, string? PublicKey);
}

internal sealed class DesktopConnectionsPanel : UserControl
{
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(150, 155, 167));
    private static readonly HttpClient PeerClient = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
    }) { Timeout = TimeSpan.FromSeconds(8) };
    private readonly JmdroidApiServer _server;
    private readonly SettingsStore _settingsStore;
    private readonly UserSettings _settings;
    private readonly StackPanel _deviceList = new();
    private readonly StackPanel _pendingList = new();
    private readonly StackPanel _pairedList = new();
    private readonly DispatcherTimer _pendingTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _serviceStatus = new() { Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _discoveryStatus = new() { Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly CheckBox _discoveryToggle = new() { Content = "Atualizar descoberta automaticamente enquanto esta tela estiver aberta", Foreground = Brushes.White, FontSize = 12 };
    private readonly Button _scanButton = new() { Content = "Procurar agora", Padding = new Thickness(12, 7, 12, 7), Background = new SolidColorBrush(Color.FromRgb(48, 52, 62)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
    private readonly Button _firewallButton = new() { Content = "Configurar acesso no Firewall", Padding = new Thickness(12, 7, 12, 7), Background = new SolidColorBrush(Color.FromRgb(48, 52, 62)), Foreground = Brushes.White, BorderThickness = new Thickness(0), Margin = new Thickness(8, 0, 0, 0) };
    private readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private CancellationTokenSource? _scanCancellation;
    private bool _loading;
    private IReadOnlyList<DiscoveredJmd> _lastDevices = [];
    private bool _refreshingPending;

    public DesktopConnectionsPanel(JmdroidApiServer server, UserSettings settings, SettingsStore settingsStore)
    {
        _server = server;
        _settings = settings;
        _settingsStore = settingsStore;
        Foreground = Brushes.White;

        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        root.Children.Add(new TextBlock { Text = "JMDs encontrados na rede", Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });

        var serviceCard = CreateCard();
        serviceCard.Child = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "Este computador", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold },
                _serviceStatus,
                _firewallButton
            }
        };
        ((StackPanel)serviceCard.Child).Children.Insert(2, _discoveryToggle);
        root.Children.Add(serviceCard);

        var pendingCard = CreateCard();
        var pendingContent = new StackPanel();
        pendingContent.Children.Add(new TextBlock { Text = "Pedidos de conexão aguardando aprovação", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        pendingContent.Children.Add(_pendingList);
        pendingCard.Child = pendingContent;
        root.Children.Add(pendingCard);

        var pairedCard = CreateCard();
        var pairedContent = new StackPanel();
        pairedContent.Children.Add(new TextBlock { Text = "Computadores pareados", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        pairedContent.Children.Add(_pairedList);
        pairedCard.Child = pairedContent;
        root.Children.Add(pairedCard);

        _scanButton.Click += async (_, _) => await ScanAsync();
        _firewallButton.Click += async (_, _) => await ConfigureFirewallAsync();
        _discoveryToggle.Checked += DiscoveryToggle_Changed;
        _discoveryToggle.Unchecked += DiscoveryToggle_Changed;
        _scanTimer.Tick += async (_, _) => await ScanAsync(automatic: true);
        _pendingTimer.Tick += (_, _) => RefreshPendingConnections();
        server.StateChanged += Server_StateChanged;
        server.PairingRequestsChanged += Server_StateChanged;
        IsVisibleChanged += Panel_IsVisibleChanged;

        var resultsCard = CreateCard();
        var results = new DockPanel();
        DockPanel.SetDock(_scanButton, Dock.Top);
        results.Children.Add(_scanButton);
        var statusPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        statusPanel.Children.Add(_discoveryStatus);
        DockPanel.SetDock(statusPanel, Dock.Top);
        results.Children.Add(statusPanel);
        var listScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 330, Content = _deviceList };
        results.Children.Add(listScroll);
        resultsCard.Child = results;
        root.Children.Add(resultsCard);

        root.Children.Add(new TextBlock
        {
            Text = "A descoberta consulta somente a rede IPv4 privada local. Cada instalação mantém uma chave privada exclusiva protegida pelo Windows e compartilha apenas a chave pública. A conexão só é criada após você solicitar e o outro computador aprovar.",
            Foreground = MutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 8, 2, 0)
        });

        _loading = true;
        _discoveryToggle.IsChecked = _settings.DesktopNetworkDiscoveryEnabled;
        _loading = false;
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
        RefreshServiceStatus();
        RefreshPendingConnections();
        RenderPairedPeers();
        if (_settings.DesktopNetworkDiscoveryEnabled) _ = ScanAsync();
    }

    private static Border CreateCard() => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(34, 37, 45)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(49, 52, 61)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(14),
        Margin = new Thickness(0, 0, 0, 10)
    };

    private async void Panel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && _settings.DesktopNetworkDiscoveryEnabled)
        {
            _scanTimer.Start();
            _pendingTimer.Start();
            RefreshPendingConnections();
            await ScanAsync(automatic: true);
        }
        else
        {
            _scanTimer.Stop();
            _pendingTimer.Stop();
            _scanCancellation?.Cancel();
        }
    }

    private void Server_StateChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshServiceStatus));
            return;
        }
        RefreshServiceStatus();
    }

    private void RefreshServiceStatus()
    {
        var address = _server.ListeningAddress;
        if (_server.IsRunning && address is not null)
        {
            _serviceStatus.Text = $"API disponível em {(_server.UsesHttp ? "http" : "https")}://{address}:{_server.ListeningPort} · rede local privada";
            _firewallButton.IsEnabled = true;
        }
        else
        {
            _serviceStatus.Text = "A API ainda não está disponível. Conecte-se a uma rede privada e reabra esta tela para tentar novamente.";
            _firewallButton.IsEnabled = false;
        }
    }

    private void DiscoveryToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var previous = _settings.DesktopNetworkDiscoveryEnabled;
        _settings.DesktopNetworkDiscoveryEnabled = _discoveryToggle.IsChecked == true;
        try { _settingsStore.Save(_settings); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _settings.DesktopNetworkDiscoveryEnabled = previous;
            _loading = true;
            _discoveryToggle.IsChecked = previous;
            _loading = false;
            _discoveryStatus.Text = $"Não foi possível salvar a preferência de descoberta: {exception.Message}";
            return;
        }

        if (_settings.DesktopNetworkDiscoveryEnabled && IsVisible)
        {
            _scanTimer.Start();
            _ = ScanAsync(automatic: true);
        }
        else
        {
            _scanTimer.Stop();
            _scanCancellation?.Cancel();
            _discoveryStatus.Text = "Descoberta automática desativada.";
        }
    }

    private async Task ScanAsync(bool automatic = false)
    {
        if (!IsVisible || (automatic && !_settings.DesktopNetworkDiscoveryEnabled)) return;
        if (automatic && _scanCancellation is { IsCancellationRequested: false }) return;
        _scanCancellation?.Cancel();
        _scanCancellation?.Dispose();
        _scanCancellation = new CancellationTokenSource();
        var token = _scanCancellation.Token;
        _scanButton.IsEnabled = false;
        _discoveryStatus.Text = "Procurando JMDs nos endereços privados desta rede…";
        try
        {
            var result = await JmdNetworkDiscovery.FindAsync(token);
            _deviceList.Children.Clear();
            _lastDevices = result.Devices;
            foreach (var device in result.Devices)
            {
                var peer = _settings.DesktopPeers.FirstOrDefault(candidate => candidate.Id == device.PeerId);
                if (peer is not null)
                {
                    if (peer.PublicKey == device.PublicKey && await VerifyKnownPeerAsync(device))
                    {
                        var peerNameChanged = peer.Name != device.ComputerName;
                        peer.Name = device.ComputerName;
                        if (peerNameChanged) _settingsStore.Save(_settings);
                        _server.UpdateDesktopPeerAddress(device.PeerId, new Uri(device.BaseUrl));
                    }
                    else
                    {
                        _discoveryStatus.Text = $"O endereço {device.Address} anunciou a identidade de {peer.Name}, mas não provou possuir a chave privada pareada.";
                        continue;
                    }
                }
                _deviceList.Children.Add(BuildDeviceRow(device, peer is not null));
            }
            RenderPairedPeers();

            _discoveryStatus.Text = result.Devices.Count == 0
                ? "Nenhum outro JMD encontrado. A busca automática será repetida em 30 segundos."
                : $"{result.Devices.Count} JMD{(result.Devices.Count == 1 ? " encontrado" : "s encontrados")}.";
            if (result.SkippedNetworks > 0)
                _discoveryStatus.Text += " Algumas interfaces foram ignoradas por terem sub-redes muito grandes ou sem faixa de hosts.";
            if (result.Devices.Count == 0)
                _deviceList.Children.Add(new TextBlock { Text = "Confira se os outros computadores estão ligados à mesma rede privada e se o JMD está aberto.", Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 4, 2, 2) });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is NetworkInformationException or SocketException or InvalidOperationException)
        {
            _discoveryStatus.Text = $"Não foi possível pesquisar esta rede: {exception.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested) _scanButton.IsEnabled = true;
        }
    }

    private static async Task<bool> VerifyKnownPeerAsync(DiscoveredJmd device)
    {
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        try
        {
            var identity = await PeerClient.GetFromJsonAsync<IdentityChallengeResponse>(device.BaseUrl + "/api/v1/desktop/identity/" + nonce);
            return identity is not null && identity.PeerId == device.PeerId && identity.PublicKey == device.PublicKey &&
                   DesktopPeerIdentity.Verify(device.PublicKey, $"identity\n{nonce}\n{device.PeerId}", identity.Signature);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or CryptographicException)
        {
            return false;
        }
    }

    private UIElement BuildDeviceRow(DiscoveredJmd device, bool isPaired)
    {
        var row = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(42, 45, 54)),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6)
        };
        var details = new StackPanel();
        details.Children.Add(new TextBlock { Text = device.ComputerName, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold });
        details.Children.Add(new TextBlock { Text = $"{device.Address}:{device.Port}" + (string.IsNullOrWhiteSpace(device.AppVersion) ? string.Empty : $" · JMD {device.AppVersion}"), Foreground = MutedBrush, FontSize = 11 });
        details.Children.Add(new TextBlock { Text = $"Chave {device.PeerId[..12]}", Foreground = MutedBrush, FontSize = 10, ToolTip = device.PeerId });
        var connect = new Button
        {
            Content = isPaired ? "Conectado" : "Conectar",
            IsEnabled = !isPaired,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 7, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(180, 243, 106)),
            Foreground = new SolidColorBrush(Color.FromRgb(23, 25, 31)), BorderThickness = new Thickness(0)
        };
        connect.Click += async (_, _) => await ConnectAsync(device, connect);
        details.Children.Add(connect);
        row.Child = details;
        return row;
    }

    private void RefreshPendingConnections()
    {
        if (_refreshingPending || !IsVisible) return;
        _refreshingPending = true;
        try
        {
            _pendingList.Children.Clear();
            foreach (var request in _server.GetPendingDesktopConnections())
            {
                var item = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                item.Children.Add(new TextBlock { Text = request.ComputerName, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold });
                item.Children.Add(new TextBlock { Text = $"Chave {request.PeerId[..12]} · {request.CreatedAt.ToLocalTime():t}", Foreground = MutedBrush, FontSize = 10, ToolTip = request.PeerId });
                var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
                var approve = new Button { Content = "Aceitar", Padding = new Thickness(9, 4, 9, 4), Background = new SolidColorBrush(Color.FromRgb(180, 243, 106)), Foreground = new SolidColorBrush(Color.FromRgb(23, 25, 31)), BorderThickness = new Thickness(0) };
                approve.Click += (_, _) =>
                {
                    if (_server.ApproveDesktopConnection(request.Id))
                    {
                        RefreshPendingConnections();
                        RenderPairedPeers();
                        _ = ScanAsync();
                    }
                };
                var deny = new Button { Content = "Recusar", Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(6, 0, 0, 0), Background = new SolidColorBrush(Color.FromRgb(49, 53, 63)), Foreground = Brushes.White, BorderThickness = new Thickness(0) };
                deny.Click += (_, _) => { _server.DenyDesktopConnection(request.Id); RefreshPendingConnections(); };
                buttons.Children.Add(approve); buttons.Children.Add(deny); item.Children.Add(buttons); _pendingList.Children.Add(item);
            }
            if (_pendingList.Children.Count == 0)
                _pendingList.Children.Add(new TextBlock { Text = "Nenhum pedido pendente.", Foreground = MutedBrush, FontSize = 11 });
        }
        finally { _refreshingPending = false; }
    }

    private void RenderPairedPeers()
    {
        _pairedList.Children.Clear();
        if (_settings.DesktopPeers.Count == 0)
        {
            _pairedList.Children.Add(new TextBlock { Text = "Nenhum computador pareado.", Foreground = MutedBrush, FontSize = 11 });
            return;
        }
        foreach (var peer in _settings.DesktopPeers.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var online = _lastDevices.FirstOrDefault(device => device.PeerId == peer.Id);
            var item = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            item.Children.Add(new TextBlock { Text = peer.Name + (online is null ? " · offline" : " · disponível"), Foreground = online is null ? MutedBrush : new SolidColorBrush(Color.FromRgb(180, 243, 106)), FontWeight = FontWeights.SemiBold });
            item.Children.Add(new TextBlock { Text = peer.BaseUrl, Foreground = MutedBrush, FontSize = 10 });
            item.Children.Add(new TextBlock { Text = $"Identidade {peer.Id[..12]} · pareado {peer.PairedAt.ToLocalTime():d}", Foreground = MutedBrush, FontSize = 10, ToolTip = peer.Id });
            _pairedList.Children.Add(item);
        }
    }

    private async Task ConnectAsync(DiscoveredJmd device, Button button)
    {
        button.IsEnabled = false;
        button.Content = "Aguardando aprovação…";
        var requestId = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var localPeerId = DesktopPeerIdentity.PeerId(_settings.DesktopIdentityPublicKey);
        var computerName = Environment.MachineName;
        var localScheme = _server.UsesHttp ? "http" : "https";
        var claimSignature = $"connect\n{requestId}\n{computerName}\n{localPeerId}\n{_server.ListeningPort}\n{localScheme}\n{timestamp}";
        var claim = new DesktopConnectionClaim(requestId, computerName, localPeerId, _settings.DesktopIdentityPublicKey,
            _server.ListeningPort, localScheme, timestamp, DesktopPeerIdentity.Sign(_settings, claimSignature));
        try
        {
            using var response = await PeerClient.PostAsJsonAsync(device.BaseUrl + "/api/v1/desktop/connections", claim);
            response.EnsureSuccessStatusCode();
            while (IsVisible)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                var state = await PeerClient.GetFromJsonAsync<DesktopConnectionStatus>(device.BaseUrl + "/api/v1/desktop/connections/" + Uri.EscapeDataString(requestId));
                if (state is null || state.State == "denied") throw new InvalidOperationException("O outro computador recusou o pedido de conexão.");
                if (state.State == "expired") throw new InvalidOperationException("O pedido de conexão expirou sem aprovação.");
                if (state.State != "approved") continue;
                var expectedPeerId = DesktopPeerIdentity.PeerId(device.PublicKey);
                var signaturePayload = $"approved\n{requestId}\n{localPeerId}\n{expectedPeerId}";
                if (state.PeerId != expectedPeerId || state.PublicKey != device.PublicKey ||
                    !DesktopPeerIdentity.Verify(device.PublicKey, signaturePayload, state.Signature ?? string.Empty))
                    throw new CryptographicException("A identidade do computador remoto não corresponde à chave pública encontrada na descoberta.");
                var peer = _settings.DesktopPeers.FirstOrDefault(candidate => candidate.Id == state.PeerId);
                if (peer is null)
                {
                    peer = new DesktopPairedPeer { Id = state.PeerId, Name = state.ComputerName ?? device.ComputerName, PublicKey = state.PublicKey, BaseUrl = device.BaseUrl };
                    _settings.DesktopPeers.Add(peer);
                }
                else { peer.Name = state.ComputerName ?? device.ComputerName; peer.PublicKey = state.PublicKey; peer.BaseUrl = device.BaseUrl; }
                _settingsStore.Save(_settings);
                _discoveryStatus.Text = $"Conexão estabelecida com {peer.Name}. Identidade confirmada pela chave pública.";
                await ScanAsync(automatic: true);
                return;
            }
            throw new OperationCanceledException("A tela foi fechada enquanto aguardava aprovação.");
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or InvalidOperationException or CryptographicException or IOException or System.Text.Json.JsonException)
        {
            _discoveryStatus.Text = $"Não foi possível concluir a conexão com {device.ComputerName}: {exception.Message}";
            button.Content = "Conectar";
            button.IsEnabled = true;
        }
    }

    private sealed record DesktopConnectionClaim(string RequestId, string ComputerName, string PeerId, string PublicKey, int Port, string Scheme, long Timestamp, string Signature);
    private sealed record DesktopConnectionStatus(string State, string RequestId, string PeerId, string? ComputerName, string? PublicKey, string? Signature);
    private sealed record IdentityChallengeResponse(string PeerId, string PublicKey, string Signature);

    private async Task ConfigureFirewallAsync()
    {
        if (_server.ListeningAddress is not { } address || _server.ListeningPort <= 0) return;
        _firewallButton.IsEnabled = false;
        _serviceStatus.Text = "Aguardando autorização do Windows para liberar somente a porta do JMD na rede local…";
        try
        {
            await JmdroidFirewallRule.ConfigurePrivateNetworkAccessAsync(address, _server.ListeningPort);
            _serviceStatus.Text = $"API disponível em {address}:{_server.ListeningPort}; acesso do Firewall limitado à rede local privada.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            _serviceStatus.Text = $"Não foi possível configurar o Firewall: {exception.Message}";
        }
        finally { _firewallButton.IsEnabled = true; }
    }
}
