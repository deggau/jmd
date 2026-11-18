using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using QRCoder;
using JMD.Core;

namespace JMD.App;

internal sealed record JmdroidApiCommand(
    string Id,
    string Name,
    string Description,
    string Shortcut,
    bool Available,
    string? UnavailableReason,
    IReadOnlyList<string> SearchTerms,
    bool ShowWhenUnavailable = false,
    object? InputSchema = null);
internal sealed record JmdroidCommandInfo(
    string Id,
    string Name,
    string Description,
    string Shortcut,
    bool Available,
    string? UnavailableReason,
    object? InputSchema);

internal sealed record JmdroidPairingInfo(string BaseUrl, string CertificateSha256, string QrPayload, byte[] QrPng, DateTimeOffset ExpiresAt);
internal sealed record JmdroidNetworkAddress(string InterfaceName, IPAddress Address)
{
    public string DisplayName => $"{InterfaceName} — {Address}";
}
internal sealed record JmdroidPairingRequestView(string Id, string DeviceName, string AppVersion, DateTimeOffset CreatedAt);
internal sealed record JmdroidExecutionResult(string Status, string? Text = null, string? Error = null, int? InputItemCount = null, int? OutputItemCount = null);

internal sealed class JmdroidApiServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan IdleShutdownDelay = TimeSpan.FromHours(1);
    private const long MaxAttachmentUploadBytes = 25 * 1024 * 1024;
    private const int MaxAttachmentUploadFiles = 10;
    private const long MaxSingleAttachmentBytes = 20 * 1024 * 1024;
    private const int PreferredListeningPort = 47831;
    private readonly Func<IReadOnlyList<JmdroidApiCommand>> _getCommands;
    private readonly Func<string, JsonElement, CancellationToken, Task<JmdroidExecutionResult>> _executeCommand;
    private readonly IList<JmdroidPairedDevice> _devices;
    private readonly Action _saveSettings;
    private readonly TodoTaskApiService _todoTaskApi;
    private readonly Func<string>? _getTaskAttachmentsFolder;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly object _idleStopLock = new();
    private readonly object _requestLock = new();
    private readonly object _connectionLock = new();
    private readonly ConcurrentDictionary<string, PendingPairing> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<JmdroidExecutionResult>>> _executions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DeviceRateWindow> _deviceRates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _activeConnections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentBag<WebSocket>> _deviceSockets = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<WebSocket, SemaphoreSlim> _socketSendLocks = new();
    private readonly ConcurrentDictionary<string, ActiveScreenShare> _screenShares = new(StringComparer.Ordinal);
    private CancellationTokenSource? _idleStopCancellation;
    private WebApplication? _application;
    private X509Certificate2? _certificate;
    private RSA? _certificatePrivateKey;
    private PairingSession? _pairingSession;
    private bool _pairingScreenOpen;
    private bool _disposed;
    private string? _baseUrl;
    private int _listeningPort;
    private int _httpDiagnosticPort;
    private bool _usesHttp;
    private int _preferredPort = PreferredListeningPort;
    private int _screenShareMaxFps = JmdroidScreenSharePolicy.DefaultMaxFramesPerSecond;
    private IPAddress? _listeningAddress;

    public event Action? StateChanged;
    public event Action? PairingRequestsChanged;
    public event Action<string, string, string>? ScreenShareRequested;
    public event Action<string, ReadOnlyMemory<byte>>? ScreenShareFrameReceived;
    public event Action<string, string>? ScreenShareEnded;
    public event Action<string, string>? ScreenShareStatusChanged;

    public int ScreenShareMaxFps
    {
        get => Volatile.Read(ref _screenShareMaxFps);
        set => Interlocked.Exchange(ref _screenShareMaxFps, JmdroidScreenSharePolicy.NormalizeFramesPerSecond(value));
    }

    public JmdroidApiServer(
        Func<IReadOnlyList<JmdroidApiCommand>> getCommands,
        Func<string, JsonElement, CancellationToken, Task<JmdroidExecutionResult>> executeCommand,
        IList<JmdroidPairedDevice> devices,
        Action saveSettings,
        TodoTaskApiService todoTaskApi,
        Func<string>? getTaskAttachmentsFolder = null)
    {
        _getCommands = getCommands;
        _executeCommand = executeCommand;
        _devices = devices;
        _saveSettings = saveSettings;
        _todoTaskApi = todoTaskApi;
        _getTaskAttachmentsFolder = getTaskAttachmentsFolder;
    }

    public bool IsRunning => _application is not null;
    public int ConnectedDeviceCount { get { lock (_connectionLock) return _activeConnections.Count; } }
    public string? BaseUrl => _baseUrl;
    public IPAddress? ListeningAddress => _listeningAddress;
    public int ListeningPort => _listeningPort;
    public int HttpDiagnosticPort => _httpDiagnosticPort;
    public bool UsesHttp => _usesHttp;
    public IReadOnlyList<JmdroidNetworkAddress> AvailableNetworkAddresses => GetLanAddresses();
    public IReadOnlyList<JmdroidPairedDevice> PairedDevices
    {
        get { lock (_requestLock) return _devices.ToArray(); }
    }
    public bool IsDeviceConnected(string deviceId) { lock (_connectionLock) return _activeConnections.ContainsKey(deviceId); }

    public async Task<JmdroidExecutionResult> StartScreenShareAsync(JmdroidPairedDevice device, CancellationToken cancellationToken = default)
    {
        if (_screenShares.TryGetValue(device.Id, out var existing))
        {
            ScreenShareRequested?.Invoke(device.Id, device.Name, existing.ShareId);
            return new JmdroidExecutionResult("succeeded", "A transmissão da tela já está em andamento.");
        }
        if (!_deviceSockets.TryGetValue(device.Id, out var sockets))
            return new JmdroidExecutionResult("failed", Error: "O celular não está conectado ao JMD. Abra o app e aguarde a conexão.");

        var socket = sockets.FirstOrDefault(candidate => candidate.State == WebSocketState.Open);
        if (socket is null)
            return new JmdroidExecutionResult("failed", Error: "O celular não está conectado ao JMD. Abra o app e aguarde a conexão.");

        var maxFps = ScreenShareMaxFps;
        var share = new ActiveScreenShare(Guid.NewGuid().ToString("N"), device.Name, socket, maxFps);
        if (!_screenShares.TryAdd(device.Id, share))
            return await StartScreenShareAsync(device, cancellationToken);

        try
        {
            await SendSessionMessageAsync(socket, new
            {
                type = "screen_share.start",
                shareId = share.ShareId,
                maxFps,
                maxDimension = JmdroidScreenSharePolicy.MaxDimension,
                maxFrameBytes = JmdroidScreenSharePolicy.MaxFrameBytes,
                format = "jpeg",
                quality = 65
            }, cancellationToken);
            ScreenShareRequested?.Invoke(device.Id, device.Name, share.ShareId);
            return new JmdroidExecutionResult("succeeded", "Solicitação enviada. Autorize a captura de tela no Android para iniciar a transmissão.");
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            _screenShares.TryRemove(device.Id, out _);
            ScreenShareEnded?.Invoke(device.Id, "Não foi possível solicitar a captura de tela ao celular.");
            return new JmdroidExecutionResult("failed", Error: "Não foi possível solicitar a captura de tela ao celular.");
        }
    }

    public async Task StopScreenShareAsync(string deviceId)
    {
        if (!_screenShares.TryRemove(deviceId, out var share)) return;
        try
        {
            if (share.Socket.State == WebSocketState.Open)
                await SendSessionMessageAsync(share.Socket, new { type = "screen_share.stop", shareId = share.ShareId }, CancellationToken.None);
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException or OperationCanceledException) { }
        ScreenShareEnded?.Invoke(deviceId, "Compartilhamento encerrado.");
    }

    public async Task<JmdroidPairingInfo> OpenPairingScreenAsync(
        IPAddress? preferredAddress = null,
        bool useHttp = false,
        CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancelIdleShutdown();
            if (preferredAddress is not null && !GetLanAddresses().Any(candidate => candidate.Address.Equals(preferredAddress)))
                throw new InvalidOperationException("O endereço selecionado não pertence a uma interface IPv4 privada ativa.");

            var requestedAddress = preferredAddress ?? GetLanAddress();
            if (_application is not null &&
                (!_listeningAddress!.Equals(requestedAddress) || _usesHttp != useHttp))
            {
                if (_activeConnections.Count > 0)
                    throw new InvalidOperationException("Desconecte os aparelhos antes de trocar a interface de rede.");
                _pairingScreenOpen = false;
                _pairingSession = null;
                CancelIdleShutdown();
                await StopIfIdleAsync(force: true);
            }

            if (_application is null)
                await StartListenerAsync(requestedAddress, useHttp, cancellationToken);

            _pairingScreenOpen = true;
            foreach (var pending in _pending.Values) pending.State = "expired";
            var ipAddress = _listeningAddress ?? GetLanAddress();
            var fingerprint = _certificate is null
                ? string.Empty
                : Convert.ToHexString(SHA256.HashData(_certificate.RawData));
            var scheme = _usesHttp ? "http" : "https";
            var baseUrl = _baseUrl ?? $"{scheme}://{ipAddress}:{_listeningPort}";
            _baseUrl = baseUrl;
            var session = new PairingSession(
                Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                CreateSecret(),
                DateTimeOffset.UtcNow.Add(PairingLifetime));
            _pairingSession = session;
            var payload = JsonSerializer.Serialize(new PairingQrPayload(
                "jmd-pairing", 1, baseUrl, fingerprint, session.Id, session.Secret, session.ExpiresAt), JsonOptions);
            using var qrData = QRCodeGenerator.GenerateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
            var qrPng = new PngByteQRCode(qrData).GetGraphic(8);
            StateChanged?.Invoke();
            return new JmdroidPairingInfo(baseUrl, fingerprint, payload, qrPng, session.ExpiresAt);
        }
        finally { _lifecycleLock.Release(); }
    }

    public async Task StartForPairedDevicesAsync(bool useHttp, CancellationToken cancellationToken = default)
    {
        if (PairedDevices.Count == 0) return;
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancelIdleShutdown();
            if (_application is null)
                await StartListenerAsync(GetLanAddress(), useHttp, cancellationToken);
            _pairingScreenOpen = false;
            _pairingSession = null;
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
        await StopScreenShareAsync(deviceId);
        if (_deviceSockets.TryRemove(deviceId, out var sockets))
        {
            foreach (var socket in sockets)
            {
                try { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Device revoked", CancellationToken.None); }
                catch (WebSocketException) { }
            }
        }
        StateChanged?.Invoke();
        ScheduleStopWhenIdle();
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
            ScheduleStopWhenIdle();
        }
        finally { _lifecycleLock.Release(); }
    }

    private async Task StartListenerAsync(IPAddress listeningAddress, bool useHttp, CancellationToken cancellationToken)
    {
        var port = _preferredPort;
        async Task<(WebApplication App, X509Certificate2? Certificate, RSA? PrivateKey)> StartOnPortAsync(int candidatePort)
        {
            X509Certificate2? certificate = null;
            RSA? privateKey = null;
            if (!useHttp) (certificate, privateKey) = CreateServerCertificate(listeningAddress);
            WebApplication? app = null;
            try
            {
                var builder = WebApplication.CreateSlimBuilder();
                builder.Logging.ClearProviders();
                builder.Logging.SetMinimumLevel(LogLevel.Debug);
                builder.Logging.AddProvider(new JmdroidLoggerProvider());
                builder.WebHost.ConfigureKestrel(options =>
                {
                    options.Limits.MaxRequestBodySize = 1_048_576;
                    // Bind only the interface selected for this pairing session. Binding every
                    // detected adapter makes startup depend on VPN, virtual, and policy-managed
                    // interfaces that the phone may not be able to reach.
                    foreach (var address in new[] { listeningAddress, IPAddress.Loopback }.Distinct())
                        options.Listen(address, candidatePort, listen =>
                        {
                            if (certificate is not null) listen.UseHttps(certificate);
                        });
                    // Diagnostic only: expose the same status page over plain HTTP on loopback.
                    // Port 0 lets Kestrel choose a free port, and this endpoint is never reachable
                    // from the LAN or included in the Android pairing QR.
                    options.Listen(IPAddress.Loopback, 0);
                });
                app = builder.Build();
                app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
                app.Use(async (context, next) =>
                {
                    CancelIdleShutdown();
                    try { await next(); }
                    finally { ScheduleStopWhenIdle(); }
                });
                MapRoutes(app);
                await app.StartAsync(cancellationToken);
                return (app, certificate, privateKey);
            }
            catch
            {
                if (app is not null) await app.DisposeAsync();
                certificate?.Dispose();
                privateKey?.Dispose();
                throw;
            }
        }

        (WebApplication App, X509Certificate2? Certificate, RSA? PrivateKey) started;
        try
        {
            started = await StartOnPortAsync(port);
        }
        catch (Exception exception) when (IsPortUnavailable(exception))
        {
            port = ReserveEphemeralPort(listeningAddress);
            started = await StartOnPortAsync(port);
        }

        _preferredPort = port;
        _application = started.App;
        _certificate = started.Certificate;
        _certificatePrivateKey = started.PrivateKey;
        _listeningPort = port;
        _listeningAddress = listeningAddress;
        _usesHttp = useHttp;
        var serverAddresses = started.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _httpDiagnosticPort = serverAddresses?.Addresses
            .Select(address => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri : null)
            .Where(uri => uri is not null && uri.Scheme == Uri.UriSchemeHttp &&
                          IPAddress.TryParse(uri.Host, out var parsedAddress) && IPAddress.IsLoopback(parsedAddress))
            .Select(uri => uri!.Port)
            .FirstOrDefault() ?? 0;
        _baseUrl = $"{(useHttp ? "http" : "https")}://{listeningAddress}:{port}";
        StateChanged?.Invoke();
    }

    private void MapRoutes(WebApplication app)
    {
        app.MapGet("/", async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync("""
                <!doctype html>
                <html lang="pt-BR">
                <head>
                  <meta charset="utf-8">
                  <meta name="viewport" content="width=device-width, initial-scale=1">
                  <title>JMD · Serviço de pareamento</title>
                  <style>
                    :root { color-scheme: dark; font: 16px/1.5 system-ui, sans-serif; background: #17191f; color: #f4f5f7; }
                    body { box-sizing: border-box; min-height: 100vh; margin: 0; display: grid; place-items: center; padding: 24px; }
                    main { width: min(100%, 520px); box-sizing: border-box; padding: 32px; border: 1px solid #393d47; border-radius: 16px; background: #22252d; }
                    h1 { margin: 0 0 8px; font-size: 28px; }
                    p { color: #bec3cc; }
                    .status { display: inline-flex; align-items: center; gap: 9px; margin-top: 12px; padding: 9px 13px; border-radius: 999px; background: #303d2b; color: #b4f36a; font-weight: 650; }
                    .dot { width: 9px; height: 9px; border-radius: 50%; background: currentColor; }
                    a { color: #b4f36a; }
                  </style>
                </head>
                <body>
                  <main>
                    <h1>JMD · Pareamento com celular</h1>
                    <p>Esta página confirma que o serviço local do JMD está respondendo neste endereço.</p>
                    <div class="status"><span class="dot"></span>Serviço ativo</div>
                    <p>Para conectar um celular, use o QR code exibido no JMDroid e aprove o pedido no aplicativo.</p>
                    <p>Verificação técnica: <a href="/health">/health</a></p>
                  </main>
                </body>
                </html>
                """, context.RequestAborted);
        });

        app.MapGet("/health", () => Results.Json(new
        {
            status = "ok",
            service = "jmdroid-pairing",
            timestamp = DateTimeOffset.UtcNow
        }));

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
                () => commandId == "screenShare"
                    ? StartScreenShareAsync(device, CancellationToken.None)
                    : _executeCommand(commandId, body.Input.Clone(), CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));
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

        async Task<JmdroidPairedDevice?> AuthorizeTaskApiAsync(HttpContext context)
        {
            if (!TryAuthenticate(context, out var device))
            {
                await WriteErrorAsync(context, StatusCodes.Status401Unauthorized, "unauthorized", "Token ausente ou inválido.");
                return null;
            }
            if (await RateLimitAsync(context, device!.Id)) return null;
            return device;
        }

        app.MapGet("/api/v1/tasks", async context =>
        {
            if (await AuthorizeTaskApiAsync(context) is null) return;
            var status = context.Request.Query["status"].FirstOrDefault() ?? "open";
            try
            {
                var items = _todoTaskApi.GetTasks(status);
                await context.Response.WriteAsJsonAsync(new { items, count = items.Count, status }, JsonOptions, context.RequestAborted);
            }
            catch (ArgumentException exception)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_input", exception.Message);
            }
        });

        app.MapGet("/api/v1/tasks/{taskId}", async context =>
        {
            if (await AuthorizeTaskApiAsync(context) is null) return;
            var taskId = Convert.ToString(context.Request.RouteValues["taskId"]);
            var task = taskId is null ? null : _todoTaskApi.GetTask(taskId);
            if (task is null)
            {
                await WriteErrorAsync(context, StatusCodes.Status404NotFound, "task_not_found", "Tarefa inexistente.");
                return;
            }
            await context.Response.WriteAsJsonAsync(task, JsonOptions, context.RequestAborted);
        });

        app.MapPost("/api/v1/tasks", async context =>
        {
            var device = await AuthorizeTaskApiAsync(context);
            if (device is null) return;
            var body = await ReadJsonBodyAsync<TodoTaskApiTaskInput>(context);
            if (body is null)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_input", "Informe os dados da tarefa.");
                return;
            }
            try
            {
                var task = _todoTaskApi.Create(body, "JMDroid", device.Name);
                context.Response.StatusCode = StatusCodes.Status201Created;
                context.Response.Headers.Location = $"/api/v1/tasks/{Uri.EscapeDataString(task.Id)}";
                await context.Response.WriteAsJsonAsync(task, JsonOptions, context.RequestAborted);
            }
            catch (ArgumentException exception)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_input", exception.Message);
            }
        });

        app.MapPut("/api/v1/tasks/{taskId}", async context =>
        {
            var device = await AuthorizeTaskApiAsync(context);
            if (device is null) return;
            var taskId = Convert.ToString(context.Request.RouteValues["taskId"]);
            var body = await ReadJsonBodyAsync<TodoTaskApiTaskInput>(context);
            if (taskId is null || body is null)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_input", "Informe o identificador e os dados da tarefa.");
                return;
            }
            try
            {
                var task = _todoTaskApi.Update(taskId, body, "JMDroid", device.Name);
                if (task is null) await WriteErrorAsync(context, StatusCodes.Status404NotFound, "task_not_found", "Tarefa inexistente.");
                else await context.Response.WriteAsJsonAsync(task, JsonOptions, context.RequestAborted);
            }
            catch (ArgumentException exception)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_input", exception.Message);
            }
        });

        app.MapPost("/api/v1/tasks/{taskId}/attachments", async context =>
        {
            var device = await AuthorizeTaskApiAsync(context);
            if (device is null) return;
            var taskId = Convert.ToString(context.Request.RouteValues["taskId"]);
            var task = taskId is null ? null : _todoTaskApi.GetTask(taskId);
            if (task is null)
            {
                await WriteErrorAsync(context, StatusCodes.Status404NotFound, "task_not_found", "Tarefa inexistente.");
                return;
            }

            var requestSizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (requestSizeFeature is { IsReadOnly: false }) requestSizeFeature.MaxRequestBodySize = MaxAttachmentUploadBytes;
            var contentType = context.Request.ContentType;
            if (!context.Request.HasFormContentType || contentType is null || !contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_input", "Envie os anexos como multipart/form-data no campo files.");
                return;
            }

            var savedPaths = new List<string>();
            var attachmentsPersisted = false;
            try
            {
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                var files = form.Files;
                if (files.Count is < 1 or > MaxAttachmentUploadFiles)
                {
                    await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_input", $"Envie de 1 a {MaxAttachmentUploadFiles} arquivos.");
                    return;
                }
                if (files.Any(file => file.Length <= 0 || file.Length > MaxSingleAttachmentBytes) ||
                    files.Sum(file => file.Length) > MaxAttachmentUploadBytes - 1024 * 1024)
                {
                    await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "attachment_too_large", "Cada arquivo deve ter até 20 MiB e o envio total até 24 MiB.");
                    return;
                }

                var configuredFolder = _getTaskAttachmentsFolder?.Invoke();
                var rootFolder = string.IsNullOrWhiteSpace(configuredFolder)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JMD", "TaskAttachments")
                    : configuredFolder;
                var taskFolder = Path.Combine(Path.GetFullPath(rootFolder),
                    task.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    $"Task-{Math.Max(0, task.TaskNumber):D6}");
                Directory.CreateDirectory(taskFolder);

                foreach (var file in files)
                {
                    var fileName = SanitizeUploadedFileName(file.FileName);
                    var destination = GetAvailableAttachmentPath(taskFolder, fileName);
                    savedPaths.Add(destination);
                    await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        bufferSize: 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await file.CopyToAsync(output, context.RequestAborted);
                }

                var updated = _todoTaskApi.AddAttachments(task.Id, savedPaths, "JMDroid", device.Name);
                if (updated is null)
                {
                    foreach (var path in savedPaths) TryDeleteFile(path);
                    savedPaths.Clear();
                    await WriteErrorAsync(context, StatusCodes.Status404NotFound, "task_not_found", "Tarefa inexistente.");
                    return;
                }
                attachmentsPersisted = true;
                await context.Response.WriteAsJsonAsync(new { task = updated, uploaded = savedPaths }, JsonOptions, context.RequestAborted);
            }
            catch (Microsoft.AspNetCore.Http.BadHttpRequestException exception)
            {
                if (!attachmentsPersisted) foreach (var path in savedPaths) TryDeleteFile(path);
                await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "attachment_too_large", exception.Message);
            }
            catch (OperationCanceledException)
            {
                if (!attachmentsPersisted) foreach (var path in savedPaths) TryDeleteFile(path);
                throw;
            }
            catch (InvalidDataException exception)
            {
                if (!attachmentsPersisted) foreach (var path in savedPaths) TryDeleteFile(path);
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_input", exception.Message);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                if (!attachmentsPersisted) foreach (var path in savedPaths) TryDeleteFile(path);
                CriticalErrorLog.Write("Salvar anexo enviado pelo JMDroid", exception);
                await WriteErrorAsync(context, StatusCodes.Status500InternalServerError, "attachment_storage_failed", "Não foi possível salvar os anexos neste computador.");
            }
        });

        app.MapPut("/api/v1/tasks/{taskId}/checklist/{index:int}", async context =>
        {
            var device = await AuthorizeTaskApiAsync(context);
            if (device is null) return;
            var taskId = Convert.ToString(context.Request.RouteValues["taskId"]);
            var body = await ReadJsonBodyAsync<TodoTaskApiChecklistCompletion>(context);
            if (taskId is null || body is null)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_input", "Informe o estado de conclusão da etapa.");
                return;
            }
            try
            {
                var task = _todoTaskApi.SetChecklistStep(taskId, Convert.ToInt32(context.Request.RouteValues["index"]), body.IsCompleted, "JMDroid", device.Name);
                if (task is null) await WriteErrorAsync(context, StatusCodes.Status404NotFound, "task_not_found", "Tarefa inexistente.");
                else await context.Response.WriteAsJsonAsync(task, JsonOptions, context.RequestAborted);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_input", exception.Message);
            }
        });

        async Task SetTaskCompletionAsync(HttpContext context, bool completed)
        {
            var device = await AuthorizeTaskApiAsync(context);
            if (device is null) return;
            var taskId = Convert.ToString(context.Request.RouteValues["taskId"]);
            var task = taskId is null ? null : _todoTaskApi.SetCompleted(taskId, completed, "JMDroid", device.Name);
            if (task is null) await WriteErrorAsync(context, StatusCodes.Status404NotFound, "task_not_found", "Tarefa inexistente.");
            else await context.Response.WriteAsJsonAsync(task, JsonOptions, context.RequestAborted);
        }
        app.MapPost("/api/v1/tasks/{taskId}/complete", context => SetTaskCompletionAsync(context, true));
        app.MapPost("/api/v1/tasks/{taskId}/reopen", context => SetTaskCompletionAsync(context, false));

        app.MapPost("/api/v1/tasks/{taskId}/pause", async context =>
        {
            var device = await AuthorizeTaskApiAsync(context);
            if (device is null) return;
            var taskId = Convert.ToString(context.Request.RouteValues["taskId"]);
            var body = context.Request.ContentLength == 0
                ? new TodoTaskApiPauseInput()
                : await ReadJsonBodyAsync<TodoTaskApiPauseInput>(context);
            if (body is null)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_input", "O corpo da pausa não é um JSON válido.");
                return;
            }
            if (body.Reason is null || body.Responsible is null || body.Reason.Length > 1000 || body.Responsible.Length > 200)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_input", "Motivo ou responsável excede o tamanho permitido.");
                return;
            }
            try
            {
                var task = taskId is null ? null : _todoTaskApi.Pause(taskId, body.Reason, body.Responsible, "JMDroid", device.Name);
                if (task is null) await WriteErrorAsync(context, StatusCodes.Status404NotFound, "task_not_found", "Tarefa inexistente.");
                else await context.Response.WriteAsJsonAsync(task, JsonOptions, context.RequestAborted);
            }
            catch (InvalidOperationException exception)
            {
                await WriteErrorAsync(context, StatusCodes.Status409Conflict, "task_state_conflict", exception.Message);
            }
        });

        app.MapPost("/api/v1/tasks/{taskId}/unpause", async context =>
        {
            var device = await AuthorizeTaskApiAsync(context);
            if (device is null) return;
            var taskId = Convert.ToString(context.Request.RouteValues["taskId"]);
            var task = taskId is null ? null : _todoTaskApi.Unpause(taskId, "JMDroid", device.Name);
            if (task is null) await WriteErrorAsync(context, StatusCodes.Status404NotFound, "task_not_found", "Tarefa inexistente.");
            else await context.Response.WriteAsJsonAsync(task, JsonOptions, context.RequestAborted);
        });

        app.MapDelete("/api/v1/tasks/{taskId}", async context =>
        {
            var device = await AuthorizeTaskApiAsync(context);
            if (device is null) return;
            var taskId = Convert.ToString(context.Request.RouteValues["taskId"]);
            var task = taskId is null ? null : _todoTaskApi.Delete(taskId, "JMDroid", device.Name);
            if (task is null) await WriteErrorAsync(context, StatusCodes.Status404NotFound, "task_not_found", "Tarefa inexistente.");
            else context.Response.StatusCode = StatusCodes.Status204NoContent;
        });

        app.MapGet("/api/v1/tasks/history", async context =>
        {
            if (await AuthorizeTaskApiAsync(context) is null) return;
            var query = context.Request.Query["q"].ToString().Trim();
            var pageProvided = context.Request.Query.ContainsKey("page");
            var sizeProvided = context.Request.Query.ContainsKey("pageSize");
            var pageValid = int.TryParse(context.Request.Query["page"], out var page);
            var sizeValid = int.TryParse(context.Request.Query["pageSize"], out var pageSize);
            if (!sizeProvided) pageSize = 30;
            if (query.Length > 200 || (pageProvided && !pageValid) ||
                (sizeProvided && !sizeValid) || page < 0 || pageSize is < 1 or > 100)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_input", "Use page >= 0 e pageSize entre 1 e 100.");
                return;
            }
            try
            {
                var result = _todoTaskApi.GetTaskHistory(query, page, pageSize);
                await context.Response.WriteAsJsonAsync(result, JsonOptions, context.RequestAborted);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_input", exception.Message);
            }
        });

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
            await SendSessionBytesAsync(socket, ready, context.RequestAborted);
            var buffer = new byte[64 * 1024];
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketMessageType? messageType = null;
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
                    if (received.MessageType == WebSocketMessageType.Close) break;
                    messageType ??= received.MessageType;
                    var maximum = messageType == WebSocketMessageType.Binary
                        ? JmdroidScreenSharePolicy.MaxFrameBytes
                        : 16 * 1024;
                    if (message.Length + received.Count > maximum)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "Screen share message too large", CancellationToken.None);
                        return;
                    }
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                if (received.MessageType == WebSocketMessageType.Close) break;
                if (messageType == WebSocketMessageType.Binary)
                {
                    if (_screenShares.TryGetValue(device.Id, out var share) && ReferenceEquals(share.Socket, socket))
                    {
                        var frame = message.ToArray();
                        if (JmdroidScreenSharePolicy.IsValidJpegFrame(frame) && share.TryAcceptFrame(DateTimeOffset.UtcNow))
                            ScreenShareFrameReceived?.Invoke(device.Id, frame);
                    }
                }
                else if (messageType == WebSocketMessageType.Text)
                    HandleScreenShareControlMessage(device.Id, socket, message.ToArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            if (_screenShares.TryGetValue(device.Id, out var share) && ReferenceEquals(share.Socket, socket))
            {
                _screenShares.TryRemove(device.Id, out _);
                ScreenShareEnded?.Invoke(device.Id, "A conexão com o Android foi encerrada.");
            }
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
            ScheduleStopWhenIdle();
        }
    }

    private void HandleScreenShareControlMessage(string deviceId, WebSocket socket, byte[] message)
    {
        try
        {
            using var document = JsonDocument.Parse(message);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeValue) || typeValue.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("shareId", out var idValue) || idValue.ValueKind != JsonValueKind.String ||
                !_screenShares.TryGetValue(deviceId, out var share) || !ReferenceEquals(share.Socket, socket)) return;
            var type = typeValue.GetString();
            if (idValue.GetString() != share.ShareId) return;

            if (type == "screen_share.started")
                ScreenShareStatusChanged?.Invoke(deviceId, "Captura autorizada · aguardando o primeiro quadro…");
            else if (type == "screen_share.stopped")
            {
                _screenShares.TryRemove(deviceId, out _);
                ScreenShareEnded?.Invoke(deviceId, "O compartilhamento foi encerrado no Android.");
            }
            else if (type == "screen_share.error")
            {
                var reason = root.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.String
                    ? messageValue.GetString()
                    : null;
                _screenShares.TryRemove(deviceId, out _);
                ScreenShareEnded?.Invoke(deviceId, string.IsNullOrWhiteSpace(reason)
                    ? "O Android não conseguiu iniciar o compartilhamento de tela."
                    : $"O Android não conseguiu iniciar o compartilhamento: {reason}");
            }
            else if (type == "screen_share.stop")
            {
                _screenShares.TryRemove(deviceId, out _);
                ScreenShareEnded?.Invoke(deviceId, "O compartilhamento foi encerrado no Android.");
            }
        }
        catch (JsonException) { }
    }

    private async Task SendSessionMessageAsync(WebSocket socket, object message, CancellationToken cancellationToken)
        => await SendSessionBytesAsync(socket, JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions), cancellationToken);

    private async Task SendSessionBytesAsync(WebSocket socket, byte[] message, CancellationToken cancellationToken)
    {
        var sendLock = _socketSendLocks.GetValue(socket, _ => new SemaphoreSlim(1, 1));
        await sendLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State != WebSocketState.Open)
                throw new WebSocketException("A sessão WebSocket está desconectada.");
            await socket.SendAsync(message, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally { sendLock.Release(); }
    }

    private IReadOnlyList<JmdroidCommandInfo> FilterCommands(string query, bool includeUnavailable)
    {
        var candidates = _getCommands().Where(item => includeUnavailable || item.Available || item.ShowWhenUnavailable);
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
            command.Available ? command.InputSchema ?? new
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

    private static string SanitizeUploadedFileName(string fileName)
    {
        var name = Path.GetFileName((fileName ?? string.Empty).Replace('\\', '/')).Trim().Trim('.');
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        name = new string(name.Select(character => char.IsControl(character) || invalid.Contains(character) ? '_' : character).ToArray());
        if (string.IsNullOrWhiteSpace(name)) name = "anexo.bin";
        if (name.Length > 180)
        {
            var extension = Path.GetExtension(name);
            var stemLength = Math.Max(1, 180 - extension.Length);
            name = Path.GetFileNameWithoutExtension(name)[..Math.Min(stemLength, Path.GetFileNameWithoutExtension(name).Length)] + extension;
        }
        return name;
    }

    private static string GetAvailableAttachmentPath(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var suffix = 2; ; suffix++)
        {
            candidate = Path.Combine(folder, $"{stem} ({suffix}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void CancelIdleShutdown()
    {
        CancellationTokenSource? pending;
        lock (_idleStopLock)
        {
            pending = _idleStopCancellation;
            _idleStopCancellation = null;
        }
        pending?.Cancel();
    }

    private void ScheduleStopWhenIdle()
    {
        if (_pairingScreenOpen || ConnectedDeviceCount > 0 || PairedDevices.Count > 0 || _application is null) return;
        var cancellation = new CancellationTokenSource();
        lock (_idleStopLock)
        {
            _idleStopCancellation?.Cancel();
            _idleStopCancellation = cancellation;
        }
        _ = StopWhenIdleAfterGracePeriodAsync(cancellation);
    }

    private async Task StopWhenIdleAfterGracePeriodAsync(CancellationTokenSource cancellation)
    {
        var lockAcquired = false;
        try
        {
            await Task.Delay(IdleShutdownDelay, cancellation.Token);
            await _lifecycleLock.WaitAsync(cancellation.Token);
            lockAcquired = true;
            lock (_idleStopLock)
            {
                if (!ReferenceEquals(_idleStopCancellation, cancellation)) return;
                _idleStopCancellation = null;
            }
            await StopIfIdleAsync();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (lockAcquired) _lifecycleLock.Release();
            cancellation.Dispose();
        }
    }

    private async Task StopIfIdleAsync(bool force = false)
    {
        if (_pairingScreenOpen || ConnectedDeviceCount > 0 || (!force && PairedDevices.Count > 0) || _application is null) return;
        var app = _application;
        _application = null;
        _baseUrl = null;
        _listeningPort = 0;
        _httpDiagnosticPort = 0;
        _usesHttp = false;
        _listeningAddress = null;
        try { await app.StopAsync(); }
        finally
        {
            await app.DisposeAsync();
            _certificate?.Dispose();
            _certificate = null;
            _certificatePrivateKey?.Dispose();
            _certificatePrivateKey = null;
            StateChanged?.Invoke();
        }
    }

    private static bool IsPortUnavailable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is SocketException socketException &&
                socketException.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
                return true;
        return false;
    }

    private static int ReserveEphemeralPort(IPAddress address)
    {
        using var listener = new TcpListener(address, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static IReadOnlyList<JmdroidNetworkAddress> GetLanAddresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up &&
                           item.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .SelectMany(item =>
            {
                var properties = item.GetIPProperties();
                var hasIpv4Gateway = properties.GatewayAddresses.Any(gateway =>
                    gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any));
                var interfacePriority = item.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => 300,
                    NetworkInterfaceType.Ethernet => 200,
                    _ => 0
                };
                var priority = (hasIpv4Gateway ? 100 : 0) + interfacePriority;
                return properties.UnicastAddresses
                    .Select(unicast => (Address: unicast.Address, Priority: priority, InterfaceName: item.Name));
            })
            .Where(candidate => candidate.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivateIpv4(candidate.Address))
            .OrderByDescending(candidate => candidate.Priority)
            .Select(candidate => new JmdroidNetworkAddress(candidate.InterfaceName, candidate.Address))
            .DistinctBy(candidate => candidate.Address)
            .ToArray();
    }

    private static IPAddress GetLanAddress()
    {
        return GetLanAddresses().Select(candidate => candidate.Address).FirstOrDefault()
               ?? throw new InvalidOperationException("Não encontrei um endereço IPv4 da rede local.");
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    private static (X509Certificate2 Certificate, RSA PrivateKey) CreateServerCertificate(IPAddress ipAddress)
    {
        var rsa = RSA.Create(2048);
        try
        {
            var request = new CertificateRequest("CN=JMD local API", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            foreach (var address in GetLanAddresses().Select(item => item.Address).Append(ipAddress).Distinct())
                names.AddIpAddress(address);
            names.AddIpAddress(IPAddress.Loopback);
            names.AddDnsName("localhost");
            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var generatedCertificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            var pfx = generatedCertificate.Export(X509ContentType.Pfx);
            try
            {
                // Materialize a separate ephemeral key handle for SslStream. This avoids relying
                // on the temporary key handle created by CertificateRequest on Windows.
                var serverCertificate = X509CertificateLoader.LoadPkcs12(
                    pfx,
                    (string?)null,
                    X509KeyStorageFlags.EphemeralKeySet);
                return (serverCertificate, rsa);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    private static string CreateSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private sealed class JmdroidLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new JmdroidLogger(categoryName);
        public void Dispose() { }
    }

    private sealed class JmdroidLogger(string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning ||
            categoryName.StartsWith("Microsoft.AspNetCore.Server.Kestrel", StringComparison.Ordinal) && logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (exception is not null || logLevel >= LogLevel.Warning)
                CriticalErrorLog.Write($"JMDroid API · {categoryName} · evento {eventId.Id}", exception?.ToString() ?? message);
        }
    }

    private static bool FixedTimeTextEquals(string? left, string? right)
    {
        if (left is null || right is null) return false;
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static async Task<T?> ReadJsonBodyAsync<T>(HttpContext context)
    {
        try { return await context.Request.ReadFromJsonAsync<T>(JsonOptions, context.RequestAborted); }
        catch (JsonException) { return default; }
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
        CancelIdleShutdown();
        foreach (var deviceId in _screenShares.Keys.ToArray())
            await StopScreenShareAsync(deviceId);
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
            _certificatePrivateKey?.Dispose();
            _certificatePrivateKey = null;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private sealed record PairingQrPayload(string Type, int Version, string BaseUrl, string CertificateSha256, string SessionId, string PairingSecret, DateTimeOffset ExpiresAt);
    private sealed record PairingClaim(string SessionId, string PairingSecret, string DeviceName, string AppVersion);
    private sealed record ExecuteCommandRequest(string RequestId, JsonElement Input);
    private sealed class ActiveScreenShare(string shareId, string deviceName, WebSocket socket, int maxFps)
    {
        private readonly JmdroidScreenShareFrameThrottle _frameThrottle = new(maxFps);

        public string ShareId { get; } = shareId;
        public string DeviceName { get; } = deviceName;
        public WebSocket Socket { get; } = socket;

        public bool TryAcceptFrame(DateTimeOffset now) => _frameThrottle.TryAccept(now);
    }
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
