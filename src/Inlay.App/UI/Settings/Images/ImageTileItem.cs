using System.ComponentModel;
using Inlay.Core.Images;
using Inlay.Core.Settings;
using Inlay.Core.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Inlay.App.UI.Settings.Images;

/// <summary>
/// The page behind the image tiles. Tiles call it through their items with compiled event bindings
/// (<c>Click="{x:Bind Open}"</c>): plain handler names inside a page's DataTemplate are not connected.
/// </summary>
internal interface IImageTileHost
{
    void Open(ImageTileItem item);

    /// <summary>The "…" button (<paramref name="at"/> null) or a right-click at <paramref name="at"/> in <paramref name="anchor"/>.</summary>
    void ShowMenu(ImageTileItem item, FrameworkElement anchor, Point? at);
}

/// <summary>
/// An image, folder or collection as its tile shows it: a thumbnail, the code and a caption. The text is fixed (an
/// edit produces a new item, the list replaces the tile); the thumbnail arrives later and changes when a folder's
/// newest image does.
/// </summary>
public sealed partial class ImageTileItem : INotifyPropertyChanged
{
    private readonly IImageTileHost _host;
    private readonly string _signature;
    private ImageSource? _thumbnail;

    internal ImageTileItem(ImageEntry entry, string trigger, IImageTileHost host)
    {
        _host = host;
        Entry = entry;
        _signature = Signature(entry, trigger);
        var folder = ImageText.FolderName(entry.Folder);
        switch (entry.Kind)
        {
            case ImageEntryKind.Folder:
                Code = TriggerFormat.Code(trigger, entry.Name);
                Caption = ImageText.Folder(entry.Mode, folder);
                BadgeGlyph = "";
                AccessibleName = $"{Code}, folder, {Caption}";
                break;
            case ImageEntryKind.Collection:
                Code = TriggerFormat.Code(trigger, entry.Prefix + "…");
                Caption = folder;
                BadgeGlyph = "";
                AccessibleName = $"{Code}, collection from {folder}";
                break;
            default:
                Code = TriggerFormat.Code(trigger, entry.Name);
                Caption = ImageTypes.DisplayName(ImageTypes.FromPath(entry.File));
                BadgeGlyph = "";
                AccessibleName = $"{Code}, {Caption}";
                break;
        }
    }

    internal ImageEntry Entry { get; }

    /// <summary>":lgtm"; for collections the prefix and an ellipsis (":gif_…").</summary>
    public string Code { get; }

    /// <summary>"GIF", "Latest in Screenshots", or a collection's folder name.</summary>
    public string Caption { get; }

    public string AccessibleName { get; }

    public string MoreOptionsName => $"More options for {Code}";

    /// <summary>Folders and collections carry a small badge in the thumbnail's corner.</summary>
    public bool HasBadge => Entry.Kind != ImageEntryKind.Image;

    public string BadgeGlyph { get; }

    public string BadgeName => Entry.Kind == ImageEntryKind.Collection ? "Collection" : "Folder";

    /// <summary>Shown until the thumbnail arrives, or when there is none (an empty folder).</summary>
    public string PlaceholderGlyph => Entry.Kind == ImageEntryKind.Image ? "" : "";

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            if (ReferenceEquals(_thumbnail, value))
                return;
            var hadOne = _thumbnail is not null;
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            if (hadOne != value is not null)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowPlaceholder)));
        }
    }

    public bool ShowPlaceholder => _thumbnail is null;

    /// <summary>The file the thumbnail should show (the image, or a folder's newest), or null.</summary>
    internal string? SourcePath { get; private set; }

    /// <summary>The source's last-write time (default for library files, which never change).</summary>
    internal DateTime SourceVersion { get; private set; }

    /// <summary>The source the current <see cref="Thumbnail"/> was made from.</summary>
    private (string? Path, DateTime Version) _shown;

    /// <summary>True when the item no longer matches its entry (edited, or the trigger changed): make a new one.</summary>
    internal bool IsStale(string trigger) => _signature != Signature(Entry, trigger);

    /// <summary>Sets what the thumbnail should show. Returns true when that changed.</summary>
    internal bool SetSource(string? path, DateTime version)
    {
        if (string.Equals(path, SourcePath, StringComparison.OrdinalIgnoreCase) && version == SourceVersion)
            return false;
        SourcePath = path;
        SourceVersion = version;
        if (path is null)
            ShowThumbnail(null);
        return true;
    }

    /// <summary>True when the thumbnail shown isn't the current source's yet.</summary>
    internal bool NeedsThumbnail => SourcePath is not null && (_thumbnail is null || _shown != (SourcePath, SourceVersion));

    /// <summary>Shows a thumbnail of the current source (null: the placeholder).</summary>
    internal void ShowThumbnail(ImageSource? bitmap)
    {
        _shown = bitmap is null ? default : (SourcePath, SourceVersion);
        Thumbnail = bitmap;
    }

    public void Open() => _host.Open(this);

    public void ShowMenu(object sender, RoutedEventArgs e) => _host.ShowMenu(this, (FrameworkElement)sender, null);

    /// <summary>Right-click, Shift+F10 or the menu key on the tile.</summary>
    public void OnContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        _host.ShowMenu(this, (FrameworkElement)sender, e.TryGetPosition(sender, out var at) ? at : null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static string Signature(ImageEntry e, string trigger) =>
        string.Join('\u001f', trigger, e.Name, e.Kind, e.File, e.Folder, e.Mode, e.Prefix, e.IncludeSubfolders, string.Join(',', e.FileTypes));
}
