using static Amberline.Tests.BbM3;
using static Amberline.Tests.BbP132Pins;

namespace Amberline.Tests;

/// <summary>
/// Batch 842 (17 November 2026): the rest of the 1.0.0.128 Shyvana row (change list 128.04): forms (driven), Fury and flight knock-back
/// (pins of the shipped values). Bonus against debuffed targets is not pinned here: the rule is not located in the shipped scripts.
/// </summary>
public static class BbB842ShyvanaChecks
{
    // 128.04: forms. Transform swaps the three basic slots: the dragon spells in, the human spells back on the way out.
    [Check("scripting.p132-shyvana-transform-swaps-her-first-three-spells-into-dragon-form")]
    public static void TransformSwapsSpells()
    {
        using var match = new Match("Shyvana");
        var hero = match.Hero;
        match.Learn(1);
        match.Refill();
        TestAssert.Equal("ShyvanaDoubleAttack", match.Module.SpellName(hero, 0), "human form: slot 0 is Twin Bite");
        ScriptBuff(match.Runtime, hero, "ShyvanaTransform", 25000);
        TestAssert.Equal("ShyvanaDoubleAttackDragon", match.Module.SpellName(hero, 0), "dragon form: slot 0 is the dragon double strike");
        TestAssert.Equal("ShyvanaImmolateDragon", match.Module.SpellName(hero, 1), "dragon form: slot 1 is the dragon immolate");
        TestAssert.Equal("ShyvanaFireballDragon2", match.Module.SpellName(hero, 2), "dragon form: slot 2 is the second dragon fireball");
        TestAssert.Equal("", string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)), "no script failure");
    }

    // 128.04: Fury (the PAR_OTHER resource): a hit adds 2 (CharOnHitUnit) and UpdateSelfBuffActions adds 1 (its period is not pinned).
    [Check("scripting.p132-shyvana-fury-gains-two-per-hit-and-one-per-update")]
    public static void FuryGains()
    {
        var character = CharScript("Shyvana");
        TestAssert.True(Blocks(character, "CharOnHitUnit").Any(block => block.Function == "BBIncPAR" && Named(block, "PARType") == "PAR_OTHER"
            && Is(Number(block, "Delta"), 2)), "a hit adds 2 Fury (CharOnHitUnit)");
        TestAssert.True(Blocks(character, "UpdateSelfBuffActions").Any(block => block.Function == "BBIncPAR" && Named(block, "PARType") == "PAR_OTHER"
            && Is(Number(block, "Delta"), 1)), "UpdateSelfBuffActions adds 1 Fury (its period is not pinned)");
    }

    // 128.04: flight knock-back. The dragon's leap moves at 1100 and stuns the unit it lands on (ShyvanaTransformDamage).
    [Check("scripting.p132-shyvana-dragon-leap-moves-at-eleven-hundred-and-stuns-on-landing")]
    public static void LeapKnocksBack()
    {
        var leap = Script("ShyvanaTransformLeap");
        TestAssert.True(Calls(leap, "BBMove").Any(block => Is(Number(block, "Speed"), 1100)), "the leap moves at 1100 (shipped ShyvanaTransformLeap)");
        TestAssert.True(Calls(leap, "BBSpellBuffAdd").Any(block => Named(block, "BuffName") == "ShyvanaTransformDamage" && Named(block, "BuffType") == "BUFF_Stun"),
            "the landing stuns the struck unit (ShyvanaTransformDamage)");
    }
}
