using System.Globalization;
using System.Text.RegularExpressions;

namespace ZabbixMap.Core;

public enum NodeHealth { Unknown, Ok, Problem, Down }

public record ElementStatus(NodeHealth Health, int Severity, IReadOnlyList<TriggerInfo> Problems);

/// <summary>Point-in-time view of the server used to color the map and detect outages.</summary>
public sealed partial class MonitoringSnapshot
{
    public const int HighSeverity = 4;

    readonly Dictionary<string, List<TriggerInfo>> _problemsByHost = [];

    public IReadOnlyDictionary<string, HostInfo> Hosts { get; }
    public IReadOnlyDictionary<string, HostGroupInfo> Groups { get; }
    public IReadOnlyList<TriggerInfo> Triggers { get; }
    public IReadOnlyDictionary<(string HostId, string Key), ItemInfo> Items { get; set; } = new Dictionary<(string, string), ItemInfo>();

    public MonitoringSnapshot(IEnumerable<HostInfo> hosts, IEnumerable<HostGroupInfo> groups, IEnumerable<TriggerInfo> triggers)
    {
        Hosts = hosts.ToDictionary(h => h.Id);
        Groups = groups.ToDictionary(g => g.Id);
        Triggers = triggers.ToList();
        foreach (var trigger in Triggers)
            foreach (var hostId in trigger.HostIds)
            {
                if (!_problemsByHost.TryGetValue(hostId, out var list)) _problemsByHost[hostId] = list = [];
                list.Add(trigger);
            }
    }

    [GeneratedRegex(@"unavailable|unreachable|\bdown\b|indispon|inacess", RegexOptions.IgnoreCase)]
    private static partial Regex OutageTrigger();

    /// <summary>A host is down when its interfaces are unavailable or a High+ trigger describes an outage (e.g. "Unavailable by ICMP ping").</summary>
    public bool IsDown(string hostId) =>
        Hosts.TryGetValue(hostId, out var host) && host.Monitored &&
        (host.Unavailable || ProblemsOf(hostId).Any(t => t.Severity >= HighSeverity && OutageTrigger().IsMatch(t.Description)));

    public IReadOnlyList<TriggerInfo> ProblemsOf(string hostId) =>
        _problemsByHost.TryGetValue(hostId, out var list) ? list : [];

    public IEnumerable<string> DownHostIds => Hosts.Keys.Where(IsDown);

    public ElementStatus HostStatus(string hostId)
    {
        if (!Hosts.TryGetValue(hostId, out var host) || !host.Monitored) return new(NodeHealth.Unknown, 0, []);
        var problems = ProblemsOf(hostId);
        var severity = problems.Count == 0 ? 0 : problems.Max(t => t.Severity);
        var health = IsDown(hostId) ? NodeHealth.Down : problems.Count > 0 ? NodeHealth.Problem : NodeHealth.Ok;
        return new(health, severity, problems);
    }

    public ElementStatus Aggregate(IEnumerable<ElementStatus> statuses)
    {
        var list = statuses.ToList();
        if (list.Count == 0) return new(NodeHealth.Unknown, 0, []);
        return new(list.Max(s => s.Health), list.Max(s => s.Severity), list.SelectMany(s => s.Problems).DistinctBy(t => t.Id).ToList());
    }

    /// <summary>Status of an element; submaps recurse through <paramref name="maps"/>, guarding against cycles.</summary>
    public ElementStatus StatusOf(MapElement element, IReadOnlyDictionary<string, SysMap>? maps = null, HashSet<string>? visiting = null)
    {
        switch (element.Type)
        {
            case ElementType.Host:
                return HostStatus(element.RefId);
            case ElementType.HostGroup:
                return Groups.TryGetValue(element.RefId, out var group) ? Aggregate(group.HostIds.Select(HostStatus)) : new(NodeHealth.Unknown, 0, []);
            case ElementType.Trigger:
                var trigger = Triggers.FirstOrDefault(t => t.Id == element.RefId);
                return trigger == null ? new(NodeHealth.Ok, 0, []) : new(NodeHealth.Problem, trigger.Severity, [trigger]);
            case ElementType.Map:
                visiting ??= [];
                if (maps == null || !maps.TryGetValue(element.RefId, out var submap) || !visiting.Add(element.RefId))
                    return new(NodeHealth.Unknown, 0, []);
                var result = Aggregate(submap.Elements.Where(e => e.Type != ElementType.Image).Select(e => StatusOf(e, maps, visiting)));
                visiting.Remove(element.RefId);
                return result;
            default:
                return new(NodeHealth.Unknown, 0, []);
        }
    }
}

/// <summary>Tracks transitions between polls. The first poll only sets the baseline, so startup does not fire one alert per host.</summary>
public sealed class OutageTracker
{
    HashSet<string>? _down;

    public (IReadOnlyList<HostInfo> WentDown, IReadOnlyList<HostInfo> Recovered, int Baseline) Update(MonitoringSnapshot snapshot)
    {
        var current = snapshot.DownHostIds.ToHashSet();
        if (_down == null)
        {
            _down = current;
            return ([], [], current.Count);
        }
        var wentDown = current.Except(_down).Select(id => snapshot.Hosts[id]).ToList();
        var recovered = _down.Except(current).Where(snapshot.Hosts.ContainsKey).Select(id => snapshot.Hosts[id]).ToList();
        _down = current;
        return (wentDown, recovered, 0);
    }
}

public static partial class LabelResolver
{
    [GeneratedRegex(@"\{\?last\(/(?<host>[^/]+)/(?<key>.+?)\)\}")]
    private static partial Regex ExpressionMacro();

    // Pre-5.4 syntax: {host:key.last()}
    [GeneratedRegex(@"\{(?<host>[^:{}]+):(?<key>[^{}]+)\.last\([^)]*\)\}")]
    private static partial Regex LegacyMacro();

    public static IEnumerable<(string HostName, string Key)> ReferencedItems(string label)
    {
        foreach (Match m in ExpressionMacro().Matches(label)) yield return (m.Groups["host"].Value, m.Groups["key"].Value);
        foreach (Match m in LegacyMacro().Matches(label)) yield return (m.Groups["host"].Value, m.Groups["key"].Value);
    }

    public static string BuildItemMacro(string host, string key, bool expressionSyntax) =>
        expressionSyntax ? $"{{?last(/{host}/{key})}}" : $"{{{host}:{key}.last()}}";

    public static string Resolve(string label, MapElement? element, MonitoringSnapshot? snapshot, Func<string, string?>? submapName = null)
    {
        if (string.IsNullOrEmpty(label)) return "";
        if (snapshot == null) return label;

        if (element != null)
        {
            if (element.Type == ElementType.Host && snapshot.Hosts.TryGetValue(element.RefId, out var host))
                label = label.Replace("{HOST.NAME}", host.Name).Replace("{HOST.HOST}", host.Host)
                    .Replace("{HOST.IP}", host.Ip).Replace("{HOST.CONN}", host.Ip).Replace("{HOST.DNS}", host.Ip);
            if (element.Type == ElementType.HostGroup && snapshot.Groups.TryGetValue(element.RefId, out var group))
                label = label.Replace("{HOSTGROUP.NAME}", group.Name);
            if (element.Type == ElementType.Map && submapName?.Invoke(element.RefId) is { } name)
                label = label.Replace("{MAP.NAME}", name);
        }

        var byHostName = snapshot.Hosts.Values.ToLookup(h => h.Host);
        string Value(Match m)
        {
            var hostId = byHostName[m.Groups["host"].Value].FirstOrDefault()?.Id;
            return hostId != null && snapshot.Items.TryGetValue((hostId, m.Groups["key"].Value), out var item)
                ? FormatValue(item.LastValue, item.Units)
                : "?";
        }
        label = ExpressionMacro().Replace(label, Value);
        return LegacyMacro().Replace(label, Value);
    }

    public static string FormatValue(string value, string units)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return value;
        switch (units)
        {
            case "unixtime":
                return number <= 0 ? "" : DateTimeOffset.FromUnixTimeSeconds((long)number).ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
            case "s" or "uptime":
                return FormatDuration(number);
        }
        // Units Zabbix never scales with K/M/G.
        if (units is "" or "%" or "ms" or "rpm" or "RPM" || units.StartsWith('!'))
        {
            var text = number.ToString(number % 1 == 0 ? "0" : "0.##", CultureInfo.InvariantCulture);
            return units == "" ? text : units == "%" ? text + " %" : $"{text} {units.TrimStart('!')}";
        }
        // Zabbix scales bytes by 1024 and everything else by 1000.
        double step = units is "B" or "Bps" ? 1024 : 1000;
        string[] prefixes = ["", "K", "M", "G", "T", "P"];
        var index = 0;
        while (Math.Abs(number) >= step && index < prefixes.Length - 1)
        {
            number /= step;
            index++;
        }
        return $"{number.ToString("0.##", CultureInfo.InvariantCulture)} {prefixes[index]}{units}";
    }

    /// <summary>Largest three units, as Zabbix shows durations ("1d 4h 12m", "350ms").</summary>
    static string FormatDuration(double seconds)
    {
        var sign = seconds < 0 ? "-" : "";
        seconds = Math.Abs(seconds);
        if (seconds == 0) return "0s";
        if (seconds < 1) return $"{sign}{Math.Round(seconds * 1000)}ms";
        var span = TimeSpan.FromSeconds(Math.Round(seconds));
        var parts = new List<string>();
        if (span.Days >= 365) parts.Add($"{span.Days / 365}y");
        if (span.Days % 365 > 0) parts.Add($"{span.Days % 365}d");
        if (span.Hours > 0) parts.Add($"{span.Hours}h");
        if (span.Minutes > 0) parts.Add($"{span.Minutes}m");
        if (span.Seconds > 0) parts.Add($"{span.Seconds}s");
        return sign + string.Join(' ', parts.Take(3));
    }
}
