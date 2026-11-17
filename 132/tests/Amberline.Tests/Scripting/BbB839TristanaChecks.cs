using Amberline.Core;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 839 (17 November 2026): the 1.0.0.132 Tristana row of patch 1.0.0.127 (change list 129.27: Explosive Shot auto-attacks its target).
/// Explosive Shot is the slot-two spell <c>DetonatingShot</c>: its <c>TargetExecute</c> orders Tristana to attack the target
/// (<c>BBIssueOrder AI_ATTACKTO</c>) and puts <c>ExplosiveShotDebuff</c> on it.
/// </summary>
public static class BbB839TristanaChecks
{
    [Check("scripting.p132-tristana-explosive-shot-makes-her-attack-its-target")]
    public static void ExplosiveShotAttacksItsTarget()
    {
        using var match = new Match("Tristana", foeChampion: "Orianna");
        var hero = match.Hero;
        var foe = match.Foe;
        match.Learn(1);
        match.Refill();
        foe.Position = new Position2(7300, 7000);
        TestAssert.Equal("DetonatingShot", match.Module.SpellName(hero, 2), "slot two is Explosive Shot (DetonatingShot)");
        TestAssert.True(match.Cast(2, foe), "Explosive Shot is cast on the foe");
        match.Advance(0.5);
        TestAssert.True(Buff(foe, "ExplosiveShotDebuff") is not null, "the foe carries the Explosive Shot debuff (positive control); buffs: " + Names(foe));
        TestAssert.True(hero.AttackTarget == foe.NetId, "Tristana is ordered to attack the foe (AI_ATTACKTO)");
        TestAssert.Equal("", string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)), "no script failure");
    }
}
