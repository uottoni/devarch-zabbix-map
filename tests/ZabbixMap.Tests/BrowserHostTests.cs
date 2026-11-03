using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Xunit;
using ZabbixMap.UI;
using ZabbixMap.UI.Services;

namespace ZabbixMap.Tests;

/// <summary>In the browser there is no Window: dialogs and panels must open as overlays inside MainView.</summary>
public class BrowserHostTests
{
    static Button ButtonIn(Panel overlay, string text) =>
        overlay.GetLogicalDescendants().OfType<Button>().First(b => b.Content as string == text);

    [AvaloniaFact]
    public async Task ModalDialog_WithoutWindow_UsesOverlay()
    {
        var view = new MainView(new Settings());
        var overlay = view.FindControl<Panel>("OverlayHost")!;

        var answer = Dialogs.ConfirmAsync(view, "Excluir mapa", "Tem certeza?");
        Assert.True(overlay.IsVisible);
        Assert.False(answer.IsCompleted);

        ButtonIn(overlay, "Confirmar").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(await answer);
        Assert.False(overlay.IsVisible);
    }

    [AvaloniaFact]
    public async Task ClosingOverlayWithX_CancelsDialog()
    {
        var view = new MainView(new Settings());
        var overlay = view.FindControl<Panel>("OverlayHost")!;
        var answer = Dialogs.PromptAsync(view, "Novo mapa", "Nome");
        ButtonIn(overlay, "✕").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Null(await answer);
        Assert.Empty(overlay.Children);
    }

    [AvaloniaFact]
    public void Panel_WithoutWindow_OpensAndCloses()
    {
        var view = new MainView(new Settings());
        var overlay = view.FindControl<Panel>("OverlayHost")!;
        DialogPresenter.ShowPanel(view, "Resumo", new TextBlock { Text = "conteúdo" }, 600, 400);
        Assert.Single(overlay.Children);
        ButtonIn(overlay, "✕").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.False(overlay.IsVisible);
    }
}
