using Avalonia.Controls;
using ZabbixMap.UI.Services;

namespace ZabbixMap.UI;

/// <summary>Desktop window around <see cref="MainView"/>; the browser hosts the view directly.</summary>
public sealed class MainWindow : Window
{
    public MainView View { get; }

    /// <summary>When true, closing hides to the tray so monitoring continues.</summary>
    public bool HideOnClose { get; set; }

    public MainWindow() : this(Settings.Load())
    {
    }

    public MainWindow(Settings settings)
    {
        View = new MainView(settings);
        Content = View;
        Title = View.Title;
        Width = 1400;
        Height = 860;
        View.TitleChanged += title => Title = title;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.CloseReason is WindowCloseReason.WindowClosing && HideOnClose)
        {
            e.Cancel = true;
            Hide();
        }
    }
}
