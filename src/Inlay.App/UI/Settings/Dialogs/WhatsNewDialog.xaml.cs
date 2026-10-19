using Inlay.Core.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Inlay.App.UI.Settings.Dialogs;

/// <summary>
/// "See what's new": the newer release's version, date and full notes, with Download (opens the release page in
/// the browser) and Not now. Show it with <c>await new WhatsNewDialog(release, XamlRoot).ShowAsync()</c>.
/// </summary>
public sealed partial class WhatsNewDialog : ContentDialog
{
    /// <summary>The notes' share of the window's height, so the dialog never fills it.</summary>
    private const double NotesHeightShare = 0.6;

    internal WhatsNewDialog(AppRelease release, XamlRoot root)
    {
        InitializeComponent();
        XamlRoot = root;
        VersionText.Text = release.Published is { } published
            ? $"Inlay {release.DisplayVersion} · Released {AppHost.FormatDate(published)}"
            : $"Inlay {release.DisplayVersion}";
        NotesView.Notes = release.Notes ?? "This release has no notes. The release page may have more.";
        NotesScroller.MaxHeight = root.Size.Height * NotesHeightShare;
        PrimaryButtonClick += (_, _) => AppHost.OpenInBrowser(release.Page);
    }
}
