using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace ZabbixMap.Core;

public sealed class ZabbixApiException(string message) : Exception(message);

/// <summary>JSON-RPC client. Detects the server version and adapts to the differences between API generations.</summary>
public sealed class ZabbixClient : IZabbixClient, IDisposable
{
    readonly HttpClient _http;
    readonly Uri _endpoint;
    string? _token;
    int _nextId;

    public Version ServerVersion { get; private set; } = new(0, 0);
    public string BaseUrl { get; }

    public ZabbixClient(string url, bool ignoreCertificateErrors = false, HttpMessageHandler? handler = null)
    {
        BaseUrl = NormalizeBaseUrl(url);
        _endpoint = new Uri(BaseUrl + "/api_jsonrpc.php");
        handler ??= ignoreCertificateErrors
            ? new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator }
            : new HttpClientHandler();
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public static string NormalizeBaseUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
        if (url.EndsWith("/api_jsonrpc.php", StringComparison.OrdinalIgnoreCase))
            url = url[..^"/api_jsonrpc.php".Length];
        if (!url.Contains("://")) url = "https://" + url;
        return url;
    }

    // Header auth exists from 6.4 on; the "auth" body field was removed in 7.2.
    bool UsesBearer => ServerVersion >= new Version(6, 4);
    // Selement "elements" array replaced "elementid" in 3.4.
    bool UsesElementsArray => ServerVersion >= new Version(3, 4);
    // Availability moved from host to interface in 5.4; login's "user" became "username" there too.
    bool UsesInterfaceAvailability => ServerVersion >= new Version(5, 4);
    public bool UsesExpressionMacros => ServerVersion >= new Version(5, 4);

    public async Task ConnectWithTokenAsync(string token, CancellationToken ct = default)
    {
        await DetectVersionAsync(ct);
        _token = token;
        await CallAsync("host.get", new JsonObject { ["countOutput"] = true, ["limit"] = 1 }, ct);
    }

    public async Task ConnectWithPasswordAsync(string user, string password, CancellationToken ct = default)
    {
        await DetectVersionAsync(ct);
        var result = await CallAsync("user.login", new JsonObject
        {
            [UsesInterfaceAvailability ? "username" : "user"] = user,
            ["password"] = password,
        }, ct, anonymous: true);
        _token = result.GetValue<string>();
    }

    async Task DetectVersionAsync(CancellationToken ct)
    {
        var raw = (await CallAsync("apiinfo.version", new JsonArray(), ct, anonymous: true)).GetValue<string>();
        ServerVersion = Version.Parse(string.Join('.', raw.Split('.', '-').Take(2)));
    }

    public async Task<JsonNode> CallAsync(string method, JsonNode parameters, CancellationToken ct = default, bool anonymous = false)
    {
        var body = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters,
            ["id"] = Interlocked.Increment(ref _nextId),
        };
        if (!anonymous && _token != null && !UsesBearer) body["auth"] = _token;

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Content = JsonContent.Create<JsonNode>(body, mediaType: new("application/json-rpc"));
        if (!anonymous && _token != null && UsesBearer)
            request.Headers.Authorization = new("Bearer", _token);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonNode>(ct) ?? throw new ZabbixApiException("Resposta vazia do servidor.");
        if (json["error"] is { } error)
            throw new ZabbixApiException($"{method}: {error["message"]} {error["data"]}".Trim());
        return json["result"] ?? throw new ZabbixApiException($"{method}: resposta sem resultado.");
    }

    public async Task<IReadOnlyList<MapSummary>> GetMapsAsync(CancellationToken ct = default)
    {
        var result = await CallAsync("map.get", new JsonObject { ["output"] = "extend", ["sortfield"] = "name" }, ct);
        return result.AsArray().Select(m => new MapSummary(m.Str("sysmapid"), m.Str("name"))).ToList();
    }

    public async Task<SysMap?> GetMapAsync(string id, CancellationToken ct = default)
    {
        var result = await CallAsync("map.get", new JsonObject
        {
            ["sysmapids"] = new JsonArray(id),
            ["output"] = "extend",
            ["selectSelements"] = "extend",
            ["selectLinks"] = "extend",
        }, ct);
        return result.AsArray().FirstOrDefault() is JsonObject o ? SysMap.FromJson(o) : null;
    }

    public async Task<string> CreateMapAsync(string name, CancellationToken ct = default)
    {
        var result = await CallAsync("map.create", new JsonArray(new JsonObject { ["name"] = name, ["width"] = 1600, ["height"] = 1000 }), ct);
        return result["sysmapids"]![0]!.ToString();
    }

    public async Task SaveMapAsync(SysMap map, CancellationToken ct = default)
    {
        await CallAsync("map.update", new JsonArray(map.ToUpdateJson(UsesElementsArray)), ct);
        map.IsDirty = false;
    }

    public async Task DeleteMapAsync(string id, CancellationToken ct = default) =>
        await CallAsync("map.delete", new JsonArray(id), ct);

    public async Task<string> GetDefaultIconIdAsync(CancellationToken ct = default)
    {
        var images = await CallAsync("image.get", new JsonObject { ["output"] = new JsonArray("imageid", "name"), ["filter"] = new JsonObject { ["imagetype"] = 1 } }, ct);
        var all = images.AsArray();
        var chosen = all.FirstOrDefault(i => i.Str("name").Contains("Router", StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
        return chosen?.Str("imageid") ?? throw new ZabbixApiException("O servidor não tem ícones cadastrados (image.get), necessários para criar elementos.");
    }

    public async Task<IReadOnlyList<HostGroupInfo>> GetHostGroupsAsync(CancellationToken ct = default)
    {
        var result = await CallAsync("hostgroup.get", new JsonObject
        {
            ["output"] = "extend",
            ["selectHosts"] = new JsonArray("hostid"),
            ["sortfield"] = "name",
        }, ct);
        return result.AsArray()
            .Select(g => new HostGroupInfo(g.Str("groupid"), g.Str("name"), g!["hosts"]?.AsArray().Select(h => h.Str("hostid")).ToList() ?? []))
            .Where(g => g.HostIds.Count > 0)
            .ToList();
    }

    public async Task<IReadOnlyList<HostInfo>> GetHostsAsync(CancellationToken ct = default)
    {
        var result = await CallAsync("host.get", new JsonObject { ["output"] = "extend", ["selectInterfaces"] = "extend", ["sortfield"] = "name" }, ct);
        return result.AsArray().Select(h =>
        {
            var interfaces = h!["interfaces"]?.AsArray() ?? [];
            var main = interfaces.FirstOrDefault(i => i.Str("main") == "1") ?? interfaces.FirstOrDefault();
            var ip = main?.Str("useip") == "0" ? main.Str("dns") : main?.Str("ip") ?? "";
            return new HostInfo(h.Str("hostid"), h.Str("host"), h.Str("name") is { Length: > 0 } n ? n : h.Str("host"), ip,
                h.Str("status") == "0", IsUnavailable(h));
        }).ToList();
    }

    bool IsUnavailable(JsonNode? host)
    {
        if (UsesInterfaceAvailability)
        {
            var states = host!["interfaces"]?.AsArray().Select(i => i.Str("available")).Where(a => a != "0").ToList() ?? [];
            return states.Count > 0 && states.All(a => a == "2");
        }
        var legacy = new[] { "available", "snmp_available", "ipmi_available", "jmx_available" }
            .Select(host.Str).Where(a => a is "1" or "2").ToList();
        return legacy.Count > 0 && legacy.All(a => a == "2");
    }

    public async Task<IReadOnlyList<TriggerInfo>> GetActiveTriggersAsync(CancellationToken ct = default)
    {
        var result = await CallAsync("trigger.get", new JsonObject
        {
            ["output"] = "extend",
            ["monitored"] = true,
            ["skipDependent"] = true,
            ["expandDescription"] = true,
            ["filter"] = new JsonObject { ["value"] = 1 },
            ["selectHosts"] = new JsonArray("hostid"),
            ["sortfield"] = "priority",
            ["sortorder"] = "DESC",
        }, ct);
        return result.AsArray().Select(t => new TriggerInfo(
            t.Str("triggerid"), t.Str("description"), t.Int("priority"),
            DateTimeOffset.FromUnixTimeSeconds(t.Int("lastchange")),
            t!["hosts"]?.AsArray().Select(h => h.Str("hostid")).ToList() ?? [])).ToList();
    }

    public async Task<IReadOnlyList<ItemInfo>> GetItemsAsync(IEnumerable<string> hostIds, string? keySearch = null, CancellationToken ct = default)
    {
        var ids = new JsonArray(hostIds.Distinct().Select(i => (JsonNode)i!).ToArray());
        if (ids.Count == 0) return [];
        var query = new JsonObject
        {
            ["hostids"] = ids,
            ["output"] = new JsonArray("itemid", "hostid", "name", "key_", "lastvalue", "units"),
            ["monitored"] = true,
        };
        if (keySearch != null) query["search"] = new JsonObject { ["key_"] = keySearch };
        var result = await CallAsync("item.get", query, ct);
        return result.AsArray().Select(i => new ItemInfo(i.Str("hostid"), i.Str("name"), i.Str("key_"), i.Str("lastvalue"), i.Str("units"))).ToList();
    }

    public void Dispose() => _http.Dispose();
}

static class JsonExt
{
    public static string Str(this JsonNode? node, string key) => node?[key]?.ToString() ?? "";
    public static int Int(this JsonNode? node, string key) => int.TryParse(node.Str(key), out var v) ? v : 0;
}
