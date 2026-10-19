using System.Collections.ObjectModel;
using System.ComponentModel;
using Comet.Core.Settings;
using Comet.Core.Snippets;
using Comet.Platform.Context;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Comet.App.UI.Settings;

/// <summary>A snippet as its card shows it. Immutable: an edit produces a new item (the list replaces the card).</summary>
public sealed partial class SnippetItem
{
    internal SnippetItem(SnippetDefinition definition)
    {
        Definition = definition;
        Name = definition.Name;
        Text = definition.Text;
        Preview = OneLine(definition.Text);
        HasWarning = SnippetTemplate.Validate(definition.Text).Count > 0;
    }

    internal SnippetDefinition Definition { get; }

    public string Name { get; }

    internal string Text { get; }

    public string Preview { get; }

    public string MoreOptionsName => $"More options for {Name}";

    /// <summary>A placeholder is malformed (it would be inserted as typed): the card shows a warning icon.</summary>
    public bool HasWarning { get; }

    internal bool IsCurrent => Definition.Name == Name && Definition.Text == Text;

    /// <summary>One line of text, whitespace collapsed, for list previews (the card trims it with an ellipsis).</summary>
    internal static string OneLine(string text)
    {
        var trimmed = text.Trim();
        var newline = trimmed.IndexOfAny(['\r', '\n']);
        var line = newline < 0 ? trimmed : trimmed[..newline] + " …";
        return line.Length > 120 ? line[..120] + "…" : line;
    }
}

/// <summary>An auto-replace entry as its card shows it ("cmq → comunque").</summary>
public sealed partial class ReplacementItem
{
    internal ReplacementItem(AutoReplaceDefinition definition)
    {
        Definition = definition;
        Abbreviation = definition.Abbreviation;
        Replacement = definition.Replacement;
        MatchCase = definition.MatchCase;
        Header = $"{definition.Abbreviation} → {SnippetItem.OneLine(definition.Replacement)}";
        Description = definition.MatchCase ? "Match case" : "";
        _enabled = definition.Enabled;
    }

    private bool _enabled;

    internal AutoReplaceDefinition Definition { get; }

    internal string Abbreviation { get; }

    internal string Replacement { get; }

    internal bool MatchCase { get; }

    public string Header { get; }

    public string Description { get; }

    /// <summary>Read when a card is bound to this item; the switch then owns the state and reports it through <see cref="SetEnabled"/>.</summary>
    public bool IsOn => _enabled;

    public string MoreOptionsName => $"More options for {Abbreviation}";

    public string ToggleName => $"Use {Abbreviation}";

    internal bool IsCurrent => Definition.Abbreviation == Abbreviation && Definition.Replacement == Replacement
        && Definition.MatchCase == MatchCase && Definition.Enabled == _enabled;

    /// <summary>The card's switch was flipped. Returns false when nothing changed (a recycled card being bound).</summary>
    internal bool SetEnabled(bool on)
    {
        if (on == _enabled)
            return false;
        _enabled = on;
        Definition.Enabled = on;
        return true;
    }
}

/// <summary>
/// A program in an Excluded apps list or in the Add app dialog. Its details and icon arrive later (read in the
/// background), so the card shows the file name at once and fills in the rest.
/// </summary>
public sealed partial class AppItem : INotifyPropertyChanged
{
    private ImageSource? _icon;

    internal AppItem(ProgramInfo program)
    {
        Program = program;
    }

    internal ProgramInfo Program { get; private set; }

    public string FileName => Program.FileName;

    public string DisplayName => Program.DisplayName;

    /// <summary>The path, or the file name when the path isn't known (the app isn't running and was never browsed to).</summary>
    public string Location => Program.Path ?? Program.FileName;

    public string RemoveName => $"Remove {Program.DisplayName}";

    public ImageSource? Icon
    {
        get => _icon;
        private set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Shows the program's description and path once they have been read. UI thread.</summary>
    internal void Resolve(ProgramInfo program)
    {
        if (program == Program)
            return;
        Program = program;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Location)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RemoveName)));
    }

    /// <summary>Shows icon pixels extracted in the background. UI thread.</summary>
    internal void SetIcon(IconPixels? pixels)
    {
        if (pixels is null)
            return;
        var bitmap = new WriteableBitmap(pixels.Size, pixels.Size);
        using (var stream = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(bitmap.PixelBuffer))
            stream.Write(pixels.Bgra);
        bitmap.Invalidate();
        Icon = bitmap;
    }
}

/// <summary>
/// Brings an observable collection in line with a target list using single inserts, removes and moves, never a
/// reset: the list control keeps its realized cards, scroll position and focus, and only the changed cards move.
/// </summary>
internal static class ListSync
{
    /// <summary>Returns the number of changes made.</summary>
    public static int SyncTo<T>(this ObservableCollection<T> items, IReadOnlyList<T> target)
        where T : class
    {
        var changes = 0;
        var wanted = new HashSet<T>(target, ReferenceEqualityComparer.Instance);
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(items[i]))
            {
                items.RemoveAt(i);
                changes++;
            }
        }

        for (var i = 0; i < target.Count; i++)
        {
            if (i < items.Count && ReferenceEquals(items[i], target[i]))
                continue;
            var at = IndexOf(items, target[i], i + 1);
            if (at >= 0)
                items.Move(at, i);
            else
                items.Insert(i, target[i]);
            changes++;
        }

        return changes;
    }

    private static int IndexOf<T>(ObservableCollection<T> items, T item, int start)
        where T : class
    {
        for (var i = start; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], item))
                return i;
        }

        return -1;
    }
}
