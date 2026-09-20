namespace YetAnotherLosslessCutter.Plugins;

/// <summary>
/// Notified when the main window loads a different source file, or releases the
/// one it had (null). Lets a plugin keep per-file state — e.g. a status badge that
/// describes the current file. Called on the UI thread; keep it cheap.
/// </summary>
public interface ICurrentFileObserver
{
    void OnCurrentFileChanged(string? sourceFile);
}
