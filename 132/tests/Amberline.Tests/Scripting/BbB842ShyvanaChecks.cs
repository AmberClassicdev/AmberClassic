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

    /// <summary>
    /// Batch 877 (batch 873's client log: "character shyvanadragon does not have animation 'Attack3' (spell 'ShyvanaBasicAttack3')" on dragon-form
    /// attacks): the server's attack roll reads the odds of the record the unit shows. <c>ShyvanaTransform</c> pushes <c>ShyvanaDragon</c> without
    /// its spells; the client's character-data setter keeps the basic attack names of the spells record and ends on the pushed record, which
    /// holds the odds (<c>mAttackProbability</c>): ShyvanaDragon's .6/.4 over two attacks (its model has Attack1 and Attack2 only), not
    /// Shyvana's .4/.3/.3 over three. The pop on the form's end restores her own odds.
    /// </summary>
    [Check("scripting.p132-shyvana-dragon-form-rolls-the-dragon-record-attack-odds")]
    public static void DragonFormRollsTheDragonOdds()
    {
        using var match = new Match("Shyvana");
        var hero = match.Hero;
        match.Learn(1);
        match.Refill();
        static string Odds(Amberline.Core.Entity unit) => string.Join("/", Amberline.Simulation.Combat.CombatService.AttackOdds(unit, Amberline.Data.GameData.Default).Select(odd => odd.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)));
        TestAssert.Equal("0.4/0.3/0.3", Odds(hero), "human form: Shyvana's three attacks");
        ScriptBuff(match.Runtime, hero, "ShyvanaTransform", 1.0);
        TestAssert.True(hero.ScriptCharacterData is { SkinName: "ShyvanaDragon", OverrideSpells: false }, "the form pushes ShyvanaDragon without its spells");
        TestAssert.Equal("0.6/0.4", Odds(hero), "dragon form: the dragon record's two attacks (no slot 0x42, Attack3)");
        match.Advance(1.5);
        TestAssert.True(hero.ScriptCharacterData is null, "the form's end pops the dragon layer");
        TestAssert.Equal("0.4/0.3/0.3", Odds(hero), "human form again: her own odds");

        // Every record a shipped script pushes on a hero reads (no throw, no empty roll): the rule reaches them all.
        var pushed = new[] { "ShyvanaDragon", "RammusDBC", "RammusPB", "SwainRaven", "SwainNoBird", "AniviaEgg", "UdyrTiger", "UdyrTurtle",
            "UdyrPhoenix", "OriannaNoBall", "Cassiopeia_Death", "KogMawDead", "Nidalee_Cougar" };
        var seen = string.Join(" ", pushed.Select(skin =>
        {
            hero.ScriptCharacterData = new Amberline.Core.ScriptCharacterLayer(99u, skin, OverrideSpells: false);
            return $"{skin}={Odds(hero)}";
        }));
        hero.ScriptCharacterData = null;
        TestAssert.Equal("ShyvanaDragon=0.6/0.4 RammusDBC=1 RammusPB=1 SwainRaven=1/0 SwainNoBird=0.75/0.25 AniviaEgg=1 UdyrTiger=1 UdyrTurtle=1 " +
                         "UdyrPhoenix=1 OriannaNoBall=0.3/0.4/0.3 Cassiopeia_Death=0.6/0.4 KogMawDead=0.5/0.5 Nidalee_Cougar=0.5/0.5", seen,
            "the pushed records' odds (shipped inibins)");
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
