using Amberline.Scripting.BuildingBlocks;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 840 (17 November 2026): readers of the shipped 1.0.0.132 champion scripts for the batch 840 pins. A check names the shipped
/// value it expects; these readers find the block by its function and its variable or stat, not by its position in the file.
/// </summary>
internal static class BbP132Pins
{
    /// <summary>Every block of every hook of a shipped script, nested blocks included.</summary>
    public static IEnumerable<BbBlock> All(BbScript script) => script.Events.SelectMany(hook => hook.Blocks.SelectMany(Flatten));

    /// <summary>The blocks of a shipped script that call <paramref name="function"/>.</summary>
    public static IEnumerable<BbBlock> Calls(BbScript script, string function) => All(script).Where(block => block.Function == function);

    /// <summary>The <c>BBSetVarInTable</c> blocks that set <paramref name="variable"/>.</summary>
    public static IEnumerable<BbBlock> Sets(BbScript script, string variable) =>
        Calls(script, "BBSetVarInTable").Where(block => Named(block, "DestVar") == variable);

    /// <summary>A reference or text parameter (a stat, a damage type, a variable name) by its key; empty when absent.</summary>
    public static string Named(BbBlock block, string key) => block.Param(key) switch
    {
        BbValue.Reference reference => reference.Name,
        BbValue.Text text => text.Value,
        _ => string.Empty,
    };

    /// <summary>A numeric parameter; NaN when absent.</summary>
    public static double Number(BbBlock block, string key) => block.Param(key) is BbValue.Number number ? number.Value : double.NaN;

    /// <summary>A boolean parameter; false when absent.</summary>
    public static bool Flag(BbBlock block, string key) => block.Param(key) is BbValue.Bool flag && flag.Value;

    /// <summary>Whether a block carries a per-level table under <paramref name="key"/>.</summary>
    public static bool HasLevels(BbBlock block, string key) => block.Param(key) is BbValue.Table;

    /// <summary>The per-level table under <paramref name="key"/>.</summary>
    public static double[] Levels(BbBlock block, string key) => Numbers(block.Param(key)!);

    /// <summary>Equal within float32 rounding (the script numbers are read as float32).</summary>
    public static bool Is(double value, double expected) => Math.Abs(value - expected) < 1e-6;

    /// <summary>Whether a block has a per-level table equal to <paramref name="expected"/>.</summary>
    public static bool LevelsAre(BbBlock block, string key, params double[] expected)
    {
        if (!HasLevels(block, key)) return false;
        var values = Levels(block, key);
        return values.Length >= expected.Length && expected.Select((value, index) => Is(values[index], value)).All(same => same);
    }
}
