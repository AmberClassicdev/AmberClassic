using Amberline.Core;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 839 (17 November 2026): the 1.0.0.132 Skarner row of patch 1.0.0.127 (change list 127.13: Fracture heals when its hit kills).
/// Shipped rule (Spells/SkarnerFractureMissile.luaobj TargetExecute): when the hit kills the target, Skarner heals 30 at rank one (plus 0.3
/// ability power) times the HealingMod of his stacks (1 at none). A hit that does not kill heals nothing.
/// Batch 841: the check was red because it set Skarner's health to 1000 after <c>Learn</c>, whose stat refresh had put his maximum back to
/// the real 536, and a heal of a unit at or above its maximum restores nothing (<c>Healing.Apply</c>). The kill branch did run (Skarner held
/// his SkarnerFracture stack and the foe no SkarnerFractureMissile mark). The hero now starts below his maximum, and a lane minion kill
/// (the case the change list is about: the dead target leaves inside the same TargetExecute) is pinned beside the champion kill.
/// </summary>
public static class BbB839SkarnerFractureChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    private static Match Skarner()
    {
        var match = new Match("Skarner", foeChampion: "Orianna");
        match.Learn(1);
        match.Refill();
        match.Hero.Health = match.Hero.MaxHealth - 200.0;
        return match;
    }

    [Check("scripting.p132-skarner-fracture-heals-when-its-hit-kills")]
    public static void FractureHealsOnAKill()
    {
        using var match = Skarner();
        var hero = match.Hero;
        var foe = match.Foe;
        foe.Position = new Position2(7300, 7000);
        foe.Health = 40.0;
        var before = hero.Health;
        TestAssert.True(match.Cast(2, foe), "Fracture is cast on the low foe");
        match.Advance(1.0);
        TestAssert.True(foe.Health <= 0.0, "the hit kills the foe");
        TestAssert.Near(before + 30.0, hero.Health, 5.0, $"the kill heals Skarner for the shipped 30 at rank one (AP 0) [hero={Names(hero)}]");

        // Without a kill there is no heal: the same cast on a healthy foe.
        match.Refill();
        var healthy = match.FarFoe;
        healthy.Position = new Position2(7300, 7000);
        var afterKill = hero.Health;
        TestAssert.True(match.Cast(2, healthy), "Fracture is cast on the healthy foe");
        match.Advance(1.0);
        TestAssert.True(healthy.Health < healthy.MaxHealth, "the hit lands on the healthy foe");
        TestAssert.True(hero.Health < afterKill + 5.0, "a hit that does not kill heals nothing");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    [Check("scripting.p132-skarner-fracture-heals-when-its-hit-kills-a-minion")]
    public static void FractureHealsOnAMinionKill()
    {
        using var match = Skarner();
        var hero = match.Hero;
        match.Foe.Position = new Position2(12000, 11000);
        var minion = LaneMinion(match, new Position2(7300, 7000));
        minion.Health = 40.0;
        var before = hero.Health;
        TestAssert.True(match.Cast(2, minion), "Fracture is cast on the low lane minion");
        match.Advance(1.0);
        TestAssert.True(!minion.Alive || minion.Health <= 0.0, "the hit kills the minion");
        TestAssert.Near(before + 30.0, hero.Health, 5.0, $"the minion kill heals Skarner for the shipped 30 at rank one [hero={Names(hero)}]");
        TestAssert.Equal("", Failures(match), "no script failure");
    }
}
