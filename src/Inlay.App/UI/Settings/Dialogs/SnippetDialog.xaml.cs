using System.Globalization;
using Inlay.Core.Settings;
using Inlay.Core.Snippets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Inlay.App.UI.Settings.Dialogs;

/// <summary>Add/Edit a snippet: inline validation under each field, placeholder help and a live preview.</summary>
public sealed partial class SnippetDialog : ContentDialog
{
    /// <summary>Shown in the accent color where <c>{cursor}</c> will leave the caret.</summary>
    private const string CaretMarker = "▏";

    private readonly IReadOnlyList<string> _otherNames;
    private readonly Func<string, bool> _isEmojiShortcode;
    private readonly string _trigger;
    private readonly IReadOnlyList<string> _imageNames;
    private readonly PreviewEnvironment _environment = new();

    /// <param name="newName">A name to start a new snippet with (from Quick Insert); the text box gets the focus.</param>
    internal SnippetDialog(SnippetDefinition? editing, IReadOnlyList<string> otherNames, Func<string, bool> isEmojiShortcode, string trigger, IReadOnlyList<string> imageNames, string? newName = null)
    {
        _otherNames = otherNames;
        _isEmojiShortcode = isEmojiShortcode;
        _trigger = trigger;
        _imageNames = imageNames;
        InitializeComponent();
        Title = editing is null ? "Add snippet" : "Edit snippet";
        foreach (var (placeholder, description) in SnippetTemplate.Catalog)
        {
            var item = new MenuFlyoutItem { Text = placeholder, KeyboardAcceleratorTextOverride = description };
            item.Click += (_, _) => InsertPlaceholder(placeholder);
            PlaceholderMenu.Items.Add(item);
        }

        // A multi-line text box handles Enter itself; see it anyway, for Ctrl+Enter.
        TextBox.AddHandler(KeyDownEvent, new KeyEventHandler(OnTextKeyDown), handledEventsToo: true);
        NameBox.Text = editing?.Name ?? newName ?? "";
        if (editing is null && newName is not null)
            Opened += (_, _) => TextBox.Focus(FocusState.Programmatic);
        TextBox.Text = editing?.Text ?? "";
        Validate();
    }

    public string SnippetName => NameBox.Text.Trim();

    /// <summary>The text with Windows line endings normalized to \n.</summary>
    public string SnippetText => TextBox.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>True when the dialog was closed with Ctrl+Enter from the text box (Enter there types a new line).</summary>
    public bool SavedWithShortcut { get; private set; }

    private void OnChanged(object sender, TextChangedEventArgs e) => Validate();

    private void OnTextKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || !IsPrimaryButtonEnabled)
            return;
        if (!Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            return;
        e.Handled = true;
        SavedWithShortcut = true;
        Hide();
    }

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
        var messages = EntryValidation.ValidateSnippet(NameBox.Text, SnippetText, _otherNames, _imageNames, _isEmojiShortcode, _trigger);
        // Don't scold an empty, untouched dialog: only show problems once something was typed.
        var pristine = NameBox.Text.Length == 0 && TextBox.Text.Length == 0;
        FieldMessage.Show(NameError, NameWarning, messages, ValidationField.Name, pristine);
        FieldMessage.Show(TextError, TextWarning, messages, ValidationField.Text, pristine);
        IsPrimaryButtonEnabled = !EntryValidation.HasErrors(messages);
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        var text = SnippetText;
        PreviewHint.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var expansion = SnippetTemplate.Expand(text, _environment);
        if (expansion.CursorIndex < 0)
        {
            BeforeCaret.Text = expansion.Text;
            Caret.Text = "";
            AfterCaret.Text = "";
        }
        else
        {
            BeforeCaret.Text = expansion.Text[..expansion.CursorIndex];
            Caret.Text = CaretMarker;
            AfterCaret.Text = expansion.Text[expansion.CursorIndex..];
        }
    }

    /// <summary>
    /// Real data for the preview. The clipboard is read once per dialog, the first time a <c>{clipboard}</c>
    /// placeholder needs it (reading it can wait on another app), and shortened so the preview stays readable.
    /// </summary>
    private sealed class PreviewEnvironment : ISnippetEnvironment
    {
        private const int ClipboardPreviewLength = 40;
        private string? _clipboard;

        public DateTime Now => DateTime.Now;

        public CultureInfo Culture => CultureInfo.CurrentCulture;

        public string ClipboardText()
        {
            if (_clipboard is null)
            {
                var text = ClipboardReader.Text();
                _clipboard = text.Length <= ClipboardPreviewLength ? text : string.Concat(text.AsSpan(0, ClipboardPreviewLength), "…");
            }

            return _clipboard;
        }
    }
}
