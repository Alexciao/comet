namespace Inlay.Core.Text;

/// <summary>
/// The one place a code is written out with the trigger character. Everything that shows a code (Quick Insert
/// rows, Settings cards, tiles, dialogs, validation messages, descriptions) goes through here, so a custom
/// trigger such as <c>;</c> shows up everywhere.
/// </summary>
public static class TriggerFormat
{
    /// <summary>Placeholder for the trigger character in UI copy ("Type {trigger} and a few letters…").</summary>
    public const string Placeholder = "{trigger}";

    /// <summary><c>:fire</c> for the trigger <c>:</c> and the name <c>fire</c>.</summary>
    public static string Code(string trigger, string name) => string.Concat(trigger, name);

    /// <summary>The closing form that inserts directly: <c>:fire:</c>.</summary>
    public static string Closed(string trigger, string name) => string.Concat(trigger, name, trigger);

    /// <summary>Replaces <see cref="Placeholder"/> in <paramref name="text"/> with the trigger character.</summary>
    public static string Fill(string text, string trigger) => text.Replace(Placeholder, trigger, StringComparison.Ordinal);
}
