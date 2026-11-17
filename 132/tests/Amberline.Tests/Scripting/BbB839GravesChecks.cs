using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Buffs;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 839 (17 November 2026): the 1.0.0.132 Graves rules of patch 1.0.0.127 (change list 127.03: True Grit, Buckshot, Smoke Screen, Quickdraw,
/// Collateral Damage). Graves plays from his shipped scripts through the production script engine (<see cref="BbM3.Match"/>). The numbers are
/// read from the shipped 132 files (<c>Characters/Graves/Scripts/CharScriptGraves.luaobj</c> and <c>Spells/Graves*.luaobj</c>).
/// </summary>
public static class BbB839GravesChecks
{
    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    /// <summary>The <c>SrcValueByLevel</c> table of the first block of the character script that sets <paramref name="variable"/> in a hook.</summary>
    private static double[] CharByLevel(string hook, string variable)
    {
        var block = Blocks(CharScript("Graves"), hook).First(item => item.Function == "BBSetVarInTable" && item.Text("DestVar") == variable);
        return Numbers(block.Param("SrcValueByLevel")!);
    }

    // ------------------------------------------------------------------------------------------------------------
    // True Grit (GravesPassive, GravesPassiveCounter, GravesPassiveGrit; CharScriptGraves CharOnPreDealDamage)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Graves's True Grit: a hit he deals starts the counter (three seconds) and the Grit stacks (two on the first hit, one more each second
    /// while the counter lasts, up to eleven). Each Grit stack adds <c>ArmorAmount</c> armour and magic resist and the counter takes one
    /// <c>ArmorAmount</c> away, so the net bonus is (stacks - 1) x <c>ArmorAmount</c>. The bonus goes when the counter runs out.
    /// </summary>
    [Check("scripting.p132-graves-true-grit-armour-is-one-less-than-its-stacks-and-ends-out-of-combat")]
    public static void TrueGritArmourFollowsItsStacks()
    {
        using var match = new Match("Graves");
        var hero = match.Hero;
        match.Learn(1);
        match.Refill();
        match.Foe.Position = new Position2(7300, 7000);
        // CharScriptGraves [SetVarsByLevel]: ArmorAmount is 1 for levels 1 to 6.
        var armourAmount = CharByLevel("SetVarsByLevel", "ArmorAmount")[0];
        TestAssert.Near(1.0, armourAmount, 0.0, "ArmorAmount at level 1 (shipped table)");
        match.Runtime.HeroStats.Refresh(hero, push: false);
        var before = hero.Armor;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, match.Foe), "Graves's basic attack is queued on the foe");
        match.Advance(1.0);
        var grit = Buff(hero, "GravesPassiveGrit");
        TestAssert.True(grit is not null && grit.Stacks >= 2, "the first hit starts True Grit at two stacks; buffs: " + Names(hero));
        TestAssert.Near(before + (grit!.Stacks - 1) * armourAmount, hero.Armor, 0.01,
            "net armour is (stacks - 1) x ArmorAmount in combat (GravesPassiveCounter takes one away)");
        var stacks = grit.Stacks;
        match.Advance(1.0);
        TestAssert.True(Buff(hero, "GravesPassiveGrit")!.Stacks > stacks, "the counter adds a Grit stack each second in combat");
        // The counter lasts three seconds after the last hit; Grit goes with it (BuffOnUpdateAmmo).
        match.Advance(4.5);
        TestAssert.True(Buff(hero, "GravesPassiveGrit") is null, "True Grit ends once Graves is out of combat");
        TestAssert.Near(before, hero.Armor, 0.01, "and the armour bonus goes with it");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Buckshot (Q: GravesClusterShot, the three pellets; GravesClusterShotAttack TargetExecute)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Buckshot fires three pellets. The first pellet on a target deals the rank's damage (60 at rank one, plus 0.8 bonus AD); a pellet that
    /// lands while the first one's 0.25 s mark is still on the target deals a quarter of it (the shipped <c>BBMath</c> x 0.25).
    /// </summary>
    [Check("scripting.p132-graves-buckshot-hits-three-pellets-and-the-extra-two-at-a-quarter")]
    public static void BuckshotPelletsAtAQuarter()
    {
        using var match = new Match("Graves");
        match.Learn(1);
        match.Refill();
        match.Foe.Position = new Position2(7300, 7000);
        var first = ByLevel("GravesClusterShotAttack", "TargetExecute", "BonusDamage")[0];
        TestAssert.Near(60.0, first, 0.0, "the shipped rank-one Buckshot damage is 60");
        TestAssert.True(match.Cast(0, at: match.Foe.Position), "Buckshot is cast at the foe");
        match.Advance(1.0);
        var hits = HitsOn(match, "Foe");
        TestAssert.Equal(3, hits.Count, "three pellets hit the point-blank foe; log: " + string.Join(" / ", match.Log.Hits));
        TestAssert.Near(first, hits[0].Raw, 0.01, "the first pellet deals the rank's damage");
        TestAssert.Near(first * 0.25, hits[1].Raw, 0.01, "the second pellet deals a quarter of it");
        TestAssert.Near(first * 0.25, hits[2].Raw, 0.01, "the third pellet deals a quarter of it");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Smoke Screen (W: GravesSmokeGrenade cloud; GravesSmokeGrenadeBoomSlow; GravesSmokeGrenadeBoom)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Smoke Screen's cloud (300 around the point) slows the enemies inside it by the rank's amount (15% at rank one, multiplicative) and takes
    /// 800 of their sight while they stand in it (GravesSmokeGrenadeBoom's permanent bubble radius). A unit outside the cloud gets neither.
    /// </summary>
    [Check("scripting.p132-graves-smoke-screen-slows-and-cuts-sight-inside-the-cloud-only")]
    public static void SmokeScreenSlowsAndCutsSight()
    {
        using var match = new Match("Graves");
        match.Learn(1);
        match.Refill();
        var inside = match.Foe;
        var outside = match.FarFoe;
        inside.Position = new Position2(7300, 7000);
        // The cloud's own script: 300 around the point; the slow table by rank; the sight cut is a fixed -800 (GravesSmokeGrenade).
        var slow = ByLevel("GravesSmokeGrenade", "BuffOnUpdateActions", "MovementSpeedMod")[0];
        TestAssert.Near(-0.15, slow, 0.0, "the shipped rank-one slow is 15%");
        match.Runtime.HeroStats.Refresh(inside, push: false);
        var speedBefore = inside.Speed;
        TestAssert.True(match.Cast(1, at: new Position2(7300, 7000)), "Smoke Screen is cast on the foe's spot");
        match.Advance(0.5);
        TestAssert.True(Buff(inside, "GravesSmokeGrenadeBoomSlow") is not null, "the foe in the cloud is slowed; buffs: " + Names(inside));
        TestAssert.True(Buff(inside, "GravesSmokeGrenadeBoom") is not null, "and carries the sight cut; buffs: " + Names(inside));
        match.Runtime.HeroStats.Refresh(inside, push: false);
        TestAssert.Near(speedBefore * (1.0 + slow), inside.Speed, 0.5, "the slowed foe moves at 85% of his speed");
        TestAssert.True(Buff(outside, "GravesSmokeGrenadeBoomSlow") is null, "a unit outside the cloud is not slowed");
        TestAssert.True(Buff(outside, "GravesSmokeGrenadeBoom") is null, "and keeps its sight");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Quickdraw (E: GravesMove; CharScriptGraves CharOnHitUnit cuts the E cooldown by 1.05 s on each hit on a non-turret AI unit)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A basic attack that lands on a non-turret AI unit cuts Quickdraw's cooldown by 1.05 seconds (CharScriptGraves CharOnHitUnit). The
    /// cooldown otherwise runs on its own clock.
    /// </summary>
    [Check("scripting.p132-graves-quickdraw-cooldown-drops-one-and-a-bit-seconds-on-a-hit")]
    public static void QuickdrawCooldownCutOnHit()
    {
        using var match = new Match("Graves");
        match.Learn(1);
        match.Refill();
        var hero = match.Hero;
        match.Foe.Position = new Position2(7300, 7000);
        hero.SpellCooldowns[2] = 10.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, match.Foe), "a basic attack is queued on the foe");
        match.Advance(1.0);
        TestAssert.True(HitsOn(match, "Foe").Count >= 1, "the basic attack lands");
        // One second of time and the 1.05 cut.
        TestAssert.Near(10.0 - 1.0 - 1.05, hero.SpellCooldowns[2], 0.1, "Quickdraw's cooldown loses 1.05 seconds on the hit (shipped CharOnHitUnit)");
        TestAssert.Equal("", Failures(match), "no script failure");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Collateral Damage (R: GravesChargeShot, the shot GravesChargeShotShot; the explosions GravesChargeShotXplode)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Collateral Damage's shot hits the first enemy for 250 plus 1.4 bonus AD at rank one (SpellBaseDamage 250/350/450). The explosions it sets
    /// off (GravesChargeShotXplode) do 140/250/360 plus 1.2 bonus AD to the units they reach other than the shot's target. A unit far from the
    /// shot is untouched.
    /// </summary>
    [Check("scripting.p132-graves-collateral-damage-hits-the-shot-target-then-its-cone-and-spares-the-rest")]
    public static void CollateralDamageShotAndCone()
    {
        using var match = new Match("Graves");
        match.Learn(1);
        match.Refill();
        var foe = match.Foe;
        foe.Position = new Position2(7300, 7000);
        var shot = ByLevel("GravesChargeShotShot", "TargetExecute", "SpellBaseDamage")[0];
        var explosion = ByLevel("GravesChargeShotXplode", "TargetExecute", "BaseDmg")[0];
        TestAssert.Near(250.0, shot, 0.0, "the shipped rank-one shot damage is 250");
        TestAssert.Near(140.0, explosion, 0.0, "the shipped rank-one explosion damage is 140");
        TestAssert.True(match.Cast(3, foe), "Collateral Damage is cast at the foe");
        match.Advance(2.0);
        var direct = HitsOn(match, "Foe");
        TestAssert.True(direct.Count >= 1 && direct[0].Raw >= shot, "the shot hits the foe for at least its base damage; log: " + string.Join(" / ", match.Log.Hits));
        TestAssert.True(HitsOn(match, "FarEnemy").Count == 0, "a far unit outside the shot and its cone is untouched");
        TestAssert.Equal("", Failures(match), "no script failure");
    }
}
