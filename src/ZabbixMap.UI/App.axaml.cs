using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ZabbixMap.UI.Services;

namespace ZabbixMap.UI;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Closing the window keeps monitoring from the tray; "Sair" is what ends the app.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new MainWindow { HideOnClose = true };
            desktop.MainWindow = window;
            var tray = new TrayController(this, window, desktop);
            window.View.DownCountChanged += tray.SetDownCount;
            window.Opened += async (_, _) => await window.View.StartAsync();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            // Browser (WebAssembly): one view, no tray, credentials only in memory.
            var view = new MainView(new Settings());
            singleView.MainView = view;
            view.Loaded += async (_, _) => await view.StartAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
