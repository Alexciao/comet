using System.Collections.ObjectModel;
using Inlay.Core.Input;
using Inlay.Platform.Input;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace Inlay.App.UI.Settings.Dialogs;

/// <summary>
/// Records a keyboard shortcut, in the style of PowerToys: the keys show as caps while they are held, the last full
/// combination stays, and why it can't be used appears under it (the parser's rules, or another app holding it).
/// <para>
/// Keys are read from the dialog's own key events, with the modifiers from the thread's key state. Windows keeps
/// combinations that it or another app registered (Win+L, Win+Shift+S, a hotkey of another program): those never
/// reach the dialog, so they can't be recorded, which is fine because none of them could be registered either.
/// Releasing Win on its own still opens Start, as anywhere else.
/// </para>
/// Tab, Shift+Tab, Enter and Esc without Win, Ctrl or Alt keep their dialog meaning (move focus, Save, Cancel).
/// </summary>
public sealed partial class ShortcutDialog : ContentDialog
{
    private const string InUseMessage = "This shortcut is in use by another app. Choose another combination.";
    private const string OffMessage = "No shortcut. Press a combination of keys to set one.";

    private readonly ObservableCollection<string> _keys = [];
    private readonly Shortcut _default;
    private readonly Func<Shortcut, bool>? _canRegister;
    private string? _problem;

    /// <summary>Modifiers held before any other key of the current chord (shown as a partial combination).</summary>
    private ShortcutModifiers _held;

    /// <summary>A key other than a modifier was pressed since the modifiers went down.</summary>
    private bool _keyInChord;

    /// <param name="canRegister">Whether a combination is free to register (null: not checked).</param>
    internal ShortcutDialog(string title, Shortcut current, Shortcut defaultShortcut, Func<Shortcut, bool>? canRegister)
    {
        _default = defaultShortcut;
        _canRegister = canRegister;
        InitializeComponent();
        Title = title;
        ResetButton.IsEnabled = !defaultShortcut.IsNone;
        ToolTipService.SetToolTip(ResetButton, defaultShortcut.IsNone ? null : $"Use {ShortcutLabels.Text(defaultShortcut)}");
        Choose(current);
    }

    public ObservableCollection<string> Keys => _keys;

    /// <summary>The combination on screen; what Save keeps.</summary>
    public Shortcut Chosen { get; private set; }

    protected override void OnPreviewKeyDown(KeyRoutedEventArgs e)
    {
        var vk = (int)e.Key;
        var modifier = ModifierOf(vk);
        if (modifier != ShortcutModifiers.None)
        {
            if (!_keyInChord)
                _held = HeldModifiers() | modifier;
            Show();
            e.Handled = true;
            return;
        }

        var modifiers = HeldModifiers();
        if ((e.Key is VirtualKey.Tab or VirtualKey.Enter or VirtualKey.Escape) && (modifiers & ~ShortcutModifiers.Shift) == ShortcutModifiers.None)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;
        if (!Shortcut.IsSupportedKey(vk))
            return; // Caps Lock, media keys, Print Screen…: not shortcut keys
        _held = ShortcutModifiers.None;
        _keyInChord = modifiers != ShortcutModifiers.None;
        Choose(new Shortcut(modifiers, vk));
    }

    protected override void OnPreviewKeyUp(KeyRoutedEventArgs e)
    {
        var released = ModifierOf((int)e.Key);
        if (released == ShortcutModifiers.None)
        {
            base.OnPreviewKeyUp(e);
            return;
        }

        // The key state may still count the key being released as down while its key-up is handled.
        var held = HeldModifiers() & ~released;
        if (held == ShortcutModifiers.None)
            _keyInChord = false;
        _held = _keyInChord ? ShortcutModifiers.None : held;
        Show();
        e.Handled = true;
    }

    private void OnReset(object sender, RoutedEventArgs e) => Choose(_default);

    private void OnClear(object sender, RoutedEventArgs e) => Choose(Shortcut.None);

    private void Choose(Shortcut shortcut)
    {
        Chosen = shortcut;
        _problem = shortcut.Problem;
        if (_problem is null && !shortcut.IsNone && _canRegister is not null && !_canRegister(shortcut))
            _problem = InUseMessage;
        IsPrimaryButtonEnabled = _problem is null;
        Show();
    }

    private void Show()
    {
        var partial = _held != ShortcutModifiers.None;
        var shown = partial ? new Shortcut(_held, 0) : Chosen;
        var parts = ShortcutLabels.Parts(shown);
        _keys.Clear();
        foreach (var part in parts)
            _keys.Add(part);

        var off = !partial && Chosen.IsNone;
        var ready = !partial && !off && _problem is null;
        AccentKeys.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        PlainKeys.Visibility = !ready && !off ? Visibility.Visible : Visibility.Collapsed;
        OffText.Visibility = off ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(KeysArea, off ? "No shortcut" : string.Join(" + ", parts));

        Set(ErrorText, partial ? null : _problem);
        Set(HintText, partial ? shown.Problem : off ? OffMessage : null);
    }

    private static void Set(TextBlock block, string? text)
    {
        block.Text = text ?? "";
        block.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static ShortcutModifiers ModifierOf(int vk) => vk switch
    {
        (int)VirtualKey.LeftWindows or (int)VirtualKey.RightWindows => ShortcutModifiers.Win,
        (int)VirtualKey.Control or (int)VirtualKey.LeftControl or (int)VirtualKey.RightControl => ShortcutModifiers.Ctrl,
        (int)VirtualKey.Menu or (int)VirtualKey.LeftMenu or (int)VirtualKey.RightMenu => ShortcutModifiers.Alt,
        (int)VirtualKey.Shift or (int)VirtualKey.LeftShift or (int)VirtualKey.RightShift => ShortcutModifiers.Shift,
        _ => ShortcutModifiers.None,
    };

    private static ShortcutModifiers HeldModifiers()
    {
        var m = ShortcutModifiers.None;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
            m |= ShortcutModifiers.Win;
        if (IsDown(VirtualKey.Control))
            m |= ShortcutModifiers.Ctrl;
        if (IsDown(VirtualKey.Menu))
            m |= ShortcutModifiers.Alt;
        if (IsDown(VirtualKey.Shift))
            m |= ShortcutModifiers.Shift;
        return m;
    }

    private static bool IsDown(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
