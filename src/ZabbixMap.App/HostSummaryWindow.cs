using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ZabbixMap.App.Controls;
using ZabbixMap.Core;

namespace ZabbixMap.App;

/// <summary>Host summary: latest data grouped by component, with a history chart for the selected item.</summary>
public sealed class HostSummaryWindow : Window
{
    static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);
    static readonly (string Label, TimeSpan Period)[] Ranges =
        [("1 hora", TimeSpan.FromHours(1)), ("6 horas", TimeSpan.FromHours(6)), ("24 horas", TimeSpan.FromDays(1)), ("7 dias", TimeSpan.FromDays(7))];
    static readonly Color ReceivedColor = Color.Parse("#2E9E3F");
    static readonly Color SentColor = Color.Parse("#2F7DE1");

    public sealed record SectionRow(string Title, int Count);
    public sealed record ItemRow(HostItem Item)
    {
        public string Age => Item.LastCheck is { } t ? FormatAge(DateTimeOffset.UtcNow - t) : "sem dados";
    }

    readonly IZabbixClient _client;
    readonly HostInfo _host;
    readonly DispatcherTimer _timer = new() { Interval = RefreshInterval };
    readonly ListBox _list = new();
    readonly WrapPanel _chips = new() { Orientation = Orientation.Horizontal };
    readonly TextBox _search = new() { PlaceholderText = "Filtrar itens", Width = 220 };
    readonly CheckBox _showEmpty = new() { Content = "Itens sem dados" };
    readonly ComboBox _range = new() { ItemsSource = Ranges.Select(r => r.Label).ToList(), SelectedIndex = 0, Width = 110 };
    readonly TextBlock _chartTitle = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _status = new() { Foreground = Brushes.DimGray };
    readonly HashSet<string> _selectedComponents = [];
    IReadOnlyList<HostItem> _items = [];
    HostItem? _chartItem;
    int _chartRequest;

    public LineChart Chart { get; } = new();
    public IReadOnlyList<object> Rows => (IReadOnlyList<object>?)_list.ItemsSource ?? [];

    public HostSummaryWindow(IZabbixClient client, HostInfo host, ElementStatus status)
    {
        _client = client;
        _host = host;
        Title = $"{host.Name} - resumo";
        Width = 1250;
        Height = 780;
        FontSize = 12;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(PaddingProperty, new Thickness(4, 3)), new Setter(MinHeightProperty, 0d) },
        });
        _list.DataTemplates.Add(new FuncDataTemplate<SectionRow>((row, _) => new Border
        {
            Background = new SolidColorBrush(Color.Parse("#EEF2F6")),
            Padding = new Thickness(6, 4),
            Child = new TextBlock { Text = $"{row.Title}  ({row.Count})", FontWeight = FontWeight.SemiBold },
        }));
        _list.DataTemplates.Add(new FuncDataTemplate<ItemRow>((row, _) =>
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,150,90,80"), Margin = new Thickness(14, 1, 4, 1) };
            void Cell(int column, string text, IBrush? brush = null, HorizontalAlignment align = HorizontalAlignment.Left)
            {
                var block = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = align, Foreground = brush ?? Brushes.Black };
                Grid.SetColumn(block, column);
                grid.Children.Add(block);
            }
            Cell(0, (row.Item.IsNumeric ? "📈 " : "") + row.Item.Name);
            Cell(1, row.Item.DisplayValue, align: HorizontalAlignment.Right);
            Cell(2, row.Item.Change, Brushes.DimGray, HorizontalAlignment.Right);
            Cell(3, row.Age, Brushes.Gray, HorizontalAlignment.Right);
            ToolTip.SetTip(grid, row.Item.IsNumeric ? row.Item.Key : $"{row.Item.Key}\n{row.Item.LastValue}");
            return grid;
        }));
        _list.SelectionChanged += async (_, _) =>
        {
            if (_list.SelectedItem is ItemRow row) await ShowChartAsync(row.Item);
        };

        _search.TextChanged += (_, _) => Rebuild();
        _showEmpty.IsCheckedChanged += (_, _) => Rebuild();
        _range.SelectionChanged += async (_, _) =>
        {
            if (_chartItem != null) await ShowChartAsync(_chartItem);
        };
        var refresh = new Button { Content = "Atualizar" };
        refresh.Click += async (_, _) => await LoadAsync();
        _timer.Tick += async (_, _) => await LoadAsync();

        Content = Layout(status, refresh);
        Opened += async (_, _) =>
        {
            await LoadAsync();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    Control Layout(ElementStatus status, Button refresh)
    {
        var header = new StackPanel { Spacing = 2, Margin = new Thickness(12, 10, 12, 6) };
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(MapCanvas.HealthColor(status)), VerticalAlignment = VerticalAlignment.Center },
                new TextBlock { Text = _host.Name, FontSize = 18, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = $"{_host.Host} · {_host.Ip}", Foreground = Brushes.DimGray, VerticalAlignment = VerticalAlignment.Center },
            },
        });
        if (status.Health == NodeHealth.Down && status.Problems.Count == 0)
            header.Children.Add(new TextBlock { Text = "Interfaces indisponíveis", Foreground = Brushes.Firebrick });
        foreach (var problem in status.Problems.Take(5))
            header.Children.Add(new TextBlock
            {
                Text = $"• {problem.Description}  (desde {problem.Since.ToLocalTime():dd/MM HH:mm})",
                Foreground = new SolidColorBrush(MapCanvas.HealthColor(new(NodeHealth.Problem, problem.Severity, [problem]))),
                FontWeight = FontWeight.SemiBold,
            });
        if (status.Problems.Count > 5)
            header.Children.Add(new TextBlock { Text = $"… e mais {status.Problems.Count - 5} problema(s)", Foreground = Brushes.Firebrick });

        var filters = new DockPanel { Margin = new Thickness(12, 0, 12, 6) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _showEmpty, _search, refresh } };
        DockPanel.SetDock(right, Dock.Right);
        filters.Children.Add(right);
        filters.Children.Add(_chips);

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,150,90,80"), Margin = new Thickness(26, 0, 22, 2) };
        string[] titles = ["Nome", "Último valor", "Variação", "Checagem"];
        for (var i = 0; i < titles.Length; i++)
        {
            var title = new TextBlock { Text = titles[i], Foreground = Brushes.Gray, HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right };
            Grid.SetColumn(title, i);
            columns.Children.Add(title);
        }
        var listPanel = new DockPanel();
        DockPanel.SetDock(columns, Dock.Top);
        listPanel.Children.Add(columns);
        listPanel.Children.Add(_list);

        var chartHeader = new DockPanel { Margin = new Thickness(8, 0, 8, 4) };
        DockPanel.SetDock(_range, Dock.Right);
        chartHeader.Children.Add(_range);
        chartHeader.Children.Add(_chartTitle);
        var chartPanel = new DockPanel();
        DockPanel.SetDock(chartHeader, Dock.Top);
        chartPanel.Children.Add(chartHeader);
        chartPanel.Children.Add(Chart);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,5,2*") };
        var splitter = new GridSplitter { Background = new SolidColorBrush(Color.Parse("#CCCCCC")) };
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(chartPanel, 2);
        body.Children.Add(listPanel);
        body.Children.Add(splitter);
        body.Children.Add(chartPanel);

        var root = new DockPanel();
        var footer = new Border { Padding = new Thickness(12, 4), Background = new SolidColorBrush(Color.Parse("#F0F0F0")), Child = _status };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(filters, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(filters);
        root.Children.Add(footer);
        root.Children.Add(body);
        return root;
    }

    public async Task LoadAsync()
    {
        try
        {
            _items = await _client.GetHostItemsAsync(_host.Id);
            BuildChips();
            Rebuild();
            _status.Text = $"{_items.Count} itens · atualizado às {DateTime.Now:HH:mm:ss}";
            var initial = _chartItem == null
                ? _items.FirstOrDefault(i => HostSummary.TrafficDirection(i) == "in" && i.HasData) ?? _items.FirstOrDefault(i => i.IsNumeric && i.HasData)
                : _items.FirstOrDefault(i => i.Id == _chartItem.Id);
            if (initial != null) await ShowChartAsync(initial);
        }
        catch (Exception e) when (e is ZabbixApiException or HttpRequestException or TaskCanceledException)
        {
            _status.Text = $"falha ao carregar: {e.Message}";
        }
    }

    void BuildChips()
    {
        var counts = HostSummary.ComponentCounts(_items.Where(i => i.HasData || _showEmpty.IsChecked == true));
        _selectedComponents.IntersectWith(counts.Keys);
        _chips.Children.Clear();
        foreach (var (component, count) in counts)
        {
            var chip = new ToggleButton { Content = $"{component} {count}", IsChecked = _selectedComponents.Contains(component), Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(8, 2) };
            chip.IsCheckedChanged += (_, _) =>
            {
                if (chip.IsChecked == true) _selectedComponents.Add(component);
                else _selectedComponents.Remove(component);
                Rebuild();
            };
            _chips.Children.Add(chip);
        }
    }

    /// <summary>Flattens sections into header and item rows; no component selected means all.</summary>
    void Rebuild()
    {
        var filter = _search.Text?.Trim() ?? "";
        var visible = _items.Where(i =>
            (i.HasData || _showEmpty.IsChecked == true) &&
            (filter == "" || i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || i.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)));
        var rows = new List<object>();
        foreach (var section in HostSummary.Group(visible))
        {
            if (_selectedComponents.Count > 0 && !_selectedComponents.Contains(section.Component)) continue;
            rows.Add(new SectionRow(section.Subgroup == null ? section.Component : $"{section.Component} › {section.Subgroup}", section.Items.Count));
            rows.AddRange(section.Items.Select(i => new ItemRow(i)));
        }
        var selectedId = (_list.SelectedItem as ItemRow)?.Item.Id;
        _list.ItemsSource = rows;
        _list.SelectedItem = rows.OfType<ItemRow>().FirstOrDefault(r => r.Item.Id == selectedId);
    }

    public async Task ShowChartAsync(HostItem item)
    {
        _chartItem = item;
        if (!item.IsNumeric)
        {
            _chartTitle.Text = item.Name;
            Chart.SetMessage("Item de texto: sem gráfico");
            return;
        }
        var partner = HostSummary.TrafficPartner(item, _items);
        var pair = partner == null ? [item] : new[] { item, partner }.OrderBy(i => HostSummary.TrafficDirection(i) == "out").ToArray();
        _chartTitle.Text = partner == null ? item.Name : $"Tráfego {item.Tag("interface") ?? item.Key}";

        var request = ++_chartRequest;
        var period = Ranges[Math.Max(0, _range.SelectedIndex)].Period;
        try
        {
            var histories = await Task.WhenAll(pair.Select(i => _client.GetHistoryAsync(i, period)));
            // A newer selection may have finished first.
            if (request != _chartRequest) return;
            var series = pair.Select((i, n) => new ChartSeries(
                partner == null ? i.Name : HostSummary.TrafficDirection(i) == "in" ? "Recebido" : "Enviado",
                partner != null && HostSummary.TrafficDirection(i) == "out" ? SentColor : partner == null ? SentColor : ReceivedColor,
                histories[n])).ToList();
            Chart.SetData(series, item.Units);
        }
        catch (Exception e) when (e is ZabbixApiException or HttpRequestException or TaskCanceledException)
        {
            if (request == _chartRequest) Chart.SetMessage($"Falha ao carregar o histórico: {e.Message}");
        }
    }

    static string FormatAge(TimeSpan age) =>
        age.TotalSeconds < 60 ? $"{Math.Max(0, (int)age.TotalSeconds)}s"
        : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes}m {age.Seconds}s"
        : age.TotalHours < 24 ? $"{(int)age.TotalHours}h {age.Minutes}m"
        : $"{(int)age.TotalDays}d {age.Hours}h";
}
