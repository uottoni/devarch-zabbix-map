using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace ZabbixMap.App.Controls;

/// <summary>Overview of the whole map; click or drag to move the main canvas.</summary>
public sealed class MiniMap : Control
{
    static readonly IPen ViewportPen = new Pen(Brushes.Black, 1.5);
    static readonly IPen LinkPen = new Pen(new SolidColorBrush(Colors.Gray, 0.6), 0.5);

    MapCanvas? _canvas;
    bool _dragging;

    public MapCanvas? Canvas
    {
        get => _canvas;
        set
        {
            if (_canvas != null) _canvas.ViewChanged -= InvalidateVisual;
            _canvas = value;
            if (_canvas != null) _canvas.ViewChanged += InvalidateVisual;
            InvalidateVisual();
        }
    }

    (Rect World, double Scale, Point Origin) Transform()
    {
        var world = _canvas!.WorldBounds.Inflate(20);
        var scale = Math.Min(Bounds.Width / world.Width, Bounds.Height / world.Height);
        var origin = new Point((Bounds.Width - world.Width * scale) / 2, (Bounds.Height - world.Height * scale) / 2);
        return (world, scale, origin);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.White, new Rect(Bounds.Size));
        if (_canvas?.Map == null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var (world, scale, origin) = Transform();
        Point Map(Point p) => new(origin.X + (p.X - world.X) * scale, origin.Y + (p.Y - world.Y) * scale);

        var positions = _canvas.Map.Elements.ToDictionary(e => e.Id, e => new Point(e.X, e.Y));
        foreach (var link in _canvas.Map.Links)
            if (positions.TryGetValue(link.Element1Id, out var a) && positions.TryGetValue(link.Element2Id, out var b))
                context.DrawLine(LinkPen, Map(a), Map(b));

        foreach (var (element, bounds) in _canvas.Nodes)
        {
            var brush = new SolidColorBrush(MapCanvas.HealthColor(_canvas.StatusOf(element)));
            context.FillRectangle(brush, new Rect(Map(bounds.TopLeft), Map(bounds.BottomRight)));
        }

        var view = _canvas.WorldViewport;
        var rect = new Rect(Map(view.TopLeft), Map(view.BottomRight)).Intersect(new Rect(Bounds.Size));
        if (rect.Width > 0 && rect.Height > 0) context.DrawRectangle(ViewportPen, rect);
    }

    void Navigate(Point position)
    {
        if (_canvas?.Map == null) return;
        var (world, scale, origin) = Transform();
        _canvas.CenterOn(new Point(world.X + (position.X - origin.X) / scale, world.Y + (position.Y - origin.Y) / scale));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragging = true;
        e.Pointer.Capture(this);
        Navigate(e.GetPosition(this));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) Navigate(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }
}
