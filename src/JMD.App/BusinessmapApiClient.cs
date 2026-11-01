using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace JMD.App;

internal sealed record BusinessmapBoard(string Id, string Name);
internal sealed record BusinessmapLane(string Id, string Name, string WorkflowId = "", string WorkflowName = "");
internal sealed record BusinessmapColumn(string Id, string Name, string WorkflowId = "");
internal sealed record BusinessmapOwner(string Id, string Name);
internal sealed record BusinessmapTaskCard(
    string Id,
    string BoardId,
    string BoardName,
    string WorkflowId,
    string LaneId,
    string LaneName,
    string ColumnId,
    string ColumnName,
    string Title,
    string Description,
    string OwnerId,
    string OwnerName,
    string? Deadline);

internal sealed class BusinessmapApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _accountRoot;
    private readonly string _apiKey;
    private readonly SemaphoreSlim _openApiGate = new(1, 1);
    private JsonDocument? _openApi;
    private string _apiPrefix = "/api/v2";
    private string _apiKeyHeader = "apikey";

    public BusinessmapApiClient(string accountUrl, string apiKey)
    {
        if (!Uri.TryCreate(accountUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var accountUri) ||
            accountUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(accountUri.UserInfo) ||
            !string.IsNullOrEmpty(accountUri.Query) || !string.IsNullOrEmpty(accountUri.Fragment))
            throw new ArgumentException("Informe a URL base HTTPS da conta Businessmap, sem /openapi ou /api/v2; por exemplo https://empresa.kanbanize.com.", nameof(accountUrl));
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("Informe a chave de API.", nameof(apiKey));

        _accountRoot = new Uri(accountUri.GetLeftPart(UriPartial.Authority) + "/");
        _apiKey = apiKey.Trim();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("apikey", _apiKey);
    }

    public async Task<IReadOnlyList<BusinessmapBoard>> GetBoardsAsync(CancellationToken cancellationToken = default)
    {
        // This endpoint both verifies the API key's read access and returns the boards
        // visible to that user. It lets connection setup work independently from OpenAPI.
        using var document = await GetJsonAsync("api/v2/boards", "GET /api/v2/boards (validar acesso e listar quadros)", cancellationToken);
        return FindItems(document.RootElement)
            .Select(item => new BusinessmapBoard(ReadString(item, "board_id", "id"), ReadString(item, "name", "board_name", "title")))
            .Where(board => board.Id.Length > 0 && board.Name.Length > 0)
            .DistinctBy(board => board.Id)
            .OrderBy(board => board.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<BusinessmapLane>> GetLanesAsync(string boardId, CancellationToken cancellationToken = default)
    {
        await EnsureOpenApiAsync(cancellationToken);
        var operation = FindOperation("GetLanes");
        var workflowsTask = GetWorkflowsAsync(boardId, cancellationToken);
        using var document = await InvokeAsync(operation, new Dictionary<string, string> { ["board_id"] = boardId }, cancellationToken);
        var workflowNames = (await workflowsTask).ToDictionary(workflow => workflow.Id, workflow => workflow.Name, StringComparer.Ordinal);
        return FindItems(document.RootElement).Select(item =>
            {
                var workflowId = ReadString(item, "workflow_id", "workflow");
                return new BusinessmapLane(ReadString(item, "lane_id", "id"), ReadString(item, "name", "lane_name", "title"),
                    workflowId, workflowNames.GetValueOrDefault(workflowId, ReadString(item, "workflow_name")));
            })
            .Where(lane => lane.Id.Length > 0 && lane.Name.Length > 0)
            .DistinctBy(lane => lane.Id)
            .OrderBy(lane => lane.WorkflowName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(lane => lane.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<BusinessmapColumn>> GetColumnsAsync(string boardId, CancellationToken cancellationToken = default)
    {
        await EnsureOpenApiAsync(cancellationToken);
        using var document = await InvokeAsync(FindOperation("GetColumns"), new Dictionary<string, string> { ["board_id"] = boardId }, cancellationToken);
        return FindItems(document.RootElement).Select(item => new BusinessmapColumn(
                ReadString(item, "column_id", "id"), ReadString(item, "name", "column_name"), ReadString(item, "workflow_id")))
            .Where(column => column.Id.Length > 0 && column.Name.Length > 0)
            .DistinctBy(column => column.Id)
            .ToList();
    }

    public async Task<IReadOnlyList<BusinessmapOwner>> GetBoardOwnersAsync(string boardId, CancellationToken cancellationToken = default)
    {
        await EnsureOpenApiAsync(cancellationToken);
        using var assignees = await InvokeAsync(FindOperation("GetBoardAssignees"), new Dictionary<string, string> { ["board_id"] = boardId }, cancellationToken);
        var userIds = FindItems(assignees.RootElement).Select(item => ReadString(item, "user_id", "id"))
            .Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (userIds.Count == 0) return [];
        using var users = await InvokeAsync(FindOperation("GetUsers"), new Dictionary<string, string> { ["user_ids"] = string.Join(',', userIds) }, cancellationToken);
        return FindItems(users.RootElement).Select(item => new BusinessmapOwner(
                ReadString(item, "user_id", "id"), ReadString(item, "realname", "username", "name")))
            .Where(owner => owner.Id.Length > 0)
            .DistinctBy(owner => owner.Id)
            .OrderBy(owner => owner.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<BusinessmapTaskCard>> GetCardsAsync(string boardId, CancellationToken cancellationToken = default)
    {
        await EnsureOpenApiAsync(cancellationToken);
        var operation = FindOperation("GetCards");
        var allCards = new List<BusinessmapTaskCard>();
        var maxPages = 100;
        for (var page = 1; page <= maxPages; page++)
        {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["board_id"] = boardId, ["board_ids"] = boardId, ["page"] = page.ToString(), ["per_page"] = "200",
                ["state"] = "active", ["fields"] = "card_id,title,description,owner_user_id,deadline,board_id,workflow_id,column_id,lane_id"
            };
            using var document = await InvokeAsync(operation, parameters, cancellationToken);
            var root = document.RootElement;
            var items = FindItems(root).ToList();
            allCards.AddRange(items.Select(ParseCard).Where(card => card.Id.Length > 0 && card.Title.Length > 0));
            var allPages = ReadNestedInt(root, "pagination", "all_pages") ?? ReadNestedInt(root, "pagination", "total_pages");
            if (allPages is null ? items.Count < 200 : page >= allPages.Value) break;
        }
        return allCards.DistinctBy(card => card.Id).ToList();
    }

    public async Task ValidateConnectionAsync(CancellationToken cancellationToken = default)
    {
        var boards = await GetBoardsAsync(cancellationToken);
        if (boards.Count == 0) throw new InvalidOperationException("A conexão funcionou, mas nenhum quadro está visível para esta chave de API.");
    }

    private async Task<IReadOnlyList<BusinessmapWorkflow>> GetWorkflowsAsync(string boardId, CancellationToken cancellationToken)
    {
        var operation = FindOperation("GetWorkflows");
        var document = await InvokeAsync(operation, new Dictionary<string, string> { ["board_id"] = boardId }, cancellationToken);
        return FindItems(document.RootElement)
            .Select(item => new BusinessmapWorkflow(ReadString(item, "workflow_id", "id"), ReadString(item, "name", "workflow_name", "title")))
            .Where(workflow => workflow.Id.Length > 0)
            .ToList();
    }

    private async Task EnsureOpenApiAsync(CancellationToken cancellationToken)
    {
        if (_openApi is not null) return;
        await _openApiGate.WaitAsync(cancellationToken);
        try
        {
            if (_openApi is not null) return;
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_accountRoot, "openapi/json"));
            using var response = await _http.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, "Não foi possível carregar a OpenAPI da conta");
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            _openApi = JsonDocument.Parse(content);
            DiscoverApiPrefix(_openApi.RootElement);
            DiscoverApiKeyHeader(_openApi.RootElement);
        }
        finally { _openApiGate.Release(); }
    }

    private async Task<JsonDocument> GetJsonAsync(string path, string operationName, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_accountRoot, path));
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, operationName);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private void DiscoverApiPrefix(JsonElement root)
    {
        if (!root.TryGetProperty("servers", out var servers) || servers.ValueKind != JsonValueKind.Array || servers.GetArrayLength() == 0) return;
        var serverUrl = ReadString(servers[0], "url");
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri))
        {
            if (serverUrl.StartsWith('/')) _apiPrefix = serverUrl.TrimEnd('/');
            return;
        }
        var prefix = serverUri.AbsolutePath.TrimEnd('/');
        if (prefix.Length > 0) _apiPrefix = prefix;
    }

    private void DiscoverApiKeyHeader(JsonElement root)
    {
        if (!root.TryGetProperty("components", out var components) || !components.TryGetProperty("securitySchemes", out var schemes)) return;
        foreach (var scheme in schemes.EnumerateObject().Select(pair => pair.Value))
        {
            if (ReadString(scheme, "type").Equals("apiKey", StringComparison.OrdinalIgnoreCase) &&
                ReadString(scheme, "in").Equals("header", StringComparison.OrdinalIgnoreCase) &&
                ReadString(scheme, "name") is { Length: > 0 } header)
            {
                _apiKeyHeader = header;
                _http.DefaultRequestHeaders.Remove("apikey");
                _http.DefaultRequestHeaders.TryAddWithoutValidation(_apiKeyHeader, _apiKey);
                return;
            }
        }
    }

    private ApiOperation FindOperation(string operationHint)
    {
        if (_openApi is null || !_openApi.RootElement.TryGetProperty("paths", out var paths))
            throw new InvalidOperationException("A especificação OpenAPI da conta não contém operações.");
        foreach (var path in paths.EnumerateObject())
        {
            if (!path.Value.TryGetProperty("get", out var get)) continue;
            var operationId = ReadString(get, "operationId");
            if (operationId.Contains(operationHint, StringComparison.OrdinalIgnoreCase))
                return new ApiOperation(path.Name, path.Value, get);
        }
        throw new InvalidOperationException($"A OpenAPI desta conta não publicou a operação de leitura {operationHint}. Confira se a API V2 está habilitada.");
    }

    private async Task<JsonDocument> InvokeAsync(ApiOperation operation, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        var relativePath = operation.Path;
        var query = new List<string>();
        foreach (var parameter in GetParameters(operation))
        {
            var name = ParameterName(parameter);
            if (!TryGetParameterValue(name, values, out var value))
            {
                if (IsRequired(parameter)) throw new InvalidOperationException($"A operação {ReadString(operation.Definition, "operationId")} requer o parâmetro {name}, que esta versão do JMD ainda não consegue preencher.");
                continue;
            }
            var location = ReadString(parameter, "in");
            if (location.Equals("path", StringComparison.OrdinalIgnoreCase))
                relativePath = relativePath.Replace("{" + name + "}", Uri.EscapeDataString(value), StringComparison.OrdinalIgnoreCase);
            else if (location.Equals("query", StringComparison.OrdinalIgnoreCase))
                query.Add(Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value));
        }

        if (relativePath.Contains('{')) throw new InvalidOperationException("A OpenAPI contém parâmetros de rota que não foram resolvidos.");
        var path = relativePath.TrimStart('/');
        var prefix = _apiPrefix.Trim('/');
        if (path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase)) prefix = string.Empty;
        var url = prefix.Length == 0 ? path : prefix + "/" + path;
        if (query.Count > 0) url += "?" + string.Join('&', query);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_accountRoot, url));
        using var response = await _http.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, $"Falha ao consultar a operação {ReadString(operation.Definition, "operationId")}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private IEnumerable<JsonElement> GetParameters(ApiOperation operation)
    {
        var pathParameters = operation.PathItem.TryGetProperty("parameters", out var pathValues) && pathValues.ValueKind == JsonValueKind.Array
            ? pathValues.EnumerateArray()
            : Enumerable.Empty<JsonElement>();
        var operationParameters = operation.Definition.TryGetProperty("parameters", out var operationValues) && operationValues.ValueKind == JsonValueKind.Array
            ? operationValues.EnumerateArray()
            : Enumerable.Empty<JsonElement>();
        return pathParameters.Concat(operationParameters).Select(ResolveReference)
            .Where(parameter => parameter.ValueKind == JsonValueKind.Object).ToArray();
    }

    private JsonElement ResolveReference(JsonElement value)
    {
        if (!value.TryGetProperty("$ref", out var reference) || reference.ValueKind != JsonValueKind.String || _openApi is null)
            return value;
        var parts = (reference.GetString() ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal));
        var current = _openApi.RootElement;
        foreach (var part in parts)
        {
            if (part.StartsWith('#')) continue;
            if (!TryGetProperty(current, part, out current)) return default;
        }
        return current;
    }

    private static string ParameterName(JsonElement parameter) => ReadString(parameter, "name");
    private static bool IsRequired(JsonElement parameter) => parameter.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True;

    private static bool TryGetParameterValue(string name, IReadOnlyDictionary<string, string> values, out string value)
    {
        if (values.TryGetValue(name, out value!)) return true;
        if (name.Contains("board", StringComparison.OrdinalIgnoreCase) && values.TryGetValue("board_id", out value!)) return true;
        if (name.Contains("workflow", StringComparison.OrdinalIgnoreCase) && values.TryGetValue("workflow_id", out value!)) return true;
        if (name.Equals("page", StringComparison.OrdinalIgnoreCase) && values.TryGetValue("page", out value!)) return true;
        value = string.Empty;
        return false;
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string message)
    {
        if (response.IsSuccessStatusCode) return;
        var details = await response.Content.ReadAsStringAsync();
        var safeDetails = details.Length > 400 ? details[..400] : details;
        var status = (int)response.StatusCode;
        var hint = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => " A API não aceitou a chave. Confira se ela está ativa e pertence a esta conta.",
            HttpStatusCode.Forbidden => " O servidor recusou esta operação. Confira se o usuário tem acesso à API V2 e aos quadros.",
            (HttpStatusCode)429 => " A API limitou as consultas; aguarde um pouco e tente novamente.",
            _ => string.Empty
        };
        throw new HttpRequestException($"{message} (HTTP {status}).{hint} {safeDetails}", null, response.StatusCode);
    }

    private static IEnumerable<JsonElement> FindItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray().ToArray();
        if (root.ValueKind != JsonValueKind.Object) return [];
        foreach (var propertyName in new[] { "data", "boards", "lanes", "cards", "workflows", "items" })
        {
            if (!TryGetProperty(root, propertyName, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().ToArray();
            if (value.ValueKind == JsonValueKind.Object)
            {
                var nested = FindItems(value).ToArray();
                if (nested.Length > 0) return nested;
            }
        }
        return [];
    }

    private static BusinessmapTaskCard ParseCard(JsonElement item)
    {
        var owner = TryGetProperty(item, "owner", out var ownerValue) ? ownerValue : default;
        var ownerId = ReadString(item, "owner_user_id", "owner_id") is { Length: > 0 } id
            ? id : ReadString(owner, "user_id", "id", "owner_user_id");
        var ownerName = ReadString(item, "owner_name", "owner_user_name", "owner_username") is { Length: > 0 } name
            ? name : owner.ValueKind == JsonValueKind.String ? owner.GetString() ?? string.Empty : ReadString(owner, "name", "full_name", "username");
        return new BusinessmapTaskCard(
            ReadString(item, "card_id", "id"),
            ReadString(item, "board_id"),
            ReadString(item, "board_name"),
            ReadString(item, "workflow_id"),
            ReadString(item, "lane_id"),
            ReadString(item, "lane_name"),
            ReadString(item, "column_id"),
            ReadString(item, "column_name"),
            ReadString(item, "title", "name"),
            ReadString(item, "description"), ownerId, ownerName,
            ReadString(item, "deadline") is { Length: > 0 } deadline ? deadline : null);
    }

    private static string ReadString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return string.Empty;
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
            if (value.ValueKind == JsonValueKind.Number) return value.ToString();
        }
        return string.Empty;
    }

    private static int? ReadNestedInt(JsonElement root, string objectName, string propertyName)
    {
        if (TryGetProperty(root, objectName, out var nested) && TryGetProperty(nested, propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var property in root.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.Object && ReadNestedInt(property.Value, objectName, propertyName) is { } nestedNumber)
                    return nestedNumber;
        return null;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }

    public void Dispose()
    {
        _openApi?.Dispose();
        _openApiGate.Dispose();
        _http.Dispose();
    }

    private sealed record ApiOperation(string Path, JsonElement PathItem, JsonElement Definition);
    private sealed record BusinessmapWorkflow(string Id, string Name);
}
