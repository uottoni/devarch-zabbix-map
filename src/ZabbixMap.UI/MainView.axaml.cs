using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using ZabbixMap.UI.Controls;
using ZabbixMap.UI.Services;
using ZabbixMap.Core;

namespace ZabbixMap.UI;

public partial class MainView : UserControl
{
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    static readonly DataFormat<string> HostDragFormat = DataFormat.CreateStringApplicationFormat("zabbixmap-host");

    readonly ObservableCollection<TreeNode> _roots = [];
    readonly TreeNode _mapsNode = new(TreeNodeKind.Section, "Mapas") { IsExpanded = true };
    readonly TreeNode _groupsNode = new(TreeNodeKind.Section, "Hosts") { IsExpanded = true };
    readonly TreeNode _problemsNode = new(TreeNodeKind.Section, "Problemas");
    readonly DispatcherTimer _timer = new() { Interval = PollInterval };
    readonly Settings _settings;

    IZabbixClient? _client;
    OutageTracker _outages = new();
    MonitoringSnapshot? _snapshot;
    IReadOnlyList<HostGroupInfo> _groups = [];
    string? _iconId;
    bool _refreshing;
    bool _started;
    int _searchIndex;
    string _treeSignature = "";

    public event Action<int>? DownCountChanged;
    public event Action<string>? TitleChanged;
    public string Title { get; private set; } = "ZabbixMap";
    public Action<string, string> Notify { get; set; } = DesktopNotifier.Show;
    public ISecretStore Secrets { get; set; } = SecretStores.CreateDefault();
    public Func<ConnectionInput, Task<(IZabbixClient Client, string Secret)>> Connect { get; set; } = ConnectToServerAsync;
    public Func<string?, Task<ConnectionInput?>> AskConnection { get; set; }

    public MainView() : this(Settings.Load())
    {
    }

    public MainView(Settings settings)
    {
        _settings = settings;
        AskConnection = AskConnectionAsync;
        InitializeComponent();
        _roots.Add(_mapsNode);
        _roots.Add(_groupsNode);
        _roots.Add(_problemsNode);
        Tree.ItemsSource = _roots;
        Overview.Canvas = Canvas;
        ZoomBox.ItemsSource = new[] { "25%", "50%", "75%", "100%", "150%", "200%" };

        _timer.Tick += async (_, _) => await RefreshAsync();
        ConnectButton.Click += async (_, _) => await ShowConnectionAsync(null);
        ReloadButton.Click += async (_, _) => await RefreshAsync();
        NewMapButton.Click += async (_, _) => await GuardAsync(CreateMapAsync);
        DeleteMapButton.Click += async (_, _) => await GuardAsync(DeleteMapAsync);
        AddHostButton.Click += async (_, _) => await GuardAsync(AddSelectedTreeHostAsync);
        DeleteButton.Click += (_, _) => DeleteSelection();
        SaveButton.Click += async (_, _) => await GuardAsync(SaveAsync);
        FitButton.Click += (_, _) => Canvas.FitToView();
        EditToggle.IsCheckedChanged += (_, _) => ApplyEditState();
        LinkToggle.IsCheckedChanged += (_, _) => Canvas.LinkMode = LinkToggle.IsChecked == true;
        TrafficCheck.IsCheckedChanged += (_, _) =>
        {
            Canvas.ShowTraffic = TrafficCheck.IsChecked == true;
            Canvas.InvalidateVisual();
        };
        ZoomBox.SelectionChanged += (_, _) =>
        {
            if (ZoomBox.SelectedItem is string s) Canvas.SetZoom(double.Parse(s.TrimEnd('%')) / 100);
        };

        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                FocusNextMatch();
                e.Handled = true;
            }
        };
        SearchBox.TextChanged += (_, _) =>
        {
            _searchIndex = 0;
            RebuildHostTree(force: true);
        };

        Tree.DoubleTapped += async (_, _) => await GuardAsync(ActivateTreeSelectionAsync);
        Tree.ContextRequested += OnTreeContext;
        Tree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);
        Tree.AddHandler(PointerMovedEvent, OnTreePointerMoved, RoutingStrategies.Tunnel);

        Canvas.Edited += UpdateTitle;
        Canvas.ViewChanged += () => StatusZoom();
        Canvas.ElementActivated += async e => await GuardAsync(() => ActivateElementAsync(e));
        Canvas.LinkDrawn += (a, b) =>
        {
            Canvas.Map?.AddLink(a, b);
            Canvas.Layout();
            Canvas.InvalidateVisual();
            UpdateTitle();
        };
        Canvas.ContextRequested += OnCanvasContext;
        Canvas.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.DataTransfer.Contains(HostDragFormat) && EditToggle.IsChecked == true ? DragDropEffects.Copy : DragDropEffects.None;
        });
        Canvas.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (e.DataTransfer.TryGetValue(HostDragFormat) is { } hostId && EditToggle.IsChecked == true)
            {
                var world = Canvas.ToWorld(e.GetPosition(Canvas));
                await GuardAsync(() => AddHostAsync(hostId, world));
            }
        });

        KeyDown += OnWindowKeyDown;
        ApplyEditState();
    }

    /// <summary>Connects with the saved credential, or asks for one. Runs once: Opened fires again on every restore from the tray.</summary>
    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;

        if (_settings.Url != "" && !string.IsNullOrEmpty(_settings.Token))
        {
            await Secrets.SetAsync(_settings.Url, _settings.Token);
            _settings.Token = null;
            _settings.Save();
        }

        var secret = _settings.Url == "" ? null : await Secrets.GetAsync(_settings.Url);
        if (secret == null)
        {
            await ShowConnectionAsync(null);
            return;
        }
        try
        {
            var (client, _) = await Connect(new ConnectionInput(_settings.Url, secret, "", "", _settings.IgnoreCertificateErrors));
            await AttachAsync(client);
            if (_settings.LastMapId != "") await OpenMapAsync(_settings.LastMapId);
        }
        catch (ZabbixApiException e)
        {
            // The server rejected the credential; an unreachable server keeps it for the next try.
            await Secrets.DeleteAsync(_settings.Url);
            await ShowConnectionAsync(_settings.SessionLogin ? "A sessão salva expirou. Entre novamente." : e.Message);
        }
        catch (Exception e) when (IsConnectionError(e))
        {
            await ShowConnectionAsync(e.Message);
        }
    }

    static bool IsConnectionError(Exception e) =>
        e is ZabbixApiException or HttpRequestException or TaskCanceledException or FormatException or UriFormatException;

    async Task ShowConnectionAsync(string? error)
    {
        while (true)
        {
            var input = await AskConnection(error);
            if (input == null) return;
            try
            {
                await ConnectAndSaveAsync(input);
                return;
            }
            catch (Exception e) when (IsConnectionError(e))
            {
                error = e.Message;
            }
        }
    }

    /// <summary>Connects and stores the credential: the API token, or the session a password login returns. The password itself is never stored.</summary>
    public async Task ConnectAndSaveAsync(ConnectionInput input)
    {
        var (client, secret) = await Connect(input);
        if (_settings.Url != "" && _settings.Url != client.BaseUrl) await Secrets.DeleteAsync(_settings.Url);
        _settings.Url = client.BaseUrl;
        _settings.User = input.User;
        _settings.IgnoreCertificateErrors = input.IgnoreCertificateErrors;
        _settings.SessionLogin = input.Password != "";
        _settings.Save();
        await Secrets.SetAsync(client.BaseUrl, secret);
        await AttachAsync(client);
    }

    static async Task<(IZabbixClient Client, string Secret)> ConnectToServerAsync(ConnectionInput input)
    {
        var client = new ZabbixClient(input.Url, input.IgnoreCertificateErrors);
        try
        {
            if (input.Password != "") await client.ConnectWithPasswordAsync(input.User, input.Password);
            else await client.ConnectWithTokenAsync(input.Token);
            return (client, client.AuthToken!);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    async Task<ConnectionInput?> AskConnectionAsync(string? error)
    {
        var savedToken = _settings.Url == "" || _settings.SessionLogin ? "" : await Secrets.GetAsync(_settings.Url) ?? "";
        return await Dialogs.ConnectionAsync(this, _settings, savedToken, error);
    }

    public async Task AttachAsync(IZabbixClient client)
    {
        (_client as IDisposable)?.Dispose();
        _client = client;
        _outages = new OutageTracker();
        _iconId = null;
        Canvas.SetMap(null);
        await ReloadMapListAsync();
        await RefreshAsync();
        _timer.Start();
    }

    async Task ReloadMapListAsync()
    {
        if (_client == null) return;
        var maps = await _client.GetMapsAsync();
        _mapsNode.Children.Clear();
        foreach (var map in maps) _mapsNode.Children.Add(new TreeNode(TreeNodeKind.Map, map.Name, map.Id));
    }

    public async Task RefreshAsync()
    {
        if (_client == null || _refreshing) return;
        _refreshing = true;
        try
        {
            var hostsTask = _client.GetHostsAsync();
            var groupsTask = _client.GetHostGroupsAsync();
            var triggersTask = _client.GetActiveTriggersAsync();
            await Task.WhenAll(hostsTask, groupsTask, triggersTask);

            var snapshot = new MonitoringSnapshot(hostsTask.Result, groupsTask.Result, triggersTask.Result);
            _groups = groupsTask.Result;
            await LoadReferencedItemsAsync(snapshot);
            _snapshot = snapshot;
            Canvas.SetSnapshot(snapshot);
            Overview.InvalidateVisual();
            RebuildHostTree();
            UpdateProblems(snapshot);
            ReportOutages(snapshot);
            StatusText.Text = $"conectado a {_client.BaseUrl}{Version()} · atualizado às {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception e) when (e is ZabbixApiException or HttpRequestException or TaskCanceledException)
        {
            // Stale colors would look like a healthy network, so the failure stays visible until the next good poll.
            StatusText.Text = $"falha ao atualizar: {e.Message}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    async Task LoadReferencedItemsAsync(MonitoringSnapshot snapshot)
    {
        if (_client == null || Canvas.Map is not { } map) return;
        var labels = map.Elements.Select(e => e.Label).Concat(map.Links.Select(l => l.Label));
        var byName = snapshot.Hosts.Values.ToLookup(h => h.Host);
        var hostIds = labels.SelectMany(LabelResolver.ReferencedItems)
            .SelectMany(r => byName[r.HostName]).Select(h => h.Id).Distinct().ToList();
        if (hostIds.Count == 0) return;
        var items = await _client.GetItemsAsync(hostIds);
        snapshot.Items = items.GroupBy(i => (i.HostId, i.Key)).ToDictionary(g => g.Key, g => g.First());
    }

    string Version() => _client is ZabbixClient { ServerVersion: var v } ? $" (Zabbix {v})" : "";

    void ReportOutages(MonitoringSnapshot snapshot)
    {
        var (wentDown, recovered, baseline) = _outages.Update(snapshot);
        foreach (var host in wentDown)
        {
            ShowToast($"PARADO: {host.Name}", true);
            Notify("Host parado", host.Name);
        }
        foreach (var host in recovered)
        {
            ShowToast($"Recuperado: {host.Name}", false);
            Notify("Host recuperado", host.Name);
        }
        if (baseline > 0) ShowToast($"{baseline} host(s) parado(s) ao conectar", true);

        var count = snapshot.DownHostIds.Count();
        DownText.Text = count > 0 ? $"⚑ {count} host(s) parado(s)" : "";
        DownCountChanged?.Invoke(count);
    }

    void ShowToast(string text, bool bad)
    {
        var toast = new Border
        {
            Background = new SolidColorBrush(Color.Parse(bad ? "#D32F2F" : "#2E7D32")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8),
            Child = new TextBlock { Text = text, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold },
        };
        Toasts.Children.Add(toast);
        while (Toasts.Children.Count > 6) Toasts.Children.RemoveAt(0);
        DispatcherTimer.RunOnce(() => Toasts.Children.Remove(toast), TimeSpan.FromSeconds(10));
    }

    void RebuildHostTree(bool force = false)
    {
        if (_snapshot == null) return;
        var filter = SearchBox.Text?.Trim() ?? "";
        var signature = filter + "|" + string.Join(',', _groups.Select(g => g.Id + ":" + string.Join('.', g.HostIds)));
        if (force || signature != _treeSignature)
        {
            _treeSignature = signature;
            _groupsNode.Children.Clear();
            foreach (var group in _groups)
            {
                var node = new TreeNode(TreeNodeKind.Group, group.Name, group.Id) { IsExpanded = filter != "" };
                foreach (var hostId in group.HostIds)
                    if (_snapshot.Hosts.TryGetValue(hostId, out var host) && Matches(host, filter))
                        node.Children.Add(new TreeNode(TreeNodeKind.Host, host.Name, host.Id));
                if (node.Children.Count > 0) _groupsNode.Children.Add(node);
            }
        }
        foreach (var group in _groupsNode.Children)
        {
            foreach (var host in group.Children) host.Dot = DotBrush(_snapshot.HostStatus(host.Id));
            group.Dot = DotBrush(_snapshot.Aggregate(group.Children.Select(h => _snapshot.HostStatus(h.Id))));
        }
    }

    static bool Matches(HostInfo host, string filter) =>
        filter == "" || host.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        host.Host.Contains(filter, StringComparison.OrdinalIgnoreCase) || host.Ip.Contains(filter, StringComparison.OrdinalIgnoreCase);

    static IBrush DotBrush(ElementStatus status) => new SolidColorBrush(MapCanvas.HealthColor(status));

    void UpdateProblems(MonitoringSnapshot snapshot)
    {
        _problemsNode.Title = $"Problemas ({snapshot.Triggers.Count})";
        _problemsNode.Children.Clear();
        foreach (var trigger in snapshot.Triggers.Take(500))
        {
            var hostId = trigger.HostIds.FirstOrDefault() ?? "";
            var hostName = snapshot.Hosts.GetValueOrDefault(hostId)?.Name ?? "?";
            _problemsNode.Children.Add(new TreeNode(TreeNodeKind.Problem, $"{hostName}: {trigger.Description}", hostId)
            {
                Dot = DotBrush(new(NodeHealth.Problem, trigger.Severity, [trigger])),
            });
        }
    }

    public async Task OpenMapAsync(string id)
    {
        if (_client == null || !await ConfirmDiscardAsync()) return;
        var map = await _client.GetMapAsync(id);
        if (map == null)
        {
            StatusText.Text = "Mapa não encontrado.";
            return;
        }
        Canvas.SetMap(map, await LoadSubmapsAsync(map));
        _settings.LastMapId = id;
        _settings.Save();
        UpdateTitle();
        await RefreshAsync();
    }

    async Task<Dictionary<string, SysMap>> LoadSubmapsAsync(SysMap root)
    {
        var loaded = new Dictionary<string, SysMap>();
        var pending = new Queue<SysMap>([root]);
        while (pending.TryDequeue(out var current))
            foreach (var element in current.Elements.Where(e => e.Type == ElementType.Map && !loaded.ContainsKey(e.RefId)))
                if (await _client!.GetMapAsync(element.RefId) is { } submap)
                {
                    loaded[element.RefId] = submap;
                    pending.Enqueue(submap);
                }
        return loaded;
    }

    public async Task<bool> ConfirmDiscardAsync() =>
        Canvas.Map?.IsDirty != true || await Dialogs.ConfirmAsync(this, "Alterações não salvas", $"Descartar as alterações em \"{Canvas.Map.Name}\"?");

    async Task SaveAsync()
    {
        if (_client == null || Canvas.Map is not { } map) return;
        await _client.SaveMapAsync(map);
        // Reload so temporary ids become the ids the server assigned.
        var viewZoom = Canvas.Zoom;
        var viewOffset = Canvas.WorldViewport.Center;
        if (await _client.GetMapAsync(map.Id) is { } saved)
        {
            Canvas.SetMap(saved, await LoadSubmapsAsync(saved));
            Canvas.SetZoom(viewZoom);
            Canvas.CenterOn(viewOffset);
        }
        UpdateTitle();
        StatusText.Text = $"\"{map.Name}\" salvo no Zabbix";
        await RefreshAsync();
    }

    async Task CreateMapAsync()
    {
        if (_client == null) return;
        var name = await Dialogs.PromptAsync(this, "Novo mapa", "Nome do mapa");
        if (string.IsNullOrWhiteSpace(name)) return;
        var id = await _client.CreateMapAsync(name.Trim());
        await ReloadMapListAsync();
        await OpenMapAsync(id);
        EditToggle.IsChecked = true;
    }

    async Task DeleteMapAsync()
    {
        if (_client == null) return;
        var (id, name) = Tree.SelectedItem is TreeNode { Kind: TreeNodeKind.Map } node ? (node.Id, node.Title) : (Canvas.Map?.Id, Canvas.Map?.Name);
        if (id == null) return;
        if (!await Dialogs.ConfirmAsync(this, "Excluir mapa", $"Excluir \"{name}\" do Zabbix? Esta ação não pode ser desfeita.")) return;
        await _client.DeleteMapAsync(id);
        if (Canvas.Map?.Id == id)
        {
            Canvas.SetMap(null);
            UpdateTitle();
        }
        await ReloadMapListAsync();
    }

    async Task AddSelectedTreeHostAsync()
    {
        if (Tree.SelectedItem is TreeNode { Kind: TreeNodeKind.Host } node)
            await AddHostAsync(node.Id, Canvas.WorldViewport.Center);
        else
            StatusText.Text = "Selecione um host na árvore.";
    }

    async Task AddHostAsync(string hostId, Point world)
    {
        if (_client == null || Canvas.Map is not { } map || EditToggle.IsChecked != true) return;
        _iconId ??= await _client.GetDefaultIconIdAsync();
        map.AddElement(ElementType.Host, hostId, "{HOST.NAME}", _iconId, world.X, world.Y);
        Canvas.Layout();
        Canvas.InvalidateVisual();
        Overview.InvalidateVisual();
        UpdateTitle();
    }

    void DeleteSelection()
    {
        if (EditToggle.IsChecked != true || Canvas.Map is not { } map) return;
        foreach (var element in Canvas.Selected.ToList()) map.RemoveElement(element);
        if (Canvas.SelectedLink is { } link) map.RemoveLink(link);
        Canvas.Layout();
        Canvas.InvalidateVisual();
        UpdateTitle();
    }

    async Task ActivateTreeSelectionAsync()
    {
        switch (Tree.SelectedItem)
        {
            case TreeNode { Kind: TreeNodeKind.Map } map:
                await OpenMapAsync(map.Id);
                break;
            case TreeNode { Kind: TreeNodeKind.Host or TreeNodeKind.Problem } host:
                FocusHost(host.Id);
                break;
        }
    }

    void FocusHost(string hostId)
    {
        var element = Canvas.Map?.Elements.FirstOrDefault(e => e.Type == ElementType.Host && e.RefId == hostId);
        if (element != null) Canvas.Focus(element);
        else StatusText.Text = "Este host não está no mapa aberto.";
    }

    void FocusNextMatch()
    {
        var filter = SearchBox.Text?.Trim() ?? "";
        if (filter == "" || Canvas.Map is not { } map || _snapshot == null) return;
        var matches = map.Elements.Where(e => e.Type == ElementType.Host && _snapshot.Hosts.TryGetValue(e.RefId, out var h) && Matches(h, filter)).ToList();
        if (matches.Count == 0)
        {
            StatusText.Text = $"\"{filter}\" não está no mapa aberto.";
            return;
        }
        Canvas.Focus(matches[_searchIndex++ % matches.Count]);
    }

    async Task ActivateElementAsync(MapElement element)
    {
        if (element.Type == ElementType.Map) await OpenMapAsync(element.RefId);
        else if (element.Type == ElementType.Host) ShowHostSummary(element.RefId);
    }

    /// <summary>Opens the latest-data summary; non-modal so several hosts can be compared.</summary>
    public HostSummaryView? ShowHostSummary(string hostId)
    {
        if (_client == null || _snapshot == null || !_snapshot.Hosts.TryGetValue(hostId, out var host)) return null;
        var view = new HostSummaryView(_client, host, _snapshot.HostStatus(hostId));
        DialogPresenter.ShowPanel(this, view.Title, view, 1250, 780);
        return view;
    }

    /// <summary>Shows content above the map (browser dialogs and panels); returns the action that removes it.</summary>
    internal Action ShowOverlay(Control content)
    {
        OverlayHost.Children.Add(content);
        OverlayHost.IsVisible = true;
        return () =>
        {
            OverlayHost.Children.Remove(content);
            OverlayHost.IsVisible = OverlayHost.Children.Count > 0;
        };
    }

    void OnTreeContext(object? sender, ContextRequestedEventArgs e)
    {
        var node = (e.Source as Control)?.DataContext as TreeNode;
        if (node is not { Kind: TreeNodeKind.Host or TreeNodeKind.Problem } || node.Id == "") return;
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem("Resumo do host…", () => { ShowHostSummary(node.Id); return Task.CompletedTask; }));
        menu.Items.Add(CreateMenuItem("Localizar no mapa", () => { FocusHost(node.Id); return Task.CompletedTask; }));
        menu.Open(Tree);
        e.Handled = true;
    }

    void OpenInZabbix(MapElement element)
    {
        if (_client == null) return;
        var url = $"{_client.BaseUrl}/zabbix.php?action=problem.view&filter_set=1&hostids%5B%5D={Uri.EscapeDataString(element.RefId)}";
        TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(url));
    }

    void OnCanvasContext(object? sender, ContextRequestedEventArgs e)
    {
        var menu = new ContextMenu();
        void Item(string text, Func<Task> action) =>
            menu.Items.Add(CreateMenuItem(text, action));

        var element = Canvas.Selected.Count == 1 ? Canvas.Selected.First() : null;
        var editable = EditToggle.IsChecked == true;
        if (element != null)
        {
            if (element.Type == ElementType.Host)
            {
                Item("Resumo do host…", () => { ShowHostSummary(element.RefId); return Task.CompletedTask; });
                Item("Abrir problemas no Zabbix", () => { OpenInZabbix(element); return Task.CompletedTask; });
            }
            if (element.Type == ElementType.Map) Item("Abrir submapa", () => OpenMapAsync(element.RefId));
            if (editable) Item("Editar rótulo…", () => EditLabelAsync(element));
        }
        if (Canvas.SelectedLink is { } link && editable) Item("Editar link…", () => EditLinkAsync(link));
        if (editable && (Canvas.Selected.Count > 0 || Canvas.SelectedLink != null))
            Item("Remover", () => { DeleteSelection(); return Task.CompletedTask; });
        if (menu.Items.Count == 0) return;
        menu.Open(Canvas);
        e.Handled = true;
    }

    MenuItem CreateMenuItem(string text, Func<Task> action)
    {
        var item = new MenuItem { Header = text };
        item.Click += async (_, _) => await GuardAsync(action);
        return item;
    }

    async Task EditLabelAsync(MapElement element)
    {
        var label = await Dialogs.PromptAsync(this, "Rótulo", "Aceita {HOST.NAME}, {HOST.IP} e {?last(/host/chave)}", element.Label, multiline: true);
        if (label == null || Canvas.Map is not { } map) return;
        element.Label = label;
        map.IsDirty = true;
        Canvas.Layout();
        Canvas.InvalidateVisual();
        UpdateTitle();
        await RefreshAsync();
    }

    async Task EditLinkAsync(MapLink link)
    {
        if (_client == null || Canvas.Map is not { } map || _snapshot == null) return;
        var hostIds = map.Elements.Where(e => e.Id == link.Element1Id || e.Id == link.Element2Id)
            .Where(e => e.Type == ElementType.Host).Select(e => e.RefId).ToList();
        var items = await _client.GetItemsAsync(hostIds, "net.if");
        var options = items.Where(i => _snapshot.Hosts.ContainsKey(i.HostId))
            .Select(i => (_snapshot.Hosts[i.HostId].Host, i)).ToList();
        if (!await Dialogs.LinkAsync(this, link, options, _client.UsesExpressionMacros)) return;
        map.IsDirty = true;
        UpdateTitle();
        await RefreshAsync();
    }

    PointerPressedEventArgs? _treePress;
    TreeNode? _treePressNode;
    Point _treePressPoint;

    void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _treePressNode = (e.Source as Control)?.DataContext as TreeNode;
        _treePress = e;
        _treePressPoint = e.GetPosition(Tree);
    }

    async void OnTreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_treePress is not { } press || _treePressNode is not { Kind: TreeNodeKind.Host } node || EditToggle.IsChecked != true) return;
        var delta = e.GetPosition(Tree) - _treePressPoint;
        if (!e.GetCurrentPoint(Tree).Properties.IsLeftButtonPressed || Math.Abs(delta.X) + Math.Abs(delta.Y) < 6) return;
        _treePress = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(HostDragFormat, node.Id));
        await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy);
    }

    void ApplyEditState()
    {
        var editable = EditToggle.IsChecked == true;
        Canvas.Editable = editable;
        LinkToggle.IsEnabled = AddHostButton.IsEnabled = DeleteButton.IsEnabled = editable;
        if (!editable) LinkToggle.IsChecked = false;
    }

    void UpdateTitle()
    {
        var map = Canvas.Map;
        Title = map == null ? "ZabbixMap" : $"{(map.IsDirty ? "* " : "")}{map.Name} - ZabbixMap";
        TitleChanged?.Invoke(Title);
        EmptyHint.IsVisible = map == null;
        SaveButton.IsEnabled = map?.IsDirty == true;
    }

    void StatusZoom()
    {
        var text = $"{Canvas.Zoom:P0}";
        ZoomBox.PlaceholderText = text;
    }

    void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        var typing = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;
        switch (e.Key)
        {
            case Key.S when ctrl:
                _ = GuardAsync(SaveAsync);
                break;
            case Key.F when ctrl:
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case Key.F5:
                _ = RefreshAsync();
                break;
            case Key.F when !typing:
                Canvas.FitToView();
                break;
            case Key.Delete when !typing:
                DeleteSelection();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e) when (e is ZabbixApiException or HttpRequestException or TaskCanceledException)
        {
            StatusText.Text = $"erro: {e.Message}";
            ShowToast(e.Message, true);
        }
    }
}
