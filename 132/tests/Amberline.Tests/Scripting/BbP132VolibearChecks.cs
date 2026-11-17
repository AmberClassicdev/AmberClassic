using static Amberline.Tests.BbM3;
using static Amberline.Tests.BbP132Pins;

namespace Amberline.Tests;

/// <summary>
/// Batch 840 (17 November 2026): the 1.0.0.132 Volibear rows (130.03 and 132.43). The numbers are the shipped script's (the
/// Volibear scripts, read by the engine's loader); the speed check also casts Rolling Thunder through the match.
/// </summary>
public static class BbP132VolibearChecks
{
    // 130.03 and 132.43: Rolling Thunder (Q) adds 15% movement speed, and 45% while Volibear chases a visible champion in front of him.
    [Check("scripting.p132-volibear-rolling-thunder-speed-is-fifteen-percent-and-forty-five-toward-champions")]
    public static void RollingThunderSpeedIsFifteenAndFortyFivePercent()
    {
        var q = Script("VolibearQ");
        TestAssert.True(Sets(q, "SpeedMod").Any(block => LevelsAre(block, "SrcValueByLevel", 0.45, 0.45, 0.45, 0.45, 0.45)),
            "the pursuit speed is 45% at every rank (shipped VolibearQ)");
        var speed = Script("VolibearQSpeed");
        TestAssert.True(Calls(speed, "BBIncStat").Any(block => Named(block, "Stat") == "IncPercentMovementSpeedMod" && Is(Number(block, "Delta"), 0.15)),
            "without a champion in front, the speed buff adds 15% (shipped VolibearQSpeed)");
        TestAssert.True(Calls(speed, "BBIncStat").Any(block => Named(block, "Stat") == "IncPercentMovementSpeedMod" && Named(block, "DeltaVar") == "SpeedMod"),
            "toward a visible champion the speed buff adds SpeedMod (shipped VolibearQSpeed)");
        using var match = new Match("Volibear", foeChampion: "Orianna");
        match.Learn(1);
        match.Refill();
        TestAssert.True(match.Cast(0), "Q is cast");
        match.Advance(0.1);
        TestAssert.True(Buff(match.Hero, "VolibearQSpeed") is not null, "the cast gives Volibear the speed buff; buffs: " + Names(match.Hero));
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // 130.03: Frenzy (W) bites once Volibear has three stacks of attack speed (CanCast at three), for 80/125/170/215/260 plus 15% of his health pool.
    [Check("scripting.p132-volibear-frenzy-bites-for-80-to-260-at-three-stacks-plus-fifteen-percent-of-health")]
    public static void FrenzyBitesAtThreeStacks()
    {
        var w = Script("VolibearW");
        TestAssert.True(Sets(w, "Damage").Any(block => LevelsAre(block, "SrcValueByLevel", 80, 125, 170, 215, 260)),
            "the bite deals 80/125/170/215/260 (shipped VolibearW TargetExecute)");
        TestAssert.True(Calls(w, "BBMath").Any(block => Named(block, "Src1Var") == "HPPoolMod" && Is(Number(block, "Src2Value"), 0.15)),
            "the bite adds 15% of the health pool (shipped VolibearW)");
        TestAssert.True(Calls(w, "BBIf").Any(block => Named(block, "Src1Var") == "Count" && Is(Number(block, "Value2"), 3)),
            "the bite is castable at three stacks (shipped VolibearW CanCast)");
    }

    // 130.03: Chosen of the Storm heals when a hit leaves Volibear at 30% health or less: 30% of maximum health over six seconds (120 s cooldown).
    [Check("scripting.p132-volibear-chosen-of-the-storm-heals-thirty-percent-over-six-seconds-below-thirty-percent")]
    public static void ChosenOfTheStormHealsBelowThirtyPercent()
    {
        var check = Script("VolibearPassiveHealCheck");
        TestAssert.True(Calls(check, "BBIf").Any(block => Named(block, "Src1Var") == "percentHealthRemaining" && Is(Number(block, "Value2"), 0.3)),
            "the heal starts at 30% health or less (shipped VolibearPassiveHealCheck)");
        TestAssert.True(Sets(check, "Duration").Any(block => Is(Number(block, "SrcValue"), 6)),
            "the heal lasts six seconds (shipped VolibearPassiveHealCheck)");
        TestAssert.True(Sets(CharScript("Volibear"), "RegenPercent").Any(block => Is(Number(block, "SrcValue"), 0.3)),
            "the heal is 30% of maximum health (shipped CharScriptVolibear)");
    }

    // 130.03: Thunder Claws (R) chains lightning from the target to one visible foe at a time, within 500 of each bounce.
    [Check("scripting.p132-volibear-thunder-claws-chain-reaches-five-hundred-and-one-foe-a-bounce")]
    public static void ThunderClawsChainsWithinFiveHundred()
    {
        var chain = Script("VolibearRChain");
        TestAssert.True(Calls(chain, "BBForEachUnitInTargetArea").Any(block => Is(Number(block, "Range"), 500)),
            "the chain searches 500 around the bounce (shipped VolibearRChain)");
        TestAssert.True(Calls(chain, "BBForEachVisibleUnitInTargetAreaRandom")
                .Any(block => Is(Number(block, "Range"), 500) && Is(Number(block, "MaximumUnitsToPick"), 1)),
            "each bounce picks one visible foe within 500 (shipped VolibearRChain)");
    }
}
