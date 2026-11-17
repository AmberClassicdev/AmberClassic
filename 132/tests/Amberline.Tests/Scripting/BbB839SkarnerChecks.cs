using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 839 (17 November 2026): the 1.0.0.132 Skarner rows of patch 1.0.0.127 (change list 127.12 Energize not on turrets, 127.13 Fracture heals
/// when its hit kills). Driven through the production script engine (<see cref="BbM3.Match"/>); the numbers are the shipped 132 literals
/// (<c>Characters/Skarner/Scripts/CharScriptSkarner.luaobj</c>, <c>Spells/SkarnerFracture.luaobj</c>, <c>Spells/SkarnerFractureMissile.luaobj</c>).
/// </summary>
public static class BbB839SkarnerChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    // Energize (CharScriptSkarner CharOnHitUnit): a basic attack that lands on a champion takes one second off each cooldown; the shipped
    // CharOnHitUnit runs only for a non-turret AI unit (the 132 change: a turret hit no longer triggers it).
    [Check("scripting.p132-skarner-energize-does-not-trigger-on-a-turret")]
    public static void EnergizeIgnoresTurrets()
    {
        using var match = new Match("Skarner", foeChampion: "Orianna");
        var hero = match.Hero;
        match.Learn(1);
        match.Refill();
        var turret = match.ChaosLaneTurret;
        hero.Position = new Position2(turret.Position.X - 150.0, turret.Position.Z);
        TestAssert.True(match.Cast(1, hero), "Crystalline Exoskeleton starts its cooldown");
        match.Advance(0.5);
        var cooldown = hero.SpellCooldowns[1];
        TestAssert.True(cooldown > 10.0, $"the W is cooling down ({cooldown:0.00})");
        var turretHealth = turret.Health;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, turret), "a basic attack on the turret is queued");
        match.Advance(1.0);
        TestAssert.True(turret.Health < turretHealth, "the turret takes the hit, so the attack really landed");
        TestAssert.Near(cooldown - 1.0, hero.SpellCooldowns[1], 0.1, "the cooldown loses only the second of time: no Energize for a turret");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

}
