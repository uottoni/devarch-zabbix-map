using System.Text.Json.Nodes;

namespace ZabbixMap.Core;

public interface IZabbixClient
{
    string BaseUrl { get; }
    bool UsesExpressionMacros { get; }
    Task<IReadOnlyList<MapSummary>> GetMapsAsync(CancellationToken ct = default);
    Task<SysMap?> GetMapAsync(string id, CancellationToken ct = default);
    Task<string> CreateMapAsync(string name, CancellationToken ct = default);
    Task SaveMapAsync(SysMap map, CancellationToken ct = default);
    Task DeleteMapAsync(string id, CancellationToken ct = default);
    Task<string> GetDefaultIconIdAsync(CancellationToken ct = default);
    Task<IReadOnlyList<HostGroupInfo>> GetHostGroupsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<HostInfo>> GetHostsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<TriggerInfo>> GetActiveTriggersAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ItemInfo>> GetItemsAsync(IEnumerable<string> hostIds, string? keySearch = null, CancellationToken ct = default);
    Task<IReadOnlyList<HostItem>> GetHostItemsAsync(string hostId, CancellationToken ct = default);
    Task<IReadOnlyList<HistoryPoint>> GetHistoryAsync(HostItem item, TimeSpan period, CancellationToken ct = default);
}

public record MapSummary(string Id, string Name);
public record HostGroupInfo(string Id, string Name, IReadOnlyList<string> HostIds);
public record HostInfo(string Id, string Host, string Name, string Ip, bool Monitored, bool Unavailable);
public record TriggerInfo(string Id, string Description, int Severity, DateTimeOffset Since, IReadOnlyList<string> HostIds);
public record ItemInfo(string HostId, string Name, string Key, string LastValue, string Units);

public enum ElementType { Host = 0, Map = 1, Trigger = 2, HostGroup = 3, Image = 4 }

/// <summary>
/// Map document. Elements and links keep the JSON the server sent, so properties this app
/// does not edit (URLs, label positions, link triggers, shapes...) survive a save.
/// </summary>
public sealed class SysMap
{
    long _nextTempId = 900_000_000;
    JsonObject _raw = new();

    public string Id { get; private set; } = "";
    public string Name { get; set; } = "";
    public List<MapElement> Elements { get; } = [];
    public List<MapLink> Links { get; } = [];
    public bool IsDirty { get; set; }

    public static SysMap FromJson(JsonObject o)
    {
        var map = new SysMap { _raw = o, Id = o.Str("sysmapid"), Name = o.Str("name") };
        foreach (var e in o["selements"]?.AsArray() ?? []) map.Elements.Add(new MapElement(e!.AsObject()));
        foreach (var l in o["links"]?.AsArray() ?? []) map.Links.Add(new MapLink(l!.AsObject()));
        return map;
    }

    public MapElement AddElement(ElementType type, string refId, string label, string iconId, double x, double y)
    {
        var element = new MapElement(new JsonObject
        {
            // Temporary id: links reference it until the server assigns the real one.
            ["selementid"] = (_nextTempId++).ToString(),
            ["elementtype"] = (int)type,
            ["iconid_off"] = iconId,
        }) { RefId = refId, Label = label, X = x, Y = y };
        Elements.Add(element);
        IsDirty = true;
        return element;
    }

    public MapLink AddLink(MapElement a, MapElement b)
    {
        var link = new MapLink(new JsonObject
        {
            ["selementid1"] = a.Id,
            ["selementid2"] = b.Id,
            ["color"] = "00CC00",
            ["drawtype"] = 2,
            ["label"] = "",
        });
        Links.Add(link);
        IsDirty = true;
        return link;
    }

    public void RemoveElement(MapElement element)
    {
        Elements.Remove(element);
        Links.RemoveAll(l => l.Element1Id == element.Id || l.Element2Id == element.Id);
        IsDirty = true;
    }

    public void RemoveLink(MapLink link)
    {
        Links.Remove(link);
        IsDirty = true;
    }

    public JsonObject ToUpdateJson(bool useElementsArray)
    {
        const int margin = 100, zabbixMax = 65535;
        var width = Elements.Count == 0 ? 0 : (int)Elements.Max(e => e.X) + margin;
        var height = Elements.Count == 0 ? 0 : (int)Elements.Max(e => e.Y) + margin;
        return new JsonObject
        {
            ["sysmapid"] = Id,
            ["name"] = Name,
            ["width"] = Math.Clamp(Math.Max(width, _raw.Int("width")), 1, zabbixMax),
            ["height"] = Math.Clamp(Math.Max(height, _raw.Int("height")), 1, zabbixMax),
            ["selements"] = new JsonArray(Elements.Select(e => (JsonNode)e.ToJson(useElementsArray)).ToArray()),
            ["links"] = new JsonArray(Links.Select(l => (JsonNode)l.ToJson()).ToArray()),
        };
    }
}

public sealed class MapElement
{
    readonly JsonObject _raw;

    internal MapElement(JsonObject raw)
    {
        _raw = raw;
        X = raw.Int("x");
        Y = raw.Int("y");
        Label = raw.Str("label");
        // 3.4+ uses elements[]; older servers use elementid.
        var first = raw["elements"]?.AsArray().FirstOrDefault();
        RefId = Type switch
        {
            ElementType.Host => first.Str("hostid"),
            ElementType.Map => first.Str("sysmapid"),
            ElementType.Trigger => first.Str("triggerid"),
            ElementType.HostGroup => first.Str("groupid"),
            _ => "",
        };
        if (RefId == "" && raw.Str("elementid") != "0") RefId = raw.Str("elementid");
    }

    public string Id => _raw.Str("selementid");
    public ElementType Type => (ElementType)_raw.Int("elementtype");
    public string RefId { get; set; } = "";
    public string Label { get; set; }
    public double X { get; set; }
    public double Y { get; set; }

    internal JsonObject ToJson(bool useElementsArray)
    {
        var json = _raw.DeepClone().AsObject();
        json["x"] = (int)Math.Max(0, X);
        json["y"] = (int)Math.Max(0, Y);
        json["label"] = Label;
        var refKey = Type switch
        {
            ElementType.Host => "hostid",
            ElementType.Map => "sysmapid",
            ElementType.Trigger => "triggerid",
            ElementType.HostGroup => "groupid",
            _ => null,
        };
        json.Remove("elements");
        json.Remove("elementid");
        if (refKey != null)
        {
            if (useElementsArray) json["elements"] = new JsonArray(new JsonObject { [refKey] = RefId });
            else json["elementid"] = RefId;
        }
        return json;
    }
}

public sealed class MapLink
{
    readonly JsonObject _raw;

    internal MapLink(JsonObject raw)
    {
        _raw = raw;
        Element1Id = raw.Str("selementid1");
        Element2Id = raw.Str("selementid2");
        Color = raw.Str("color");
        DrawType = raw.Int("drawtype");
        Label = raw.Str("label");
        Triggers = raw["linktriggers"]?.AsArray()
            .Select(t => new LinkTrigger(t.Str("triggerid"), t.Str("color"), t.Int("drawtype"))).ToList() ?? [];
    }

    public string Element1Id { get; }
    public string Element2Id { get; }
    public string Color { get; set; }
    public int DrawType { get; set; }
    public string Label { get; set; }
    public IReadOnlyList<LinkTrigger> Triggers { get; }

    internal JsonObject ToJson()
    {
        var json = _raw.DeepClone().AsObject();
        json["color"] = Color;
        json["drawtype"] = DrawType;
        json["label"] = Label;
        return json;
    }
}

public record LinkTrigger(string TriggerId, string Color, int DrawType);
