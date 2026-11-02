using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ZabbixMap.App.Services;
using ZabbixMap.Core;

namespace ZabbixMap.App;

public sealed record ConnectionInput(string Url, string Token, string User, string Password, bool IgnoreCertificateErrors);

/// <summary>Small modal dialogs.</summary>
public static class Dialogs
{
    static Window Shell(string title, Control content, out StackPanel buttons, double width = 460)
    {
        buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(content);
        return new Window
        {
            Title = title,
            Content = root,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
    }

    static Button Add(StackPanel panel, string text, Action onClick)
    {
        var button = new Button { Content = text, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        button.Click += (_, _) => onClick();
        panel.Children.Add(button);
        return button;
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message)
    {
        var window = Shell(title, new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, out var buttons);
        Add(buttons, "Cancelar", () => window.Close(false));
        Add(buttons, "Confirmar", () => window.Close(true));
        return await window.ShowDialog<bool>(owner);
    }

    public static async Task<string?> PromptAsync(Window owner, string title, string caption, string initial = "", bool multiline = false)
    {
        var box = new TextBox { Text = initial, AcceptsReturn = multiline, MinHeight = multiline ? 90 : 0 };
        var body = new StackPanel { Spacing = 6, Children = { new TextBlock { Text = caption }, box } };
        var window = Shell(title, body, out var buttons);
        Add(buttons, "Cancelar", () => window.Close(null));
        Add(buttons, "OK", () => window.Close(box.Text ?? "")).IsDefault = !multiline;
        window.Opened += (_, _) => box.Focus();
        return await window.ShowDialog<string?>(owner);
    }

    public static async Task<ConnectionInput?> ConnectionAsync(Window owner, Settings settings, string savedToken, string? error)
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
    public static async Task<bool> LinkAsync(Window owner, MapLink link, IReadOnlyList<(string Host, ItemInfo Item)> items, bool expressionMacros)
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
