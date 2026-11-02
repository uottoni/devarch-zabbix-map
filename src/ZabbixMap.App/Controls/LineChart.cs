using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ZabbixMap.Core;

namespace ZabbixMap.App.Controls;

public sealed record ChartSeries(string Name, Color Color, IReadOnlyList<HistoryPoint> Points);

/// <summary>Time series chart with value axis in the item's units and a hover readout.</summary>
public sealed class LineChart : Control
{
    const double Left = 78, Right = 12, Top = 26, Bottom = 24;
    static readonly Typeface Face = new("Inter");
    static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#E4E4E4")));
    static readonly IPen AxisPen = new Pen(new SolidColorBrush(Color.Parse("#999999")));
    static readonly IPen HoverPen = new Pen(new SolidColorBrush(Color.Parse("#888888")), 1, DashStyle.Dash);

    IReadOnlyList<ChartSeries> _series = [];
    string _units = "";
    string _message = "Selecione um item numérico";
    double? _hoverX;

    public IReadOnlyList<ChartSeries> Series => _series;

    public void SetData(IReadOnlyList<ChartSeries> series, string units)
    {
        _series = series;
        _units = units;
        _message = series.All(s => s.Points.Count == 0) ? "Sem dados no período" : "";
        InvalidateVisual();
    }

    public void SetMessage(string message)
    {
        _series = [];
        _message = message;
        InvalidateVisual();
    }

    FormattedText Text(string text, double size = 11, IBrush? brush = null) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, size, brush ?? Brushes.DimGray);

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.White, new Rect(Bounds.Size));
        var plot = new Rect(Left, Top, Math.Max(0, Bounds.Width - Left - Right), Math.Max(0, Bounds.Height - Top - Bottom));
        if (_message != "" || plot.Width < 20 || plot.Height < 20)
        {
            var text = Text(_message, 13);
            context.DrawText(text, new Point((Bounds.Width - text.Width) / 2, (Bounds.Height - text.Height) / 2));
            return;
        }

        var points = _series.SelectMany(s => s.Points).ToList();
        var t0 = points.Min(p => p.Time).ToUnixTimeSeconds();
        var t1 = Math.Max(points.Max(p => p.Time).ToUnixTimeSeconds(), t0 + 1);
        var (y0, y1, step) = NiceRange(Math.Min(0, points.Min(p => p.Value)), points.Max(p => p.Value));
        double X(DateTimeOffset t) => plot.Left + (t.ToUnixTimeSeconds() - t0) / (double)(t1 - t0) * plot.Width;
        double Y(double v) => plot.Bottom - (v - y0) / (y1 - y0) * plot.Height;

        for (var value = y0; value <= y1 + step / 2; value += step)
        {
            var y = Y(value);
            context.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(LabelResolver.FormatValue(value.ToString(CultureInfo.InvariantCulture), _units));
            context.DrawText(label, new Point(plot.Left - label.Width - 6, y - label.Height / 2));
        }
        var span = TimeSpan.FromSeconds(t1 - t0);
        var timeFormat = span > TimeSpan.FromDays(1.5) ? "dd/MM HH:mm" : "HH:mm";
        for (var i = 0; i <= 4; i++)
        {
            var time = DateTimeOffset.FromUnixTimeSeconds(t0 + (t1 - t0) * i / 4).ToLocalTime();
            var label = Text(time.ToString(timeFormat, CultureInfo.InvariantCulture));
            var x = Math.Clamp(plot.Left + plot.Width * i / 4 - label.Width / 2, plot.Left - 20, Bounds.Width - label.Width - 2);
            context.DrawText(label, new Point(x, plot.Bottom + 5));
        }
        context.DrawLine(AxisPen, plot.BottomLeft, plot.BottomRight);

        using (context.PushClip(plot.Inflate(1)))
            foreach (var series in _series.Where(s => s.Points.Count > 0))
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(new Point(X(series.Points[0].Time), Y(series.Points[0].Value)), false);
                    foreach (var p in series.Points.Skip(1)) g.LineTo(new Point(X(p.Time), Y(p.Value)));
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, new Pen(new SolidColorBrush(series.Color), 1.5), geometry);
            }

        DrawLegend(context, plot);
        if (_hoverX is { } hx && hx >= plot.Left && hx <= plot.Right)
            DrawHover(context, plot, t0 + (long)((hx - plot.Left) / plot.Width * (t1 - t0)), hx, Y);
    }

    void DrawLegend(DrawingContext context, Rect plot)
    {
        var x = plot.Left;
        foreach (var series in _series)
        {
            context.FillRectangle(new SolidColorBrush(series.Color), new Rect(x, 9, 12, 3));
            var last = series.Points.Count > 0 ? LabelResolver.FormatValue(series.Points[^1].Value.ToString(CultureInfo.InvariantCulture), _units) : "-";
            var text = Text($"{series.Name}: {last}", 11, Brushes.Black);
            context.DrawText(text, new Point(x + 16, 3));
            x += text.Width + 34;
        }
    }

    void DrawHover(DrawingContext context, Rect plot, long time, double x, Func<double, double> y)
    {
        context.DrawLine(HoverPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        var lines = new List<string> { DateTimeOffset.FromUnixTimeSeconds(time).ToLocalTime().ToString("dd/MM HH:mm:ss", CultureInfo.InvariantCulture) };
        foreach (var series in _series.Where(s => s.Points.Count > 0))
        {
            var nearest = series.Points.MinBy(p => Math.Abs(p.Time.ToUnixTimeSeconds() - time))!;
            lines.Add($"{series.Name}: {LabelResolver.FormatValue(nearest.Value.ToString(CultureInfo.InvariantCulture), _units)}");
            context.DrawEllipse(new SolidColorBrush(series.Color), null, new Point(x, y(nearest.Value)), 3, 3);
        }
        var text = Text(string.Join('\n', lines), 11, Brushes.White);
        var left = x + 10 + text.Width + 12 > Bounds.Width ? x - text.Width - 22 : x + 10;
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#E0222222")), null, new Rect(left, plot.Top + 4, text.Width + 12, text.Height + 8), 4, 4);
        context.DrawText(text, new Point(left + 6, plot.Top + 8));
    }

    /// <summary>Rounds the axis to 1/2/5 steps so tick labels read cleanly.</summary>
    static (double Min, double Max, double Step) NiceRange(double min, double max)
    {
        if (max <= min) max = min + 1;
        var raw = (max - min) / 5;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1, 2, 5, 10 }.Select(m => m * magnitude).First(s => s >= raw);
        return (Math.Floor(min / step) * step, Math.Ceiling(max / step) * step, step);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hoverX = e.GetPosition(this).X;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverX = null;
        InvalidateVisual();
    }
}
