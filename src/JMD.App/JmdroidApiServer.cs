using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;
using QRCoder;

namespace JMD.App;

internal sealed record JmdroidApiCommand(
    string Id,
    string Name,
    string Description,
    string Shortcut,
    bool Available,
    string? UnavailableReason,
    IReadOnlyList<string> SearchTerms);
internal sealed record JmdroidCommandInfo(
    string Id,
    string Name,
    string Description,
    string Shortcut,
    bool Available,
    string? UnavailableReason,
    object? InputSchema);

internal sealed record JmdroidPairingInfo(string BaseUrl, string CertificateSha256, string QrPayload, byte[] QrPng, DateTimeOffset ExpiresAt);
internal sealed record JmdroidPairingRequestView(string Id, string DeviceName, string AppVersion, DateTimeOffset CreatedAt);
internal sealed record JmdroidExecutionResult(string Status, string? Text = null, string? Error = null, int? InputItemCount = null, int? OutputItemCount = null);

internal sealed class JmdroidApiServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(2);
    private readonly Func<IReadOnlyList<JmdroidApiCommand>> _getCommands;
    private readonly Func<string, JsonElement, CancellationToken, Task<JmdroidExecutionResult>> _executeCommand;
    private readonly IList<JmdroidPairedDevice> _devices;
    private readonly Action _saveSettings;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _requestLock = new();
    private readonly object _connectionLock = new();
    private readonly ConcurrentDictionary<string, PendingPairing> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<JmdroidExecutionResult>>> _executions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DeviceRateWindow> _deviceRates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _activeConnections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentBag<WebSocket>> _deviceSockets = new(StringComparer.Ordinal);
    private WebApplication? _application;
    private X509Certificate2? _certificate;
    private PairingSession? _pairingSession;
    private bool _pairingScreenOpen;
    private bool _disposed;
    private string? _baseUrl;
    private int _listeningPort;
    private IPAddress? _listeningAddress;

    public event Action? StateChanged;
    public event Action? PairingRequestsChanged;

    public JmdroidApiServer(
        Func<IReadOnlyList<JmdroidApiCommand>> getCommands,
        Func<string, JsonElement, CancellationToken, Task<JmdroidExecutionResult>> executeCommand,
        IList<JmdroidPairedDevice> devices,
        Action saveSettings)
    {
        _getCommands = getCommands;
        _executeCommand = executeCommand;
        _devices = devices;
        _saveSettings = saveSettings;
    }

    public bool IsRunning => _application is not null;
    public int ConnectedDeviceCount { get { lock (_connectionLock) return _activeConnections.Count; } }
    public string? BaseUrl => _baseUrl;
    public IReadOnlyList<JmdroidPairedDevice> PairedDevices
    {
        get { lock (_requestLock) return _devices.ToArray(); }
    }
    public bool IsDeviceConnected(string deviceId) { lock (_connectionLock) return _activeConnections.ContainsKey(deviceId); }

    public async Task<JmdroidPairingInfo> OpenPairingScreenAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_application is null)
                await StartListenerAsync(cancellationToken);

            _pairingScreenOpen = true;
            foreach (var pending in _pending.Values) pending.State = "expired";
            var ipAddress = _listeningAddress ?? GetLanAddress();
            var certificate = _certificate ?? throw new InvalidOperationException("Certificado HTTPS da API não está disponível.");
            var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
            _baseUrl ??= $"https://{ipAddress}:{_listeningPort}";
            var session = new PairingSession(
                Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                CreateSecret(),
                DateTimeOffset.UtcNow.Add(PairingLifetime));
            _pairingSession = session;
            var payload = JsonSerializer.Serialize(new PairingQrPayload(
                "jmd-pairing", 1, _baseUrl, fingerprint, session.Id, session.Secret, session.ExpiresAt), JsonOptions);
            using var qrData = QRCodeGenerator.GenerateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
            var qrPng = new PngByteQRCode(qrData).GetGraphic(8);
            StateChanged?.Invoke();
            return new JmdroidPairingInfo(_baseUrl, fingerprint, payload, qrPng, session.ExpiresAt);
        }
        finally { _lifecycleLock.Release(); }
    }

    public IReadOnlyList<JmdroidPairingRequestView> GetPendingRequests()
        => _pending.Values
            .Where(item => item.State == "pending")
            .OrderBy(item => item.CreatedAt)
            .Select(item => new JmdroidPairingRequestView(item.Id, item.DeviceName, item.AppVersion, item.CreatedAt))
            .ToArray();

    public bool ApprovePairing(string requestId)
    {
        if (!_pending.TryGetValue(requestId, out var request) || request.State != "pending") return false;
        lock (_requestLock)
        {
            if (request.State != "pending" || _pairingSession is not { } session || request.SessionId != session.Id || session.ExpiresAt <= DateTimeOffset.UtcNow)
                return false;
            var token = CreateSecret();
            request.DeviceId = Guid.NewGuid().ToString("N");
            request.AccessToken = token;
            request.State = "approved";
            _devices.Add(new JmdroidPairedDevice
            {
                Id = request.DeviceId,
                Name = request.DeviceName,
                TokenSha256 = TokenHash(token),
                PairedAt = DateTimeOffset.UtcNow
            });
            _saveSettings();
        }
        PairingRequestsChanged?.Invoke();
        return true;
    }

    public bool DenyPairing(string requestId)
    {
        if (!_pending.TryGetValue(requestId, out var request) || request.State != "pending") return false;
        request.State = "denied";
        PairingRequestsChanged?.Invoke();
        return true;
    }

    public async Task RevokeDeviceAsync(string deviceId)
    {
        lock (_requestLock)
        {
            var device = _devices.FirstOrDefault(item => item.Id == deviceId);
            if (device is null) return;
            _devices.Remove(device);
            _saveSettings();
        }
        if (_deviceSockets.TryRemove(deviceId, out var sockets))
        {
            foreach (var socket in sockets)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Device revoked", CancellationToken.None); }
                catch (WebSocketException) { }
            }
        }
        StateChanged?.Invoke();
    }

    public async Task ClosePairingScreenAsync()
    {
        await _lifecycleLock.WaitAsync();
        try
        {
            _pairingScreenOpen = false;
            _pairingSession = null;
            foreach (var request in _pending.Values.Where(item => item.State == "pending")) request.State = "expired";
            _pending.Clear();
            await StopIfIdleAsync();
        }
        finally { _lifecycleLock.Release(); }
    }

    private async Task StartListenerAsync(CancellationToken cancellationToken)
    {
        var port = ReserveEphemeralPort();
        var listeningAddress = GetLanAddress();
        var certificate = CreateServerCertificate(listeningAddress);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 1_048_576;
            options.Listen(listeningAddress, port, listen => listen.UseHttps(certificate));
        });
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        MapRoutes(app);
        try
        {
            await app.StartAsync(cancellationToken);
            _application = app;
            _certificate = certificate;
            _listeningPort = port;
            _listeningAddress = listeningAddress;
            _baseUrl = $"https://{listeningAddress}:{port}";
            StateChanged?.Invoke();
        }
        catch
        {
            certificate.Dispose();
            await app.DisposeAsync();
            throw;
        }
    }

    private void MapRoutes(WebApplication app)
    {
        app.MapPost("/api/v1/pairing/requests", async context =>
        {
            var payload = await context.Request.ReadFromJsonAsync<PairingClaim>(JsonOptions, context.RequestAborted);
            var session = _pairingSession;
            if (payload is null || session is null || !_pairingScreenOpen || session.ExpiresAt <= DateTimeOffset.UtcNow ||
                payload.SessionId != session.Id || !FixedTimeTextEquals(payload.PairingSecret, session.Secret))
            {
                await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "invalid_pairing_session", "Sessão de pareamento inválida ou expirada.");
                return;
            }

            if (string.IsNullOrWhiteSpace(payload.DeviceName) || payload.DeviceName.Length > 80 || string.IsNullOrWhiteSpace(payload.AppVersion) || payload.AppVersion.Length > 32)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_input", "Identificação do aparelho inválida.");
                return;
            }

            PendingPairing? request = null;
            lock (_requestLock)
            {
                if (!session.Claimed && ReferenceEquals(_pairingSession, session) && _pairingScreenOpen &&
                    !_pending.Values.Any(item => item.State == "pending" && item.SessionId == session.Id))
                {
                    request = new PendingPairing(Guid.NewGuid().ToString("N"), session.Id, payload.DeviceName.Trim(), payload.AppVersion.Trim());
                    session.Claimed = true;
                    _pending[request.Id] = request;
                }
            }
            if (request is null)
            {
                await WriteErrorAsync(context, StatusCodes.Status429TooManyRequests, "rate_limited", "Já existe um pedido aguardando aprovação.");
                return;
            }
            PairingRequestsChanged?.Invoke();
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            await context.Response.WriteAsJsonAsync(new { requestId = request.Id, state = "pending", pollAfterMs = 1500 }, JsonOptions, context.RequestAborted);
        });

        app.MapGet("/api/v1/pairing/requests/{requestId}", async context =>
        {
            var secret = context.Request.Headers["X-JMD-Pairing-Secret"].ToString();
            var session = _pairingSession;
            var requestId = Convert.ToString(context.Request.RouteValues["requestId"]);
            if (session is null || requestId is null || !FixedTimeTextEquals(secret, session.Secret) || !_pending.TryGetValue(requestId, out var request))
            {
                await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "unauthorized", "Pedido de pareamento inválido.");
                return;
            }

            if (request.State == "pending")
            {
                context.Response.StatusCode = StatusCodes.Status202Accepted;
                await context.Response.WriteAsJsonAsync(new { state = "pending", pollAfterMs = 1500 }, JsonOptions, context.RequestAborted);
            }
            else if (request.State == "approved")
                await context.Response.WriteAsJsonAsync(new { state = "approved", deviceId = request.DeviceId, accessToken = request.AccessToken }, JsonOptions, context.RequestAborted);
            else
            {
                var state = request.State;
                _pending.TryRemove(request.Id, out _);
                await context.Response.WriteAsJsonAsync(new { state }, JsonOptions, context.RequestAborted);
            }
        });

        app.MapGet("/api/v1/commands", async context =>
        {
            if (!TryAuthenticate(context, out var device))
            {
                await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "unauthorized", "Token ausente ou inválido.");
                return;
            }
            if (await RateLimitAsync(context, device!.Id)) return;
            var query = context.Request.Query["q"].ToString().Trim();
            var includeUnavailable = bool.TryParse(context.Request.Query["includeUnavailable"], out var include) && include;
            var matches = FilterCommands(query, includeUnavailable);
            await context.Response.WriteAsJsonAsync(new { items = matches, count = matches.Count, query }, JsonOptions, context.RequestAborted);
        });

        async Task ExecuteCommandHandler(HttpContext context)
        {
            if (!TryAuthenticate(context, out var device))
            {
                await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "unauthorized", "Token ausente ou inválido.");
                return;
            }
            if (await RateLimitAsync(context, device!.Id)) return;
            var commandId = Convert.ToString(context.Request.RouteValues["commandId"]);
            if (string.IsNullOrWhiteSpace(commandId))
            {
                await WriteErrorAsync(context, StatusCodes.Status404NotFound, "command_not_found", "Comando inexistente.");
                return;
            }
            var command = _getCommands().FirstOrDefault(item => item.Id == commandId);
            if (command is null)
            {
                await WriteErrorAsync(context, StatusCodes.Status404NotFound, "command_not_found", "Comando inexistente.");
                return;
            }
            if (!command.Available)
            {
                await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "command_unavailable", command.UnavailableReason ?? "Comando não disponível remotamente.");
                return;
            }
            var body = await context.Request.ReadFromJsonAsync<ExecuteCommandRequest>(JsonOptions, context.RequestAborted);
            if (body is null || string.IsNullOrWhiteSpace(body.RequestId) || body.RequestId.Length > 80 || body.Input.ValueKind != JsonValueKind.Object)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_input", "Pedido de execução inválido.");
                return;
            }

            if (_executions.Count > 5000)
            {
                foreach (var cached in _executions.Where(item => item.Value.IsValueCreated && item.Value.Value.IsCompleted).Take(1000))
                    _executions.TryRemove(cached.Key, out _);
            }
            if (_executions.Count > 5000)
            {
                await WriteErrorAsync(context, StatusCodes.Status429TooManyRequests, "rate_limited", "A fila de execuções está cheia.");
                return;
            }
            var cacheKey = $"{device!.Id}:{body.RequestId}";
            var execution = _executions.GetOrAdd(cacheKey, _ => new Lazy<Task<JmdroidExecutionResult>>(
                () => _executeCommand(commandId, body.Input.Clone(), CancellationToken.None), LazyThreadSafetyMode.ExecutionAndPublication));
            JmdroidExecutionResult result;
            try { result = await execution.Value; }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return; }
            catch (Exception)
            {
                result = new JmdroidExecutionResult("failed", Error: "Falha ao executar o comando.");
            }
            await context.Response.WriteAsJsonAsync(new
            {
                requestId = body.RequestId,
                commandId,
                status = result.Status,
                result = result.Text is null ? null : new
                {
                    text = result.Text,
                    inputItemCount = result.InputItemCount,
                    outputItemCount = result.OutputItemCount
                },
                error = result.Error is null ? null : new { code = "invalid_input", message = result.Error }
            }, JsonOptions, context.RequestAborted);
        }
        app.MapPost("/api/v1/commands/{commandId}/execute", ExecuteCommandHandler);

        app.Map("/api/v1/session", HandleSessionAsync);
    }

    private async Task HandleSessionAsync(HttpContext context)
    {
        if (!TryAuthenticate(context, out var device))
        {
            await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "unauthorized", "Token ausente ou inválido.");
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_input", "É necessário estabelecer uma sessão WebSocket.");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        lock (_connectionLock)
            _activeConnections[device!.Id] = _activeConnections.TryGetValue(device.Id, out var current) ? current + 1 : 1;
        var sockets = _deviceSockets.GetOrAdd(device.Id, _ => new ConcurrentBag<WebSocket>());
        sockets.Add(socket);
        StateChanged?.Invoke();
        try
        {
            var ready = JsonSerializer.SerializeToUtf8Bytes(new { type = "session.ready", deviceId = device.Id, apiVersion = 1 }, JsonOptions);
            await socket.SendAsync(ready, WebSocketMessageType.Text, true, context.RequestAborted);
            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (received.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            lock (_connectionLock)
            {
                if (_activeConnections.TryGetValue(device.Id, out var count))
                {
                    if (count <= 1) _activeConnections.TryRemove(device.Id, out _);
                    else _activeConnections[device.Id] = count - 1;
                }
            }
            if (socket.State == WebSocketState.Open)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session ended", CancellationToken.None); }
                catch (WebSocketException) { }
            }
            StateChanged?.Invoke();
            _ = StopWhenIdleAsync();
        }
    }

    private IReadOnlyList<JmdroidCommandInfo> FilterCommands(string query, bool includeUnavailable)
    {
        var candidates = _getCommands().Where(item => includeUnavailable || item.Available);
        var matches = query.Length == 0
            ? candidates.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            : candidates
            .Select(command =>
            {
                var fields = new[] { command.Name, command.Description, command.Shortcut }.Concat(command.SearchTerms);
                var rank = fields.Any(value => string.Equals(value, query, StringComparison.CurrentCultureIgnoreCase)) ? 0
                    : fields.Any(value => value.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)) ? 1 : 2;
                var visible = fields.Any(value => value.Contains(query, StringComparison.CurrentCultureIgnoreCase));
                return (Command: command, Rank: rank, Visible: visible);
            })
            .Where(item => item.Visible)
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.Command.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => item.Command);
        return matches.Select(command => new JmdroidCommandInfo(
            command.Id,
            command.Name,
            command.Description,
            command.Shortcut,
            command.Available,
            command.UnavailableReason,
            command.Available ? new
            {
                type = "object",
                properties = new { text = new { type = "string", maxLength = 1_000_000 } },
                required = new[] { "text" }
            } : null)).ToArray();
    }

    private bool TryAuthenticate(HttpContext context, out JmdroidPairedDevice? device)
    {
        device = null;
        var auth = context.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
        var token = auth[7..].Trim();
        if (token.Length is < 40 or > 256) return false;
        var hash = TokenHash(token);
        lock (_requestLock)
            device = _devices.FirstOrDefault(item => FixedTimeTextEquals(item.TokenSha256, hash));
        return device is not null;
    }

    private async Task<bool> RateLimitAsync(HttpContext context, string deviceId)
    {
        var window = _deviceRates.GetOrAdd(deviceId, _ => new DeviceRateWindow());
        var now = DateTimeOffset.UtcNow;
        var limited = false;
        lock (window)
        {
            if (now - window.StartedAt >= TimeSpan.FromMinutes(1))
            {
                window.StartedAt = now;
                window.RequestCount = 0;
            }
            limited = ++window.RequestCount > 120;
        }
        if (!limited) return false;
        context.Response.Headers.RetryAfter = "60";
        await WriteErrorAsync(context, StatusCodes.Status429TooManyRequests, "rate_limited", "Limite de chamadas excedido; tente novamente em um minuto.");
        return true;
    }

    private async Task StopWhenIdleAsync()
    {
        await _lifecycleLock.WaitAsync();
        try { await StopIfIdleAsync(); }
        finally { _lifecycleLock.Release(); }
    }

    private async Task StopIfIdleAsync()
    {
        if (_pairingScreenOpen || _activeConnections.Count > 0 || _application is null) return;
        var app = _application;
        _application = null;
        _baseUrl = null;
        _listeningPort = 0;
        _listeningAddress = null;
        try { await app.StopAsync(); }
        finally
        {
            await app.DisposeAsync();
            _certificate?.Dispose();
            _certificate = null;
            StateChanged?.Invoke();
        }
    }

    private static int ReserveEphemeralPort()
    {
        using var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static IPAddress GetLanAddress()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up && item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(item => item.GetIPProperties().UnicastAddresses)
            .Select(item => item.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && IsPrivateIpv4(address))
            .ToArray();
        return candidates.FirstOrDefault(address => address.ToString().StartsWith("192.168.", StringComparison.Ordinal))
               ?? candidates.FirstOrDefault(address => address.ToString().StartsWith("10.", StringComparison.Ordinal))
               ?? candidates.FirstOrDefault(address => IsPrivateIpv4(address))
               ?? throw new InvalidOperationException("Não encontrei um endereço IPv4 da rede local.");
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    private static X509Certificate2 CreateServerCertificate(IPAddress ipAddress)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=JMD local API", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(ipAddress);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static string CreateSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static bool FixedTimeTextEquals(string? left, string? right)
    {
        if (left is null || right is null) return false;
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static async Task WriteErrorAsync(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = new { code, message } }, JsonOptions, context.RequestAborted);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _lifecycleLock.WaitAsync();
        try
        {
            _pairingScreenOpen = false;
            if (_application is { } app)
            {
                _application = null;
                try { await app.StopAsync(); }
                finally { await app.DisposeAsync(); }
            }
            _certificate?.Dispose();
            _certificate = null;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private sealed record PairingQrPayload(string Type, int Version, string BaseUrl, string CertificateSha256, string SessionId, string PairingSecret, DateTimeOffset ExpiresAt);
    private sealed record PairingClaim(string SessionId, string PairingSecret, string DeviceName, string AppVersion);
    private sealed record ExecuteCommandRequest(string RequestId, JsonElement Input);
    private sealed class PairingSession(string id, string secret, DateTimeOffset expiresAt)
    {
        public string Id { get; } = id;
        public string Secret { get; } = secret;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public bool Claimed { get; set; }
    }

    private sealed class PendingPairing(string id, string sessionId, string deviceName, string appVersion)
    {
        public string Id { get; } = id;
        public string SessionId { get; } = sessionId;
        public string DeviceName { get; } = deviceName;
        public string AppVersion { get; } = appVersion;
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public string State { get; set; } = "pending";
        public string? DeviceId { get; set; }
        public string? AccessToken { get; set; }
    }

    private sealed class DeviceRateWindow
    {
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
        public int RequestCount { get; set; }
    }
}
