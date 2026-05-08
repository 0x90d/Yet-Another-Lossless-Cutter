namespace YetAnotherLosslessCutter.Hotkeys;

/// <summary>
/// Catalog entry for one user-facing keyboard action. <see cref="Id"/> is the stable
/// key for settings persistence — never change it for an existing action without a
/// migration. <see cref="DefaultBinding"/> is what users get out of the box;
/// <see cref="Category"/> + <see cref="Description"/> drive both the F1 help dialog
/// and the Settings → Hotkeys editor.
/// </summary>
public sealed record HotkeyAction(
    string Id,
    string Category,
    string Description,
    KeyChord DefaultBinding);
