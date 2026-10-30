using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ZabbixMap.App;

public enum TreeNodeKind { Section, Map, Group, Host, Problem }

public sealed partial class TreeNode(TreeNodeKind kind, string title, string id = "") : ObservableObject
{
    public TreeNodeKind Kind { get; } = kind;
    public string Id { get; } = id;
    public ObservableCollection<TreeNode> Children { get; } = [];

    [ObservableProperty] string _title = title;
    [ObservableProperty] IBrush _dot = Brushes.Transparent;
    [ObservableProperty] bool _isExpanded;
}
