using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using ZabbixMap.UI;
using ZabbixMap.UI.Controls;
using ZabbixMap.UI.Services;
using ZabbixMap.Core;

namespace ZabbixMap.Tests;

/// <summary>DZM-1: restoring from the tray must not ask for the connection again, and credentials must survive restarts.</summary>
public class ConnectionTests
{
    const string Url = "https://zabbix.test";

    sealed class Harness
    {
        public FakeClient Client { get; } = UiTests.Network();
        public MemorySecretStore Store { get; } = new();
        public List<string> TokensUsed { get; } = [];
        public List<string?> Asked { get; } = [];
        public Func<ConnectionInput, string> Login { get; set; } = input => input.Password != "" ? "sessao-123" : input.Token;
        public Exception? Failure { get; set; }

        public MainWindow Window(Settings settings)
        {
            var window = new MainWindow(settings);
            Configure(window.View);
            return window;
        }

        public MainView View(Settings settings) => Configure(new MainView(settings));

        MainView Configure(MainView view)
        {
            view.Notify = (_, _) => { };
            view.Secrets = Store;
            view.Connect = input =>
            {
                if (Failure != null) throw Failure;
                TokensUsed.Add(input.Password != "" ? "senha" : input.Token);
                return Task.FromResult<(IZabbixClient, string)>((Client, Login(input)));
            };
            view.AskConnection = error =>
            {
                Asked.Add(error);
                return Task.FromResult<ConnectionInput?>(null);
            };
            return view;
        }
    }

    [AvaloniaFact]
    public void RestoreFromTray_KeepsConnectionAndView()
    {
        var h = new Harness();
        h.Store.Values[Url] = "token-api";
        var window = h.Window(new Settings { Url = Url, LastMapId = "5" });
        // Same wiring as App: start on Opened, hide to the tray on close.
        window.Opened += async (_, _) => await window.View.StartAsync();
        window.HideOnClose = true;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var canvas = window.View.FindControl<MapCanvas>("Canvas")!;
        Assert.Equal(["token-api"], h.TokensUsed);
        Assert.NotNull(canvas.Map);
        canvas.SetZoom(2);
        var map = canvas.Map;

        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);

        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.IsVisible);
        Assert.Single(h.TokensUsed);
        Assert.Empty(h.Asked);
        Assert.Same(map, canvas.Map);
        Assert.Equal(2, canvas.Zoom);
    }

    [AvaloniaFact]
    public async Task PasswordLogin_StoresSessionOutsideSettings_AndReusesItOnNextStart()
    {
        var h = new Harness();
        var first = h.View(new Settings());
        await first.ConnectAndSaveAsync(new ConnectionInput(Url, "", "ulisses", "senha-secreta", false));

        Assert.Equal("sessao-123", h.Store.Values[Url]);
        var saved = File.ReadAllText(Settings.FilePath);
        Assert.DoesNotContain("sessao-123", saved);
        Assert.DoesNotContain("senha-secreta", saved);

        var next = h.View(Settings.Load());
        await next.StartAsync();
        Assert.Equal(["senha", "sessao-123"], h.TokensUsed);
        Assert.Empty(h.Asked);
    }

    [AvaloniaFact]
    public async Task ExpiredSession_IsForgotten_AndUserIsAskedWithReason()
    {
        var h = new Harness { Failure = new ZabbixApiException("Session terminated, re-login, please.") };
        h.Store.Values[Url] = "sessao-velha";
        await h.View(new Settings { Url = Url, SessionLogin = true }).StartAsync();

        Assert.Empty(h.Store.Values);
        Assert.Contains("expirou", Assert.Single(h.Asked));
    }

    [AvaloniaFact]
    public async Task UnreachableServer_KeepsSavedCredential()
    {
        var h = new Harness { Failure = new HttpRequestException("Connection refused") };
        h.Store.Values[Url] = "token-api";
        await h.View(new Settings { Url = Url }).StartAsync();

        Assert.Equal("token-api", h.Store.Values[Url]);
        Assert.Equal("Connection refused", Assert.Single(h.Asked));
    }

    [AvaloniaFact]
    public async Task PlainTextTokenFromOlderVersion_MovesToSecretStore()
    {
        var h = new Harness();
        var settings = new Settings { Url = Url, Token = "token-antigo" };
        await h.View(settings).StartAsync();

        Assert.Equal("token-antigo", h.Store.Values[Url]);
        Assert.Null(settings.Token);
        Assert.DoesNotContain("token-antigo", File.ReadAllText(Settings.FilePath));
        Assert.Equal(["token-antigo"], h.TokensUsed);
    }

    [Fact]
    public async Task FileStore_RoundTrip()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "secrets.json");
        var store = new FileSecretStore(path, protect: false);
        await store.SetAsync(Url, "s1");
        Assert.Equal("s1", await store.GetAsync(Url));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        await store.DeleteAsync(Url);
        Assert.Null(await store.GetAsync(Url));
    }
}

public class KeyringTests
{
    /// <summary>Uses the real OS keyring, so it only runs when ZABBIXMAP_KEYRING_TEST=1.</summary>
    [Fact]
    public async Task OsKeyring_RoundTrip()
    {
        if (Environment.GetEnvironmentVariable("ZABBIXMAP_KEYRING_TEST") != "1") return;
        const string account = "zabbixmap-test://probe";
        var store = SecretStores.CreateDefault();
        await store.SetAsync(account, "valor-de-teste");
        Assert.Equal("valor-de-teste", await store.GetAsync(account));
        Assert.False(File.Exists(Path.Combine(Settings.Directory, "secrets.json")) &&
            File.ReadAllText(Path.Combine(Settings.Directory, "secrets.json")).Contains("valor-de-teste"));
        await store.DeleteAsync(account);
        Assert.Null(await store.GetAsync(account));
    }
}
