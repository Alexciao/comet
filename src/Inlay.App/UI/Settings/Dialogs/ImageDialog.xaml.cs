using System.Globalization;
using System.Runtime.InteropServices;
using Inlay.App.UI.Settings.Images;
using Inlay.Core.Images;
using Inlay.Core.Settings;
using Inlay.Core.Text;
using Inlay.Platform;
using Inlay.Platform.Images;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Inlay.App.UI.Settings.Dialogs;

/// <summary>What the image dialog needs from the page.</summary>
/// <param name="SnippetNames">Every snippet name.</param>
/// <param name="OtherImageNames">Every name images use, the edited entry's own excluded.</param>
/// <param name="PickFolder">Shows the folder picker; null when cancelled.</param>
internal sealed record ImageDialogContext(
    string Trigger,
    IReadOnlyList<string> SnippetNames,
    IReadOnlyList<string> OtherImageNames,
    Func<string, bool> IsEmojiShortcode,
    ImageLibrary Library,
    FolderFiles Folders,
    Func<Task<string?>> PickFolder);

/// <summary>
/// Add/Edit an image, a folder or a collection: a preview of what the code inserts (animated for GIFs), the fields
/// for the entry's kind, inline validation under each field, and for collections the names their files get. Files
/// and folders are read in the background; the dialog never waits for them.
/// </summary>
public sealed partial class ImageDialog : ContentDialog
{
    /// <summary>Collections list this many names, then "and 12 more".</summary>
    private const int ShownNames = 20;

    /// <summary>The preview frame's height (<c>ImagePreviewHeight</c>) less its border and a margin around the picture.</summary>
    private const double PreviewMaxHeight = 180 - 2 - 16;
    private const double PreviewFallbackWidth = 592 - 2 - 16;

    private static readonly string[] s_typeExtensions = ["png", "jpg", "gif", "webp", "bmp"];

    private readonly ImageEntry _draft;
    private readonly bool _isNew;
    private readonly ImageDialogContext _context;
    private readonly HashSet<string> _taken;
    private readonly CheckBox[] _typeBoxes;
    private readonly bool _animate = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
    private string? _folder;
    private long? _fileSize;
    private IReadOnlyList<ImageFile>? _files;
    private string? _previewPath;
    private int _folderRead;
    private bool _ready;
    private bool _touched;
    private bool _nameTyped;
    private bool _settingName;

    /// <param name="draft">The entry to edit (a copy: nothing changes until the page applies <see cref="Result"/>).</param>
    /// <param name="isNew">Adding rather than editing (the title, and no messages before anything is chosen).</param>
    internal ImageDialog(ImageEntry draft, bool isNew, ImageDialogContext context)
    {
        _draft = draft;
        _isNew = isNew;
        _context = context;
        _taken = new HashSet<string>(context.SnippetNames.Concat(context.OtherImageNames).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
        InitializeComponent();
        _typeBoxes = [PngBox, JpgBox, GifBox, WebpBox, BmpBox];

        var kind = draft.Kind;
        Title = (isNew ? "Add " : "Edit ") + kind switch
        {
            ImageEntryKind.Folder => "folder",
            ImageEntryKind.Collection => "collection",
            _ => "image",
        };
        Show(NameSection, kind != ImageEntryKind.Collection);
        Show(PreviewSection, kind != ImageEntryKind.Collection);
        Show(FolderSection, kind != ImageEntryKind.Image);
        Show(TypesSection, kind != ImageEntryKind.Image);
        Show(ModeSection, kind == ImageEntryKind.Folder);
        Show(PrefixSection, kind == ImageEntryKind.Collection);
        Show(NamesSection, kind == ImageEntryKind.Collection);
        if (kind == ImageEntryKind.Folder)
            NameBox.PlaceholderText = "For example, screenshot";

        _settingName = true;
        NameBox.Text = draft.Name;
        _settingName = false;
        KeywordsBox.Text = draft.Keywords;
        PrefixBox.Text = draft.Prefix;
        InsertAsBox.SelectedIndex = (int)draft.InsertAs;
        ModeBox.SelectedIndex = (int)draft.Mode;
        PickCountBox.Value = Math.Clamp(draft.PickCount, ImageEntry.MinPickCount, ImageEntry.MaxPickCount);
        SubfoldersBox.IsChecked = draft.IncludeSubfolders;
        var types = ImageTypes.Filter(draft.FileTypes);
        for (var i = 0; i < _typeBoxes.Length; i++)
            _typeBoxes[i].IsChecked = (types & ImageTypes.FromExtension(s_typeExtensions[i])) != 0;
        _folder = draft.Folder;
        BuildFolderMenu();

        _ready = true;
        ShowFolder();
        ShowHints();
        Validate();
        if (kind == ImageEntryKind.Image)
            _ = ShowImageAsync();
        else
            _ = ReadFolderAsync();
        Opened += OnOpened;
    }

    /// <summary>The entry as edited, with the draft's id.</summary>
    internal ImageEntry Result => new()
    {
        Id = _draft.Id,
        Kind = _draft.Kind,
        File = _draft.File,
        SourceName = _draft.SourceName,
        Name = _draft.Kind == ImageEntryKind.Collection ? _draft.Name : NameBox.Text.Trim(),
        Keywords = KeywordsBox.Text.Trim(),
        InsertAs = (ImageInsertAs)Math.Max(0, InsertAsBox.SelectedIndex),
        Folder = _draft.Kind == ImageEntryKind.Image ? _draft.Folder : _folder,
        Mode = (FolderMode)Math.Max(0, ModeBox.SelectedIndex),
        PickCount = PickCount,
        IncludeSubfolders = SubfoldersBox.IsChecked == true,
        FileTypes = _draft.Kind == ImageEntryKind.Image ? [.. _draft.FileTypes] : CheckedTypes(),
        Prefix = PrefixBox.Text.Trim(),
    };

    /// <summary>A collection's number of names when the dialog closed, or -1 when its folder hadn't been read.</summary>
    internal int CollectionCount { get; private set; } = -1;

    private int PickCount => double.IsNaN(PickCountBox.Value)
        ? new ImageEntry().PickCount
        : Math.Clamp((int)Math.Round(PickCountBox.Value), ImageEntry.MinPickCount, ImageEntry.MaxPickCount);

    private bool Recursive => SubfoldersBox.IsChecked == true;

    private ImageType Types
    {
        get
        {
            var types = ImageType.None;
            for (var i = 0; i < _typeBoxes.Length; i++)
            {
                if (_typeBoxes[i].IsChecked == true)
                    types |= ImageTypes.FromExtension(s_typeExtensions[i]);
            }

            return types;
        }
    }

    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        if (_draft.Kind == ImageEntryKind.Collection)
        {
            (_folder is null ? FolderButton : (Control)PrefixBox).Focus(FocusState.Programmatic);
            return;
        }

        NameBox.Focus(FocusState.Programmatic);
        NameBox.SelectAll();
    }

    // ------------------------------------------------------------------ fields

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingName)
        {
            _nameTyped = true;
            _touched = true;
        }

        Validate();
    }

    private void OnPrefixChanged(object sender, TextChangedEventArgs e)
    {
        _touched = true;
        ShowFiles();
        Validate();
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
            return;
        ShowHints();
        ShowFiles();
    }

    private void OnPickCountChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue))
            sender.Value = PickCount; // cleared: back to a number
    }

    private void OnInsertAsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready)
            ShowHints();
    }

    /// <summary>Include subfolders or a file type changed.</summary>
    private void OnFolderOptionChanged(object sender, RoutedEventArgs e)
    {
        _touched = true;
        if (ReferenceEquals(sender, SubfoldersBox))
            _ = ReadFolderAsync();
        else
            ShowFiles();
        Validate();
    }

    private void ShowHints()
    {
        PickCountBox.IsEnabled = ModeBox.SelectedIndex == (int)FolderMode.Pick;
        ModeHint.Text = (FolderMode)ModeBox.SelectedIndex switch
        {
            FolderMode.Random => "Inserts a random image from the folder",
            FolderMode.Pick => "Quick Insert shows the newest images to choose from",
            _ => "Inserts the newest image in the folder",
        };
        InsertAsHint.Text = (ImageInsertAs)InsertAsBox.SelectedIndex switch
        {
            ImageInsertAs.File => "Attaches the file, as if you dragged it into the app",
            ImageInsertAs.Path => "Types the file's full path as text",
            _ => "Pastes the picture itself, as if you copied it from a browser",
        };
    }

    private List<string> CheckedTypes()
    {
        List<string> types = [];
        for (var i = 0; i < _typeBoxes.Length; i++)
        {
            if (_typeBoxes[i].IsChecked == true)
                types.Add(s_typeExtensions[i]);
        }

        return types.Count == s_typeExtensions.Length ? [] : types; // every type: stored as "all"
    }

    private void Validate()
    {
        if (!_ready)
            return;
        var entry = Result;
        var messages = EntryValidation.ValidateImage(entry, _context.SnippetNames, _context.OtherImageNames, _context.IsEmojiShortcode, _fileSize, _context.Trigger);
        // Don't scold a new, untouched dialog: show problems once something was typed or chosen.
        var pristine = _isNew && !_touched && NameBox.Text.Length == 0 && _folder is null;
        FieldMessage.Show(NameError, NameWarning, messages, ValidationField.Name, pristine);
        FieldMessage.Show(PrefixError, PrefixWarning, messages, ValidationField.Prefix, pristine);
        FieldMessage.Show(FolderError, FolderWarning, messages, ValidationField.Folder, pristine);
        FieldMessage.Show(FileError, FileWarning, messages, ValidationField.File, hide: false);
        var noTypes = entry.Kind != ImageEntryKind.Image && Types == ImageType.None;
        Show(TypesError, noTypes);
        IsPrimaryButtonEnabled = !EntryValidation.HasErrors(messages) && !noTypes;

        // The code as typed in other apps, once the name is usable.
        var usable = entry.Name.Length > 0 && NameError.Visibility == Visibility.Collapsed && NameWarning.Visibility == Visibility.Collapsed;
        NameHint.Text = usable ? $"Type {TriggerFormat.Code(_context.Trigger, entry.Name)} in any app to insert it" : "";
        Show(NameHint, usable);
    }

    // ------------------------------------------------------------------ folder

    private void BuildFolderMenu()
    {
        foreach (var (token, name) in ImageText.KnownFolders)
        {
            var item = new MenuFlyoutItem { Text = name };
            item.Click += (_, _) => SetFolder(token);
            FolderMenu.Items.Add(item);
        }

        FolderMenu.Items.Add(new MenuFlyoutSeparator());
        var browse = new MenuFlyoutItem { Text = "Choose a folder…", Icon = new FontIcon { Glyph = "" } };
        browse.Click += OnBrowse;
        FolderMenu.Items.Add(browse);
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await _context.PickFolder().ConfigureAwait(true) is { } path)
                SetFolder(path);
        }
        catch (COMException ex)
        {
            Log.Warn($"images: folder picker failed ({ex.HResult:X8})");
        }
    }

    private void SetFolder(string folder)
    {
        _folder = folder;
        _touched = true;
        // A new folder entry without a name yet is named after the folder (the user can change it).
        if (_draft.Kind == ImageEntryKind.Folder && !_nameTyped && NameBox.Text.Length == 0)
        {
            _settingName = true;
            NameBox.Text = new ImageNames(_context.SnippetNames, _context.OtherImageNames, _context.IsEmojiShortcode).Take(ImageNames.FromFile(ImageText.FolderName(folder)));
            _settingName = false;
        }

        ShowFolder();
        _ = ReadFolderAsync();
        Validate();
    }

    private void ShowFolder()
    {
        var known = ImageText.KnownFolders.Any(k => string.Equals(k.Token, _folder, StringComparison.OrdinalIgnoreCase));
        FolderText.Text = _folder is null ? "Choose a folder" : known ? ImageText.FolderName(_folder) : _folder;
        ToolTipService.SetToolTip(FolderButton, _folder is null || known ? null : _folder);
    }

    /// <summary>Reads the chosen folder in the background, then shows its preview or names.</summary>
    private async Task ReadFolderAsync()
    {
        var read = ++_folderRead;
        _files = null;
        if (_folder is null)
        {
            ShowFiles();
            return;
        }

        var scan = _context.Folders.Scan(_folder, Recursive);
        if (!scan.IsCompleted)
            ShowFiles(); // "Reading the folder…"
        var files = await scan.ConfigureAwait(true);
        if (read != _folderRead)
            return; // another folder was chosen meanwhile
        _files = files;
        ShowFiles();
    }

    /// <summary>The folder's preview (folders) or names (collections), from the files read last.</summary>
    private void ShowFiles()
    {
        if (!_ready || _draft.Kind == ImageEntryKind.Image)
            return;
        var types = Types;
        if (_draft.Kind == ImageEntryKind.Folder)
        {
            PreviewCaption.Text = _folder is null ? "" : ImageText.Folder((FolderMode)Math.Max(0, ModeBox.SelectedIndex), ImageText.FolderName(_folder));
            if (_folder is null || _files is null)
            {
                ShowPlaceholder("", _folder is null ? "Choose a folder to see its newest image" : "Reading the folder…");
                return;
            }

            if (FolderResolve.Newest(_files, types) is not { } newest)
            {
                ShowPlaceholder("", "No images in this folder yet");
                return;
            }

            if (!string.Equals(newest.Path, _previewPath, StringComparison.OrdinalIgnoreCase))
                _ = ShowFolderImageAsync(newest.Path);
            return;
        }

        if (_folder is null || _files is null)
        {
            CollectionCount = -1;
            ShowNamesMessage(_folder is null ? "Choose a folder to see the names" : "Reading the folder…");
            Show(NamesMore, false);
            return;
        }

        var names = CollectionNaming.Name(_files, PrefixBox.Text.Trim(), types, _taken.Contains);
        CollectionCount = names.Count;
        if (names.Count == 0)
            ShowNamesMessage("No images in this folder yet");
        else
            ShowNamesMessage(null, string.Join("    ", names.Take(ShownNames).Select(n => TriggerFormat.Code(_context.Trigger, n.Name))));
        NamesMore.Text = $"and {(names.Count - ShownNames).ToString(CultureInfo.CurrentCulture)} more";
        Show(NamesMore, names.Count > ShownNames);
    }

    /// <summary>The names box shows either the names or a message about them (in secondary text).</summary>
    private void ShowNamesMessage(string? message, string names = "")
    {
        NamesText.Text = names;
        NamesMessage.Text = message ?? "";
        Show(NamesMessage, message is not null);
    }

    // ------------------------------------------------------------------ preview

    private async Task ShowImageAsync()
    {
        if (_draft.File is not { } file)
        {
            ShowPlaceholder("", "");
            return;
        }

        ShowPlaceholder("", "");
        var path = _context.Library.PathOf(file);
        var facts = await Task.Run(() => ImageFacts.Read(path)).ConfigureAwait(true);
        _fileSize = facts.Bytes >= 0 ? facts.Bytes : null;
        var description = ImageText.Describe(ImageTypes.FromPath(file), facts.Bytes, CultureInfo.CurrentCulture);
        PreviewCaption.Text = string.IsNullOrEmpty(_draft.SourceName) ? description : $"{description} · {_draft.SourceName}";
        if (facts.Bytes < 0)
            ShowPlaceholder("", "Couldn't read this image. Add it again from the original file.");
        else
            ShowPicture(path, facts);
        Validate();
    }

    private async Task ShowFolderImageAsync(string path)
    {
        _previewPath = path;
        var facts = await Task.Run(() => ImageFacts.Read(path)).ConfigureAwait(true);
        if (!string.Equals(path, _previewPath, StringComparison.OrdinalIgnoreCase))
            return;
        if (facts.Bytes < 0)
            ShowPlaceholder("", "Couldn't read the newest image");
        else
            ShowPicture(path, facts);
    }

    private void ShowPlaceholder(string glyph, string message)
    {
        _previewPath = null;
        PreviewImage.Source = null;
        PreviewPlaceholderIcon.Glyph = glyph;
        PreviewMessage.Text = message;
        Show(PreviewMessage, message.Length > 0);
        Show(PreviewPlaceholder, true);
    }

    /// <summary>
    /// Shows <paramref name="path"/> at its own size or smaller, decoded no larger than shown (never the full image);
    /// GIFs play unless animations are off in Windows.
    /// </summary>
    private void ShowPicture(string path, ImageFacts facts)
    {
        var maxWidth = PreviewFrame.ActualWidth > 0 ? PreviewFrame.ActualWidth - 18 : PreviewFallbackWidth;
        var bitmap = new BitmapImage { DecodePixelType = DecodePixelType.Logical, AutoPlay = _animate };
        if (facts.Width > 0 && facts.Height > 0)
        {
            var scale = Math.Min(1, Math.Min(maxWidth / facts.Width, PreviewMaxHeight / facts.Height));
            PreviewImage.Width = Math.Max(1, Math.Round(facts.Width * scale));
            PreviewImage.Height = Math.Max(1, Math.Round(facts.Height * scale));
            bitmap.DecodePixelWidth = (int)PreviewImage.Width;
        }
        else
        {
            // Size unknown (an unusual header): fit the frame.
            PreviewImage.Width = PreviewImage.Height = double.NaN;
            PreviewImage.MaxWidth = maxWidth;
            PreviewImage.MaxHeight = PreviewMaxHeight;
            bitmap.DecodePixelHeight = (int)PreviewMaxHeight;
        }

        bitmap.ImageOpened += (_, _) =>
        {
            if (PreviewImage.Source == bitmap)
                Show(PreviewPlaceholder, false);
        };
        bitmap.ImageFailed += (_, e) =>
        {
            Log.Warn($"images: preview failed ({e.ErrorMessage})");
            if (PreviewImage.Source == bitmap)
                ShowPlaceholder(_draft.Kind == ImageEntryKind.Image ? "" : "", "Couldn't show this image");
        };
        bitmap.UriSource = new Uri(path);
        PreviewImage.Source = bitmap;
        _previewPath = path;
    }

    private static void Show(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
}
