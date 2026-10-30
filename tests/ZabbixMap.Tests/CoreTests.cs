using System.Text.Json.Nodes;
using Xunit;
using ZabbixMap.Core;

namespace ZabbixMap.Tests;

public class ClientTests
{
    static ZabbixClient Client(FakeZabbixServer server) => new("https://z.test/", handler: server);

    [Theory]
    [InlineData("z.test", "https://z.test")]
    [InlineData("http://z.test/zabbix/", "http://z.test/zabbix")]
    [InlineData("https://z.test/api_jsonrpc.php", "https://z.test")]
    public void NormalizesUrl(string input, string expected) => Assert.Equal(expected, ZabbixClient.NormalizeBaseUrl(input));

    [Fact]
    public async Task Version7_UsesBearerHeaderAndUsername()
    {
        var server = new FakeZabbixServer("7.0.5");
        server.Methods["user.login"] = _ => JsonValue.Create("session");
        using var client = Client(server);
        await client.ConnectWithPasswordAsync("Admin", "pw");
        await client.GetMapsAsync();

        Assert.Equal("Admin", server.Last("user.login")["params"]!["username"]!.ToString());
        Assert.Equal("Bearer session", server.AuthHeaders[^1]);
        Assert.Null(server.Last("map.get")["auth"]);
    }

    [Fact]
    public async Task Version2_UsesAuthFieldAndUser()
    {
        var server = new FakeZabbixServer("2.0.17");
        server.Methods["user.login"] = _ => JsonValue.Create("session");
        using var client = Client(server);
        await client.ConnectWithPasswordAsync("Admin", "pw");
        await client.GetMapsAsync();

        Assert.Equal("Admin", server.Last("user.login")["params"]!["user"]!.ToString());
        Assert.Null(server.AuthHeaders[^1]);
        Assert.Equal("session", server.Last("map.get")["auth"]!.ToString());
        Assert.Null(server.Last("user.login")["auth"]);
    }

    [Fact]
    public async Task ApiError_BecomesException()
    {
        var server = new FakeZabbixServer("7.0.0");
        server.Methods["host.get"] = _ => throw new InvalidOperationException();
        var handler = new ErrorHandler();
        using var client = new ZabbixClient("https://z.test", handler: handler);
        var error = await Assert.ThrowsAsync<ZabbixApiException>(() => client.GetMapsAsync());
        Assert.Contains("No permissions", error.Message);
    }

    sealed class ErrorHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"jsonrpc":"2.0","error":{"code":-32602,"message":"Invalid params.","data":"No permissions"},"id":1}"""),
            });
    }

    static JsonObject MapJson(string selement) => JsonNode.Parse($$"""
        {"sysmapid":"5","name":"M","width":"800","height":"600","custom_prop":"x",
         "selements":[{{selement}}],
         "links":[{"linkid":"7","selementid1":"10","selementid2":"10","color":"00CC00","drawtype":"2","label":"L",
                   "linktriggers":[{"linktriggerid":"3","triggerid":"44","color":"DD0000","drawtype":"4"}]}]}
        """)!.AsObject();

    [Fact]
    public async Task Save_ModernServer_KeepsUnknownFieldsAndUsesElementsArray()
    {
        var server = new FakeZabbixServer("7.0.0");
        server.Methods["host.get"] = _ => JsonValue.Create("0");
        using var client = Client(server);
        await client.ConnectWithTokenAsync("t");

        var map = SysMap.FromJson(MapJson("""{"selementid":"10","elementtype":"0","elements":[{"hostid":"100"}],"iconid_off":"9","x":"10","y":"20","label":"a","urls":[{"name":"u","url":"http://x"}]}"""));
        Assert.Equal("100", map.Elements[0].RefId);
        map.Elements[0].X = 123.7;
        await client.SaveMapAsync(map);

        var sent = server.Last("map.update")["params"]![0]!;
        var element = sent["selements"]![0]!;
        Assert.Equal("100", element["elements"]![0]!["hostid"]!.ToString());
        Assert.Null(element["elementid"]);
        Assert.Equal(123, element["x"]!.GetValue<int>());
        Assert.Equal("http://x", element["urls"]![0]!["url"]!.ToString());
        Assert.Equal("44", sent["links"]![0]!["linktriggers"]![0]!["triggerid"]!.ToString());
        Assert.False(map.IsDirty);
    }

    [Fact]
    public async Task Save_Version2_UsesElementId()
    {
        var server = new FakeZabbixServer("2.0.17");
        server.Methods["host.get"] = _ => JsonValue.Create("0");
        using var client = Client(server);
        await client.ConnectWithTokenAsync("t");

        var map = SysMap.FromJson(MapJson("""{"selementid":"10","elementtype":"0","elementid":"100","iconid_off":"9","x":"10","y":"20","label":"a"}"""));
        Assert.Equal("100", map.Elements[0].RefId);
        await client.SaveMapAsync(map);

        var element = server.Last("map.update")["params"]![0]!["selements"]![0]!;
        Assert.Equal("100", element["elementid"]!.ToString());
        Assert.Null(element["elements"]);
    }

    [Fact]
    public void NewElementsAndLinks_ShareTemporaryIds()
    {
        var map = SysMap.FromJson(MapJson("""{"selementid":"10","elementtype":"0","elements":[{"hostid":"100"}],"x":"0","y":"0","label":""}"""));
        var a = map.AddElement(ElementType.Host, "1", "{HOST.NAME}", "9", 5000, 10);
        var b = map.AddElement(ElementType.Host, "2", "{HOST.NAME}", "9", 20, 3000);
        map.AddLink(a, b);
        var json = map.ToUpdateJson(true);
        Assert.Equal(a.Id, json["links"]![1]!["selementid1"]!.ToString());
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(5100, json["width"]!.GetValue<int>());
        Assert.Equal(3100, json["height"]!.GetValue<int>());
    }

    [Fact]
    public void RemovingElement_RemovesItsLinks()
    {
        var map = SysMap.FromJson(MapJson("""{"selementid":"10","elementtype":"0","elements":[{"hostid":"100"}],"x":"0","y":"0","label":""}"""));
        map.RemoveElement(map.Elements[0]);
        Assert.Empty(map.Links);
        Assert.True(map.IsDirty);
    }

    [Fact]
    public async Task Hosts_AvailabilityFollowsVersion()
    {
        var modern = new FakeZabbixServer("7.0.0");
        modern.Methods["host.get"] = _ => JsonNode.Parse("""[{"hostid":"1","host":"a","name":"A","status":"0","interfaces":[{"main":"1","useip":"1","ip":"10.0.0.1","available":"2"}]}]""")!;
        using var modernClient = Client(modern);
        await modernClient.ConnectWithTokenAsync("t");
        var host = Assert.Single(await modernClient.GetHostsAsync());
        Assert.True(host.Unavailable);
        Assert.Equal("10.0.0.1", host.Ip);

        var legacy = new FakeZabbixServer("2.0.17");
        legacy.Methods["host.get"] = _ => JsonNode.Parse("""[{"hostid":"1","host":"a","name":"A","status":"0","available":"2","snmp_available":"0","interfaces":[]}]""")!;
        using var legacyClient = Client(legacy);
        await legacyClient.ConnectWithTokenAsync("t");
        Assert.True(Assert.Single(await legacyClient.GetHostsAsync()).Unavailable);
    }
}

public class MonitoringTests
{
    static HostInfo Host(string id, bool unavailable = false, bool monitored = true) => new(id, "h" + id, "Host " + id, "10.0.0." + id, monitored, unavailable);
    static TriggerInfo Trigger(string host, int severity, string text) => new("t" + host + severity, text, severity, DateTimeOffset.UnixEpoch, [host]);
    static MonitoringSnapshot Snap(HostInfo[] hosts, params TriggerInfo[] triggers) => new(hosts, [], triggers);

    [Fact]
    public void Down_RequiresOutageTriggerOrUnavailableInterface()
    {
        var snapshot = Snap([Host("1"), Host("2"), Host("3", unavailable: true), Host("4")],
            Trigger("1", 4, "Unavailable by ICMP ping"),
            Trigger("2", 4, "High CPU utilization"),
            Trigger("4", 2, "Interface eth0: Link down"));
        Assert.True(snapshot.IsDown("1"));
        Assert.False(snapshot.IsDown("2"));
        Assert.True(snapshot.IsDown("3"));
        Assert.False(snapshot.IsDown("4"));
        Assert.Equal(NodeHealth.Problem, snapshot.HostStatus("2").Health);
    }

    [Fact]
    public void DisabledHost_IsNeverDown()
    {
        var snapshot = Snap([Host("1", unavailable: true, monitored: false)]);
        Assert.False(snapshot.IsDown("1"));
        Assert.Equal(NodeHealth.Unknown, snapshot.HostStatus("1").Health);
    }

    [Fact]
    public void Tracker_BaselineThenTransitions()
    {
        var tracker = new OutageTracker();
        var first = tracker.Update(Snap([Host("1", true), Host("2")]));
        Assert.Empty(first.WentDown);
        Assert.Equal(1, first.Baseline);

        var second = tracker.Update(Snap([Host("1", true), Host("2", true)]));
        Assert.Equal("2", Assert.Single(second.WentDown).Id);

        var third = tracker.Update(Snap([Host("1"), Host("2", true)]));
        Assert.Equal("1", Assert.Single(third.Recovered).Id);
        Assert.Empty(third.WentDown);
    }

    [Fact]
    public void Submap_CycleDoesNotOverflow()
    {
        var a = SysMap.FromJson(JsonNode.Parse("""{"sysmapid":"1","name":"a","selements":[{"selementid":"1","elementtype":"1","elements":[{"sysmapid":"2"}]},{"selementid":"2","elementtype":"0","elements":[{"hostid":"1"}]}]}""")!.AsObject());
        var b = SysMap.FromJson(JsonNode.Parse("""{"sysmapid":"2","name":"b","selements":[{"selementid":"3","elementtype":"1","elements":[{"sysmapid":"1"}]},{"selementid":"4","elementtype":"0","elements":[{"hostid":"2"}]}]}""")!.AsObject());
        var snapshot = Snap([Host("1"), Host("2", true)]);
        var maps = new Dictionary<string, SysMap> { ["1"] = a, ["2"] = b };
        Assert.Equal(NodeHealth.Down, snapshot.StatusOf(a.Elements[0], maps).Health);
    }

    [Theory]
    [InlineData("1090000000", "bps", "1.09 Gbps")]
    [InlineData("2048", "B", "2 KB")]
    [InlineData("0", "bps", "0 bps")]
    [InlineData("42.5", "%", "42.5%")]
    [InlineData("abc", "", "abc")]
    [InlineData("", "bps", "")]
    public void FormatsValues(string value, string units, string expected) =>
        Assert.Equal(expected, LabelResolver.FormatValue(value, units));

    [Theory]
    [InlineData("Rx: {?last(/h1/net.if.in[ifHCInOctets.1])}")]
    [InlineData("Rx: {h1:net.if.in[ifHCInOctets.1].last()}")]
    [InlineData("Rx: {h1:net.if.in[ifHCInOctets.1].last(0)}")]
    public void ResolvesBothMacroSyntaxes(string label)
    {
        var snapshot = Snap([Host("1")]);
        snapshot.Items = new Dictionary<(string, string), ItemInfo>
        {
            [("1", "net.if.in[ifHCInOctets.1]")] = new("1", "n", "net.if.in[ifHCInOctets.1]", "2790000000", "bps"),
        };
        Assert.Equal("Rx: 2.79 Gbps", LabelResolver.Resolve(label, null, snapshot));
        Assert.Equal(("h1", "net.if.in[ifHCInOctets.1]"), Assert.Single(LabelResolver.ReferencedItems(label)));
    }

    [Fact]
    public void UnknownItem_ShowsQuestionMark() =>
        Assert.Equal("Rx: ?", LabelResolver.Resolve("Rx: {?last(/none/k)}", null, Snap([Host("1")])));
}
