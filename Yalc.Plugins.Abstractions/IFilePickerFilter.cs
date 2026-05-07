using System.Collections.Generic;

namespace YetAnotherLosslessCutter.Plugins;

/// <summary>
/// Post-processes the result of "Open from folder" file enumeration. Receives the
/// already-filtered, already-sorted list from core and may filter or reorder further.
/// Useful for workflows where only a recorder-specific subset is interesting.
/// </summary>
public interface IFilePickerFilter
{
    IReadOnlyList<string> Apply(IReadOnlyList<string> files, FilePickerContext context);
}

public sealed class FilePickerContext
{
    public string FolderPath { get; init; } = "";
    public bool IncludeSubFolders { get; init; }
}
