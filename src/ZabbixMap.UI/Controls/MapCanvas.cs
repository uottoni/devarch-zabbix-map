using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ZabbixMap.Core;

namespace ZabbixMap.UI.Controls;

/// <summary>Interactive map surface: pan, zoom, drag nodes, draw links. World coordinates are the Zabbix x/y.</summary>
public sealed class MapCanvas : Control
{
    const double MinZoom = 0.1, MaxZoom = 4;
    static readonly Typeface Face = new("Inter");
    static readonly IBrush LinkLabelBack = new SolidColorBrush(Colors.White, 0.85);
    static readonly IBrush SelectionFill = new SolidColorBrush(Color.Parse("#2F7DE1"), 0.15);
    static readonly IPen SelectionPen = new Pen(new SolidColorBrush(Color.Parse("#2F7DE1")), 2);
    static readonly IPen NodeBorder = new Pen(new SolidColorBrush(Colors.Black, 0.25));

    readonly Dictionary<MapElement, Rect> _bounds = [];
    readonly Dictionary<string, MapElement> _byId = [];
    readonly HashSet<MapElement> _selected = [];
    readonly Dictionary<string, FormattedText> _textCache = [];

    SysMap? _map;
    MonitoringSnapshot? _snapshot;
    IReadOnlyDictionary<string, SysMap> _submaps = new Dictionary<string, SysMap>();
    MapLink? _selectedLink;
    MapElement? _hover;
    MapElement? _linkSource;
    Point _pointer, _pressWorld, _rubberStart;
    bool _panning, _dragging, _rubber;

    public double Zoom { get; private set; } = 1;
    public Point Offset { get; private set; }
    public bool Editable { get; set; }
    public bool LinkMode { get; set; }
    public bool ShowTraffic { get; set; } = true;
    public SysMap? Map => _map;
    public MapLink? SelectedLink => _selectedLink;
    public IReadOnlyCollection<MapElement> Selected => _selected;
    public IReadOnlyDictionary<string, SysMap> Submaps => _submaps;
    public MonitoringSnapshot? Snapshot => _snapshot;

    public event Action? ViewChanged;
    public event Action? Edited;
    public event Action? SelectionChanged;
    public event Action<MapElement>? ElementActivated;
    public event Action<MapElement, MapElement>? LinkDrawn;

    public MapCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public void SetMap(SysMap? map, IReadOnlyDictionary<string, SysMap>? submaps = null)
    {
        _map = map;
        _submaps = submaps ?? new Dictionary<string, SysMap>();
        _selected.Clear();
        _selectedLink = null;
        _hover = null;
        Invalidate();
        Layout();
        FitToView();
    }

    public void SetSnapshot(MonitoringSnapshot? snapshot)
    {
        _snapshot = snapshot;
        Layout();
        Invalidate();
    }

    /// <summary>Recomputes node sizes after labels, data or membership change.</summary>
    public void Layout()
    {
        _bounds.Clear();
        _byId.Clear();
        if (_map == null) return;
        foreach (var element in _map.Elements)
        {
            _byId[element.Id] = element;
            var size = Text(ResolveLabel(element), 12, Brushes.Black);
            var width = Math.Max(size.Width + 16, 48);
            var height = Math.Max(size.Height + 8, 22);
            _bounds[element] = new Rect(element.X - width / 2, element.Y - height / 2, width, height);
        }
    }

    string ResolveLabel(MapElement element)
    {
        var text = LabelResolver.Resolve(element.Label, element, _snapshot, id => _submaps.GetValueOrDefault(id)?.Name);
        return text.Length > 0 ? text : "(sem rótulo)";
    }

    public ElementStatus StatusOf(MapElement element) =>
        _snapshot?.StatusOf(element, _submaps) ?? new(NodeHealth.Unknown, 0, []);

    FormattedText Text(string text, double size, IBrush brush)
    {
        var key = $"{size}|{brush.GetHashCode()}|{text}";
        if (!_textCache.TryGetValue(key, out var formatted))
        {
            if (_textCache.Count > 5000) _textCache.Clear();
            formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush)
            {
                TextAlignment = TextAlignment.Center,
            };
            _textCache[key] = formatted;
        }
        return formatted;
    }

    public Point ToWorld(Point screen) => new((screen.X - Offset.X) / Zoom, (screen.Y - Offset.Y) / Zoom);

    public Rect WorldViewport => new(ToWorld(default), ToWorld(new Point(Bounds.Width, Bounds.Height)));

    public Rect WorldBounds
    {
        get
        {
            if (_bounds.Count == 0) return new Rect(0, 0, 100, 100);
            return _bounds.Values.Aggregate((a, b) => a.Union(b));
        }
    }

    public IEnumerable<(MapElement Element, Rect Bounds)> Nodes => _bounds.Select(p => (p.Key, p.Value));

    public static Color HealthColor(ElementStatus status) => status.Health switch
    {
        NodeHealth.Down => Color.Parse("#E02020"),
        NodeHealth.Ok => Color.Parse("#3CCB4B"),
        NodeHealth.Problem => status.Severity switch
        {
            5 => Color.Parse("#E45959"),
            4 => Color.Parse("#E97659"),
            3 => Color.Parse("#FFA059"),
            2 => Color.Parse("#FFC859"),
            1 => Color.Parse("#7499FF"),
            _ => Color.Parse("#97AAB3"),
        },
        _ => Color.Parse("#C8C8C8"),
    };

    public void SetZoom(double zoom, Point? anchor = null)
    {
        var center = anchor ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        var world = ToWorld(center);
        Zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        Offset = new Point(center.X - world.X * Zoom, center.Y - world.Y * Zoom);
        Invalidate();
    }

    public void CenterOn(Point world)
    {
        Offset = new Point(Bounds.Width / 2 - world.X * Zoom, Bounds.Height / 2 - world.Y * Zoom);
        Invalidate();
    }

    public void FitToView()
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || _bounds.Count == 0) return;
        var world = WorldBounds.Inflate(40);
        Zoom = Math.Clamp(Math.Min(Bounds.Width / world.Width, Bounds.Height / world.Height), MinZoom, 1);
        CenterOn(world.Center);
    }

    public void Focus(MapElement element)
    {
        _selected.Clear();
        _selected.Add(element);
        _selectedLink = null;
        CenterOn(new Point(element.X, element.Y));
        SelectionChanged?.Invoke();
    }

    void Invalidate()
    {
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (e.PreviousSize.Width <= 0) FitToView();
        Invalidate();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.White, new Rect(Bounds.Size));
        if (_map == null) return;

        using (context.PushTransform(Matrix.CreateScale(Zoom, Zoom) * Matrix.CreateTranslation(Offset.X, Offset.Y)))
        {
            var view = WorldViewport.Inflate(50);
            foreach (var link in _map.Links) DrawLink(context, link, view);
            if (_linkSource != null)
                context.DrawLine(new Pen(Brushes.DodgerBlue, 2 / Zoom, DashStyle.Dash), new Point(_linkSource.X, _linkSource.Y), ToWorld(_pointer));
            foreach (var (element, bounds) in _bounds)
                if (view.Intersects(bounds)) DrawNode(context, element, bounds);
        }

        if (_rubber)
        {
            var rect = new Rect(_rubberStart, _pointer).Normalize();
            context.DrawRectangle(SelectionFill, SelectionPen, rect);
        }
        if (_hover != null) DrawTooltip(context, _hover);
    }

    void DrawLink(DrawingContext context, MapLink link, Rect view)
    {
        if (!_byId.TryGetValue(link.Element1Id, out var a) || !_byId.TryGetValue(link.Element2Id, out var b)) return;
        var p1 = new Point(a.X, a.Y);
        var p2 = new Point(b.X, b.Y);
        if (!view.Intersects(new Rect(p1, p2).Normalize().Inflate(1))) return;

        var color = link.Color;
        var drawType = link.DrawType;
        var active = _snapshot == null ? null : link.Triggers.FirstOrDefault(t => _snapshot.Triggers.Any(x => x.Id == t.TriggerId));
        if (active != null)
        {
            color = active.Color;
            drawType = active.DrawType;
        }
        else if (StatusOf(a).Health == NodeHealth.Down || StatusOf(b).Health == NodeHealth.Down)
        {
            color = "E02020";
            drawType = 4;
        }

        if (!Color.TryParse("#" + color, out var parsed)) parsed = Colors.Black;
        var width = drawType == 2 ? 3.5 : 1.5;
        if (link == _selectedLink) context.DrawLine(new Pen(Brushes.DodgerBlue, width + 4), p1, p2);
        var dash = drawType switch { 3 => DashStyle.Dot, 4 => DashStyle.Dash, _ => null };
        context.DrawLine(new Pen(new SolidColorBrush(parsed), width, dash), p1, p2);

        if (!ShowTraffic || string.IsNullOrWhiteSpace(link.Label) || Zoom < 0.35) return;
        var text = Text(LabelResolver.Resolve(link.Label, null, _snapshot), 10, Brushes.Black);
        var mid = new Point((p1.X + p2.X) / 2, (p1.Y + p2.Y) / 2);
        var rect = new Rect(mid.X - text.Width / 2 - 2, mid.Y - text.Height / 2 - 1, text.Width + 4, text.Height + 2);
        context.FillRectangle(LinkLabelBack, rect);
        context.DrawText(text, new Point(mid.X - text.Width / 2, mid.Y - text.Height / 2));
    }

    void DrawNode(DrawingContext context, MapElement element, Rect bounds)
    {
        var baseColor = HealthColor(StatusOf(element));
        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Lighten(baseColor), 0), new GradientStop(baseColor, 1) },
        };
        context.DrawRectangle(fill, _selected.Contains(element) ? SelectionPen : NodeBorder, bounds, 4, 4);
        if (Zoom < 0.25) return;
        var text = Text(ResolveLabel(element), 12, Brushes.Black);
        context.DrawText(text, new Point(bounds.Center.X - text.Width / 2, bounds.Center.Y - text.Height / 2));
    }

    static Color Lighten(Color c) => Color.FromRgb((byte)(c.R + (255 - c.R) * .45), (byte)(c.G + (255 - c.G) * .45), (byte)(c.B + (255 - c.B) * .45));

    void DrawTooltip(DrawingContext context, MapElement element)
    {
        var status = StatusOf(element);
        if (status.Problems.Count == 0 && status.Health != NodeHealth.Down) return;
        var lines = status.Problems.Take(8).Select(p => "• " + p.Description).ToList();
        if (status.Problems.Count > 8) lines.Add($"… e mais {status.Problems.Count - 8}");
        if (lines.Count == 0) lines.Add("Interfaces indisponíveis");
        var text = new FormattedText(string.Join('\n', lines), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 12, Brushes.White);
        var x = Math.Min(_pointer.X + 14, Bounds.Width - text.Width - 12);
        var y = Math.Min(_pointer.Y + 14, Bounds.Height - text.Height - 12);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#E0222222")), null, new Rect(x - 6, y - 4, text.Width + 12, text.Height + 8), 4, 4);
        context.DrawText(text, new Point(x, y));
    }

    public MapElement? ElementAt(Point world)
    {
        // Last drawn is on top.
        foreach (var (element, bounds) in _bounds.Reverse())
            if (bounds.Contains(world)) return element;
        return null;
    }

    public MapLink? LinkAt(Point world)
    {
        if (_map == null) return null;
        var tolerance = 6 / Zoom;
        foreach (var link in _map.Links)
        {
            if (!_byId.TryGetValue(link.Element1Id, out var a) || !_byId.TryGetValue(link.Element2Id, out var b)) continue;
            if (SegmentDistance(world, new Point(a.X, a.Y), new Point(b.X, b.Y)) <= tolerance) return link;
        }
        return null;
    }

    static double SegmentDistance(Point p, Point a, Point b)
    {
        var d = b - a;
        var lengthSquared = d.X * d.X + d.Y * d.Y;
        var t = lengthSquared == 0 ? 0 : Math.Clamp(((p.X - a.X) * d.X + (p.Y - a.Y) * d.Y) / lengthSquared, 0, 1);
        var closest = new Point(a.X + t * d.X, a.Y + t * d.Y);
        var v = p - closest;
        return Math.Sqrt(v.X * v.X + v.Y * v.Y);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        _pointer = point.Position;
        var world = ToWorld(_pointer);
        var element = ElementAt(world);
        var link = element == null ? LinkAt(world) : null;

        if (point.Properties.IsMiddleButtonPressed)
        {
            _panning = true;
            e.Pointer.Capture(this);
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            if (element != null && !_selected.Contains(element))
            {
                _selected.Clear();
                _selected.Add(element);
            }
            _selectedLink = link;
            if (link != null) _selected.Clear();
            SelectionChanged?.Invoke();
            Invalidate();
            return;
        }

        if (!point.Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2 && element != null)
        {
            ElementActivated?.Invoke(element);
            return;
        }

        if (LinkMode && Editable && element != null)
        {
            _linkSource = element;
            e.Pointer.Capture(this);
            return;
        }

        var additive = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (element != null)
        {
            _selectedLink = null;
            if (additive)
            {
                if (!_selected.Remove(element)) _selected.Add(element);
            }
            else if (!_selected.Contains(element))
            {
                _selected.Clear();
                _selected.Add(element);
            }
            _dragging = Editable;
            _pressWorld = world;
        }
        else
        {
            _selected.Clear();
            _selectedLink = link;
            if (link == null)
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    _rubber = true;
                    _rubberStart = _pointer;
                }
                else _panning = true;
            }
        }
        e.Pointer.Capture(this);
        SelectionChanged?.Invoke();
        Invalidate();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var previous = _pointer;
        _pointer = e.GetPosition(this);
        var world = ToWorld(_pointer);

        if (_panning)
        {
            Offset += _pointer - previous;
            Invalidate();
        }
        else if (_dragging)
        {
            var delta = world - _pressWorld;
            _pressWorld = world;
            foreach (var element in _selected)
            {
                element.X += delta.X;
                element.Y += delta.Y;
                if (_bounds.TryGetValue(element, out var b)) _bounds[element] = b.Translate(delta);
            }
            if (_map != null && _selected.Count > 0)
            {
                _map.IsDirty = true;
                Edited?.Invoke();
            }
            Invalidate();
        }
        else if (_linkSource != null || _rubber)
        {
            InvalidateVisual();
        }
        else
        {
            var hover = ElementAt(world);
            if (hover != _hover || hover != null)
            {
                _hover = hover;
                InvalidateVisual();
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_linkSource != null)
        {
            var target = ElementAt(ToWorld(_pointer));
            var source = _linkSource;
            _linkSource = null;
            if (target != null && target != source) LinkDrawn?.Invoke(source, target);
        }
        if (_rubber)
        {
            var rect = new Rect(ToWorld(_rubberStart), ToWorld(_pointer)).Normalize();
            foreach (var (element, bounds) in _bounds)
                if (rect.Intersects(bounds)) _selected.Add(element);
            SelectionChanged?.Invoke();
        }
        _panning = _dragging = _rubber = false;
        e.Pointer.Capture(null);
        Invalidate();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        SetZoom(Zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), e.GetPosition(this));
        e.Handled = true;
    }
}
