namespace YetAnotherLosslessCutter.Plugins;

/// <summary>
/// Transforms the output directory for a single cut. Called from the cut-output-path
/// computation per segment. Plugins may inspect the source path, apply their own logic,
/// and return a transformed directory — or return the input unchanged if not applicable.
/// </summary>
public interface IOutputPathPlugin
{
    /// <summary>
    /// Return the directory the cut should be written to. <paramref name="baseOutputDir"/>
    /// is what the core resolved (either the source folder or the user's configured output
    /// directory). Implementations should be pure — no side effects, no directory creation.
    /// </summary>
    string TransformOutputDirectory(string sourceFile, string baseOutputDir, OutputPathContext context);
}

/// <summary>
/// Read-only context surfaced to <see cref="IOutputPathPlugin"/>. Lets plugins react to
/// global flags without depending on the full Settings type.
/// </summary>
public sealed class OutputPathContext
{
    /// <summary>True when the user configured "save next to source"; plugins typically
    /// short-circuit (return the input) in this case so the cut stays adjacent.</summary>
    public bool SaveToSourceFolder { get; init; }
}
