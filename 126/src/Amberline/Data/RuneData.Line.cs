namespace Amberline.Data;

/// <summary>The release line's part of <see cref="RuneCatalog"/> (batch 861; the 1.0.0.132 line keeps its own copy of this file).</summary>
public sealed partial class RuneCatalog
{
    /// <summary>
    /// The rune stats this line no longer sells: none. Patch 1.0.0.126 (October 2011) still sold the dodge runes; 1.0.0.132 removed them
    /// (that line's copy of this file).
    /// </summary>
    private static readonly HashSet<string> RetiredStats = new(StringComparer.Ordinal);
}
