using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Spells;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// BuildingBlocks engine M3: Sivir plays from her shipped scripts in a production <c>MatchRuntime</c>. Every expected number is
/// read from the shipped files (script tables through <see cref="BbScriptLoader"/>, spell inibins through <see cref="GameData"/>)
/// and pinned a second time as the literal the two files agree on.
/// </summary>
public static class BbSivirChecks
{
    // Batch 203: APBonusDamageToTowers and DisconnectTarget run from their shipped scripts now (a champion's CharOnActivate adds the first).
    // They stay in this list because the check counts the shared system buffs of the closure, not what the engine applies.
    private static readonly string[] SystemBuffs =
    [
        "APBonusDamageToTowers", "ChampionChampionDelta", "PositiveChampionDelta", "NegativeChampionDelta",
        "DisconnectTimer", "DisconnectTarget", "FortifyCheck", "FortifyBuff",
    ];

    // ------------------------------------------------------------------------------------------------------------
    // Closure, packets
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-sivir-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndRunsEveryNativeOfItsHooks()
    {
        var module = new BbChampionModule("Sivir");
        var hero = new Entity { ChampionName = "Sivir" };
        TestAssert.Equal(["SpiralBlade", "Ricochet", "SpellShield", "OnTheHunt"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Sivir.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(), "ranks");

        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Sivir");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(28, closure.ScriptNames.Count(), "the Sivir closure is 28 scripts (audit bb-coverage)");
        TestAssert.Equal(8, closure.ScriptNames.Count(name => SystemBuffs.Contains(name, StringComparer.OrdinalIgnoreCase)),
            "eight of the 17 scripts are the shared system buffs (batch 203: the engine runs APBonusDamageToTowers and DisconnectTarget from their shipped scripts)");
        var unbound = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in closure.Scripts.Where(item => !SystemBuffs.Contains(item.Name, StringComparer.OrdinalIgnoreCase)))
            foreach (var (hook, block) in script.Script.Walk())
                if (hook != BbScript.PreLoadHook && hook != "CharOnDisconnect" &&
                    BbNativeCatalog.OwnerOf(block.Function) is not null && !BbNativeHandlers.IsBound(block.Function))
                    unbound.Add($"{script.Name}:{hook}:{block.Function}");
        TestAssert.Equal(0, unbound.Count, "every native a Sivir hook runs is bound: " + string.Join(", ", unbound));
        // The extra spells of her character inibin: the attack override (slot 42) and the Spiral Blade child (slot 43).
        var extras = GameData.Default.Champion("Sivir").Raw;
        string Extra(int number) => extras.TryGetValue(InibinReader.KeyHash($"Data*ExtraSpell{number}"), out var value) ? value.AsString() : "";
        TestAssert.Equal(new[] { "RicochetAttack", "SpiralBladeMissile" }, new[] { Extra(1), Extra(2) }, "Data*ExtraSpell1 and 2");
    }

    [Check("scripting.bb-sivir-cast-packets-pass-the-four-client-gates")]
    public static void CastPacketsCarryNamesTimesCostsAndTargets()
    {
        using var match = new Match("Sivir");
        match.Learn(1);
        var spells = GameData.Default.Champion("Sivir").SpellNames;
        var snapshot = match.Runtime.HeroStats.Snapshot(match.Hero);
        // 132 inibins, rank one: Spiral Blade 70 (126: 80), Ricochet 40 (126: 0; the toggle is paid), Spell Shield 75, On The Hunt 100.
        TestAssert.Equal([70.0, 40.0, 75.0, 100.0], snapshot.ManaCosts.ToArray(), "rank-one mana costs from the spell inibins");
        TestAssert.True(snapshot.CanCastBits1 == uint.MaxValue && snapshot.CanCastBits2 == uint.MaxValue, "cast bits are open");
        // 132 cooldowns at rank one: Spiral Blade 9, Ricochet 7 (126: 0), Spell Shield 22, On The Hunt 100 (126: 90).
        var expectedCooldown = new[] { 9.0, 7.0, 22.0, 100.0 };
        var expectedCost = new[] { 70.0, 40.0, 75.0, 100.0 };
        for (var slot = 0; slot < 4; slot++)
        {
            match.Refill();
            var before = match.Log.Casts.Count;
            TestAssert.True(match.Cast(slot, null, at: slot == 0 ? new Position2(7900, 7000) : null), $"{spells[slot]} is accepted");
            // The Spiral Blade cast is announced with its child (slot 43, the missile); the others are one cast each.
            TestAssert.Equal(before + (slot == 0 ? 2 : 1), match.Log.Casts.Count, $"{spells[slot]} announces its cast{(slot == 0 ? " and the child" : "")}");
            var cast = match.Log.Casts[before];
            var data = GameData.Default.Spell(spells[slot], slot == 3 ? 3 : 5);
            TestAssert.Equal(SpellSystem.ProtocolHash(spells[slot]), cast.SpellHash, $"{spells[slot]} hash");
            TestAssert.Equal((byte)slot, cast.Slot, $"{spells[slot]} slot");
            TestAssert.Equal(1, cast.Rank, $"{spells[slot]} rank");
            TestAssert.Equal(match.Hero.NetId, cast.CasterNetId, $"{spells[slot]} caster");
            TestAssert.Near(expectedCost[slot], cast.ManaCost, 1e-6, $"{spells[slot]} mana cost");
            TestAssert.Near(expectedCooldown[slot], cast.Cooldown, 1e-6, $"{spells[slot]} cooldown");
            TestAssert.Near(data.CastDelay, cast.DesignerCastTime, 1e-6, $"{spells[slot]} designer cast time is the data's");
            TestAssert.Near(data.TotalTime, cast.DesignerTotalTime, 1e-6, $"{spells[slot]} designer total time is the data's");
            TestAssert.True(cast.DesignerTotalTime > 0.0, $"{spells[slot]} designer total time {cast.DesignerTotalTime}");
            // A self-centred area cast lists the enemies its area holds (SpellSystem.AreaCastTargets, batch 111, the reveal rule): On the Hunt's
            // radius holds the foe 300 away and not the far one; the aimed and the self casts list nothing.
            var listed = slot == 3 ? new[] { match.Foe } : Array.Empty<Entity>();
            TestAssert.True(slot != 3 || data.CastRadius is >= 300.0 and < 10000.0, $"{spells[slot]} radius {data.CastRadius} holds the foe, not the far one");
            TestAssert.Equal(listed.Length, cast.Targets.Count, $"{spells[slot]} target list");
            if (listed.Length > 0) TestAssert.Equal(listed[0].NetId, cast.Targets[0].NetId, $"{spells[slot]} lists the foe in its area");
            TestAssert.Equal(0u, cast.MissileNetId, $"{spells[slot]} itself has no missile");
            if (slot == 0)
            {
                var child = match.Log.Casts[before + 1];
                var childData = GameData.Default.Spell("SpiralBladeMissile", 5);
                TestAssert.Equal(SpellSystem.ProtocolHash("SpiralBladeMissile"), child.SpellHash, "child hash");
                TestAssert.Equal((byte)43, child.Slot, "the child sits in extra slot 2 (42 + 1)");
                TestAssert.Equal(1, child.Rank, "the child takes the parent's rank");
                TestAssert.True(child.MissileNetId != 0u, "the child carries the missile");
                TestAssert.Near(0.0, child.ManaCost, 1e-9, "the child costs nothing");
                TestAssert.Near(0.0, child.Cooldown, 1e-9, "and has no cooldown of its own");
                TestAssert.Near(childData.CastDelay, child.DesignerCastTime, 1e-6, "the child's own designer cast time");
            }
            match.Advance(1.0);
        }
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Q Spiral Blade
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-sivir-q-spiral-blade-hits-each-way-and-decays")]
    public static void SpiralBladeFliesOutAndBackAndHitsEveryUnitOnEachPass()
    {
        var script = Blocks(Script("SpiralBladeMissile"), "TargetExecute").First(block => block.Function == "BBApplyDamage");
        var damage = Numbers(script.Param("DamageByLevel")!);
        // 132 SpiralBladeMissile.luaobj: DamageByLevel 70/115/160/205/250 (126: 80/125/170/215/260); the ability power ratio 0.5 is the
        // APDamage BBMath (its SpellDamageRatio parameter is 0; 126 applied 0.75 in the BBApplyDamage).
        TestAssert.Equal([70.0, 115.0, 160.0, 205.0, 250.0], damage, "SpiralBladeMissile.luaobj DamageByLevel");
        TestAssert.Near(0.5, Blocks(Script("SpiralBladeMissile"), "TargetExecute").First(block => block.Function == "BBMath" && block.Text("DestVar") == "APDamage")
            .Number("Src1Value")!.Value, 1e-12, "SpiralBladeMissile.luaobj: 0.5 of ability power (APDamage)");
        var decay = Blocks(Script("SpiralBladeMissile"), "TargetExecute").Where(block => block.Function == "BBMath" && block.Text("DestVar") == "percentOfAttack").ToArray();
        TestAssert.Near(0.8, decay[0].Number("Src2Value")!.Value, 1e-12, "20 percent less for every unit hit");
        TestAssert.Near(0.4, decay[1].Number("Src2Value")!.Value, 1e-12, "down to 40 percent");
        var spell = GameData.Default.Spell("SpiralBlade", 5);
        var child = GameData.Default.Spell("SpiralBladeMissile", 5);
        TestAssert.Equal([9.0, 9.0, 9.0, 9.0, 9.0], spell.Cooldowns.ToArray(), "Spiral Blade cooldowns");
        TestAssert.Equal([70.0, 80.0, 90.0, 100.0, 110.0], spell.ManaCosts.ToArray(), "Spiral Blade mana costs (132; 126: 80 to 120)");
        TestAssert.Near(1350.0, child.MissileSpeed, 1e-9, "SpiralBladeMissile.inibin MissileSpeed");
        TestAssert.Near(1175.0, child.CastRange, 1e-9, "SpiralBladeMissile.inibin CastRange: how far it flies");
        TestAssert.Near(90.0, ChampionSpellSupportLineWidth(child), 1e-9, "SpiralBladeMissile.inibin LineWidth");
        TestAssert.Equal(3, BbChampionModule.CastType(child), "the child is a line missile (CastType 3)");
        TestAssert.Equal(1, BbChampionModule.RawCount(child, "SpellData*LineMissileBounces"), "it bounces once: it returns");

        using var match = new Match("Sivir");
        match.GiveAbilityPower();
        // A Long Sword: ten bonus attack damage on top of the champion's base.
        match.Hero.Inventory = [new InventorySlot(1058), new InventorySlot(1036), null, null, null, null];
        match.Runtime.HeroStats.Refresh(match.Hero, push: false);
        match.Learn(3);
        var ap = match.Hero.AbilityPower;
        var bonus = match.Hero.AttackDamage - GameData.Default.StatsAtLevel(GameData.Default.Champion("Sivir"), match.Hero.Level).AttackDamage;
        TestAssert.Near(10.0, bonus, 1e-6, "the Long Sword adds ten attack damage");
        var minion = Enemy(match, "Minion", new Position2(7800, 7000));
        match.Foe.Position = new Position2(7300, 7000);
        match.Refill();
        // 132 SpiralBladeMissile.luaobj deals PHYSICAL_DAMAGE (126: magic), so the armour mitigates it.
        var foeArmor = match.Foe.Armor;
        var minionArmor = minion.Armor;
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(0, null, at: new Position2(8000, 7000)), "Q cast along the line");
        var cast = match.Now;
        TestAssert.Near(mana - 90.0, match.Hero.Mana, 1e-6, "rank 3 costs 90 mana (132; 126: 100)");
        TestAssert.Near(9.0, match.Hero.SpellCooldowns[0], 1e-6, "9 s cooldown");
        match.Advance(0.2);
        TestAssert.Equal(0, HitsOn(match, "Foe").Count + HitsOn(match, "Minion").Count, "nothing is hit before the windup ends");
        match.Advance(2.2);
        var foe = HitsOn(match, "Foe");
        var minions = HitsOn(match, "Minion");
        TestAssert.Equal(2, foe.Count, "the Foe is hit once out and once back");
        TestAssert.Equal(2, minions.Count, "so is the minion");
        var windup = GameData.Default.Spell("SpiralBlade", 3).CastTime;
        // 132: the bonus attack damage counts 1.1 times (BBMath Src1Value 1.1 on bonusDamage; 126: 1.0) and ability power 0.5 (126: 0.75).
        var raw = damage[2] + 1.1 * bonus + 0.5 * ap;
        // Out: Foe (100 percent), minion (80); back: minion (64), Foe (51.2). Kayle-style decay lives in CharVars, so it runs on.
        var expected = new[] { (foe[0], 1.0), (minions[0], 0.8), (minions[1], 0.64), (foe[1], 0.512) };
        foreach (var (hit, percent) in expected)
        {
            NearF32(raw * percent, hit.Raw, $"hit at {R(hit.At - cast)} s carries {R(percent)} of 160 + 1.1 bonus AD + 0.5 AP");
            TestAssert.True(hit.Kind == "SpellAoe", "damage source SPELLAOE");
        }
        NearF32(Mitigated(raw, foeArmor), foe[0].Dealt, "Spiral Blade's damage is physical: mitigated by the armour");
        NearF32(Mitigated(raw * 0.8, minionArmor), minions[0].Dealt, "the minion's first hit");
        // Timing: the missile leaves when the windup ends and flies at 1350; a unit is met when the line's contact radius (the line
        // width plus the unit's own radius) reaches it, like the hand-written line missiles.
        var foeReach = 90.0 + match.Foe.CombatRadius;
        var minionReach = 90.0 + minion.CombatRadius;
        TestAssert.Near(windup + (300.0 - foeReach) / 1350.0, foe[0].At - cast, 0.07, "out: the Foe, 300 away");
        TestAssert.Near(windup + (800.0 - minionReach) / 1350.0, minions[0].At - cast, 0.07, "out: the minion, 800 away");
        TestAssert.Near(windup + 1175.0 / 1350.0 + (375.0 - minionReach) / 1350.0, minions[1].At - cast, 0.07, "back: the minion again");
        TestAssert.Near(windup + 1175.0 / 1350.0 + (875.0 - foeReach) / 1350.0, foe[1].At - cast, 0.07, "back: the Foe again");
        TestAssert.True(foe[0].At < minions[0].At && minions[0].At < minions[1].At && minions[1].At < foe[1].At, "out in order, back in reverse");
        TestAssert.Equal(0, match.Runtime.Spells.SharedProjectiles.Count, "the missile has come home and is gone");
        TestAssert.Equal(0, HitsOn(match, "Ally").Count + HitsOn(match, "Sivir").Count, "friends and the caster are not hit");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");

        // The decay restarts at every cast and stops at 40 percent: five units on the line, the fifth hit takes 40 percent.
        match.Refill();
        var a = Enemy(match, "A", new Position2(7400, 7000));
        var b = Enemy(match, "B", new Position2(7500, 7000));
        var c = Enemy(match, "C", new Position2(7600, 7000));
        var d = Enemy(match, "D", new Position2(7700, 7000));
        match.Foe.Position = new Position2(11000, 11000);
        minion.Position = new Position2(11500, 11000);
        TestAssert.True(match.Cast(0, null, at: new Position2(8100, 7000)), "second cast");
        match.Advance(1.2);
        var percents = new[] { a, b, c, d }.Select(unit => HitsOn(match, unit.Name).First().Raw / raw).ToArray();
        TestAssert.Equal([1.0, 0.8, 0.64, 0.512], percents.Select(value => Math.Round(value, 6)).ToArray(), "the first pass restarts at 100 percent");
        match.Advance(1.2);
        var back = new[] { d, c, b, a }.Select(unit => HitsOn(match, unit.Name).Last().Raw / raw).ToArray();
        TestAssert.Equal([0.4096, 0.4, 0.4, 0.4], back.Select(value => Math.Round(value, 6)).ToArray(), "the way back decays on, to the 40 percent floor");
    }

    private static double ChampionSpellSupportLineWidth(SpellData data) =>
        Amberline.Simulation.Spells.Champions.ChampionSpellSupport.LineWidth(data, 0.0);

    [Check("scripting.bb-sivir-q-blade-is-cancelled-by-death-in-the-windup-and-survives-it-in-flight")]
    public static void TheBladeIsCancelledByDeathBeforeLaunchAndSurvivesItAfter()
    {
        using var match = new Match("Sivir");
        match.Learn(1);
        match.Refill();
        TestAssert.True(match.Cast(0, null, at: new Position2(8000, 7000)), "Q cast");
        match.Runtime.Combat.Kill(match.Hero, match.Foe.NetId);
        match.Advance(2.5);
        TestAssert.Equal(0, HitsOn(match, "Foe").Count, "a blade whose caster died in the windup never left");
        TestAssert.Equal(0, match.Runtime.Spells.SharedProjectiles.Count, "and the queue is empty");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");

        // A blade already in the air survives its caster's death: it hits on the way out and comes home to where he fell.
        using var second = new Match("Sivir");
        second.Learn(1);
        second.Refill();
        Enemy(second, "Target", new Position2(8000, 7000));
        second.Foe.Position = new Position2(11000, 11000);
        TestAssert.True(second.Cast(0, null, at: new Position2(8000, 7000)), "second Q cast");
        second.Advance(0.6);
        second.Runtime.Combat.Kill(second.Hero, second.Foe.NetId);
        second.Advance(2.0);
        TestAssert.True(HitsOn(second, "Target").Count >= 1, "the launched blade still hits");
        TestAssert.Equal(0, second.Runtime.Spells.SharedProjectiles.Count, "and ends at the dead caster's position");
        TestAssert.Equal(0, second.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // W Ricochet
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// "Sivir W disappears after hitting the first target" (owner, 16 October 2026). The attack's missile is a chain missile too: the
    /// client creates it at the attack's cast frame (one windup after the swing's cast) and takes its hops from a ChainMissileSync
    /// sent in the missile's name (see SpellPackets.ChainMissileSync), not from the attacker.
    /// </summary>
    [Check("scripting.bb-sivir-chain-attack-sync-reaches-the-client-in-the-missiles-name")]
    public static void ChainAttackSyncFollowsTheAttackMissile()
    {
        using var match = new Match("Sivir");
        match.Learn(3);
        match.FarFoe.Position = new Position2(7450, 7000);
        var wire = Observe(match, Team.Order, match.Hero);
        match.Advance(0.3);
        match.Refill();
        TestAssert.True(match.Cast(1), "W toggles on");
        match.Advance(0.2);
        wire.Packets.Clear();
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "attack");
        match.Advance(1.2);
        // A chain attack leaves as a cast of the override spell flagged as an auto attack (extra slot 42), not as a 0x0D.
        TestAssert.True(wire.Packets.All(item => item.Packet.Plaintext.Span[0] != 0x0D), "no 0x0D for a chain attack");
        var attack = wire.Packets.First(item => item.Packet.Plaintext.Span[0] == 0xBE);
        var swing = match.Log.Casts.Last(item => item.Slot == SpellSystem.ExtraSpellSlotStart);
        TestAssert.Equal((byte)1, swing.Flags, "the cast is flagged as an auto attack");
        TestAssert.Equal(1, swing.Targets.Count, "the cast names the attacked unit only");
        var missile = swing.MissileNetId;
        TestAssert.True(missile != 0, "the attack carries its missile");
        var syncs = wire.Packets.Where(item => item.Packet.Plaintext.Span[0] == 0x70).ToArray();
        TestAssert.Equal(2, syncs.Length, "the list is sent twice");
        TestAssert.Near(match.Hero.AttackWindup + BbChainSync.FirstMargin, syncs[0].At - attack.At, 0.1, "one windup and a margin after the attack");
        foreach (var (_, packet) in syncs)
        {
            var bytes = packet.Plaintext.Span;
            TestAssert.Equal(missile, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]), "FromID is the attack's missile");
            TestAssert.Equal(2, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes[5..]), "the target and the one hop");
            TestAssert.Equal(match.Hero.NetId, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[9..]), "owner is Sivir");
            TestAssert.Equal(match.Foe.NetId, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[13..]), "first hop is the target");
            TestAssert.Equal(match.FarFoe.NetId, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[17..]), "then the other hero");
        }
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    /// <summary>
    /// A ranged attack's missile whose target dies in flight is not destroyed: the client's missile flies on to the corpse (and, for
    /// a chain missile, goes on down its list from there). The server keeps the pending hit flying, lands it on the corpse without
    /// damage and starts the chain from there.
    /// </summary>
    [Check("scripting.bb-sivir-chain-attack-bounces-off-a-corpse-and-is-not-destroyed")]
    public static void RicochetGoesOnWhenItsFirstTargetDiesInFlight()
    {
        using var match = new Match("Sivir");
        match.Learn(3);
        var m1 = Enemy(match, "M1", new Position2(7450, 7000));
        Enemy(match, "M2", new Position2(7700, 7000));
        Enemy(match, "M3", new Position2(7900, 7000));
        match.Refill();
        TestAssert.True(match.Cast(1), "W toggles on");
        match.Hero.Mana = match.Hero.MaxMana;
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "attack");
        var launched = false;
        for (var frame = 0; frame < 60 && !launched; frame++)
        {
            match.Advance(Step);
            launched = match.Runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == match.Hero.NetId && hit.WindupRemaining <= 0.0 && hit.MissileNetId != 0);
        }
        TestAssert.True(launched, "the swing's missile has left");
        TestAssert.True(match.Runtime.Combat.Kill(match.Foe, match.Hero.NetId), "the target dies under the missile");
        TestAssert.True(match.Runtime.Combat.PendingHits.Any(hit => hit.SourceNetId == match.Hero.NetId), "the missile flies on to the corpse");
        match.Advance(1.8);
        TestAssert.Equal(0, HitsOn(match, "Foe").Count, "the corpse takes no hit");
        TestAssert.Equal(1, HitsOn(match, "M1").Count, "the orb went on from the corpse: M1");
        TestAssert.Equal(1, HitsOn(match, "M2").Count, "M2");
        TestAssert.Equal(1, HitsOn(match, "M3").Count, "M3");
        TestAssert.True(HitsOn(match, "M1")[0].At < HitsOn(match, "M2")[0].At && HitsOn(match, "M2")[0].At < HitsOn(match, "M3")[0].At, "in order");
        TestAssert.Equal(0, match.Log.DestroyedMissiles.Count, "the attack's missile was never destroyed");
        TestAssert.Equal(0, match.Runtime.Spells.SharedProjectiles.Count, "the orb is gone");
        TestAssert.Equal(0, match.Runtime.Combat.PendingHits.Count, "and no hit is left over");
        TestAssert.True(m1.Alive, "M1 survived its one bounce");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    /// <summary>
    /// Batch 348: an override swing pays its spell's <c>ManaCost</c> at its cast frame. A 0x0D cannot carry an override (its handler
    /// takes the character's own attack, GetBasicAttack, slots 0x40..0x46), so the swing is a cast at the extra slot
    /// (42), and the shared cost routine <c>ApplyCostsOfSpell</c> that the cast-and-serve routine runs at the cast frame spends
    /// the cast's <c>ManaCost</c> (the cast info's cost field) for any slot but 0x40..0x46. The cast tells the client
    /// the six, and a swing stopped in its windup never reaches its cast frame and costs nothing. Ricochet's own script charges nothing; patch
    /// history: "Per-attack cost increased to 6 from 4" (1 May 2009, wiki, Sivir patch history).
    /// </summary>
    [Check("scripting.bb-sivir-ricochet-swing-pays-six-mana-at-its-cast-frame")]
    public static void RicochetSwingPaysAtItsCastFrame()
    {
        using var match = new Match("Sivir");
        match.Learn(3);
        match.Refill();
        TestAssert.True(match.Cast(1), "W toggles on");
        var start = match.Hero.MaxMana - 30.0;
        match.Hero.Mana = start;
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "attack");
        TestAssert.Near(start, match.Hero.Mana, 1e-6, "nothing at the swing's start");
        var swing = match.Log.Casts.Last();
        TestAssert.Equal(42, (int)swing.Slot, "the swing is a cast at the extra slot");
        // 132 RicochetAttack.luaobj ManaCost is 0 (126: 6); the W toggle is what costs mana now (Ricochet 40), so a swing spends nothing.
        TestAssert.Near(0.0, swing.ManaCost, 1e-6, "the cast carries no mana cost (132)");
        match.Advance(match.Hero.AttackWindup + Step);
        TestAssert.True(match.Hero.Mana > start - 1.0, $"no mana at the cast frame (mana {match.Hero.Mana} from {start})");

        match.Advance(2.0);
        match.Hero.Mana = start;
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "a second attack");
        match.Runtime.Combat.InterruptBasicAttack(match.Hero);
        match.Advance(match.Hero.AttackWindup + Step);
        TestAssert.True(match.Hero.Mana > start - 1.0, $"a swing stopped in its windup costs nothing (mana {match.Hero.Mana} from {start})");
        TestAssert.Equal("none", match.Module.ScriptFailures.Count == 0 ? "none" : "failures", "no script failure");
    }

    [Check("scripting.bb-sivir-w-ricochet-toggles-and-bounces-her-attacks")]
    public static void RicochetOverridesHerAttackAndTheOrbBouncesWithDecay()
    {
        var decay = Blocks(Script("RicochetAttack"), "TargetExecute").First(block => block.Function == "BBMath" && block.Text("DestVar") == "DamagePercent");
        // 132 RicochetAttack.luaobj: each hop keeps 80 percent (126: 75 percent).
        TestAssert.Near(0.8, decay.Number("Src2Value")!.Value, 1e-12, "RicochetAttack.luaobj: 20 percent less per hop");
        var attack = GameData.Default.Spell("RicochetAttack", 5);
        TestAssert.Near(1100.0, attack.MissileSpeed, 1e-9, "RicochetAttack.inibin MissileSpeed (132; 126: 900)");
        TestAssert.Equal(2, BbChampionModule.CastType(attack), "RicochetAttack is a chain missile");
        // 132 RicochetAttack.inibin costs nothing; the W toggle (Ricochet.inibin) pays 40 and has cooldowns 7/6/5 (126: 6 a swing, none).
        TestAssert.Equal([0.0, 0.0, 0.0, 0.0, 0.0], attack.ManaCosts.ToArray(), "RicochetAttack.inibin ManaCost: none (132)");
        var ricochet = GameData.Default.Spell("Ricochet", 5);
        TestAssert.Equal([7.0, 6.0, 5.0, 4.0, 3.0], ricochet.Cooldowns.ToArray(), "Ricochet.inibin cooldowns (132)");
        TestAssert.Equal([40.0, 40.0, 40.0, 40.0, 40.0], ricochet.ManaCosts.ToArray(), "and its cost is 40 (132)");

        // 1.0.0.132 (batch 788): Ricochet is no longer a toggle. Ricochet.luaobj SelfExecute adds the Ricochet buff for 6 s; its OnBuffActivate
        // overrides the attack and seals W, its BuffOnLaunchAttack removes the buff when the next swing leaves, and its OnBuffDeactivate removes
        // the override, opens W and starts W's cooldown (SpellCooldown 9 / 7.5 / 6 / 4.5 / 3 x (1 + the cooldown modifier)).
        TestAssert.Equal([9.0, 7.5, 6.0, 4.5, 3.0], ByLevel("Ricochet", "SelfExecute", "SpellCooldown"), "Ricochet.luaobj: the cooldown W starts when the buff ends");
        using var match = new Match("Sivir");
        match.Learn(3);
        var m1 = Enemy(match, "M1", new Position2(7450, 7000));
        var m2 = Enemy(match, "M2", new Position2(7700, 7000));
        var m3 = Enemy(match, "M3", new Position2(7900, 7000));
        var m4 = Enemy(match, "M4", new Position2(8100, 7000));
        match.Refill();
        var mana = match.Hero.Mana;
        TestAssert.True(match.Cast(1), "W is cast");
        TestAssert.Near(mana - 40.0, match.Hero.Mana, 1e-6, "40 mana (132 Ricochet cost)");
        // 132 Ricochet.luaobj: BUFF_CombatEnchancer (126: Aura).
        TestAssert.True(Buff(match.Hero, "Ricochet") is { Type: BuffType.CombatEnhancer }, "the buff: " + Names(match.Hero));
        TestAssert.True(match.Hero.ScriptAttackOverride is { Slot: 42, SpellName: "RicochetAttack", Level: 3 }, "the attack is the extra spell 1 at the W level");
        TestAssert.True(match.Log.AttackSlots.Any(line => line.EndsWith(" 42", StringComparison.Ordinal)), "the client is told: " + string.Join("|", match.Log.AttackSlots));
        TestAssert.Equal(1, match.Log.Casts.Count(item => item.Slot == 1), "one cast announcement");
        TestAssert.True(match.Hero.SpellSeals.IsSealed(false, 1), "W is sealed while the buff is up");
        TestAssert.False(match.Cast(1), "so a second press is refused (126 toggled it off)");

        // One swing at the Foe (300 away). 132 RicochetAttack.luaobj ChainMissileParameters MaximumHits is 6 at every rank (126: 2 to 6, four at
        // rank 3), so the chain is the target and the four others in reach, the nearest not hit yet each time.
        match.Hero.Mana = match.Hero.MaxMana;
        var before = match.Hero.Mana;
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "attack");
        TestAssert.Near(before, match.Hero.Mana, 1e-6, "nothing at the swing's start");
        TestAssert.Equal(0, match.Log.ChainSyncs.Count, "the list does not leave with the swing: the client has no missile yet");
        for (var frame = 0; frame < 60 && Buff(match.Hero, "Ricochet") is not null; frame++) match.Advance(Step);
        TestAssert.True(Buff(match.Hero, "Ricochet") is null, "the swing leaving spends the buff");
        TestAssert.True(match.Hero.ScriptAttackOverride is null && !match.Hero.SpellSeals.IsSealed(false, 1), "the normal attack is back and W is open");
        TestAssert.Near(6.0, match.Hero.SpellCooldowns[1], Step + 1e-6, "W's cooldown starts: 6 s at rank 3");
        match.Advance(1.6);
        TestAssert.Equal(2, match.Log.ChainSyncs.Count, "the list goes out twice once the attack's missile exists");
        foreach (var sync in match.Log.ChainSyncs)
            TestAssert.Equal([match.Foe.NetId, m1.NetId, m2.NetId, m3.NetId, m4.NetId], sync.Targets, "the client's chain: the target, then the nearest each time");
        // The orb in flight is the RicochetAttack it left as, though the override ended at the launch (BbHookRouter.TakeLaunchedAttack).
        var baseAd = GameData.Default.StatsAtLevel(GameData.Default.Champion("Sivir"), match.Hero.Level).AttackDamage;
        var primary = HitsOn(match, "Foe");
        TestAssert.Equal(1, primary.Count, "the Foe takes the attack once");
        // 132 RicochetAttack.luaobj TargetExecute: DamageToDeal is the W's rank base (20 / 35 / 50 / 65 / 80) plus the total attack damage; the
        // first target takes it as the attack (TargetNum 1, DAMAGESOURCE_ATTACK), each hop after it keeps 80 percent more of it (PROC).
        NearF32(50.0 + baseAd, primary[0].Raw, "the primary: 50 + the attack damage at rank 3");
        var hops = new[] { m1, m2, m3, m4 }.Select(unit => HitsOn(match, unit.Name)).ToArray();
        for (var hop = 0; hop < hops.Length; hop++)
        {
            TestAssert.Equal(1, hops[hop].Count, $"M{hop + 1} once");
            NearF32((50.0 + baseAd) * Math.Pow(0.8, hop + 1), hops[hop][0].Raw, $"hop {hop + 2}: 0.8^{hop + 1} of (50 + the attack damage)");
            TestAssert.True(hops[hop][0].Kind == "Proc", "the bounces are procs");
            if (hop > 0) TestAssert.True(hops[hop - 1][0].At < hops[hop][0].At, "in order");
        }
        TestAssert.Near(150.0 / 1100.0, hops[0][0].At - primary[0].At, 0.1, "150 away at 1100 (132)");
        TestAssert.Equal(0, match.Runtime.Spells.SharedProjectiles.Count, "the orb is gone");

        // The next attack is a plain one: no list, no bounce.
        match.Hero.AttackCooldown = 0.0;
        var syncs = match.Log.ChainSyncs.Count;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "the next attack");
        match.Advance(1.0);
        TestAssert.Equal(syncs, match.Log.ChainSyncs.Count, "no chain for a plain attack");
        TestAssert.Equal(1, HitsOn(match, "M1").Count, "nothing bounced to M1");

        // W again after its cooldown, left unused: the buff ends after its 6 s, the normal attack comes back and the cooldown starts then.
        match.Advance(5.0);
        match.Refill();
        TestAssert.True(match.Cast(1), "W again after its cooldown");
        TestAssert.True(Buff(match.Hero, "Ricochet") is not null, "on");
        match.Advance(6.0 + Step);
        TestAssert.True(Buff(match.Hero, "Ricochet") is null && match.Hero.ScriptAttackOverride is null, "after 6 s unused: the normal attack is back");
        TestAssert.True(match.Log.AttackSlots.Last().EndsWith(" -1", StringComparison.Ordinal), "the client is told: " + match.Log.AttackSlots.Last());
        TestAssert.Near(6.0, match.Hero.SpellCooldowns[1], 2.0 * Step, "and W's cooldown starts then");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Take(2).Select(f => f.Message)));
    }

    // ------------------------------------------------------------------------------------------------------------
    // E Spell Shield
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-sivir-e-spell-shield-blocks-one-spell-and-restores-mana")]
    public static void SpellShieldAbsorbsAnEnemySpellOnce()
    {
        var cast = Blocks(Script("SpellShield"), "SelfExecute").First(block => block.Function == "BBSpellBuffAdd");
        TestAssert.Near(3.0, cast.Number("Duration")!.Value, 1e-12, "SpellShield.luaobj: the buff lasts 3 seconds");
        var refund = Blocks(Script("SpellShield"), "BuffOnUpdateStats").First(block => block.Function == "BBIncPAR");
        TestAssert.Near(150.0, refund.Number("Delta")!.Value, 1e-12, "SpellShield.luaobj: 150 mana back when it pops");
        var data = GameData.Default.Spell("SpellShield", 5);
        TestAssert.Equal([22.0, 19.0, 16.0, 13.0, 10.0], data.Cooldowns.ToArray(), "Spell Shield cooldowns");
        TestAssert.Equal([75.0, 75.0, 75.0, 75.0, 75.0], data.ManaCosts.ToArray(), "Spell Shield mana costs");
        TestAssert.Equal(150.0, Amberline.Simulation.Spells.Champions.ChampionSpellSupport.RawNumber(data, "SpellData*Effect2Level1Amount"), "SpellShield.inibin Effect2 is the 150 mana");

        using var match = new Match("Sivir", foeChampion: "Annie");
        match.Learn(3);
        match.Foe.SpellRanks[0] = 1;
        match.Refill();
        TestAssert.True(match.Cast(2), "E cast");
        TestAssert.True(Buff(match.Hero, "SpellShield") is { Type: BuffType.CombatEnhancer }, "the shield: " + Names(match.Hero));
        TestAssert.Near(16.0, match.Hero.SpellCooldowns[2], 1e-6, "rank 3 cooldown 16 s");
        match.Hero.Mana = 100.0;
        match.Foe.Mana = match.Foe.MaxMana;
        // An enemy spell (Disintegrate) hits the shield: no damage, the shield is gone, 150 mana is back.
        var health = match.Hero.Health;
        TestAssert.True(match.Cast(0, match.Hero, caster: match.Foe), "the enemy casts Disintegrate");
        match.Advance(1.2);
        // The popped shield zeroes the hit, which is still recorded (18 October 2026, P4: the hand-written Annie pops it before her
        // damage like a script spell does at TargetExecute, instead of skipping the damage by hand).
        TestAssert.Near(0.0, HitsOn(match, "Sivir").Sum(hit => hit.Dealt), 0.0, "the first spell does no damage: " + string.Join(" | ", match.Log.Hits));
        TestAssert.True(Buff(match.Hero, "SpellShield") is null, "the shield popped");
        TestAssert.Near(Math.Min(match.Hero.MaxMana, 250.0), match.Hero.Mana, 1.5, "150 mana came back (regeneration aside)");
        TestAssert.Near(health, match.Hero.Health, 1.0, "no health lost");
        TestAssert.True(match.Log.Effects.Any(line => line.Contains("SpellEffect_proc.troy", StringComparison.OrdinalIgnoreCase)), "the proc effect");
        // The next one is an ordinary hit.
        match.Foe.SpellCooldowns[0] = 0.0;
        match.Foe.Mana = match.Foe.MaxMana;
        TestAssert.True(match.Cast(0, match.Hero, caster: match.Foe), "a second Disintegrate");
        match.Advance(1.2);
        TestAssert.True(HitsOn(match, "Sivir").Any(hit => hit.Dealt > 0.0), "the second spell lands");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-sivir-e-spell-shield-ignores-attacks-and-allies-and-expires")]
    public static void SpellShieldIgnoresBasicAttacksAndTimesOut()
    {
        using var match = new Match("Sivir", foeChampion: "Annie");
        match.Learn(1);
        match.Refill();
        TestAssert.True(match.Cast(2), "E cast");
        // A basic attack does not pop it.
        match.Foe.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Foe, match.Hero), "the enemy attacks");
        match.Advance(1.0);
        TestAssert.True(Buff(match.Hero, "SpellShield") is not null, "an attack does not pop the shield");
        // An ally's spell does not either (the script tests Owner and Attacker for different teams).
        match.Ally.Mana = match.Ally.MaxMana;
        match.Log.Hits.Clear();
        match.Advance(2.5);
        TestAssert.True(Buff(match.Hero, "SpellShield") is null, "after its three seconds the shield ends");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // R On The Hunt
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-sivir-r-on-the-hunt-speeds-her-and-nearby-allies")]
    public static void OnTheHuntGivesHerTheFullBuffAndAlliesTheAura()
    {
        var self = Blocks(Script("OnTheHunt"), "TargetExecute").Where(block => block.Function == "BBSetVarInTable").ToArray();
        TestAssert.Equal([0.25, 0.25, 0.25], Numbers(self[0].Param("SrcValueByLevel")!), "OnTheHunt.luaobj MoveSpeedMod");
        // 132 OnTheHunt.luaobj: her attack speed 0.3/0.45/0.6 (126: 0.3/0.6/0.9), the allies' 0.15/0.225/0.3 (126: 0.1/0.2/0.3).
        TestAssert.Equal([0.3, 0.45, 0.6], Numbers(self[1].Param("SrcValueByLevel")!), "OnTheHunt.luaobj her AttackSpeedMod");
        TestAssert.Equal([0.15, 0.225, 0.3], Numbers(self[2].Param("SrcValueByLevel")!), "OnTheHunt.luaobj allies' AttackSpeedMod");
        var data = GameData.Default.Spell("OnTheHunt", 3);
        // 132 OnTheHunt.inibin cooldowns 100/90/80 (126: 90 each).
        TestAssert.Equal([100.0, 90.0, 80.0], data.Cooldowns.ToArray(), "On The Hunt cooldowns");
        TestAssert.Equal([100.0, 100.0, 100.0], data.ManaCosts.ToArray(), "On The Hunt mana costs");
        TestAssert.Near(1000.0, data.CastRadius, 1e-9, "On The Hunt radius");
        // Effect1 is her attack speed in percent: 30/45/60 (126: 30/60/90).
        for (var rank = 1; rank <= 3; rank++)
            TestAssert.Equal(15.0 * rank + 15.0, data.EffectNumber(1, rank), $"OnTheHunt.inibin Effect1 rank {rank} is her attack speed in percent");

        using var match = new Match("Sivir");
        match.Learn(2);
        match.Ally.Position = new Position2(7500, 7000);
        match.Foe.Position = new Position2(7300, 7000);
        match.FarFoe.Position = new Position2(7400, 7000);
        match.Refill();
        var speed = match.Hero.Speed;
        var allySpeed = match.Ally.Speed;
        var attackSpeed = match.Hero.AttackSpeedMod;
        var allyAttackSpeed = match.Ally.AttackSpeedMod;
        TestAssert.True(match.Cast(3), "R cast");
        match.Advance(0.6);
        var hunt = Buff(match.Hero, "OnTheHunt");
        TestAssert.True(hunt is { Type: BuffType.Haste }, "her buff: " + Names(match.Hero));
        TestAssert.Near(match.Now + 14.4, hunt!.ExpiresAt, 0.8, "fifteen seconds");
        TestAssert.Near(0.25, hunt.Payload.MoveSpeedPercent, 1e-9, "25 percent faster");
        TestAssert.Near(0.45, hunt.Payload.AttackSpeedMod, 1e-9, "rank 2: 45 percent attack speed for her (132)");
        TestAssert.True(match.Hero.Speed > speed * 1.2, "the speed shows");
        var aura = Buff(match.Ally, "OnTheHuntAuraBuff");
        // 132 OnTheHuntAuraBuff is BUFF_CombatEnchancer (126: BUFF_Haste); her own buff stays Haste.
        TestAssert.True(aura is { Type: BuffType.CombatEnhancer }, "an ally within 1000: " + Names(match.Ally));
        TestAssert.Near(0.25, aura!.Payload.MoveSpeedPercent, 1e-9, "the same speed");
        TestAssert.Near(0.225, aura.Payload.AttackSpeedMod, 1e-9, "rank 2: 22.5 percent attack speed for allies (132)");
        TestAssert.True(match.Ally.Speed > allySpeed * 1.2, "ally speed shows");
        TestAssert.True(match.Ally.AttackSpeedMod > allyAttackSpeed, "ally attack speed shows");
        TestAssert.True(Buff(match.Foe, "OnTheHuntAuraBuff") is null && Buff(match.Foe, "OnTheHunt") is null, "enemies get nothing");
        TestAssert.True(match.Hero.AttackSpeedMod > attackSpeed, "her attack speed shows");
        TestAssert.True(match.Log.Assists.Any(line => line.Contains("Sivir->Ally", StringComparison.Ordinal)), "assist marker on the ally");
        // An ally beyond 1000 is outside.
        match.Hero.SpellCooldowns[3] = 0.0;
        match.Ally.Position = new Position2(9500, 7000);
        match.Advance(15.5);
        TestAssert.True(Buff(match.Hero, "OnTheHunt") is null && Buff(match.Ally, "OnTheHuntAuraBuff") is null, "both end");
        match.Refill();
        TestAssert.True(match.Cast(3), "second R");
        match.Advance(0.6);
        TestAssert.True(Buff(match.Ally, "OnTheHuntAuraBuff") is null, "beyond 1000 gets nothing");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Passive Fleet of Foot and the tooltip
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-sivir-passive-dodges-only-while-moving")]
    public static void FleetOfFootGivesDodgeWhileOnTheMove()
    {
        // 132: Fleet of Foot (126: a dodge by level from CharScriptSivir's SetVarsByLevel, only while moving) is gone. The 132 passive is
        // SivirPassive.luaobj: when her basic attack hits a champion, its BuffOnHitUnit adds SivirPassiveSpeed, +50 flat movement speed
        // (SivirPassiveSpeed.luaobj BBIncStat) for two seconds. The check keeps its 126 name and asserts the 132 passive.
        TestAssert.Near(50.0, Blocks(Script("SivirPassiveSpeed"), "OnBuffActivate").First(item => item.Function == "BBIncStat").Number("Delta")!.Value,
            1e-12, "SivirPassiveSpeed.luaobj: +50 flat movement speed");
        using var match = new Match("Sivir");
        TestAssert.True(Buff(match.Hero, "SivirPassive") is not null, "CharOnActivate added the passive: " + Names(match.Hero));
        match.Foe.Position = new Position2(7300, 7000);
        match.Hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "a basic attack on a champion");
        match.Advance(1.0);
        TestAssert.True(Buff(match.Hero, "SivirPassiveSpeed") is not null, "the champion hit gives the speed buff");
        TestAssert.Near(50.0, match.Hero.FlatMovementSpeedMod, 1e-9, "+50 flat movement speed");
        TestAssert.Near(0.0, match.Hero.Dodge, 1e-9, "no dodge from the 132 passive");
        match.Advance(2.5);
        TestAssert.True(Buff(match.Hero, "SivirPassiveSpeed") is null, "after two seconds the speed ends");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-sivir-passive-publishes-the-spell-tooltip")]
    public static void CharacterScriptWritesTheBonusDamageTooltip()
    {
        using var match = new Match("Sivir");
        match.Hero.Inventory = [new InventorySlot(1036), null, null, null, null, null];
        match.Runtime.HeroStats.Refresh(match.Hero, push: false);
        match.Advance(0.2);
        TestAssert.True(match.Hero.ScriptSpellTooltips.ContainsKey(0), "the character script wrote slot 0's tooltip");
        // 132 CharScriptSivir.luaobj writes 1.1 x the bonus attack damage (126: 1.0 x).
        TestAssert.Near(11.0, match.Hero.ScriptSpellTooltips[0][0], 1e-6, "Spiral Blade's tooltip: 1.1 x the bonus attack damage");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Registration, ranks and production safety
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-sivir-production-installs-the-sink-and-her-passives")]
    public static void SivirJoinsTheMatchThroughTheSharedScriptSink()
    {
        using var match = new Match("Sivir");
        TestAssert.True(((Amberline.Simulation.Runtime.MatchGameplayHooks)match.Runtime.Dependencies.ChampionHooks).ScriptHooks is not null,
            "a Sivir in the roster installs the sink when her hero is created");
        TestAssert.True(match.Module.IsBound, "the module is bound to the match");
        TestAssert.Equal(1, match.Hero.Buffs.Count(buff => buff.Name == "ChampionChampionDelta"), "the shared system buffs are applied once, natively");
        TestAssert.Equal(1, match.Hero.Buffs.Count(buff => buff.Name == "APBonusDamageToTowers"),
            "batch 203: the champion's shipped CharOnActivate add is the one tower buff; nothing refuses it any more");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    [Check("scripting.bb-sivir-ranks-levels-and-cooldowns")]
    public static void RankGatesLevelUpsAndCooldownsFollowTheData()
    {
        using var match = new Match("Sivir");
        var sivir = match.Hero;
        sivir.SkillPoints = 20;
        TestAssert.True(match.Runtime.Spells.RankUp(sivir, 0), "Q rank 1 at level 1");
        TestAssert.False(match.Runtime.Spells.RankUp(sivir, 0), "Q rank 2 needs level 3");
        TestAssert.False(match.Runtime.Spells.RankUp(sivir, 3), "R needs level 6");
        TestAssert.True(match.Runtime.Combat.AwardHeroProgress(sivir, 0.0, 100000.0), "experience");
        for (var slot = 0; slot < 4; slot++)
            while (match.Runtime.Spells.RankUp(sivir, slot)) { }
        TestAssert.Equal([5, 5, 5, 3], sivir.SpellRanks.ToArray(), "all ranks reachable at level 18");
        // Spell Shield and Spiral Blade through the cooldown reduction.
        sivir.SpellRanks[2] = 4;
        match.Refill();
        sivir.CooldownMod = -0.1;
        TestAssert.True(match.Cast(2), "E cast");
        TestAssert.Near(13.0 * 0.9, sivir.SpellCooldowns[2], 1e-6, "rank 4 Spell Shield cooldown with 10 percent reduction");
        match.Advance(0.5);
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Sivir bots in a headless match
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-sivir-bots-play-a-six-minute-headless-match", Serial = true)]
    public static void SivirBotsPlayWithoutScriptExceptionsOrPacketViolations()
    {
        var root = Path.Combine(Path.GetTempPath(), "league-sivir-match-" + Guid.NewGuid().ToString("N"));
        try
        {
            string[] roster = ["Sivir", "Annie", "Janna", "Ezreal", "Vayne", "Sivir", "Orianna", "Blitzcrank", "Nidalee", "Shaco"];
            var casts = new Dictionary<(uint Hero, int Slot), int>();
            var previous = new Dictionary<(uint Hero, int Slot), double>();
            var toggled = new Dictionary<uint, bool>();
            var failures = new List<string>();
            var options = new Amberline.Tools.Diagnostics.HeadlessMatchOptions(20261016, Path.Combine(root, "m"), 6.0, roster)
            {
                OnStep = runtime =>
                {
                    foreach (var hero in runtime.World.Heroes().Where(hero => hero.ChampionName == "Sivir"))
                    {
                        for (var slot = 0; slot < 4; slot++)
                        {
                            var key = (hero.NetId, slot);
                            var now = hero.SpellCooldowns[slot];
                            if (previous.TryGetValue(key, out var before) && now > before + 0.5) casts[key] = casts.GetValueOrDefault(key) + 1;
                            previous[key] = now;
                        }
                        // Ricochet has no cooldown: the bot's cast of it shows as the toggle going on or off.
                        var on = hero.Buffs.Any(buff => buff.Name == "Ricochet");
                        var wasOn = toggled.GetValueOrDefault(hero.NetId);
                        if (on != wasOn) casts[(hero.NetId, 1)] = casts.GetValueOrDefault((hero.NetId, 1)) + 1;
                        toggled[hero.NetId] = on;
                    }
                    var module = (BbChampionModule)runtime.Dependencies.ChampionDispatcher.FindModule("Sivir")!;
                    foreach (var failure in module.ScriptFailures.Skip(failures.Count))
                        failures.Add($"{failure.Script}/{failure.Hook}/{failure.BlockPath}: {failure.Message}");
                },
            };
            var report = Amberline.Tools.Diagnostics.HeadlessMatch.Run(options);
            TestAssert.Equal("cap", report.Outcome, "six game minutes end at the cap, not in a crash");
            TestAssert.Equal(0L, report.PacketViolations, "the packet-stream checker finds no violation");
            TestAssert.Equal(0L, report.LifecycleViolations, "the lifecycle monitor stays quiet");
            TestAssert.Equal(0, failures.Count, "no script exception: " + string.Join(" | ", failures.Take(3)));
            var log = File.ReadAllText(Path.Combine(root, "m", "session.jsonl"));
            TestAssert.False(log.Contains("script.exception", StringComparison.Ordinal), "no script.exception line in the session log");
            TestAssert.True(casts.Values.Sum() > 0, "the Sivir bots cast their spells: " +
                string.Join(", ", casts.OrderBy(pair => pair.Key.Slot).Select(pair => $"slot {pair.Key.Slot} x{pair.Value}")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
