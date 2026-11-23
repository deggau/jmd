using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JMD.Core;

namespace JMD.App;

internal sealed class DesktopTaskApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
    }) { Timeout = TimeSpan.FromSeconds(15) };
    private readonly UserSettings _settings;
    private readonly SettingsStore _settingsStore;

    public DesktopTaskApiClient(UserSettings settings, SettingsStore settingsStore)
    {
        _settings = settings;
        _settingsStore = settingsStore;
    }

    public async Task<IReadOnlyList<TodoTaskEntry>> GetTasksAsync(DesktopPairedPeer peer, string status, CancellationToken cancellationToken = default)
    {
        try { return await GetTasksAtAddressAsync(peer, peer.BaseUrl, status, cancellationToken); }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException or CryptographicException or SocketException or NetworkInformationException or InvalidOperationException)
        {
            var recovered = await FindPeerAsync(peer, cancellationToken);
            if (recovered is null) throw new HttpRequestException($"Não foi possível localizar {peer.Name} na rede privada.", exception);
            peer.BaseUrl = recovered.BaseUrl;
            peer.Name = recovered.ComputerName;
            _settingsStore.Save(_settings);
            return await GetTasksAtAddressAsync(peer, peer.BaseUrl, status, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<ConversionHistoryEntry>> GetTaskHistoryAsync(DesktopPairedPeer peer, CancellationToken cancellationToken = default)
    {
        var result = new List<ConversionHistoryEntry>();
        for (var pageNumber = 0; ; pageNumber++)
        {
            var path = $"/api/v1/tasks/history?q=Tarefa&page={pageNumber}&pageSize=100";
            using var response = await SendAsync(peer, HttpMethod.Get, path, null, cancellationToken);
            var page = await EnsureSuccessfulAsync<TodoTaskApiPage<ConversionHistoryEntry>>(response, cancellationToken)
                ?? throw new InvalidDataException($"{peer.Name} não retornou o histórico de tarefas.");
            result.AddRange(page.Items);
            if (result.Count >= page.TotalCount || page.Items.Count == 0) break;
        }
        return result;
    }

    public async Task<TodoTaskEntry> CreateTaskAsync(DesktopPairedPeer peer, TodoTaskApiTaskInput task, CancellationToken cancellationToken = default)
    {
        var result = await SendJsonAsync<TodoTaskEntry>(peer, HttpMethod.Post, "/api/v1/tasks", task, cancellationToken);
        return result ?? throw new InvalidDataException("O outro JMD não retornou a tarefa criada.");
    }

    public async Task<TodoTaskEntry> ImportTaskAsync(DesktopPairedPeer peer, TodoTaskEntry task, CancellationToken cancellationToken = default)
    {
        var result = await SendJsonAsync<TodoTaskEntry>(peer, HttpMethod.Post, "/api/v1/tasks/import", task, cancellationToken);
        return result ?? throw new InvalidDataException("O outro JMD não confirmou a tarefa transferida.");
    }

    public async Task DeleteTaskAsync(DesktopPairedPeer peer, string taskId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(peer, HttpMethod.Delete, "/api/v1/tasks/" + Uri.EscapeDataString(taskId), null, cancellationToken);
        await EnsureSuccessfulAsync(response, cancellationToken);
    }

    public async Task UploadAttachmentsAsync(DesktopPairedPeer peer, string taskId, IReadOnlyList<string> paths,
        IReadOnlyList<string>? existingRemotePaths = null, CancellationToken cancellationToken = default)
    {
        var knownNames = (existingRemotePaths ?? []).Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!File.Exists(path)) throw new FileNotFoundException($"O anexo não foi encontrado e não pode ser transferido: {path}", path);
            var info = new FileInfo(path);
            if (knownNames.Contains(info.Name)) continue;
            if (info.Length <= 0 || info.Length > 20 * 1024 * 1024)
                throw new InvalidOperationException($"O anexo {info.Name} excede o limite de 20 MiB ou está vazio.");
            var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(await File.ReadAllBytesAsync(path, cancellationToken));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(fileContent, "files", info.Name);
            using var response = await SendAsync(peer, HttpMethod.Post,
                "/api/v1/tasks/" + Uri.EscapeDataString(taskId) + "/attachments", content, cancellationToken);
            await EnsureSuccessfulAsync(response, cancellationToken);
            knownNames.Add(info.Name);
        }
    }

    private async Task<IReadOnlyList<TodoTaskEntry>> GetTasksAtAddressAsync(DesktopPairedPeer peer, string baseUrl, string status, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(peer, HttpMethod.Get, "/api/v1/tasks?status=" + Uri.EscapeDataString(status), null, cancellationToken, baseUrl);
        var page = await EnsureSuccessfulAsync<TaskListResponse>(response, cancellationToken);
        return page?.Items ?? [];
    }

    private async Task<T?> SendJsonAsync<T>(DesktopPairedPeer peer, HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(peer, method, path, JsonContent.Create(body, options: JsonOptions), cancellationToken);
        return await EnsureSuccessfulAsync<T>(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(DesktopPairedPeer peer, HttpMethod method, string path,
        HttpContent? content, CancellationToken cancellationToken, string? baseUrl = null)
    {
        var requestUri = new Uri((baseUrl ?? peer.BaseUrl).TrimEnd('/') + path);
        var body = content is null ? [] : await content.ReadAsByteArrayAsync(cancellationToken);
        var request = new HttpRequestMessage(method, requestUri);
        if (content is not null)
        {
            request.Content = new ByteArrayContent(body);
            foreach (var header in content.Headers)
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var bodyHash = Convert.ToHexString(SHA256.HashData(body));
        var canonical = $"request\n{method.Method}\n{requestUri.PathAndQuery}\n{timestamp}\n{nonce}\n{bodyHash}";
        request.Headers.TryAddWithoutValidation("X-JMD-Peer-Id", DesktopPeerIdentity.PeerId(_settings.DesktopIdentityPublicKey));
        request.Headers.TryAddWithoutValidation("X-JMD-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-JMD-Nonce", nonce);
        request.Headers.TryAddWithoutValidation("X-JMD-Signature", DesktopPeerIdentity.Sign(_settings, canonical));

        try
        {
            var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var responseContent = new ByteArrayContent(responseBytes);
            foreach (var header in response.Content.Headers) responseContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Content.Dispose();
            response.Content = responseContent;
            var expectedPeerId = DesktopPeerIdentity.PeerId(peer.PublicKey);
            var responsePeerId = response.Headers.TryGetValues("X-JMD-Peer-Id", out var peerValues) ? peerValues.FirstOrDefault() : null;
            var signature = response.Headers.TryGetValues("X-JMD-Response-Signature", out var signatureValues) ? signatureValues.FirstOrDefault() : null;
            var responseDigest = Convert.ToHexString(SHA256.HashData(responseBytes));
            var responseCanonical = $"response\n{method.Method}\n{requestUri.PathAndQuery}\n{(int)response.StatusCode}\n{nonce}\n{responseDigest}";
            if (responsePeerId != expectedPeerId || signature is null || !DesktopPeerIdentity.Verify(peer.PublicKey, responseCanonical, signature))
            {
                response.Dispose();
                throw new CryptographicException($"A resposta de {peer.Name} não pôde ser validada com a chave pública pareada.");
            }
            return response;
        }
        finally
        {
            request.Dispose();
            content?.Dispose();
        }
    }

    private static async Task<T?> EnsureSuccessfulAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var message = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"O JMD remoto respondeu {(int)response.StatusCode}: {message}", null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    private static async Task EnsureSuccessfulAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var message = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"O JMD remoto respondeu {(int)response.StatusCode}: {message}", null, response.StatusCode);
    }

    private async Task<DiscoveredJmd?> FindPeerAsync(DesktopPairedPeer peer, CancellationToken cancellationToken)
    {
        var result = await JmdNetworkDiscovery.FindAsync(cancellationToken);
        foreach (var device in result.Devices.Where(device => device.PeerId == peer.Id && device.PublicKey == peer.PublicKey))
        {
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            try
            {
                using var response = await Client.GetAsync(device.BaseUrl + "/api/v1/desktop/identity/" + nonce, cancellationToken);
                if (!response.IsSuccessStatusCode) continue;
                var identity = await response.Content.ReadFromJsonAsync<IdentityChallengeResponse>(cancellationToken: cancellationToken);
                if (identity is not null && identity.PeerId == peer.Id && identity.PublicKey == peer.PublicKey &&
                    DesktopPeerIdentity.Verify(peer.PublicKey, $"identity\n{nonce}\n{peer.Id}", identity.Signature))
                    return device;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or CryptographicException) { }
        }
        return null;
    }

    private sealed record TaskListResponse(IReadOnlyList<TodoTaskEntry> Items, int Count, string Status);
    private sealed record IdentityChallengeResponse(string PeerId, string PublicKey, string Signature);
}
