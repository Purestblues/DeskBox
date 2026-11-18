using System.Windows.Media;
using DeskBox.Models;

namespace DeskBox.Views;

public sealed class BoxViewItem
{
    public BoxItemEntry Entry { get; }
    public string DisplayName => Entry.DisplayName;
    public string Path => Entry.Path;
    public ImageSource? Icon { get; }

    public BoxViewItem(BoxItemEntry entry, ImageSource? icon)
    {
        Entry = entry;
        Icon = icon;
    }

    public override string ToString() => DisplayName;
}
