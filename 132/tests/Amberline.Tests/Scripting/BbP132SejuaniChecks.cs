using static Amberline.Tests.BbM3;
using static Amberline.Tests.BbP132Pins;

namespace Amberline.Tests;

/// <summary>
/// Batch 840 (17 November 2026): the 1.0.0.132 Sejuani rows (132.12). The numbers are the shipped scripts' (read by the engine's loader).
/// </summary>
public static class BbP132SejuaniChecks
{
    // 132.12: Sejuani's basic attacks apply Frost: movement speed -10% for three seconds.
    [Check("scripting.p132-sejuani-frost-slows-ten-percent-for-three-seconds")]
    public static void FrostSlowsTenPercentForThreeSeconds()
    {
        var apply = Script("SejuaniFrostApply");
        TestAssert.True(Sets(apply, "MovementSpeedMod").Any(block => Is(Number(block, "SrcValue"), -0.1)),
            "the Frost slow is -10% movement speed (shipped SejuaniFrostApply)");
        TestAssert.True(Sets(apply, "FrostDuration").Any(block => Is(Number(block, "SrcValue"), 3)),
            "Frost lasts three seconds (shipped SejuaniFrostApply)");
    }

    // 132.12: Northern Winds deals 1.5 times its damage to a Frost-slowed unit (the FrostBonus of the script).
    [Check("scripting.p132-sejuani-northern-winds-deals-one-and-a-half-times-to-frost-targets")]
    public static void NorthernWindsBonusAgainstFrost()
    {
        var winds = Script("SejuaniNorthernWinds");
        TestAssert.True(Sets(winds, "FrostBonus").Any(block => Is(Number(block, "SrcValue"), 1.5)),
            "the Frost bonus is 1.5 (shipped SejuaniNorthernWinds)");
        TestAssert.True(Calls(winds, "BBMath").Any(block => Named(block, "DestVar") == "DamagePerTickFrost" && Named(block, "Src1Var") == "FrostBonus"),
            "the Frost tick is the normal tick times the bonus (shipped SejuaniNorthernWinds)");
    }

    // 132.12: Glacial Prison stuns for two seconds and deals 150/250/350 magic damage.
    [Check("scripting.p132-sejuani-glacial-prison-stuns-two-seconds-and-deals-150-to-350")]
    public static void GlacialPrisonStunsAndDamages()
    {
        var prison = Script("SejuaniGlacialPrison");
        TestAssert.True(Sets(prison, "StunDuration").Any(block => Is(Number(block, "SrcValue"), 2)),
            "the stun lasts two seconds (shipped SejuaniGlacialPrison)");
        TestAssert.True(Sets(prison, "PrisonDamage").Any(block => LevelsAre(block, "SrcValueByLevel", 150, 250, 350)),
            "the prison deals 150/250/350 (shipped SejuaniGlacialPrison)");
    }
}
