using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZabbixMap.UI.Services;
using ZabbixMap.Core;

namespace ZabbixMap.UI;

public sealed record ConnectionInput(string Url, string Token, string User, string Password, bool IgnoreCertificateErrors);

/// <summary>Small modal dialogs.</summary>
public static class Dialogs
{
    static DialogHandle Shell(string title, Control content, out StackPanel buttons, double width = 460)
    {
        buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(content);
        return new DialogHandle(title, root, width);
    }

    static Button Add(StackPanel panel, string text, Action onClick)
    {
        var button = new Button { Content = text, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        button.Click += (_, _) => onClick();
        panel.Children.Add(button);
        return button;
    }

    public static async Task<bool> ConfirmAsync(Control owner, string title, string message)
    {
        var window = Shell(title, new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, out var buttons);
        Add(buttons, "Cancelar", () => window.Close(false));
        Add(buttons, "Confirmar", () => window.Close(true));
        return await window.ShowDialog<bool>(owner);
    }

    public static async Task<string?> PromptAsync(Control owner, string title, string caption, string initial = "", bool multiline = false)
    {
        var box = new TextBox { Text = initial, AcceptsReturn = multiline, MinHeight = multiline ? 90 : 0 };
        var body = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = caption }, box } };
        var window = Shell(title, body, out var buttons);
        Add(buttons, "Cancelar", () => window.Close(null));
        Add(buttons, "OK", () => window.Close(box.Text ?? "")).IsDefault = !multiline;
        box.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => box.Focus());
        return await window.ShowDialog<string?>(owner);
    }

    public static async Task<ConnectionInput?> ConnectionAsync(Control owner, Settings settings, string savedToken, string? error)
    {
        var url = new TextBox { Text = settings.Url, PlaceholderText = "https://zabbix.exemplo.com" };
        var token = new TextBox { Text = savedToken, PasswordChar = '•', PlaceholderText = "Recomendado (Zabbix 5.4+)" };
        var user = new TextBox { Text = settings.User };
        var password = new TextBox { PasswordChar = '•', PlaceholderText = "Não é salva; a sessão fica no cofre do sistema" };
        var ignore = new CheckBox { Content = "Ignorar erros de certificado TLS (somente em rede confiável)", IsChecked = settings.IgnoreCertificateErrors };
        var body = new StackPanel { Spacing = 4 };
        if (error != null) body.Children.Add(new TextBlock { Text = error, Foreground = Avalonia.Media.Brushes.Firebrick, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = "URL do Zabbix" });
        body.Children.Add(url);
        body.Children.Add(new TextBlock { Text = "Token de API" });
        body.Children.Add(token);
        body.Children.Add(new TextBlock { Text = "ou usuário", Margin = new Thickness(0, 8, 0, 0) });
        body.Children.Add(user);
        body.Children.Add(new TextBlock { Text = "e senha" });
        body.Children.Add(password);
        body.Children.Add(ignore);

        var window = Shell("Conexão com o Zabbix", body, out var buttons);
        Add(buttons, "Cancelar", () => window.Close(null));
        Add(buttons, "Conectar", () => window.Close(new ConnectionInput(url.Text ?? "", token.Text ?? "", user.Text ?? "", password.Text ?? "", ignore.IsChecked == true))).IsDefault = true;
        return await window.ShowDialog<ConnectionInput?>(owner);
    }

    /// <summary>Edits a link in place; returns false when cancelled.</summary>
    public static async Task<bool> LinkAsync(Control owner, MapLink link, IReadOnlyList<(string Host, ItemInfo Item)> items, bool expressionMacros)
    {
        var entries = items.Select(i => new LinkItem(i.Host, i.Item)).ToList();
        var rx = new ComboBox { ItemsSource = entries.Where(e => e.Item.Key.Contains(".in")).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "(nenhum)" };
        var tx = new ComboBox { ItemsSource = entries.Where(e => e.Item.Key.Contains(".out")).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "(nenhum)" };
        var label = new TextBox { Text = link.Label, AcceptsReturn = true, MinHeight = 56 };
        var style = new ComboBox { ItemsSource = new[] { "Linha", "Linha grossa", "Pontilhada", "Tracejada" }, SelectedIndex = link.DrawType switch { 2 => 1, 3 => 2, 4 => 3, _ => 0 }, HorizontalAlignment = HorizontalAlignment.Stretch };
        var color = new TextBox { Text = link.Color };

        void UpdateLabel()
        {
            var lines = new List<string>();
            if (rx.SelectedItem is LinkItem r) lines.Add("Rx: " + LabelResolver.BuildItemMacro(r.Host, r.Item.Key, expressionMacros));
            if (tx.SelectedItem is LinkItem t) lines.Add("Tx: " + LabelResolver.BuildItemMacro(t.Host, t.Item.Key, expressionMacros));
            if (lines.Count > 0) label.Text = string.Join('\n', lines);
        }
        rx.SelectionChanged += (_, _) => UpdateLabel();
        tx.SelectionChanged += (_, _) => UpdateLabel();

        var body = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "Item de Rx (tráfego de entrada)" }, rx,
                new TextBlock { Text = "Item de Tx (tráfego de saída)" }, tx,
                new TextBlock { Text = "Rótulo" }, label,
                new TextBlock { Text = "Estilo" }, style,
                new TextBlock { Text = "Cor (hexadecimal)" }, color,
            },
        };
        var window = Shell("Link", body, out var buttons, 560);
        Add(buttons, "Cancelar", () => window.Close(false));
        Add(buttons, "OK", () => window.Close(true));
        if (!await window.ShowDialog<bool>(owner)) return false;

        link.Label = label.Text ?? "";
        link.DrawType = style.SelectedIndex switch { 1 => 2, 2 => 3, 3 => 4, _ => 0 };
        link.Color = (color.Text ?? "").Trim().TrimStart('#');
        return true;
    }

    sealed record LinkItem(string Host, ItemInfo Item)
    {
        public override string ToString() => $"{Host}: {Item.Name}";
    }
}

/// <summary>A modal dialog shown as a window on desktop and as an overlay in the browser.</summary>
public sealed class DialogHandle(string title, Control content, double width)
{
    readonly TaskCompletionSource<object?> _result = new();
    Action? _dismiss;

    public void Close(object? result)
    {
        _result.TrySetResult(result);
        _dismiss?.Invoke();
    }

    public async Task<T> ShowDialog<T>(Control owner)
    {
        _dismiss = DialogPresenter.Present(owner, title, content, width, null, modal: true, onClosed: () => _result.TrySetResult(null));
        return await _result.Task is T value ? value : default!;
    }
}

public static class DialogPresenter
{
    /// <summary>Non-modal panel, such as the host summary: a separate window on desktop, an overlay in the browser.</summary>
    public static void ShowPanel(Control owner, string title, Control content, double width, double height) =>
        Present(owner, title, content, width, height, modal: false, onClosed: null);

    /// <summary>Shows the content and returns the action that dismisses it.</summary>
    public static Action Present(Control owner, string title, Control content, double width, double? height, bool modal, Action? onClosed)
    {
        if (TopLevel.GetTopLevel(owner) is Window ownerWindow)
        {
            var window = new Window
            {
                Title = title,
                Content = content,
                Width = width,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            if (height is { } h) window.Height = h;
            else
            {
                window.SizeToContent = SizeToContent.Height;
                window.CanResize = false;
            }
            window.Closed += (_, _) => onClosed?.Invoke();
            if (modal) _ = window.ShowDialog(ownerWindow);
            else window.Show(ownerWindow);
            return window.Close;
        }

        var host = owner as MainView ?? owner.FindAncestorOfType<MainView>()
            ?? throw new InvalidOperationException("Sem janela nem MainView para exibir o diálogo.");
        Action? remove = null;
        var closeButton = new Button { Content = "✕", Padding = new Thickness(8, 2), Background = Brushes.Transparent };
        var titleBar = new DockPanel { Background = new SolidColorBrush(Color.Parse("#2B3A4A")) };
        DockPanel.SetDock(closeButton, Dock.Right);
        titleBar.Children.Add(closeButton);
        titleBar.Children.Add(new TextBlock { Text = title, Foreground = Brushes.White, Margin = new Thickness(10, 6), FontWeight = FontWeight.SemiBold });
        var body = new DockPanel();
        DockPanel.SetDock(titleBar, Dock.Top);
        body.Children.Add(titleBar);
        body.Children.Add(content);
        var card = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            BoxShadow = BoxShadows.Parse("0 4 24 0 #66000000"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Width = width,
            Margin = new Thickness(16),
            Child = body,
        };
        if (height is { } panelHeight) card.Height = panelHeight;
        // Dims and blocks the map while a modal dialog is open.
        var layer = new Panel { Background = new SolidColorBrush(Colors.Black, modal ? 0.35 : 0.15), Children = { card } };
        closeButton.Click += (_, _) =>
        {
            remove?.Invoke();
            onClosed?.Invoke();
        };
        remove = host.ShowOverlay(layer);
        return () => remove();
    }
}
