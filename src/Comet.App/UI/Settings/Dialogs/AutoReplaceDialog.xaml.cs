using Comet.Core.Settings;
using Microsoft.UI.Xaml.Controls;

namespace Comet.App.UI.Settings.Dialogs;

/// <summary>Add/Edit an auto-replace entry, with validation inline under each field.</summary>
public sealed partial class AutoReplaceDialog : ContentDialog
{
    private readonly IReadOnlyList<string> _otherAbbreviations;

    internal AutoReplaceDialog(AutoReplaceDefinition? editing, IReadOnlyList<string> otherAbbreviations)
    {
        _otherAbbreviations = otherAbbreviations;
        InitializeComponent();
        Title = editing is null ? "Add replacement" : "Edit replacement";
        AbbreviationBox.Text = editing?.Abbreviation ?? "";
        ReplacementBox.Text = editing?.Replacement ?? "";
        MatchCaseBox.IsChecked = editing?.MatchCase ?? false;
        Validate();
    }

    public string Abbreviation => AbbreviationBox.Text.Trim();

    public string Replacement => ReplacementBox.Text;

    public bool MatchCase => MatchCaseBox.IsChecked == true;

    private void OnChanged(object sender, TextChangedEventArgs e) => Validate();

    private void Validate()
    {
        var messages = EntryValidation.ValidateAutoReplace(AbbreviationBox.Text, ReplacementBox.Text, _otherAbbreviations);
        var pristine = AbbreviationBox.Text.Length == 0 && ReplacementBox.Text.Length == 0;
        FieldMessage.Show(AbbreviationError, AbbreviationWarning, messages, ValidationField.Abbreviation, pristine);
        FieldMessage.Show(ReplacementError, ReplacementWarning, messages, ValidationField.Replacement, pristine);
        IsPrimaryButtonEnabled = !EntryValidation.HasErrors(messages);
    }
}
