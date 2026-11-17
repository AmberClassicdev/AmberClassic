namespace Amberline.Scripting.BuildingBlocks;

/// <summary>
/// The 1.0.0.126 line's script-value rows: none. This is the per-line half of <see cref="BbScriptOverrides"/>, whose shared rows stay in host/.
/// The 1.0.0.132 line keeps its own copy at this path, with the owner's Phosphorus Bomb row (17 November 2026).
/// </summary>
internal static class BbScriptOverridesLine
{
    /// <summary>The rows this line adds: none.</summary>
    public static readonly BbScriptOverrides.Row[] Rows = [];
}
