namespace Amberline.Scripting.BuildingBlocks;

/// <summary>
/// The 1.0.0.132 line's script-value rows: the per-line half of <see cref="BbScriptOverrides"/> (the shared rows stay in host/). The 1.0.0.126
/// line keeps its own copy at this path, with no rows, so its scripts load as shipped.
/// </summary>
internal static class BbScriptOverridesLine
{
    /// <summary>
    /// NOT RETAIL: owner decision 17 November 2026: the wiki's 6 s over the shipped script's 4 s. Phosphorus Bomb's reveal is the caster's self buff
    /// (SelfExecute, BBSpellBuffAdd Duration 4); its OnBuffDeactivate removes the perception bubble, which the script adds for 6 s, so the shipped
    /// reveal ends at 4 s. The row loads 6 into that literal (the bubble stays 6). The wiki's patch notes for 1.0.0.129 (change list 129.09) say the reveal
    /// lasts 6 s.
    /// </summary>
    public static readonly BbScriptOverrides.Row[] Rows =
    [
        new("PhosphorusBomb", "Duration", null, [4.0], [6.0],
            "NOT RETAIL: owner decision 17 November 2026: the wiki's 6 s over the shipped script's 4 s (V1.0.0.129 patch notes, change list 129.09)."),
    ];
}
