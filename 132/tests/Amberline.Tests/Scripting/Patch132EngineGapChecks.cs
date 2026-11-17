using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 833: the engine gaps the 1.0.0.132 parity audit (batch 832, fix batch A) found between the 132 server and the changes of patches
/// 1.0.0.127 to 1.0.0.132. Each check names its wiki item; the expected values are the shipped 132 files' and the 132 client's (lol132.exe).
/// </summary>
public static class Patch132EngineGapChecks
{
    // ------------------------------------------------------------------------------------------------------------
    // Map 8 harness (the zombie-timer check's production match: Yorick, an ally and a Chaos killer)
    // ------------------------------------------------------------------------------------------------------------

    private static MatchRuntime MapEight(params SessionParticipantDefinition[] roster)
    {
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.Load(GameDataPaths.Default.ForMap(GameMaps.CrystalScar)), GameData.Default,
            readySettleSeconds: 0.0, roster: roster, mapId: GameMaps.CrystalScar);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.AfkTrackingDisabled = true;
        runtime.Session.CreateWorld();
        for (var index = 0; index < roster.Length; index++)
        {
            var connection = 100L + index;
            TestAssert.True(runtime.Session.Authenticate(connection, runtime.Session.BuildKeyCheck(roster[index].PlayerId)) is not null,
                $"seat {index} authenticates");
            runtime.Session.Spawn(connection);
            TestAssert.True(runtime.Session.MarkReady(connection), $"seat {index} is ready");
        }
        runtime.Session.RunSchedulers();
        TestAssert.True(runtime.Session.Started, "the map 8 match starts");
        return runtime;
    }

    private static Entity Nexus(MatchRuntime runtime, Team team) =>
        runtime.World.Entities.Values.Single(unit => unit.Kind == EntityKind.Nexus && unit.Team == team && unit.Alive);

    /// <summary>The float of the last packet of <paramref name="opcode"/> about <paramref name="netId"/> at <paramref name="offset"/>; null when none came.</summary>
    private static float? LastFloat(WireCapture capture, byte opcode, uint netId, int offset)
    {
        foreach (var (_, packet) in Enumerable.Reverse(capture.Packets))
        {
            var span = packet.Plaintext.Span;
            if (span.Length >= offset + 4 && span[0] == opcode && BinaryPrimitives.ReadUInt32LittleEndian(span[1..]) == netId)
                return BinaryPrimitives.ReadSingleLittleEndian(span[offset..]);
        }
        return null;
    }

    // ------------------------------------------------------------------------------------------------------------
    // 131.53: NpcForceDead carries the time to respawn
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// V1.0.0.131 hotfix: "Fixed a bug where death timers weren't displaying the correct time until respawn". lol132.exe's ForceDead handler
    /// stores the packet's float at byte 5 in the hero's DeathDuration (+0x2BF8) and then
    /// calls the death routine, whose hero override (vtable slot 0x258) stamps the death time (+0x29C0) with the game time; the HUD
    /// shows ceil(DeathDuration - (now - stamp)). So the float is the time from the zombie life's end
    /// to the respawn. On the 132 Crystal Scar that is the whole timer, which starts at the life's end (V1.0.0.131, StartDeathTimerForZombies 0).
    /// </summary>
    [Check("dominion.p132-force-dead-carries-the-time-to-respawn", TimeoutSeconds = 300)]
    public static void ForceDeadCarriesTheTimeToRespawn()
    {
        var runtime = MapEight(
            new SessionParticipantDefinition(1, 0, "Order", "Yorick", Team.Order),
            new SessionParticipantDefinition(2, 1, "Order", "Annie", Team.Order),
            new SessionParticipantDefinition(3, 2, "Chaos", "Orianna", Team.Chaos));
        try
        {
            runtime.Tick(30.0);
            var seats = runtime.Session.Participants;
            var yorick = seats.Single(seat => seat.Hero!.ChampionName == "Yorick").Hero!;
            var allySeat = seats.Single(seat => seat.Hero!.ChampionName == "Annie");
            var ally = allySeat.Hero!;
            var killer = seats.Single(seat => seat.Team == Team.Chaos).Hero!;
            var capture = new WireCapture(() => runtime.Combat.CurrentTime);
            runtime.Delivery.AddObserver(new ReplicationObserver(allySeat.PlayerId, allySeat.Team, ally.NetId, capture, ready: true,
                view: allySeat.View));
            var module = runtime.Dependencies.ChampionDispatcher.FindModule("Yorick") as BbChampionModule
                ?? throw new InvalidOperationException("the match has no script-driven Yorick");
            TestAssert.True(module.Buffs!.Add(new BbBuffRequest(ally, yorick, "YorickReviveAllySelf", (int)BbBuffAddType.BUFF_REPLACE_EXISTING,
                (int)BbBuffType.BUFF_CombatEnchancer, 1, 10.0, 0.0, false, new BbTable())) is not null, "the Omen of Death marker is on the ally");
            runtime.Combat.ApplyHit(killer.NetId, ally.NetId, 100000, DamageType.True, DamageSourceKind.Spell);
            TestAssert.True(ally.YorickRevival is not null && ally.Alive, "the ally's death became a zombie life");
            runtime.Tick(1.0);
            var end = runtime.Combat.CurrentTime;
            TestAssert.True(runtime.Combat.Kill(ally, killer.NetId) is false && !ally.Alive && ally.YorickRevival is null, "a kill ends the zombie life");
            runtime.Delivery.Flush();
            var remaining = ally.RespawnAt - end;
            TestAssert.True(remaining > 10.0, $"the death timer runs from the life's end ({remaining:F3} s)");
            var sent = LastFloat(capture, 0x1E, ally.NetId, 5);
            TestAssert.True(sent is not null, "NpcForceDead (0x1E) reaches the ally's client");
            TestAssert.Near(remaining, sent!.Value, 1e-3, $"ForceDead's DeathDuration is the time to respawn ({remaining:F3} s), not 0");
        }
        finally
        {
            runtime.Dispose();
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // 128.22: Dominion's respawn adjustment is +2/-2 s by the nexus lead
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// V1.0.0.128: "Respawn time adjustments are now +2/-2 seconds regardless of the size in score difference." The adjustment is the
    /// server's (no shipped script carries it). The
    /// rule it changed is Riot's explanation of 21 October 2011 (Brackhar, "Dominion Respawn Explained", Surrender at 20, 25 October 2011): a
    /// one-point nexus lead adds 2 s to the leading team's minimum respawn time and half of it to its 12 s window, and takes 2 s off the
    /// trailing team's; the larger leads' +3/-3 and +4/-4 are what 1.0.0.128 removed. The death packet and the timer read one value.
    /// </summary>
    [Check("dominion.p132-respawn-adjusts-two-seconds-by-the-nexus-lead", TimeoutSeconds = 300)]
    public static void RespawnAdjustsTwoSecondsByTheNexusLead()
    {
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        try
        {
            using var match = new Match("Annie", navigation: NavigationGrid.Load(GameDataPaths.Default.ForMap(GameMaps.CrystalScar)),
                mapId: GameMaps.CrystalScar);
            var runtime = match.Runtime;
            var capture = Observe(match, Team.Order, match.Hero);
            match.Advance(30.0);
            void Score(double order, double chaos)
            {
                Nexus(runtime, Team.Order).Health = order;
                Nexus(runtime, Team.Chaos).Health = chaos;
            }
            double Kill(Entity victim, uint killer)
            {
                runtime.Combat.ApplyHit(killer, victim.NetId, 1e6, DamageType.True, DamageSourceKind.Spell);
                TestAssert.False(victim.Alive, $"{victim.ChampionName} dies");
                return victim.RespawnAt - runtime.Combat.CurrentTime;
            }

            Score(500, 500);
            TestAssert.Near(26.0, Kill(match.Ally, match.Foe.NetId), 1e-6, "an even score: map 8's 26 s");
            runtime.Delivery.Flush();
            var sent = LastFloat(capture, 0x62, match.Ally.NetId, 11);
            TestAssert.True(sent is not null, "the hero death packet (0x62) reaches the client");
            TestAssert.Near(26.0, sent!.Value, 1e-4, "the death packet carries the same 26 s (read before the death's own 2 nexus points)");

            match.Advance(13.0);
            Score(400, 500);
            TestAssert.Near(24.0, Kill(match.Hero, match.Foe.NetId), 1e-6, "the trailing team's new window: 26 - 2 s");
            var leaderFirst = Kill(match.Foe, 0);
            TestAssert.Near(28.0, leaderFirst, 1e-6, "the leading team's new window: 26 + 2 s");
            var foeRespawn = match.Foe.RespawnAt;
            match.Advance(12.5);
            Kill(match.FarFoe, 0);
            TestAssert.Near(foeRespawn, match.FarFoe.RespawnAt, 1e-6,
                "12.5 s later the leader's 13 s window (12 + 1) still holds: both Chaos champions respawn together");
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // 129.10: Hardening (tenacity) cuts silence and blind, and a cut control lasts at least 0.3 s
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// V1.0.0.129 (Dr. Mundo, Irelia, Singed, Trundle): the shared rule "now additionally reduces silences and blinds" and "no longer ignores
    /// persistent slow fields ... when stacked with other disable duration reductions (ex. Tenacity)". The 132 Spells/Hardening.luaobj
    /// BuffOnAllowAdd cuts an enemy's Snare, Slow, Fear, Net, Sleep, Stun, Taunt, Silence and Blind by 1 - GetPercentHardnessMod, then floors
    /// the Duration at 0.3 (BBMath MO_MAX, pc 0260-0270; 126 had no floor), so a field's short slow stacked under two cuts still lands. A native
    /// add (BuffLifecycle.AddBuff, ScriptBuffRefuses) and a script add (BbBuffRuntime.Add) both run that hook.
    /// </summary>
    [Check("scripting.p132-hardening-cuts-silence-and-blind-and-floors-a-cut-control")]
    public static void HardeningCutsSilenceAndBlindAndFloorsACutControl()
    {
        using var match = new Match("Annie");
        var hero = match.Hero;
        hero.Inventory = [new InventorySlot(3111), null, null, null, null, null];
        match.Runtime.HeroStats.Refresh(hero, push: false);
        match.Advance(1.5);
        TestAssert.True(Buff(hero, "Hardening") is not null && Buff(hero, "MercuryTreads") is not null,
            "Mercury's Treads: Hardening and the buy's MercuryTreads are on the hero");
        foreach (var (name, type) in new[] { ("Silence", BuffType.Silence), ("Blind", BuffType.Blind) })
        {
            var control = match.Runtime.Spells.Buffs.Add(hero, name, type, 1.0, match.Foe.NetId)!;
            // MercuryTreads.luaobj cuts only the first seven types, so a silence or blind is cut once, by Hardening's 0.35.
            TestAssert.Near(0.65, control.ExpiresAt, 1e-6, $"a one-second enemy {name} lasts 0.65 s");
            match.Runtime.Spells.Buffs.Remove(hero, control.Slot);
        }
        var native = match.Runtime.Spells.Buffs.Add(hero, "Slow", BuffType.Slow, 0.25, match.Foe.NetId)!;
        TestAssert.Near(0.3, native.ExpiresAt, 1e-6, "a 0.25 s enemy slow cut twice (0.65 x 0.65) is floored at 0.3 s");
        match.Runtime.Spells.Buffs.Remove(hero, native.Slot);
        var vars = new BbTable();
        vars["MoveSpeedMod"] = -0.3;
        vars["AttackSpeedMod"] = 0.0;
        var scripted = match.Runtime.Spells.UnitScripts!.Buffs!.Add(new BbBuffRequest(hero, match.Foe, "Slow",
            (int)BbBuffAddType.BUFF_RENEW_EXISTING, (int)BbBuffType.BUFF_Slow, 1, 0.25, 0.0, true, vars));
        TestAssert.True(scripted is not null, "the script Slow lands");
        TestAssert.Near(0.3, scripted!.ExpiresAt, 1e-6, "a script's 0.25 s enemy slow is floored at 0.3 s too");
    }

    // ------------------------------------------------------------------------------------------------------------
    // 127.08 and 128.12: Riven's Broken Wings
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// V1.0.0.127: "Broken Wings: lock-on counts as an attack command". The 132 Spells/RivenTriCleave.luaobj SelfExecute picks the closest
    /// enemy within 175 of the cast point that Riven can see (BBForNClosestUnitsInTargetArea, BBCanSeeTarget), sets LockOn and orders her
    /// AI_ATTACKTO at it (BBIssueOrder, pc 0323-0330; the 126 script had no order). A cast at an empty point orders nothing.
    /// </summary>
    [Check("scripting.p132-riven-broken-wings-lock-on-orders-an-attack")]
    public static void RivenBrokenWingsLockOnOrdersAnAttack()
    {
        using var match = new Match("Riven");
        match.Learn(1);
        match.Refill();
        match.Foe.Position = new Position2(7250, 7000);
        TestAssert.True(match.Cast(0, at: match.Foe.Position), "Broken Wings is cast at the foe: " + match.CanCast(0, match.Foe).Reason);
        match.Advance(0.5);
        TestAssert.Equal(match.Foe.NetId, match.Hero.AttackTarget, "the lock-on orders Riven to attack the foe");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count,
            "no script failure: " + string.Join("; ", match.Module.ScriptFailures.Select(failure => $"{failure.Script}.{failure.Hook}: {failure.Message}")));

        using var empty = new Match("Riven");
        empty.Learn(1);
        empty.Refill();
        TestAssert.True(empty.Cast(0, at: new Position2(6700, 7000)), "Broken Wings is cast at an empty point");
        empty.Advance(0.5);
        TestAssert.True(empty.Hero.AttackTarget is null, "no enemy within 175 of the point: no attack order");
    }

    /// <summary>
    /// V1.0.0.128 (Riven): "Fixed a bug that caused her to stop moving after certain commands", "... her collision radius to be improperly
    /// calculated", and Ki Burst and Broken Wings "temporarily leave dead unpathable minions behind". The fixes are in the 132 data the server
    /// reads: Riven.inibin PathfindingCollisionRadius 40 (126: 65.77), RivenTriCleave without its BBStopMoveBlock / CANCEL_ORDER block, and the
    /// CO_IS_NOT_DEAD test on the units RivenTriCleaveDamage and RivenMartyr (Ki Burst) hit.
    /// </summary>
    [Check("scripting.p132-riven-radius-stop-and-living-targets-are-the-132-data")]
    public static void RivenRadiusStopAndLivingTargetsAreThe132Data()
    {
        TestAssert.Near(40.0, GameData.Default.Champion("Riven").PathfindingRadius, 1e-6, "Riven.inibin PathfindingCollisionRadius 40 (126: 65.77)");
        static IEnumerable<Amberline.Scripting.BuildingBlocks.BbBlock> All(string name) => Script(name).Walk().Select(item => item.Block);
        TestAssert.False(All("RivenTriCleave").Any(block => block.Function == "BBStopMoveBlock"), "RivenTriCleave no longer stops her movement");
        TestAssert.True(All("RivenTriCleave").Any(block => block.Function == "BBIssueOrder"), "RivenTriCleave issues the lock-on order");
        foreach (var name in new[] { "RivenTriCleaveDamage", "RivenMartyr" })
            TestAssert.True(All(name).Any(block => block.Function == "BBIf" && block.Param("CompareOp") is Amberline.Scripting.BuildingBlocks.BbValue.Reference
                { Name: "CO_IS_NOT_DEAD" }), $"{name} hits only living units (CO_IS_NOT_DEAD)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // 130.08: Death Defied gives one kill
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// V1.0.0.130: "Fixed a bug where Leona's Sunlight and the Dominion Storm Shield could cause an immediate double kill against Karthus." The
    /// 132 Spells/DeathDefied.luaobj adds a BuffOnPreDamage that zeroes every hit while DeathDefiedBuff is on Karthus (pc 0065-0083; 126 had
    /// none), and adds that buff in BuffOnZombie only when it is not on him. A second lethal hit in the step of his death takes nothing: one
    /// death, one kill credit.
    /// </summary>
    [Check("combat.p132-karthus-death-defied-gives-one-kill")]
    public static void KarthusDeathDefiedGivesOneKill()
    {
        using var match = new Match("Karthus");
        var karthus = match.Hero;
        TestAssert.True((Buff(karthus, "DeathDefied") ?? Buff(karthus, "Death Defied")) is not null, "Karthus holds Death Defied: " + Names(karthus));
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, karthus.NetId, 1e6, DamageType.True, DamageSourceKind.Spell);
        TestAssert.True(karthus.YorickRevival is not null && karthus.Alive, "the death becomes the Death Defied zombie life");
        TestAssert.True(Buff(karthus, "DeathDefiedBuff") is not null, "BuffOnZombie adds DeathDefiedBuff");
        match.Runtime.Combat.ApplyHit(match.FarFoe.NetId, karthus.NetId, 1e6, DamageType.True, DamageSourceKind.Spell);
        TestAssert.True(karthus.Alive && karthus.YorickRevival is not null, "the second lethal hit takes nothing: the zombie life goes on");
        TestAssert.Equal(1, karthus.ChampionDeaths, "one death");
        TestAssert.Equal(1, match.Foe.ChampionKills, "one kill, for the first hit");
        TestAssert.Equal(0, match.FarFoe.ChampionKills, "no second kill");
    }

    // ------------------------------------------------------------------------------------------------------------
    // 130.34: the small camps' Healing Sigil
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// V1.0.0.130: "Small camps now have a Healing Sigil, healing the killer based upon how much health they are missing." CharScriptGolem's
    /// CharOnActivate adds RegenerationRuneAura (as GiantWolf's and Wraith's do); its BuffOnDeath, for a killer that is no turret and is an AI,
    /// spawns a TestCubeRender on the killer's team at the camp and has it cast MonsterRegenSpell at the killer, whose TargetExecute heals
    /// max(10, 60 x (1 - health percent)).
    /// </summary>
    [Check("neutral.p132-healing-sigil-heals-the-killer-by-missing-health")]
    public static void HealingSigilHealsTheKillerByMissingHealth()
    {
        using var match = new Match("Annie");
        var hero = match.Hero;
        var data = GameData.Default.Champion("Golem");
        var golem = new Entity(match.Runtime.World.Allocate(UnitType.Monster), EntityKind.Neutral, UnitType.Monster, Team.Neutral,
            new Position2(7100, 7000))
        {
            Name = "Golem", ChampionName = "Golem", Health = data.BaseHp, MaxHealth = data.BaseHp, Created = true, Visible = true,
        };
        match.Runtime.World.Register(golem);
        TestAssert.True(match.Runtime.Spells.UnitScripts!.BindUnit(golem, "Golem"), "the Golem runs CharScriptGolem");
        TestAssert.True(Buff(golem, "RegenerationAura") is not null || Buff(golem, "RegenerationRuneAura") is not null,
            "the Golem carries the Healing Sigil: " + Names(golem));
        hero.Health = hero.MaxHealth * 0.5;
        match.Runtime.Combat.ApplyHit(hero.NetId, golem.NetId, 1e6, DamageType.True, DamageSourceKind.Spell);
        TestAssert.False(golem.Alive, "the hero kills the Golem");
        var cubes = match.Runtime.World.Entities.Values.Where(unit => unit.Name == "TestCubeRender" || unit.ChampionName == "TestCubeRender").ToArray();
        TestAssert.True(cubes.Length == 1 && cubes[0].Team == hero.Team, "the sigil's TestCubeRender is spawned on the killer's team");
        // MonsterRegenSpell.inibin: CastType 1, MissileSpeed 700: the heal rides a missile from the camp to the killer (about 0.15 s here).
        var largest = 0.0;
        for (var step = 0; step < 60; step++)
        {
            var health = hero.Health;
            match.Runtime.Tick(Step);
            largest = Math.Max(largest, hero.Health - health);
        }
        var failures = string.Join("; ", match.Runtime.Spells.UnitScripts!.ScriptFailures.Select(failure => $"{failure.Script}.{failure.Hook}: {failure.Message}"));
        TestAssert.Equal(0, match.Runtime.Spells.UnitScripts!.ScriptFailures.Count, "no script failure: " + failures);
        TestAssert.Near(30.0, largest, 0.1, $"the sigil heals 60 x 0.5 missing = 30 at once (largest one-step gain {largest:F3})");
    }

    // ------------------------------------------------------------------------------------------------------------
    // 131.05: the Prototype Hex Core cannot be sold
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// V1.0.0.131 (Viktor): "The Hex Core can only be upgraded once, and cannot be sold back to the store." Items/3200.inibin (the Prototype
    /// Hex Core) and the three augments carry CanBeSold 0; the augments' refusal is pinned by items.p132-hex-core-upgrades-once.
    /// </summary>
    [Check("items.p132-prototype-hex-core-cannot-be-sold")]
    public static void PrototypeHexCoreCannotBeSold()
    {
        var catalog = new ItemCatalog(GameData.Default.Paths);
        TestAssert.False(catalog.Get(3200)!.CanBeSold, "Items/3200.inibin CanBeSold 0");
        InventorySlot?[] slots = [new InventorySlot(3200), null, null, null, null, null];
        TestAssert.Equal("unsellable_item", Inventory.Sell(catalog, slots, 0.0, 0).Reason, "the Prototype Hex Core cannot be sold");
    }

    // ------------------------------------------------------------------------------------------------------------
    // 131.46 and 131.49: Death Ray's damage is counted once, as dealt
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// V1.0.0.131 hotfixes: "the End of Game screen was reporting massive damage done values for Viktor" and Death Ray "was not doing the correct
    /// amount of damage reported". The 132 Spells/ViktorDeathRay.luaobj spawns a MaokaiSproutling for the beam (gold redirected to Viktor) and its
    /// OnBuffDeactivate ends it with 9999 true damage on itself (pc 0090-0104). Viktor's damage dealt is what the foe lost, once: the beam's own end
    /// is no damage he dealt (a hit on the dealer's own team, CombatService.ApplyHit).
    /// </summary>
    [Check("scripting.p132-viktor-death-ray-damage-counts-once")]
    public static void ViktorDeathRayDamageCountsOnce()
    {
        using var match = new Match("Viktor");
        match.Hero.Level = 6;
        match.Learn(1);
        match.Refill();
        var viktor = match.Hero;
        var foe = match.Foe;
        var dealt = viktor.MatchStatistics.TotalDamageDealt;
        var taken = foe.MatchStatistics.TotalDamageTaken;
        var health = foe.Health;
        TestAssert.True(match.Cast(2, at: foe.Position) || match.Cast(2, foe), "Death Ray is cast: " + match.CanCast(2, foe).Reason);
        Entity? beam = null;
        for (var step = 0; step < 120; step++)
        {
            match.Runtime.Tick(Step);
            beam ??= match.Runtime.World.Entities.Values.FirstOrDefault(unit => unit.Name == "MaokaiSproutling" && unit.Alive);
        }
        TestAssert.True(beam is not null, "the Death Ray spawns its MaokaiSproutling beam unit");
        TestAssert.False(beam!.Alive, "the beam unit has ended itself (its 9999 true damage on itself)");
        TestAssert.True(beam.Team == viktor.Team, "the beam is on Viktor's team, so its self-hit is no damage he dealt");
        var lost = foe.MatchStatistics.TotalDamageTaken - taken;
        TestAssert.True(lost > 0.0 && foe.Health < health, $"the beam hurts the foe ({lost:F1})");
        TestAssert.Near(lost, viktor.MatchStatistics.TotalDamageDealt - dealt, 1e-6, "Viktor's damage dealt is the foe's damage taken");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count,
            "no script failure: " + string.Join("; ", match.Module.ScriptFailures.Select(failure => $"{failure.Script}.{failure.Hook}: {failure.Message}")));
    }

    // ------------------------------------------------------------------------------------------------------------
    // 132.41: Chosen of the Storm's cooldown is not reset on death
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// V1.0.0.132 (Volibear): "Chosen of the Storm cooldown no longer resets on death". The 132 Spells/VolibearPassiveCD.luaobj sets
    /// PersistsThroughDeath (pc 0004-0005), so the server's RemoveNonPersistantBuffs at the death keeps it, and it ends on its own clock.
    /// </summary>
    [Check("scripting.p132-volibear-chosen-of-the-storm-cooldown-survives-death")]
    public static void VolibearChosenOfTheStormCooldownSurvivesDeath()
    {
        using var match = new Match("Volibear");
        var volibear = match.Hero;
        var cooldown = ScriptBuff(match.Runtime, volibear, "VolibearPassiveCD", 120.0);
        TestAssert.True(cooldown is not null, "VolibearPassiveCD is on Volibear");
        // The control: a buff of the same runtime whose script does not say PersistsThroughDeath (VolibearPassiveHeal) goes with the death.
        TestAssert.True(ScriptBuff(match.Runtime, volibear, "VolibearPassiveHeal", 120.0) is not null, "the control VolibearPassiveHeal is on Volibear");
        match.Advance(1.0);
        var left = Buff(volibear, "VolibearPassiveCD")!.ExpiresAt;
        match.Runtime.Combat.ApplyHit(match.Foe.NetId, volibear.NetId, 1e6, DamageType.True, DamageSourceKind.Spell);
        TestAssert.False(volibear.Alive, "Volibear dies");
        TestAssert.True(Buff(volibear, "VolibearPassiveHeal") is null, "the death removes the non-persistent control buff: " + Names(volibear));
        TestAssert.True(match.Runtime.Combat.RespawnImmediately(volibear), "Volibear respawns");
        var after = Buff(volibear, "VolibearPassiveCD");
        TestAssert.True(after is not null, "the cooldown is still on him after the death: " + Names(volibear));
        TestAssert.Near(left, after!.ExpiresAt, 1e-6, "on its own clock (no reset)");
    }
}
