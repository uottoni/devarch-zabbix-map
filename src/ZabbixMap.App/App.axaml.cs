using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ZabbixMap.App.Services;

namespace ZabbixMap.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the window keeps monitoring from the tray; "Sair" is what ends the app.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new MainWindow();
            desktop.MainWindow = window;
            var tray = new TrayController(this, window, desktop);
            window.DownCountChanged += tray.SetDownCount;
            window.Opened += async (_, _) => await window.StartAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
