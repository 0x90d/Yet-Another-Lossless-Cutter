using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace YetAnotherLosslessCutter.Hotkeys;

public partial class KeyCaptureDialog : Window
{
    private KeyChord? _captured;

    /// <summary>
    /// What the user picked, or null on cancel. <see cref="KeyChord.Unbound"/>
    /// means the user clicked "Unbind" — the action should have no key binding.
    /// </summary>
    public KeyChord? Result { get; private set; }

    public KeyCaptureDialog() : this(string.Empty, KeyChord.Unbound) { }

    public KeyCaptureDialog(string actionDescription, KeyChord currentBinding)
    {
        InitializeComponent();
        ActionLabel.Text = actionDescription;
        ChordLabel.Text = currentBinding.IsEmpty ? "(press a key)" : currentBinding.ToString();
        // Pre-select the existing chord so a user who opens the dialog and
        // immediately clicks Save preserves the current binding (no-op).
        if (!currentBinding.IsEmpty)
        {
            _captured = currentBinding;
            SaveButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Capture key combinations on KeyDown. Pure-modifier presses (Shift / Ctrl /
    /// Alt / Meta alone) are ignored — they're chord prefixes, not chords. Esc
    /// cancels the dialog (matches the "Esc cancels" hint).
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            Cancel_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }

        // Skip presses that are JUST a modifier — wait for the user to add a real
        // key (otherwise every Shift press would be captured as the chord).
        if (IsModifierKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        _captured = new KeyChord(e.Key, e.KeyModifiers);
        ChordLabel.Text = _captured.Value.ToString();
        SaveButton.IsEnabled = true;
        e.Handled = true;
    }

    private static bool IsModifierKey(Key k) => k is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or
        Key.LWin or Key.RWin;

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        Result = _captured;
        Close();
    }

    private void Unbind_Click(object? sender, RoutedEventArgs e)
    {
        Result = KeyChord.Unbound;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }
}
