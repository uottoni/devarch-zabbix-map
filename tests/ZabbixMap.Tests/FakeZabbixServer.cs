using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ZabbixMap.Core;

namespace ZabbixMap.Tests;

/// <summary>HTTP-level stand-in for a Zabbix server, following the map API shape documented for each version.</summary>
public sealed class FakeZabbixServer(string version) : HttpMessageHandler
{
    public List<JsonObject> Requests { get; } = [];
    public List<string?> AuthHeaders { get; } = [];
    public Dictionary<string, Func<JsonNode?, JsonNode>> Methods { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
        Requests.Add(body);
        AuthHeaders.Add(request.Headers.Authorization?.ToString());
        var method = body["method"]!.ToString();
        JsonNode result = method == "apiinfo.version" ? JsonValue.Create(version)
            : Methods.TryGetValue(method, out var handler) ? handler(body["params"])
            : new JsonArray();
        var response = new JsonObject { ["jsonrpc"] = "2.0", ["result"] = result.DeepClone(), ["id"] = body["id"]!.DeepClone() };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    public JsonObject Last(string method) => Requests.Last(r => r["method"]!.ToString() == method);
}

/// <summary>In-memory client with a network resembling a small ISP, for UI tests.</summary>
public sealed class FakeClient : IZabbixClient
{
    public string BaseUrl => "https://zabbix.test";
    public bool UsesExpressionMacros => true;
    public List<HostInfo> Hosts { get; } = [];
    public List<TriggerInfo> Triggers { get; } = [];
    public List<ItemInfo> Items { get; } = [];
    public SysMap Map { get; set; } = null!;
    public int Saves { get; private set; }

    public Task<IReadOnlyList<MapSummary>> GetMapsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MapSummary>>([new(Map.Id, Map.Name)]);
    public Task<SysMap?> GetMapAsync(string id, CancellationToken ct = default) => Task.FromResult<SysMap?>(Map);
    public Task<string> CreateMapAsync(string name, CancellationToken ct = default) => Task.FromResult("99");
    public Task SaveMapAsync(SysMap map, CancellationToken ct = default)
    {
        Saves++;
        map.IsDirty = false;
        return Task.CompletedTask;
    }
    public Task DeleteMapAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> GetDefaultIconIdAsync(CancellationToken ct = default) => Task.FromResult("1");
    public Task<IReadOnlyList<HostGroupInfo>> GetHostGroupsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HostGroupInfo>>([new("1", "Backbone", Hosts.Select(h => h.Id).ToList())]);
    public Task<IReadOnlyList<HostInfo>> GetHostsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<HostInfo>>(Hosts);
    public Task<IReadOnlyList<TriggerInfo>> GetActiveTriggersAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TriggerInfo>>(Triggers);
    public Task<IReadOnlyList<ItemInfo>> GetItemsAsync(IEnumerable<string> hostIds, string? keySearch = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ItemInfo>>(Items);
}
