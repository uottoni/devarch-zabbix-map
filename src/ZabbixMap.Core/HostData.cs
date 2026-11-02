using System.Globalization;

namespace ZabbixMap.Core;

public record ItemTag(string Name, string Value);
public record HistoryPoint(DateTimeOffset Time, double Value);

/// <summary>An item with its latest value, as shown in Zabbix "Latest data".</summary>
public sealed record HostItem(
    string Id, string Name, string Key, string LastValue, string PreviousValue, DateTimeOffset? LastCheck,
    string Units, int ValueType, IReadOnlyList<ItemTag> Tags, IReadOnlyDictionary<string, string> ValueMap)
{
    public bool IsNumeric => ValueType is 0 or 3;
    public bool HasData => LastCheck != null;
    public string? Tag(string name) => Tags.FirstOrDefault(t => t.Name == name)?.Value;

    public string DisplayValue
    {
        get
        {
            if (!HasData) return "";
            if (ValueMap.TryGetValue(LastValue, out var mapped)) return $"{mapped} ({LastValue})";
            if (IsNumeric) return LabelResolver.FormatValue(LastValue, Units);
            var line = LastValue.ReplaceLineEndings(" ");
            return line.Length > 80 ? line[..80] + "…" : line;
        }
    }

    public string Change
    {
        get
        {
            if (!IsNumeric || ValueMap.Count > 0 || Units is "unixtime" ||
                !double.TryParse(LastValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var last) ||
                !double.TryParse(PreviousValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var previous))
                return "";
            var delta = last - previous;
            if (delta == 0) return "";
            var text = LabelResolver.FormatValue(delta.ToString(CultureInfo.InvariantCulture), Units);
            return delta > 0 ? "+" + text : text;
        }
    }
}

/// <summary>Groups latest data like the Zabbix subfilter: by "component" tag, then by interface or filesystem.</summary>
public static class HostSummary
{
    public const string Other = "outros";
    static readonly string[] SubgroupTags = ["interface", "filesystem", "disk"];

    public sealed record Section(string Component, string? Subgroup, IReadOnlyList<HostItem> Items);

    public static IEnumerable<string> ComponentsOf(HostItem item)
    {
        var components = item.Tags.Where(t => t.Name == "component" && t.Value != "").Select(t => t.Value).Distinct().ToList();
        return components.Count > 0 ? components : [Other];
    }

    public static string? SubgroupOf(HostItem item) =>
        SubgroupTags.Select(item.Tag).FirstOrDefault(v => !string.IsNullOrEmpty(v));

    public static IReadOnlyDictionary<string, int> ComponentCounts(IEnumerable<HostItem> items) =>
        items.SelectMany(ComponentsOf).GroupBy(c => c)
            .OrderBy(g => g.Key == Other).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count());

    /// <summary>An item tagged with two components appears in both, as in Zabbix.</summary>
    public static IReadOnlyList<Section> Group(IEnumerable<HostItem> items) =>
        items.SelectMany(i => ComponentsOf(i).Select(c => (Component: c, Item: i)))
            .GroupBy(x => (x.Component, Subgroup: SubgroupOf(x.Item)))
            .OrderBy(g => g.Key.Component == Other).ThenBy(g => g.Key.Component, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.Subgroup != null).ThenBy(g => g.Key.Subgroup, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Section(g.Key.Component, g.Key.Subgroup,
                g.Select(x => x.Item).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();

    /// <summary>For interface traffic, the opposite direction, so received and sent share one chart.</summary>
    public static HostItem? TrafficPartner(HostItem item, IEnumerable<HostItem> all)
    {
        var direction = TrafficDirection(item);
        if (direction == null) return null;
        var interfaceTag = item.Tag("interface");
        return all.FirstOrDefault(other =>
            other.Id != item.Id && other.Units == item.Units && TrafficDirection(other) is { } d && d != direction &&
            (interfaceTag != null ? other.Tag("interface") == interfaceTag : SwapDirection(KeyArgs(other.Key)) == KeyArgs(item.Key)));
    }

    /// <summary>"in" for received, "out" for sent, null for anything else.</summary>
    public static string? TrafficDirection(HostItem item) =>
        item.Key.StartsWith("net.if.in", StringComparison.Ordinal) ? "in"
        : item.Key.StartsWith("net.if.out", StringComparison.Ordinal) ? "out"
        : null;

    static string KeyArgs(string key) => key.IndexOf('[') is var i and >= 0 ? key[i..] : "";

    static string SwapDirection(string args) =>
        args.Replace("In", "\u0001").Replace("Out", "In").Replace("\u0001", "Out")
            .Replace("in", "\u0001").Replace("out", "in").Replace("\u0001", "out");
}
