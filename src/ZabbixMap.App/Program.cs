using Avalonia;

namespace ZabbixMap.App;

static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<ZabbixMap.UI.App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
