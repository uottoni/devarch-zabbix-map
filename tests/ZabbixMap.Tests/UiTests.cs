using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using ZabbixMap.UI;
using ZabbixMap.UI.Controls;
using ZabbixMap.Core;

namespace ZabbixMap.Tests;

public class UiTests
{
    internal static FakeClient Network()
    {
        (string Id, string Label, int X, int Y)[] nodes =
        [
            ("1", "{HOST.NAME}\nRouter", 800, 180), ("2", "{HOST.NAME}", 500, 620), ("3", "{HOST.NAME}", 1100, 400),
            ("4", "{HOST.NAME}", 200, 300), ("5", "{HOST.NAME}\n{HOST.IP}", 1300, 100), ("6", "{HOST.NAME}", 900, 620),
        ];
        string[] names = ["ASBR_01", "Core_01", "NAT", "SW_01", "SW_02", "ABR_01"];
        var client = new FakeClient();
        for (var i = 0; i < names.Length; i++)
            client.Hosts.Add(new((i + 1).ToString(), names[i], names[i], "10.0.0." + (i + 1), true, false));
        client.Hosts[2] = client.Hosts[2] with { Unavailable = true };
        client.Triggers.Add(new("90", "High CPU utilization", 3, DateTimeOffset.UnixEpoch, ["4"]));
        client.Items.Add(new("1", "In", "net.if.in[1]", "7290000000", "bps"));
        client.Items.Add(new("1", "Out", "net.if.out[1]", "1380000000", "bps"));

        var selements = new JsonArray(nodes.Select(n => (JsonNode)new JsonObject
        {
            ["selementid"] = n.Id, ["elementtype"] = "0", ["elements"] = new JsonArray(new JsonObject { ["hostid"] = n.Id }),
            ["iconid_off"] = "1", ["x"] = n.X.ToString(), ["y"] = n.Y.ToString(), ["label"] = n.Label,
        }).ToArray());
        JsonNode Link(string a, string b, string label, int draw = 2, string color = "000000") => new JsonObject
        {
            ["linkid"] = a + b, ["selementid1"] = a, ["selementid2"] = b, ["color"] = color, ["drawtype"] = draw, ["label"] = label,
        };
        client.Map = SysMap.FromJson(new JsonObject
        {
            ["sysmapid"] = "5", ["name"] = "Rede", ["width"] = 1600, ["height"] = 800, ["selements"] = selements,
            ["links"] = new JsonArray(
                Link("1", "2", "Rx: {?last(/ASBR_01/net.if.in[1])}\nTx: {?last(/ASBR_01/net.if.out[1])}"),
                Link("1", "3", ""), Link("2", "6", "", 4, "7777FF"), Link("2", "4", "", 4, "7777FF"), Link("1", "5", "", 4)),
        });
        return client;
    }

    static async Task<MainWindow> Open(FakeClient client)
    {
        var window = new MainWindow();
        window.View.Notify = (_, _) => { };
        window.Show();
        await window.View.AttachAsync(client);
        await window.View.OpenMapAsync("5");
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    static T Find<T>(MainWindow window, string name) where T : Control => window.View.FindControl<T>(name)!;
    static Point Screen(MapCanvas canvas, MapElement e) => canvas.TranslatePoint(new Point(e.X * canvas.Zoom + canvas.Offset.X, e.Y * canvas.Zoom + canvas.Offset.Y), TopLevel.GetTopLevel(canvas)!)!.Value;

    [AvaloniaFact]
    public async Task RendersMap_WithStatusColorsAndMiniMap()
    {
        var window = await Open(Network());
        var frame = window.CaptureRenderedFrame()!;
        if (Environment.GetEnvironmentVariable("ZABBIXMAP_SHOT") is { } path) frame.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

        var canvas = Find<MapCanvas>(window, "Canvas");
        var nat = canvas.Map!.Elements.Single(e => e.RefId == "3");
        Assert.Equal(NodeHealth.Down, canvas.StatusOf(nat).Health);
        Assert.Equal(NodeHealth.Problem, canvas.StatusOf(canvas.Map.Elements.Single(e => e.RefId == "4")).Health);
        Assert.Equal(NodeHealth.Ok, canvas.StatusOf(canvas.Map.Elements.Single(e => e.RefId == "1")).Health);
    }

    [AvaloniaFact]
    public async Task Locked_DragPans_DoesNotMoveNode()
    {
        var window = await Open(Network());
        var canvas = Find<MapCanvas>(window, "Canvas");
        var node = canvas.Map!.Elements[0];
        var start = Screen(canvas, node);
        var x = node.X;
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(50, 50));
        window.MouseUp(start + new Point(50, 50), MouseButton.Left);
        Assert.Equal(x, node.X);
        Assert.False(canvas.Map.IsDirty);
    }

    [AvaloniaFact]
    public async Task Unlocked_DragMovesNode_MarksDirty_AndSaves()
    {
        var client = Network();
        var window = await Open(client);
        Find<ToggleButton>(window, "EditToggle").IsChecked = true;
        var canvas = Find<MapCanvas>(window, "Canvas");
        var node = canvas.Map!.Elements[0];
        var start = Screen(canvas, node);
        var x = node.X;
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(60, 0));
        window.MouseUp(start + new Point(60, 0), MouseButton.Left);

        Assert.Equal(x + 60 / canvas.Zoom, node.X, 3);
        Assert.StartsWith("* ", window.Title);

        window.KeyPressQwerty(PhysicalKey.S, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, client.Saves);
    }

    [AvaloniaFact]
    public async Task LinkMode_DrawsLink()
    {
        var window = await Open(Network());
        Find<ToggleButton>(window, "EditToggle").IsChecked = true;
        Find<ToggleButton>(window, "LinkToggle").IsChecked = true;
        var canvas = Find<MapCanvas>(window, "Canvas");
        var before = canvas.Map!.Links.Count;
        var a = canvas.Map.Elements.Single(e => e.RefId == "4");
        var b = canvas.Map.Elements.Single(e => e.RefId == "3");
        window.MouseDown(Screen(canvas, a), MouseButton.Left);
        window.MouseMove(Screen(canvas, b));
        window.MouseUp(Screen(canvas, b), MouseButton.Left);
        Assert.Equal(before + 1, canvas.Map.Links.Count);
    }

    [AvaloniaFact]
    public async Task MiniMapClick_MovesViewport()
    {
        var window = await Open(Network());
        var canvas = Find<MapCanvas>(window, "Canvas");
        var overview = Find<MiniMap>(window, "Overview");
        canvas.SetZoom(2);
        var before = canvas.WorldViewport.Center;
        var corner = overview.TranslatePoint(new Point(overview.Bounds.Width - 5, overview.Bounds.Height / 2 + 30), window)!.Value;
        window.MouseDown(corner, MouseButton.Left);
        window.MouseUp(corner, MouseButton.Left);
        Assert.NotEqual(before, canvas.WorldViewport.Center);
    }

    [AvaloniaFact]
    public async Task Search_FocusesHost_AndFiltersTree()
    {
        var window = await Open(Network());
        var canvas = Find<MapCanvas>(window, "Canvas");
        var search = Find<TextBox>(window, "SearchBox");
        search.Text = "nat";
        search.Focus();
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("3", Assert.Single(canvas.Selected).RefId);
        var center = canvas.WorldViewport.Center;
        Assert.Equal(canvas.Selected.First().X, center.X, 1);
    }

    [AvaloniaFact]
    public async Task HostGoingDown_NotifiesOnce_AndRecovers()
    {
        var client = Network();
        var window = await Open(client);
        var notes = new List<string>();
        window.View.Notify = (title, host) => notes.Add($"{title}:{host}");
        var counts = new List<int>();
        window.View.DownCountChanged += counts.Add;

        await window.View.RefreshAsync();
        Assert.Empty(notes);

        client.Hosts[0] = client.Hosts[0] with { Unavailable = true };
        await window.View.RefreshAsync();
        await window.View.RefreshAsync();
        Assert.Equal(["Host parado:ASBR_01"], notes);
        Assert.Equal(2, counts[^1]);

        client.Hosts[0] = client.Hosts[0] with { Unavailable = false };
        await window.View.RefreshAsync();
        Assert.Equal("Host recuperado:ASBR_01", notes[^1]);
        Assert.Equal(1, counts[^1]);
    }

    [AvaloniaFact]
    public async Task ServerFailure_IsShown_NotSwallowed()
    {
        var client = new ThrowingClient(Network());
        var window = await Open(client.Inner);
        await window.View.AttachAsync(client);
        Assert.Contains("falha ao atualizar", Find<TextBlock>(window, "StatusText").Text);
    }

    sealed class ThrowingClient(FakeClient inner) : IZabbixClient
    {
        public FakeClient Inner => inner;
        public string BaseUrl => inner.BaseUrl;
        public bool UsesExpressionMacros => true;
        public Task<IReadOnlyList<MapSummary>> GetMapsAsync(CancellationToken ct = default) => inner.GetMapsAsync(ct);
        public Task<SysMap?> GetMapAsync(string id, CancellationToken ct = default) => inner.GetMapAsync(id, ct);
        public Task<string> CreateMapAsync(string name, CancellationToken ct = default) => inner.CreateMapAsync(name, ct);
        public Task SaveMapAsync(SysMap map, CancellationToken ct = default) => inner.SaveMapAsync(map, ct);
        public Task DeleteMapAsync(string id, CancellationToken ct = default) => inner.DeleteMapAsync(id, ct);
        public Task<string> GetDefaultIconIdAsync(CancellationToken ct = default) => inner.GetDefaultIconIdAsync(ct);
        public Task<IReadOnlyList<HostGroupInfo>> GetHostGroupsAsync(CancellationToken ct = default) => inner.GetHostGroupsAsync(ct);
        public Task<IReadOnlyList<HostInfo>> GetHostsAsync(CancellationToken ct = default) => throw new ZabbixApiException("offline");
        public Task<IReadOnlyList<TriggerInfo>> GetActiveTriggersAsync(CancellationToken ct = default) => inner.GetActiveTriggersAsync(ct);
        public Task<IReadOnlyList<ItemInfo>> GetItemsAsync(IEnumerable<string> hostIds, string? keySearch = null, CancellationToken ct = default) => inner.GetItemsAsync(hostIds, keySearch, ct);
        public Task<IReadOnlyList<HostItem>> GetHostItemsAsync(string hostId, CancellationToken ct = default) => inner.GetHostItemsAsync(hostId, ct);
        public Task<IReadOnlyList<HistoryPoint>> GetHistoryAsync(HostItem item, TimeSpan period, CancellationToken ct = default) => inner.GetHistoryAsync(item, period, ct);
    }
}
