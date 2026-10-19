using Inlay.Core.Settings;
using Microsoft.UI.Xaml.Controls;

namespace Inlay.App.UI.Settings.Dialogs;

/// <summary>Add/Edit an shorthands entry, with validation inline under each field.</summary>
public sealed partial class ShorthandDialog : ContentDialog
{
    private readonly IReadOnlyList<string> _otherAbbreviations;

    internal ShorthandDialog(ShorthandDefinition? editing, IReadOnlyList<string> otherAbbreviations)
    {
        _otherAbbreviations = otherAbbreviations;
        InitializeComponent();
        Title = editing is null ? "Add shorthand" : "Edit shorthand";
        AbbreviationBox.Text = editing?.Shorthand ?? "";
        ReplacementBox.Text = editing?.Expansion ?? "";
        MatchCaseBox.IsChecked = editing?.MatchCase ?? false;
        Validate();
    }

    public string Shorthand => AbbreviationBox.Text.Trim();

    public string Expansion => ReplacementBox.Text;

    public bool MatchCase => MatchCaseBox.IsChecked == true;

    private void OnChanged(object sender, TextChangedEventArgs e) => Validate();

    private void Validate()
    {
        var messages = EntryValidation.ValidateShorthand(AbbreviationBox.Text, ReplacementBox.Text, _otherAbbreviations);
        var pristine = AbbreviationBox.Text.Length == 0 && ReplacementBox.Text.Length == 0;
        FieldMessage.Show(AbbreviationError, AbbreviationWarning, messages, ValidationField.Shorthand, pristine);
        FieldMessage.Show(ReplacementError, ReplacementWarning, messages, ValidationField.Expansion, pristine);
        IsPrimaryButtonEnabled = !EntryValidation.HasErrors(messages);
    }
}
