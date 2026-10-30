using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ZabbixMap.App.Services;

/// <summary>Tray icon: green when all is well, red with a flag while any host is down.</summary>
public sealed class TrayController
{
    readonly TrayIcon _tray;
    readonly MainWindow _window;
    readonly WindowIcon _okIcon = BuildIcon(false);
    readonly WindowIcon _downIcon = BuildIcon(true);

    public TrayController(Application app, MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        _window = window;
        var open = new NativeMenuItem("Abrir");
        open.Click += (_, _) => Restore();
        var quit = new NativeMenuItem("Sair");
        quit.Click += async (_, _) =>
        {
            Restore();
            if (await window.ConfirmDiscardAsync()) desktop.Shutdown();
        };
        _tray = new TrayIcon
        {
            Icon = _okIcon,
            ToolTipText = "ZabbixMap",
            IsVisible = true,
            Menu = new NativeMenu { open, quit },
        };
        _tray.Clicked += (_, _) => Restore();
        TrayIcon.SetIcons(app, [_tray]);
    }

    void Restore()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void SetDownCount(int count)
    {
        _tray.Icon = count > 0 ? _downIcon : _okIcon;
        _tray.ToolTipText = count > 0 ? $"ZabbixMap: {count} host(s) parado(s)" : "ZabbixMap: tudo normal";
    }

    static WindowIcon BuildIcon(bool down)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(32, 32));
        using (var ctx = bitmap.CreateDrawingContext())
        {
            ctx.DrawEllipse(new SolidColorBrush(down ? Color.Parse("#D32F2F") : Color.Parse("#2E9E3F")), null, new Point(16, 16), 15, 15);
            if (down)
            {
                ctx.DrawLine(new Pen(Brushes.White, 2.5), new Point(11, 7), new Point(11, 25));
                ctx.DrawGeometry(Brushes.White, null, StreamGeometry.Parse("M 12,7 L 24,11.5 L 12,16 Z"));
            }
            else
            {
                ctx.DrawEllipse(Brushes.White, null, new Point(16, 16), 5, 5);
            }
        }
        var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        stream.Position = 0;
        return new WindowIcon(stream);
    }
}
