using System.Globalization;
using Comet.Core.Settings;
using Comet.Core.Snippets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Comet.App.UI.Dialogs;

/// <summary>Add/Edit a snippet with live validation, placeholder help and a live preview of what it inserts.</summary>
public sealed partial class SnippetDialog : ContentDialog
{
    /// <summary>Shown where <c>{cursor}</c> will leave the caret.</summary>
    private const string CaretMarker = "▏";
    private const int ClipboardPreviewLength = 40;

    private readonly IReadOnlyList<string> _otherNames;
    private readonly Func<string, bool> _isEmojiShortcode;

    internal SnippetDialog(SnippetDefinition? editing, IReadOnlyList<string> otherNames, Func<string, bool> isEmojiShortcode)
    {
        _otherNames = otherNames;
        _isEmojiShortcode = isEmojiShortcode;
        InitializeComponent();
        Title = editing is null ? "Add snippet" : "Edit snippet";
        foreach (var (placeholder, description) in SnippetTemplate.Catalog)
        {
            var item = new MenuFlyoutItem { Text = placeholder, KeyboardAcceleratorTextOverride = description };
            item.Click += (_, _) => InsertPlaceholder(placeholder);
            PlaceholderMenu.Items.Add(item);
        }

        NameBox.Text = editing?.Name ?? "";
        TextBox.Text = editing?.Text ?? "";
        Validate();
    }

    public string SnippetName => NameBox.Text.Trim();

    /// <summary>The text with Windows line endings normalized to \n.</summary>
    public string SnippetText => TextBox.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private void OnChanged(object sender, TextChangedEventArgs e) => Validate();

    private void InsertPlaceholder(string placeholder)
    {
        var start = TextBox.SelectionStart;
        var text = TextBox.Text;
        TextBox.Text = string.Concat(text.AsSpan(0, start), placeholder, text.AsSpan(start + TextBox.SelectionLength));
        TextBox.SelectionStart = start + placeholder.Length;
        TextBox.Focus(FocusState.Programmatic);
    }

    private void Validate()
    {
        var messages = EntryValidation.ValidateSnippet(NameBox.Text, SnippetText, _otherNames, _isEmojiShortcode);
        // Don't scold an empty, untouched dialog: only show problems once something was typed.
        var pristine = NameBox.Text.Length == 0 && TextBox.Text.Length == 0;
        ValidationView.Show(Messages, pristine ? [] : messages);
        IsPrimaryButtonEnabled = !EntryValidation.HasErrors(messages);
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var expansion = SnippetTemplate.Expand(SnippetText, new PreviewEnvironment());
        PreviewText.Text = expansion.CursorIndex < 0
            ? expansion.Text
            : expansion.Text.Insert(expansion.CursorIndex, CaretMarker);
    }

    /// <summary>Real data for the preview; the clipboard is shortened so the preview stays readable.</summary>
    private sealed class PreviewEnvironment : ISnippetEnvironment
    {
        public DateTime Now => DateTime.Now;

        public CultureInfo Culture => CultureInfo.CurrentCulture;

        public string ClipboardText()
        {
            var text = ClipboardReader.Text();
            return text.Length <= ClipboardPreviewLength ? text : string.Concat(text.AsSpan(0, ClipboardPreviewLength), "…");
        }
    }
}
