using System.Text.Json.Nodes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using ZabbixMap.App;
using ZabbixMap.Core;

namespace ZabbixMap.Tests;

public class HostSummaryTests
{
    static readonly IReadOnlyDictionary<string, string> NoMap = new Dictionary<string, string>();

    internal static HostItem Item(string id, string name, string key, string value, string units = "", int type = 3,
        string? component = "network", string? iface = null, string prev = "", IReadOnlyDictionary<string, string>? map = null, bool data = true)
    {
        var tags = new List<ItemTag>();
        if (component != null) tags.Add(new("component", component));
        if (iface != null) tags.Add(new("interface", iface));
        return new(id, name, key, value, prev, data ? DateTimeOffset.UtcNow.AddSeconds(-47) : null, units, type, tags, map ?? NoMap);
    }

    /// <summary>Roughly the switch from the Zabbix screenshot.</summary>
    internal static List<HostItem> Switch() =>
    [
        Item("1", "#1: CPU utilization", "system.cpu.util[1]", "5", "%", 0, "cpu", prev: "6"),
        Item("2", "CPU: Temperature", "sensor.temp.cpu", "45", "°C", component: "temperature", prev: "44"),
        Item("3", "Device: Temperature", "sensor.temp.dev", "61", "°C", component: "temperature"),
        Item("4", "Disk-131072: Space utilization", "vfs.fs.pused[131072]", "82.6416", "%", 0, "storage"),
        Item("5", "Interface br_backbone(): Bits received", "net.if.in[ifHCInOctets.1]", "728", "bps", iface: "br_backbone"),
        Item("6", "Interface br_backbone(): Bits sent", "net.if.out[ifHCOutOctets.1]", "48", "bps", iface: "br_backbone"),
        Item("7", "Interface br_backbone(): Operational status", "net.if.status[ifOperStatus.1]", "1", iface: "br_backbone",
            map: new Dictionary<string, string> { ["1"] = "up", ["2"] = "down" }),
        Item("8", "Interface sfp1(): Bits received", "net.if.in[ifHCInOctets.2]", "2790000000", "bps", iface: "sfp1"),
        Item("9", "Interface sfp1(): Bits sent", "net.if.out[ifHCOutOctets.2]", "3260000", "bps", iface: "sfp1"),
        Item("10", "System description", "system.descr", "RouterOS CRS326-24G-2S+", "", 4, "system"),
        Item("11", "System uptime", "system.uptime", "391000", "uptime", component: "system"),
        Item("12", "Unused item", "custom.thing", "", component: null, data: false),
    ];

    [Fact]
    public void Groups_ByComponentThenInterface()
    {
        var sections = HostSummary.Group(Switch().Where(i => i.HasData));
        Assert.Equal(
            ["cpu", "network › br_backbone", "network › sfp1", "storage", "system", "temperature"],
            sections.Select(s => s.Subgroup == null ? s.Component : $"{s.Component} › {s.Subgroup}"));
        Assert.Equal(3, sections[1].Items.Count);
    }

    [Fact]
    public void ItemWithoutComponent_GoesToOther_AndCountsMatchZabbixSubfilter()
    {
        var counts = HostSummary.ComponentCounts(Switch());
        Assert.Equal(5, counts["network"]);
        Assert.Equal(1, counts[HostSummary.Other]);
        Assert.Equal(HostSummary.Other, counts.Keys.Last());
    }

    [Fact]
    public void ItemWithTwoComponents_AppearsInBoth()
    {
        var item = Switch()[0] with { Tags = [new("component", "cpu"), new("component", "health")] };
        Assert.Equal(["cpu", "health"], HostSummary.Group([item]).Select(s => s.Component));
    }

    [Fact]
    public void TrafficPartner_ByInterfaceTag_OrByKey()
    {
        var items = Switch();
        Assert.Equal("6", HostSummary.TrafficPartner(items[4], items)!.Id);
        Assert.Equal("8", HostSummary.TrafficPartner(items[8], items)!.Id);
        Assert.Null(HostSummary.TrafficPartner(items[6], items));

        var untagged = items.Select(i => i with { Tags = [] }).ToList();
        Assert.Equal("9", HostSummary.TrafficPartner(untagged[7], untagged)!.Id);
    }

    [Fact]
    public void DisplayValues_LikeLatestData()
    {
        var items = Switch();
        Assert.Equal("5 %", items[0].DisplayValue);
        Assert.Equal("-1 %", items[0].Change);
        Assert.Equal("+1 °C", items[1].Change);
        Assert.Equal("up (1)", items[6].DisplayValue);
        Assert.Equal("", items[6].Change);
        Assert.Equal("2.79 Gbps", items[7].DisplayValue);
        Assert.Equal("4d 12h 36m", items[10].DisplayValue);
        Assert.Equal("RouterOS CRS326-24G-2S+", items[9].DisplayValue);
        Assert.False(items[9].IsNumeric);
        Assert.Equal("", items[11].DisplayValue);
    }

    [Theory]
    [InlineData("0.35", "s", "350ms")]
    [InlineData("3725", "s", "1h 2m 5s")]
    [InlineData("0", "uptime", "0s")]
    [InlineData("1500", "rpm", "1500 rpm")]
    public void FormatsDurationsAndUnscaledUnits(string value, string units, string expected) =>
        Assert.Equal(expected, LabelResolver.FormatValue(value, units));

    static async Task<(FakeZabbixServer Server, ZabbixClient Client)> Connect(string version, Func<JsonNode?, JsonNode> items)
    {
        var server = new FakeZabbixServer(version);
        server.Methods["host.get"] = _ => JsonValue.Create("0");
        server.Methods["item.get"] = items;
        var client = new ZabbixClient("https://z.test", handler: server);
        await client.ConnectWithTokenAsync("t");
        return (server, client);
    }

    [Fact]
    public async Task Client_Modern_ReadsTagsAndValueMaps()
    {
        var (server, client) = await Connect("7.0.0", _ => JsonNode.Parse("""
            [{"itemid":"7","name":"Oper status","key_":"net.if.status[1]","lastvalue":"1","prevvalue":"1","lastclock":"1700000000","units":"","value_type":"3",
              "tags":[{"tag":"component","value":"network"},{"tag":"interface","value":"ether1"}],
              "valuemap":{"valuemapid":"3","mappings":[{"type":"0","value":"1","newvalue":"up"}]}},
             {"itemid":"8","name":"Never","key_":"x","lastvalue":"0","prevvalue":"0","lastclock":"0","units":"","value_type":"3","tags":[],"valuemap":[]}]
            """)!);
        var items = await client.GetHostItemsAsync("10");
        var query = server.Last("item.get")["params"]!;
        Assert.Equal("extend", query["selectTags"]!.ToString());
        Assert.Null(query["selectApplications"]);
        Assert.Equal("ether1", items[0].Tag("interface"));
        Assert.Equal("up (1)", items[0].DisplayValue);
        Assert.False(items[1].HasData);
    }

    [Fact]
    public async Task Client_Legacy_UsesApplicationsAsComponents()
    {
        var (server, client) = await Connect("4.0.0", _ => JsonNode.Parse("""
            [{"itemid":"1","name":"Incoming traffic","key_":"net.if.in[eth0]","lastvalue":"10","prevvalue":"5","lastclock":"1700000000","units":"bps","value_type":"3",
              "applications":[{"applicationid":"1","name":"Network interfaces"}]}]
            """)!);
        var item = Assert.Single(await client.GetHostItemsAsync("10"));
        Assert.Null(server.Last("item.get")["params"]!["selectTags"]);
        Assert.Equal(["Network interfaces"], HostSummary.ComponentsOf(item));
    }

    [Fact]
    public async Task History_UsesHistoryUpToADay_TrendsBeyond()
    {
        var (server, client) = await Connect("7.0.0", _ => new JsonArray());
        server.Methods["history.get"] = _ => JsonNode.Parse("""[{"itemid":"5","clock":"1700000060","value":"20"},{"itemid":"5","clock":"1700000000","value":"10.5"}]""")!;
        server.Methods["trend.get"] = _ => JsonNode.Parse("""[{"clock":"1700003600","value_avg":"7"}]""")!;
        var item = Switch()[4] with { ValueType = 0 };

        var day = await client.GetHistoryAsync(item, TimeSpan.FromDays(1));
        var request = server.Last("history.get")["params"]!;
        Assert.Equal(0, request["history"]!.GetValue<int>());
        Assert.Equal([10.5, 20], day.Select(p => p.Value));

        var week = await client.GetHistoryAsync(item, TimeSpan.FromDays(7));
        Assert.Equal(7, Assert.Single(week).Value);

        Assert.Empty(await client.GetHistoryAsync(Switch()[9], TimeSpan.FromHours(1)));
    }

    [AvaloniaFact]
    public async Task SummaryWindow_GroupsItems_AndChartsInterfaceTrafficTogether()
    {
        var client = UiTests.Network();
        client.HostItems.AddRange(Switch());
        var main = new MainWindow { Notify = (_, _) => { } };
        main.Show();
        await main.AttachAsync(client);
        await main.OpenMapAsync("5");

        var summary = main.ShowHostSummary("1")!;
        await summary.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        var sections = summary.Rows.OfType<HostSummaryWindow.SectionRow>().ToList();
        Assert.Equal(["cpu", "network › br_backbone", "network › sfp1", "storage", "system", "temperature"], sections.Select(r => r.Title));
        Assert.Equal(3, sections[1].Count);
        Assert.DoesNotContain(summary.Rows.OfType<HostSummaryWindow.ItemRow>(), r => !r.Item.HasData);

        // First received-traffic item is charted with its sent partner.
        Assert.Equal(["Recebido", "Enviado"], summary.Chart.Series.Select(s => s.Name));
        Assert.Contains(client.HistoryRequests, r => r.ItemId == "6");

        if (Environment.GetEnvironmentVariable("ZABBIXMAP_SHOT") is { } path)
            summary.CaptureRenderedFrame()!.Save(path.Replace(".png", "-host.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

        await summary.ShowChartAsync(Switch()[0]);
        Assert.Equal(["#1: CPU utilization"], summary.Chart.Series.Select(s => s.Name));
        await summary.ShowChartAsync(Switch()[9]);
        Assert.Empty(summary.Chart.Series);
    }
}
