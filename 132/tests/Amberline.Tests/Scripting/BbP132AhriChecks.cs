using Amberline.Core;
using Amberline.Simulation.Buffs;
using static Amberline.Tests.BbM3;
using static Amberline.Tests.BbP132Pins;

namespace Amberline.Tests;

/// <summary>
/// Batch 840 (17 November 2026): the 1.0.0.132 Ahri rows (131.03). The numbers are the shipped scripts' (the Ahri scripts, read by the
/// engine's loader); the Charm check also casts Charm (E) through the match.
/// </summary>
public static class BbP132AhriChecks
{
    // 131.03: Charm (E) stops the charmed foe from attacking, and the foe walks towards Ahri.
    [Check("scripting.p132-ahri-charm-stops-the-foe-attacking-and-walks-it-to-ahri")]
    public static void CharmStopsAttacksAndWalksTheFoe()
    {
        var seduce = Script("AhriSeduce");
        TestAssert.True(Calls(seduce, "BBSetStatus").Any(block => Flag(block, "SrcValue") == false && Named(block, "Status") == "SetCanAttack"),
            "the charm takes away attacking (shipped AhriSeduce OnBuffActivate)");
        TestAssert.True(Calls(seduce, "BBIssueOrder").Any(block => Named(block, "Order") == "AI_MOVETO"),
            "the charmed foe is ordered to walk to Ahri (shipped AhriSeduce)");
        using var match = new Match("Ahri", foeChampion: "Orianna");
        match.Hero.Facing = new Position2(1.0, 0.0);
        match.Learn(1);
        match.Refill();
        TestAssert.True(match.Cast(2, match.Foe), "E is cast at the foe, which is in front of Ahri (the charm needs it in front)");
        // The charm's length is the shipped TauntLength variable, so the first step with the buff is read, not a fixed time.
        double? on = null;
        for (var waited = 0.0; on is null && waited < 1.5; waited += Step)
        {
            match.Advance(Step);
            if (Buff(match.Foe, "AhriSeduce") is not null) on = match.Now;
        }
        TestAssert.True(on is not null, "the foe carries Charm within 1.5 seconds; buffs: " + Names(match.Foe));
        TestAssert.False(CharacterState.Of(match.Foe).CanAttack, "a charmed foe cannot attack");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // 131.03: Spirit Rush (R) has three charges: the first cast and two stacks (MaxStack 2); the cooldown after the last charge is 90/80/70.
    [Check("scripting.p132-ahri-spirit-rush-has-three-charges-and-cooldown-ninety-eighty-seventy")]
    public static void SpiritRushHasThreeCharges()
    {
        var tumble = Script("AhriTumble");
        TestAssert.True(Calls(tumble, "BBSpellBuffAdd").Any(block => Named(block, "BuffName") == "AhriTumble" && Is(Number(block, "MaxStack"), 2)),
            "the charges are the first cast and two stacks (shipped AhriTumble)");
        TestAssert.True(Sets(tumble, "NewCd").Any(block => LevelsAre(block, "SrcValueByLevel", 90, 80, 70)),
            "the cooldown after the last charge is 90/80/70 (shipped AhriTumble)");
    }

    // 131.03: the orb's return (AhriOrbDamageSilence) deals TRUE damage, 40/65/90/115/140 by rank (the outgoing orb hit is magic).
    [Check("scripting.p132-ahri-orb-return-deals-true-damage-by-rank")]
    public static void OrbReturnDealsTrueDamage()
    {
        var silence = Script("AhriOrbDamageSilence");
        TestAssert.True(Calls(silence, "BBApplyDamage").Any(block => Named(block, "DamageType") == "TRUE_DAMAGE" && LevelsAre(block, "DamageByLevel", 40, 65, 90, 115, 140)),
            "the return deals true damage 40/65/90/115/140 (shipped AhriOrbDamageSilence)");
    }

    // 131.03: Essence Theft gains a charge per spell hit; the ninth charge makes the next spell cast Essence Theft's buff (AhriSoulCrusher).
    [Check("scripting.p132-ahri-essence-theft-counts-to-nine-charges-then-the-next-spell-buff")]
    public static void EssenceTheftNineCharges()
    {
        var counter = Script("AhriSoulCrusherCounter");
        TestAssert.True(Calls(counter, "BBIf").Any(block => Named(block, "Src1Var") == "Count" && Is(Number(block, "Value2"), 9)),
            "the counter fires at nine charges (shipped AhriSoulCrusherCounter)");
        TestAssert.True(Calls(counter, "BBSpellBuffAdd").Any(block => Named(block, "BuffName") == "AhriSoulCrusher"),
            "the ninth charge adds Essence Theft's next-spell buff (shipped AhriSoulCrusherCounter)");
    }
}
