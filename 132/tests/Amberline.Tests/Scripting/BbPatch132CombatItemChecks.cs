using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using Amberline.Simulation.Stats;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 772: the combat and item changes of 1.0.0.127 to 1.0.0.132 on master's 132 data. The expected values are the 132 files' own (the
/// item records, the per-map Items.inibin lists, the shipped scripts) and the 132 client's code where the files are silent; the wiki ids
/// (a-wiki/REPORT.md) name the change.
/// </summary>
public static class BbPatch132CombatItemChecks
{
    // ------------------------------------------------------------------------------------------------------------
    // CastType 4: Ahri's fox-fires
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Fox-Fire (AhriFoxFire) casts three AhriFoxFireMissile children, CastType 4: circle missiles that the 132 client builds around their target
    /// unit (Ahri) at the launch point 150 out and turns at CircleMissileAngularVelocity -2 rad/s with no radial
    /// velocity. Their SpellOnMissileUpdate runs every 75 units flown (LuaOnMissileUpdateDistanceInterval), counts the missile's own SpellVars.Ready
    /// to three, then casts AhriFoxFireMissileTwo at the closest visible enemy champion within 650 and destroys the fox-fire.
    /// </summary>
    [Check("scripting.p132-circle-missile-fox-fire")]
    public static void FoxFiresCircleAhriThenSeekTheFoe()
    {
        using var match = new Match("Ahri");
        match.Learn(1);
        match.Refill();
        var data = GameData.Default.Spell("AhriFoxFireMissile");
        TestAssert.Equal(4, BbChampionModule.CastType(data), "AhriFoxFireMissile is CastType 4");
        TestAssert.True(match.Cast(1, match.Hero), "Fox-Fire is cast: " + match.CanCast(1, match.Hero).Reason);
        match.Advance(0.3);
        var router = match.Module.Router!;
        var circles = router.Missiles.Live.Where(flight => flight.SpellName == "AhriFoxFireMissile").ToArray();
        TestAssert.Equal(3, circles.Length, "three fox-fires are in flight");
        foreach (var circle in circles)
            TestAssert.Near(150.0, Dist(circle.Position, match.Hero.Position), 2.0, "each fox-fire circles Ahri 150 out");
        var angles = circles.Select(circle => Math.Atan2(circle.Position.Z - match.Hero.Position.Z, circle.Position.X - match.Hero.Position.X)).ToArray();
        match.Advance(0.3);
        // Still circling (the first seek is the third update, 3 x 75 units at 2 rad/s x 150 = 0.75 s): each turned about -0.6 rad.
        foreach (var (circle, before) in circles.Zip(angles))
        {
            if (circle.Ended) continue;
            var now = Math.Atan2(circle.Position.Z - match.Hero.Position.Z, circle.Position.X - match.Hero.Position.X);
            var turned = Math.IEEERemainder(now - before, 2.0 * Math.PI);
            TestAssert.Near(-0.6, turned, 0.1, $"a fox-fire turns at -2 rad/s (turned {turned:0.###})");
        }
        var health = match.Foe.Health;
        match.Advance(1.5);
        TestAssert.True(match.Foe.Health < health, $"the fox-fires seek and hit the foe ({health} -> {match.Foe.Health})");
        TestAssert.Equal(0, router.Missiles.Live.Count(flight => flight.SpellName == "AhriFoxFireMissile"), "every fox-fire has left (destroyed after its cast)");
        TestAssert.True(Buff(match.Hero, "AhriFoxFireMissile") is null, "the three AhriFoxFireMissile stacks are spent");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failure: " + string.Join(" | ", match.Module.ScriptFailures.Select(f => f.Message)));
    }

    private static double Dist(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    // ------------------------------------------------------------------------------------------------------------
    // Dodge (132.58, 132.50, 131.23, 132.26)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 1.0.0.132 removed the dodge stat (132.58). Ninja Tabi's record has no FlatDodgeMod any more (132.50), and the dodge runes' rFlatDodgeMod /
    /// rFlatDodgeModPerLevel (5019, 5020 ...) no longer reach mDodge: no 132 rune page holds them. A script buff's IncFlatDodgeMod still does: Jax's
    /// Counter Strike (JaxCounterStrike: IncFlatDodgeMod 100) dodges every basic attack for its 1.5 s.
    /// </summary>
    [Check("combat.p132-dodge-only-from-script-buffs")]
    public static void DodgeComesOnlyFromScriptBuffs()
    {
        var paths = GameData.Default.Paths;
        var items = new ItemCatalog(paths);
        TestAssert.Equal(0.0, items.Get(3047)!.Modifier("FlatDodgeMod"), "Ninja Tabi's 132 record carries no dodge");
        var runes = RunePage.CreateBot(new RuneCatalog(paths), [5019, 5020, 5353, 5354]);
        TestAssert.True(runes.Modifiers.Keys.Any(key => key.Contains("Dodge", StringComparison.Ordinal)), "control: the dodge rune records still ship");
        var inventory = new InventorySlot?[Inventory.SlotCount];
        inventory[0] = new InventorySlot(3047);
        HeroStats Stats(params BuffState[] buffs) =>
            HeroStatCalculator.Calculate(GameData.Default, items, new HeroStatsInput("Jax", 18, inventory, buffs, runes: runes));
        TestAssert.Equal(0.0, Stats().Dodge, "items and dodge runes give no dodge");
        var counterStrike = new BuffState("JaxCounterStrike", new BuffPayload { Dodge = 100.0 }, expiresAt: 1.5);
        TestAssert.Equal(1.0, Stats(counterStrike).Dodge, "Counter Strike's IncFlatDodgeMod 100 dodges every attack");
        var half = new BuffState("Probe", new BuffPayload { Dodge = 0.5 }, expiresAt: 1.0);
        TestAssert.Near(0.75, Stats(half, half).Dodge, 1e-12, "script dodge composes as 1 - product(1 - delta)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Ninja Tabi (132.50)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Items/3047.luaobj (new in 132) keeps a buff named after its own record on the holder, and that buff's BuffOnBeingHit keeps 0.9 of a
    /// DAMAGESOURCE_ATTACK hit whose attacker is not a turret (132.50: "10% less damage from non-turret basic attacks"). The control is the
    /// same holder the moment the buff is taken off (the item renews it ten seconds later).
    /// </summary>
    [Check("items.p132-ninja-tabi-cuts-non-turret-basic-attacks")]
    public static void NinjaTabiCutsNonTurretBasicAttacks()
    {
        // An Annie foe: her basic attack has no on-hit of its own (Orianna's Clockwork Windup would add magic damage that stacks per hit).
        using var match = new Match("Annie", foeChampion: "Annie");
        var tabi = match.Hero;
        tabi.Inventory = [new InventorySlot(3047), null, null, null, null, null];
        match.Runtime.HeroStats.Refresh(tabi, push: false);
        match.Advance(0.5);
        TestAssert.True(Buff(tabi, "3047") is not null, "the item script keeps its own buff on the holder");
        TestAssert.Equal(0.0, tabi.Dodge, "and gives no dodge");
        var turret = match.ChaosLaneTurret;
        double Taken(Entity source, DamageSourceKind kind)
        {
            // Each hit starts the turret's own ramp afresh (TurretDamageManager's TurretDamageMarker stacks on the turret and the victim).
            foreach (var unit in new[] { tabi, turret })
                foreach (var marker in unit.Buffs.Where(state => state.Name == "TurretDamageMarker").ToArray())
                    match.Runtime.SpellBuffs.Remove(unit, marker.Slot);
            tabi.Health = tabi.MaxHealth;
            TestAssert.True(match.Runtime.Combat.ApplyHit(source.NetId, tabi.NetId, 100.0, DamageType.Physical, kind), "the hit lands");
            return tabi.MaxHealth - tabi.Health;
        }
        var champion = Taken(match.Foe, DamageSourceKind.Basic);
        var tower = Taken(turret, DamageSourceKind.Basic);
        var spell = Taken(match.Foe, DamageSourceKind.Spell);
        var buff = tabi.Buffs.Single(state => state.Name == "3047");
        match.Runtime.SpellBuffs.Remove(tabi, buff.Slot);
        TestAssert.True(Buff(tabi, "3047") is null, "control: the buff is off");
        TestAssert.Near(0.9 * Taken(match.Foe, DamageSourceKind.Basic), champion, 1e-6, "a champion's basic attack keeps 0.9");
        TestAssert.Near(Taken(turret, DamageSourceKind.Basic), tower, 1e-6, "a turret's basic attack is untouched (CO_IS_NOT_TURRET)");
        TestAssert.Near(Taken(match.Foe, DamageSourceKind.Spell), spell, 1e-6, "spell damage is untouched (DAMAGESOURCE_ATTACK only)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Viktor's Hex Core (131.05, 131.47)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The three augments (3196 / 3197 / 3198, 1000 gold, CanBeSold 0) carry RequiredItem1 3200 beside the recipe that consumes it; the 132 client's
    /// shop refuses an item whose required item is not held. So the Prototype Hex Core is upgraded once,
    /// for 1000, and a hero without one (no other champion is given it) cannot buy an augment at all.
    /// </summary>
    [Check("items.p132-hex-core-upgrades-once")]
    public static void HexCoreUpgradesOnce()
    {
        var catalog = new ItemCatalog(GameData.Default.Paths);
        foreach (var augment in new[] { 3196, 3197, 3198 })
        {
            TestAssert.Equal(new[] { 3200 }, catalog.Get(augment)!.RequiredItems.ToArray(), $"{augment} requires the Prototype Hex Core");
            TestAssert.False(catalog.Get(augment)!.CanBeSold, $"{augment} cannot be sold");
        }
        var rules = MapItemRules.LoadMap1();
        InventorySlot?[] slots = [new InventorySlot(3200), null, null, null, null, null];
        var upgraded = Inventory.Buy(catalog, slots, 5000.0, 3196, rules);
        TestAssert.True(upgraded.Ok, "the core is upgraded: " + upgraded.Reason);
        TestAssert.Near(4000.0, upgraded.Gold, 1e-9, "for 1000 gold");
        TestAssert.Equal(3196, upgraded.Slots[upgraded.Slot]!.ItemId, "into the augment");
        TestAssert.False(upgraded.Slots.Any(slot => slot?.ItemId == 3200), "the recipe consumes the core");
        var second = Inventory.Buy(catalog, upgraded.Slots, upgraded.Gold, 3197, rules);
        TestAssert.False(second.Ok, "a second augment needs a core the hero no longer holds");
        TestAssert.Equal("missing_required_item", second.Reason, "refused for the required item");
        var stranger = Inventory.Buy(catalog, Inventory.Empty(), 5000.0, 3198, rules);
        TestAssert.Equal("missing_required_item", stranger.Reason, "no core, no augment");
        TestAssert.Equal("unsellable_item", Inventory.Sell(catalog, upgraded.Slots, upgraded.Gold, upgraded.Slot).Reason, "the augment cannot be sold");
    }

    /// <summary>
    /// The Hex Core line runs its 132 scripts (BbItemModule): 3200 and each augment renew ViktorHexCore, and an augment also its
    /// ViktorAugmentQ / W / E window, the buff Viktor's spells test.
    /// </summary>
    [Check("items.p132-hex-core-scripts-run")]
    public static void HexCoreScriptsRun()
    {
        using var match = new Match("Viktor");
        var viktor = match.Hero;
        match.Advance(0.5);
        TestAssert.True(viktor.Inventory.Any(slot => slot?.ItemId == 3200), "CharScriptViktor gives the Prototype Hex Core");
        TestAssert.True(Buff(viktor, "ViktorHexCore") is not null, "3200 keeps ViktorHexCore on Viktor");
        foreach (var (augment, window) in new[] { (3196, "ViktorAugmentQ"), (3197, "ViktorAugmentW"), (3198, "ViktorAugmentE") })
        {
            viktor.Inventory = [new InventorySlot(augment), null, null, null, null, null];
            match.Runtime.HeroStats.Refresh(viktor, push: false);
            match.Advance(0.5);
            TestAssert.True(Buff(viktor, window) is not null, $"{augment} keeps {window} on Viktor");
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // The 132 shop lists (132.48, 129.31, 132.52, 131.30, 131.31, 132.49, 132.51)
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Every 132 store change of the batch is data the shop already reads: the per-map UnpurchasableItemList (Map1, 2 and 4 dropped 3178 Ionic
    /// Spark and 3183; Map8 added 3083 Warmog's Armor) and the records' own InStore / price / stats (3131 Sword of the Divine and 3183 Priscilla's
    /// Blessing InStore 0; Ionic Spark 50 % attack speed; Nashor's Tooth 400 to combine, 65 ability power; Stinger 250 to combine).
    /// </summary>
    [Check("items.p132-shop-follows-the-132-lists")]
    public static void ShopFollowsThe132Lists()
    {
        var paths = GameData.Default.Paths;
        var catalog = new ItemCatalog(paths);
        foreach (var map in new[] { 1, 4 })
        {
            var rules = MapItemRules.Load(paths.ForMap(map));
            var spark = Inventory.Buy(catalog, Inventory.Empty(), 5000.0, 3178, rules);
            TestAssert.True(spark.Ok, $"Map{map}: Ionic Spark is bought (132.48): {spark.Reason}");
            TestAssert.Equal("unavailable_item", Inventory.Buy(catalog, Inventory.Empty(), 50000.0, 3131, rules).Reason,
                $"Map{map}: Sword of the Divine is gone (132.52)");
        }
        var dominion = MapItemRules.Load(paths.ForMap(8));
        TestAssert.Equal("unavailable_item", Inventory.Buy(catalog, Inventory.Empty(), 50000.0, 3083, dominion).Reason,
            "Map8: Warmog's Armor is not sold (129.31)");
        TestAssert.Equal("unavailable_item", Inventory.Buy(catalog, Inventory.Empty(), 50000.0, 3183, dominion).Reason,
            "Map8: Priscilla's Blessing is not sold (129.31)");
        TestAssert.Near(0.5, catalog.Get(3178)!.Modifier("PercentAttackSpeedMod"), 1e-6, "Ionic Spark 50 % attack speed (131.30)");
        TestAssert.Equal(400, catalog.Get(3115)!.Price, "Nashor's Tooth combines for 400 (132.49)");
        TestAssert.Near(65.0, catalog.Get(3115)!.Modifier("FlatMagicDamageMod"), 1e-6, "Nashor's Tooth 65 ability power (132.49)");
    }
}
