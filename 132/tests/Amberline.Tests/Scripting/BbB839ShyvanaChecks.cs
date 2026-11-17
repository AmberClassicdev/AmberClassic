using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 839 (17 November 2026): 1.0.0.132 Shyvana rules of patch 1.0.0.128 (change list 128.04: Fury, forms, double strike, bonus vs debuffed,
/// dragon armour and MR doubled, flight knock-back). This file pins the two rules whose shipped numbers and observable outcome are settled:
/// the Reinforced Scales doubling in dragon form and Twin Bite's double strike. The other Shyvana rules are not pinned here (see the record).
/// </summary>
public static class BbB839ShyvanaChecks
{
    /// <summary>The <c>DefenseToAdd</c> tables of Spells/ShyvanaDragonScales.luaobj OnBuffActivate, in script order: dragon form first, human second.</summary>
    private static double[][] ScalesTables()
    {
        var tables = Blocks(Script("ShyvanaDragonScales"), "OnBuffActivate")
            .Where(item => item.Function == "BBSetVarInTable" && item.Text("DestVar") == "DefenseToAdd")
            .Select(item => Numbers(item.Param("SrcValueByLevel")!)).ToArray();
        TestAssert.Equal(2, tables.Length, "two DefenseToAdd tables: dragon form and human form");
        return tables;
    }

    /// <summary>Reinforced Scales adds armour in each form: 15 at rank one as a human, doubled (30) in dragon form (change list 128.04).</summary>
    [Check("scripting.p132-shyvana-reinforced-scales-doubles-its-armour-in-dragon-form")]
    public static void ReinforcedScalesDoubleInDragonForm()
    {
        var tables = ScalesTables();
        var dragon = tables[0][0];
        var human = tables[1][0];
        TestAssert.Near(30.0, dragon, 0.0, "the shipped dragon-form armour at rank one is 30");
        TestAssert.Near(15.0, human, 0.0, "the shipped human-form armour at rank one is 15");

        // Human form: the scales buff alone adds the 15 of the rank-one human table.
        using (var human32 = new Match("Shyvana"))
        {
            human32.Learn(1);
            human32.Refill();
            var before = human32.Hero.Armor;
            ScriptBuff(human32.Runtime, human32.Hero, "ShyvanaDragonScales", 25000);
            human32.Runtime.HeroStats.Refresh(human32.Hero, push: false);
            TestAssert.Near(before + human, human32.Hero.Armor, 0.01, "as a human Reinforced Scales adds 15 armour");
        }

        // Dragon form: Shyvana's transform adds the scales itself (ShyvanaTransform OnBuffActivate, BUFF_REPLACE_EXISTING), and the
        // activation reads the transform's table (30 at rank one), so the transform alone gives the dragon-form armour.
        using var match = new Match("Shyvana");
        var hero = match.Hero;
        match.Learn(1);
        match.Refill();
        var beforeDragon = hero.Armor;
        ScriptBuff(match.Runtime, hero, "ShyvanaTransform", 25000);
        match.Runtime.HeroStats.Refresh(hero, push: false);
        TestAssert.True(Buff(hero, "ShyvanaDragonScales") is not null, "the transform carries the scales; buffs: " + Names(hero));
        TestAssert.Near(beforeDragon + dragon, hero.Armor, 0.01, "in dragon form the scales add 30 armour, twice the human amount");
        TestAssert.Equal("", string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)), "no script failure");
    }

    /// <summary>Twin Bite (Q, ShyvanaDoubleAttack) arms the next basic attack as a double strike: the buff is spent on that attack and the
    /// second strike (ShyvanaDoubleAttackHit) lands on the target.</summary>
    [Check("scripting.p132-shyvana-twin-bite-makes-the-next-basic-attack-a-double-strike")]
    public static void TwinBiteDoubleStrikesOnce()
    {
        using var match = new Match("Shyvana", foeChampion: "Orianna");
        var hero = match.Hero;
        var foe = match.Foe;
        match.Learn(1);
        match.Refill();
        // Shyvana's basic-attack range is 125: the foe stands 100 away so the attack can be queued.
        foe.Position = new Position2(7100, 7000);
        TestAssert.True(match.Cast(0, foe), "Twin Bite is cast");
        // The cast takes its lock before an attack is allowed again: wait out the cast.
        match.Advance(1.0);
        TestAssert.True(Buff(hero, "ShyvanaDoubleAttack") is not null, "Twin Bite arms the double strike; buffs: " + Names(hero));
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, foe), "the next basic attack is queued on the foe");
        match.Advance(1.0);
        TestAssert.True(Buff(hero, "ShyvanaDoubleAttack") is null, "the attack spends the double strike");
        TestAssert.True(Buff(foe, "ShyvanaDoubleAttackHit") is not null || HitsOn(match, "Foe").Count >= 2,
            "the second strike lands on the foe; buffs: " + Names(foe) + "; log: " + string.Join(" / ", match.Log.Hits));
        TestAssert.Equal("", string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)), "no script failure");
    }
}
