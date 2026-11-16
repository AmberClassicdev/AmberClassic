using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Platform;
using Amberline.Simulation.Movement;

namespace Amberline.Tests;

public static partial class SummonerAdvancedChecks
{
    [Check("spells-summoners-b4-teleport-mastery-completion-cancel")]
    public static void TeleportMasteriesOnlyReduceCompletedCooldown()
    {
        foreach (var slot in new[] { 0, 1 })
        {
            using var runtime = CreateRuntime();
            var hero = Hero(runtime, Team.Order, SpawnPoint(),
                slot == 0 ? ["SummonerTeleport", "SummonerDot"] : ["SummonerDot", "SummonerTeleport"]);
            // Batch 770: the 1.0.0.132 utility page: Summoner's Insight (411, utilityMastery) and Mastermind (463, 0.15).
            hero.Masteries = Amberline.Tests.Core.MasteryTestPages.Utility(new MasteryCatalog());
            runtime.HeroStats.Refresh(hero);
            var target = new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion,
                UnitType.Minion, Team.Order, new Position2(hero.Position.X + 300, hero.Position.Z))
            {
                Name = "Minion_Basic", Health = 1000, MaxHealth = 1000, Created = true, Radius = 35,
            };
            runtime.World.Register(target);
            TestAssert.True(Cast(runtime, hero, slot, target.Position, target.NetId));
            // Summoner's Insight shortens the channel to 3.5 (the 132 TargetExecute's utilityMastery == 1 branch), so the buff
            // runs out inside this step and its OnBuffDeactivate writes the completed cooldown after the step's own decrement.
            Tick(runtime, 3.5);
            TestAssert.Equal("SummonerTeleport", hero.SummonerSpells[slot]);
            TestAssert.Near(255, hero.SummonerCooldowns[slot], 1e-9,
                "the 132 completion is 300 * (1 - Mastermind's 0.15); no 132 talent writes 126's TeleportCooldownBonus 5");
            hero.SummonerCooldowns[slot] = 0;
            TestAssert.True(Cast(runtime, hero, slot, target.Position, target.NetId));
            Tick(runtime, 1);
            TestAssert.True(Cast(runtime, hero, slot, target.Position));
            TestAssert.Near(180, hero.SummonerCooldowns[slot], 1e-9, "cancel ignores cooldown masteries");
        }
    }

    [Check("spells-summoners-advanced-slot-name-packet")]
    public static void SlotNamePacket()
    {
        var packet = SpellPackets.ChangeSlotSpellName(0x1234, 1,
            "TeleportCancel", isSummoner: true);
        TestAssert.True(SpellPackets.TryReadChangeSlotSpellName(
            packet.Plaintext.Span, out var decoded), "typed 0x35 decoder");
        TestAssert.Equal((uint)0x1234, decoded.NetId, "slot name net id");
        TestAssert.Equal((byte)1, decoded.Slot, "slot name slot");
        TestAssert.True(decoded.IsSummoner, "slot name summoner bit");
        TestAssert.Equal("TeleportCancel", decoded.SpellName, "slot name");
        var wire = WirePacketDecoder.Decode(packet.Plaintext.Span, packet.Channel);
        TestAssert.True(wire.HasDecoder, "slot name wire decoder");
        TestAssert.Equal("TeleportCancel", wire.Fields["spell_name"] as string,
            "slot name diagnostic field");
    }

    [Check("spells-summoners-advanced-teleport")]
    public static void TeleportLifecycle()
    {
        var runtime = CreateRuntime();
        var hero = Hero(runtime, Team.Order, SpawnPoint(),
            ["SummonerTeleport", "SummonerDot"]);
        var target = new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, Team.Order,
            new Position2(hero.Position.X + 300.0, hero.Position.Z))
        {
            Name = "Minion_Basic", ChampionName = "Annie", Health = 1000.0,
            MaxHealth = 1000.0, Alive = true, Created = true, Radius = 35.0,
        };
        runtime.World.Register(target);
        var original = hero.SummonerSpells[0];
        TestAssert.True(Cast(runtime, hero, 0, target.Position, target.NetId),
            "Teleport cast");
        TestAssert.Equal("TeleportCancel", hero.SummonerSpells[0],
            "Teleport must expose cancel spell");
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "SummonerTeleport"),
            "Teleport applies its native channel buff");
        TestAssert.True(target.Buffs.Any(buff => buff.Name == "Teleport_Target"),
            "Teleport target lock");
        Tick(runtime, 4.0);
        TestAssert.Equal(original, hero.SummonerSpells[0],
            "Teleport must restore selected slot");
        TestAssert.Equal(300.0, hero.SummonerCooldowns[0],
            "completed Teleport cooldown");
        // SummonerTeleport.TargetExecute adds Teleport_Target with Duration 0.1 on top of its DurationVar BuffDuration, so the
        // target lock runs a tenth of a second past the channel — the same 0.1 OnBuffDeactivate adds to the elapsed time before
        // its TotalTime >= BuffDuration arrival test. Nothing on the target is removed at the arrival (the hand-written
        // ReleaseTeleportTarget did); the lock expires on its own timer, and it outlives the channel by that tail.
        TestAssert.True(target.Buffs.Any(buff => buff.Name == "Teleport_Target"),
            "the shipped target lock outlives the channel by its 0.1s tail");
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "SummonerTeleport"),
            "completed Teleport channel cleanup");
        Tick(runtime, 0.2);
        TestAssert.False(target.Buffs.Any(buff => buff.Name == "Teleport_Target"),
            "completed Teleport target cleanup");

        hero.SummonerCooldowns[0] = 0.0;
        TestAssert.True(Cast(runtime, hero, 0, target.Position, target.NetId),
            "second Teleport cast");
        var channel = hero.Buffs.Single(buff => buff.Name == "SummonerTeleport");
        TestAssert.True(runtime.SpellBuffs.Remove(hero, channel.Slot),
            "external channel removal");
        // The shipped OnBuffDeactivate writes the interrupted cooldown and restores the slot as the
        // buff leaves, so both are already in before any further step (a step would only tick it down).
        TestAssert.Equal(original, hero.SummonerSpells[0],
            "removed Teleport channel cancels the pending cast");
        TestAssert.Equal(180.0, hero.SummonerCooldowns[0],
            "removed Teleport channel uses interrupted cooldown");
        Tick(runtime, 0.1);
        TestAssert.Equal(original, hero.SummonerSpells[0],
            "the slot stays restored");

        hero.SummonerCooldowns[0] = 0.0;
        TestAssert.True(Cast(runtime, hero, 0, target.Position, target.NetId),
            "third Teleport cast");
        TestAssert.False(Cast(runtime, hero, 0, target.Position),
            "TeleportCancel is held by its one-second cooldown");
        Tick(runtime, 1.0);
        TestAssert.True(Cast(runtime, hero, 0, target.Position),
            "TeleportCancel request after cooldown");
        TestAssert.Equal(original, hero.SummonerSpells[0],
            "cancel restores selected spell");
        TestAssert.Equal(180.0, hero.SummonerCooldowns[0],
            "interrupted Teleport cooldown");
    }

    [Check("spells-summoners-teleport-movement-and-silence-gates")]
    public static void TeleportRefusesWhileImmobileButNotWhileSilencedAndOnlyNewControlInterrupts()
    {
        // SummonerTeleport.inibin CantCastWhileRooted=True, native OnCanUseSpell
        // (the client's cast check): the cast needs CanMove. A Silence already
        // on the caster is not a gate; only a newly added control interrupts.
        var runtime = CreateRuntime();
        var hero = Hero(runtime, Team.Order, SpawnPoint(), ["SummonerTeleport", "SummonerDot"]);
        var target = new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, Team.Order,
            new Position2(hero.Position.X + 300.0, hero.Position.Z))
        {
            Name = "Minion_Basic", ChampionName = "Annie", Health = 1000.0,
            MaxHealth = 1000.0, Alive = true, Created = true, Radius = 35.0,
        };
        runtime.World.Register(target);

        foreach (var type in new[] { BuffType.Snare, BuffType.Stun, BuffType.Sleep,
                     BuffType.Net, BuffType.Suppression })
        {
            var control = runtime.SpellBuffs.Add(hero, "TeleportGateProbe", type, 5.0, target.NetId,
                canMitigateDuration: false);
            TestAssert.True(control is not null, "probe buff added");
            TestAssert.False(Cast(runtime, hero, 0, target.Position, target.NetId),
                $"{type} (CanMove false) blocks Teleport");
            TestAssert.Equal(0.0, hero.SummonerCooldowns[0], $"{type}: refused cast keeps the cooldown");
            TestAssert.Equal("SummonerTeleport", hero.SummonerSpells[0], $"{type}: slot unchanged");
            runtime.SpellBuffs.Remove(hero, control!.Slot);
        }
        // SetFeared (bit 6) and SetTaunted (bit 5) leave the CanMove bit alone.
        foreach (var type in new[] { BuffType.Fear, BuffType.Taunt })
        {
            var control = runtime.SpellBuffs.Add(hero, "TeleportGateProbe", type, 5.0, target.NetId,
                canMitigateDuration: false);
            TestAssert.True(Cast(runtime, hero, 0, target.Position, target.NetId),
                $"{type} does not clear CanMove, so it does not block Teleport");
            runtime.SpellBuffs.Remove(hero, control!.Slot);
            // The channel ends by expiry, by TeleportCancel or by death (BbBuffRuntime.OnUnitDeath); here it is
            // taken off directly so the next iteration starts from a clean slot, the way a finished channel does.
            EndChannel(runtime, hero);
            hero.SummonerCooldowns[0] = 0.0;
        }
        var movementLock = runtime.SpellBuffs.Add(hero, "TeleportGateProbe", BuffType.Internal, 5.0,
            target.NetId, new BuffPayload { MovementDisabled = true }, canMitigateDuration: false);
        TestAssert.False(Cast(runtime, hero, 0, target.Position, target.NetId), "a movement lock blocks Teleport");
        runtime.SpellBuffs.Remove(hero, movementLock!.Slot);

        runtime.SpellBuffs.Add(hero, "TeleportGateSilence", BuffType.Silence, 20.0, target.NetId,
            canMitigateDuration: false);
        TestAssert.True(Cast(runtime, hero, 0, target.Position, target.NetId),
            "a pre-existing Silence does not block the start");
        Tick(runtime, 0.5);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "SummonerTeleport"),
            "the pre-existing Silence does not interrupt the channel");

        // The shipped BuffOnAllowAdd of the channel buff ends it the moment a newly added hard control
        // lands (its own Type is in the script's list), so the removal and its 180 are already in when
        // the add returns — no step is needed to notice the interrupt.
        runtime.SpellBuffs.Add(hero, "TeleportGateFreshStun", BuffType.Stun, 1.0, target.NetId,
            canMitigateDuration: false);
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "SummonerTeleport"),
            "a newly added Stun interrupts the channel");
        TestAssert.Equal("SummonerTeleport", hero.SummonerSpells[0], "interrupt restores the slot");
        TestAssert.Equal(180.0, hero.SummonerCooldowns[0], "interrupted Teleport cooldown");
    }

    [Check("spells-summoners-advanced-revive")]
    public static void ReviveLifecycle()
    {
        var catalog = new MasteryCatalog();
        Entity PrepareDeadRevive(MatchRuntime match, MasteryPage page)
        {
            var candidate = Hero(match, Team.Order, SpawnPoint(),
                ["SummonerRevive", "SummonerDot"], level: 5);
            candidate.Masteries = page;
            match.HeroStats.Refresh(candidate);
            candidate.Alive = false;
            candidate.Health = 0.0;
            candidate.RespawnAt = 20.0;
            return candidate;
        }

        var runtime = CreateRuntime();
        var hero = PrepareDeadRevive(runtime, MasteryPage.Empty);
        TestAssert.True(Cast(runtime, hero, 0, hero.Position),
            "dead-only Revive cast");
        TestAssert.True(hero.Alive, "Revive must use immediate respawn lifecycle");
        TestAssert.Equal(0.0, hero.RespawnAt, "Revive clears respawn timer");
        // Owner decision 16 Oct 2026: base 540 for every player (shipped data says 510).
        TestAssert.Equal(540.0, hero.SummonerCooldowns[0], "Revive cooldown");
        // 1.0.0.132 (batch 772, wiki 129.50 / 129.51): the bonus health is innate (ReviveMarker with the TargetExecute's HealthMod
        // {220 .. 560}[level], 300 at level five, for 120 s) and the speed boost needs Summoner's Resolve (defensiveMastery == 1).
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "SummonerReviveSpeedBoost"),
            "an ordinary Revive has no speed boost in 132");
        TestAssert.Equal(300.0, hero.Buffs.Single(buff => buff.Name == "ReviveMarker").Payload.MaxHp,
            "an ordinary Revive carries the innate level-5 health");

        var resolveRuntime = CreateRuntime();
        var resolved = PrepareDeadRevive(resolveRuntime, MasteryPage.Create(catalog, new Dictionary<int, int> { [311] = 1 }));
        var baseSpeed = resolved.Speed;
        TestAssert.True(Cast(resolveRuntime, resolved, 0, resolved.Position), "Summoner's Resolve Revive cast");
        var boost = resolved.Buffs.Single(buff => buff.Name == "SummonerReviveSpeedBoost");
        // SummonerReviveSpeedBoost.luaobj's BuffOnUpdateStats takes 0.026 (126: 0.045) off InstanceVars.MoveSpeedMod and publishes
        // what is left as IncPercentMultiplicativeMovementSpeedMod, on the shipped 0.25 s stats grid (BbBuffRuntime.StatsUpdateInterval).
        // The 132 TargetExecute starts it at 1.25 (126: 2.2). Retail runs no hook of a buff before its OnBuffActivate (batch 256), so the
        // buff stores 1.25 - 0.026 after the grid's first sample.
        TestAssert.Near(1.25 - 0.026, boost.Payload.MoveSpeedMult, 1e-9,
            "Revive stores the shipped additive speed modifier, stepped down by the sample after its activation");
        static double SoftCap(double speed) => speed <= 415.0 ? speed :
            speed <= 490.0 ? 415.0 + (speed - 415.0) * 0.8 :
            415.0 + 75.0 * 0.8 + (speed - 490.0) * 0.5;
        // After N samples of the stats grid the modifier is 1.25 - (N + 1) * 0.026: the add's one, then one step per sample.
        static double Modifier(int samples) => 1.25 - (samples + 1) * 0.026;
        TestAssert.Near(SoftCap(baseSpeed * (1.0 + Modifier(0))), resolved.Speed, 1e-9,
            "Revive initial effective speed multiplier");
        for (var sample = 0; sample < 24; sample++) Tick(resolveRuntime, 0.25);
        TestAssert.Near(SoftCap(baseSpeed * (1.0 + Modifier(24))), resolved.Speed, 1e-9,
            "Revive midpoint: 24 grid samples into the twelve seconds");
        // At 47 samples (11.75 s) 0.002 is left of the shipped modifier, the last step before the buff ends at twelve seconds;
        // the script's own clamp (BBIf CO_LESS_THAN 0.0 -> BBSetVarInTable 0.0) is never reached in the buff's life.
        for (var sample = 0; sample < 23; sample++) Tick(resolveRuntime, 0.25);
        TestAssert.Near(SoftCap(baseSpeed * (1.0 + Modifier(47))), resolved.Speed, 1e-9,
            "Revive tail: 47 grid samples in, 0.002 of the shipped modifier left");
        TestAssert.True(resolved.Buffs.Any(buff => buff.Name == "SummonerReviveSpeedBoost"),
            "the boost is still on for the last quarter second");
        Tick(resolveRuntime, 0.25);
        TestAssert.Near(baseSpeed, resolved.Speed, 1e-9,
            "Revive speed returns to base at twelve seconds");
        TestAssert.False(resolved.Buffs.Any(buff => buff.Name == "SummonerReviveSpeedBoost"),
            "Revive speed buff expiry");

        var unrelatedRuntime = CreateRuntime();
        var unrelated = PrepareDeadRevive(unrelatedRuntime,
            MasteryPage.Create(catalog, new Dictionary<int, int> { [211] = 1 }));
        TestAssert.True(Cast(unrelatedRuntime, unrelated, 0, unrelated.Position),
            "unrelated mastery Revive cast");
        TestAssert.Equal(540.0, unrelated.SummonerCooldowns[0],
            "unrelated mastery keeps the 540 base Revive cooldown (owner decision 16 Oct 2026)");

        // Batch 770: 126's Preservation (129: -30 s, ReviveMarker 400) is 132's Summoner's Resolve (311): the 132 TargetExecute
        // adds SummonerReviveSpeedBoost (MoveSpeedMod 1.25) under defensiveMastery == 1 and ReviveMarker always, with its HealthMod
        // {220 .. 560}[level]; the cooldown stays 540.
        var preservationRuntime = CreateRuntime();
        var preservation = PrepareDeadRevive(preservationRuntime,
            MasteryPage.Create(catalog, new Dictionary<int, int> { [311] = 1 }));
        TestAssert.True(Cast(preservationRuntime, preservation, 0, preservation.Position),
            "Summoner's Resolve Revive cast");
        TestAssert.Equal(540.0, preservation.SummonerCooldowns[0],
            "Summoner's Resolve does not change the 540 base");
        TestAssert.True(preservation.Buffs.Any(buff => buff.Name == "SummonerReviveSpeedBoost"),
            "Summoner's Resolve adds the Revive speed boost");
        TestAssert.Equal(300.0,
            preservation.Buffs.Single(buff => buff.Name == "ReviveMarker").Payload.MaxHp,
            "ReviveMarker carries the level-5 HealthMod of the shipped table");

        var genericRuntime = CreateRuntime();
        var generic = PrepareDeadRevive(genericRuntime, Amberline.Tests.Core.MasteryTestPages.Utility(catalog));
        TestAssert.True(Cast(genericRuntime, generic, 0, generic.Position),
            "Presence Revive cast");
        TestAssert.Near(459.0, generic.SummonerCooldowns[0], 1e-9,
            "Mastermind (463; 126's Presence 120) uses the shipped 540 Revive base before its multiplier");
    }

    // Owner decision 16 Oct 2026: Revive's base cooldown is 540 s for every player.
    // The shipped SummonerRevive SpellData says 510; the script's 540 literal used to
    // apply only on Presence's branch. Masteries adjust from 540 as the script does:
    // 132's Mastermind (463) 540 * (1 - 0.15); 132 has no flat Revive bonus (126's Preservation's 30 s).
    [Check("spells-summoners-advanced-revive-base-cooldown")]
    public static void ReviveBaseCooldownIs540()
    {
        var catalog = new MasteryCatalog();
        double Cooldown(int slot, Dictionary<int, int>? page, bool noCooldown = false)
        {
            var runtime = CreateRuntime();
            var hero = Hero(runtime, Team.Order, SpawnPoint(),
                slot == 0 ? ["SummonerRevive", "SummonerDot"] : ["SummonerDot", "SummonerRevive"],
                level: 5);
            if (page is not null) hero.Masteries = MasteryPage.Create(catalog, page);
            hero.CooldownsDisabled = noCooldown;
            runtime.HeroStats.Refresh(hero);
            hero.Alive = false;
            hero.Health = 0.0;
            hero.RespawnAt = 20.0;
            TestAssert.True(Cast(runtime, hero, slot, hero.Position), $"Revive cast, slot {slot}");
            return hero.SummonerCooldowns[slot];
        }

        // Valid 1.0.0.132 pages, as in ReviveLifecycle: Mastermind (463) and Summoner's Resolve (311).
        var presence = Amberline.Tests.Core.MasteryTestPages.Utility(catalog).Allocations
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        var preservation = new Dictionary<int, int> { [311] = 1 };
        foreach (var slot in new[] { 0, 1 })
        {
            TestAssert.Equal(540.0, Cooldown(slot, null), $"no page, slot {slot}");
            TestAssert.Equal(540.0, Cooldown(slot, new Dictionary<int, int>()), $"empty page, slot {slot}");
            TestAssert.Equal(540.0, Cooldown(slot, new Dictionary<int, int> { [211] = 1 }),
                $"unrelated mastery, slot {slot}");
            TestAssert.Near(459.0, Cooldown(slot, presence), 1e-9,
                $"Mastermind scales the 540 base, slot {slot}");
            TestAssert.Equal(540.0, Cooldown(slot, preservation),
                $"Summoner's Resolve leaves the 540 base, slot {slot}");
            TestAssert.Equal(0.0, Cooldown(slot, null, noCooldown: true), $"no-cooldown mode, slot {slot}");
        }
    }

    /// <summary>
    /// Batch 303: the cooldown every summoner cast starts with no mastery page and no rune, pinned before and after
    /// <c>AvatarVars</c> reads an unset entry as Lua nil. Under nil each script's own <c>AdjustCooldown</c> takes its
    /// <c>SummonerCooldownBonus CO_NOT_EQUAL 0</c> branch and returns its base literal times 1 (Revive's 540 through its
    /// <c>NewCooldown</c>), so the router hands back the number itself and no engine fallback answers. Every literal is the
    /// inibin <c>Cooldown</c> except Revive's (SpellData 510). Batch 772 restates the table to the 1.0.0.132 scripts (Smite and
    /// Clairvoyance moved to 70 in their scripts only; Teleport gained an <c>AdjustCooldown</c> of 300; Surge is new; Fortify and Rally are
    /// out of the pool).
    /// </summary>
    [Check("spells-summoners-cast-cooldowns-from-scripts")]
    public static void EverySummonerCastStartsItsScriptCooldown()
    {
        foreach (var (spell, expected) in new (string, double)[]
        {
            // 1.0.0.132 (batch 772): the 132 scripts' literals. Flash 265 (129.44), Smite 70 (129.52), Cleanse 210, Clairvoyance 70 (129.41),
            // Surge 180 (SummonerBattleCry, 132.53), Promote 180 (SummonerPromoteSR); Teleport has an AdjustCooldown of 300 now. Fortify and
            // Rally left the pool (129.45): their casts are refused (spells-summoners-p132-fortify-and-rally-are-refused).
            ("SummonerFlash", 265.0), ("SummonerHeal", 270.0), ("SummonerExhaust", 210.0), ("SummonerHaste", 210.0),
            ("SummonerSmite", 70.0), ("SummonerDot", 180.0), ("SummonerMana", 180.0), ("SummonerBoost", 210.0),
            ("SummonerClairvoyance", 70.0), ("SummonerRevive", 540.0), ("SummonerTeleport", 300.0), ("SummonerBattleCry", 180.0),
        })
        {
            var runtime = CreateRuntime();
            var hero = Hero(runtime, Team.Order, SpawnPoint(),
                [spell, spell == "SummonerHeal" ? "SummonerFlash" : "SummonerHeal"], level: 5);
            runtime.HeroStats.Refresh(hero);
            var at = hero.Position;
            Entity Unit(EntityKind kind, UnitType type, Team team, string name, double dx)
            {
                var unit = new Entity(runtime.World.Allocate(type), kind, type, team, new Position2(at.X + dx, at.Z))
                {
                    Name = name, ChampionName = "Annie", Health = 1000.0, MaxHealth = 1000.0,
                    Alive = true, Created = true, Visible = true, Radius = 35.0,
                };
                runtime.World.Register(unit);
                return unit;
            }

            var module = runtime.Spells.SummonerScripts!;
            var binding = module.Binding(hero, 0, runtime.Spells.LoadoutHooks!.OwnerFor(hero)!)!;
            var scripted = module.Router!.RunAdjustCooldown(binding.Script, hero, 0, binding.Rank, binding);
            TestAssert.Near(expected, scripted ?? double.NaN, 1e-9, $"{spell}: the script's own AdjustCooldown answers");
            // The script's literal is the inibin Cooldown except Revive's (SpellData 510) and the two the 132 scripts moved without the
            // record: Smite (inibin 75) and Clairvoyance (inibin 55).
            if (spell is not ("SummonerRevive" or "SummonerSmite" or "SummonerClairvoyance"))
                TestAssert.Near(GameData.Default.Spell(spell, 1).Cooldowns[0], expected, 1e-9, $"{spell}: inibin Cooldown");

            uint target = spell switch
            {
                "SummonerDot" or "SummonerExhaust" => Unit(EntityKind.Hero, UnitType.Hero, Team.Chaos, "ChaosSeat", 300.0).NetId,
                "SummonerSmite" => Unit(EntityKind.Neutral, UnitType.Monster, Team.Neutral, "Neutral", 300.0).NetId,
                "SummonerTeleport" => Unit(EntityKind.Minion, UnitType.Minion, Team.Order, "Minion_Basic", 300.0).NetId,
                _ => 0,
            };
            if (spell == "SummonerRevive")
            {
                hero.Alive = false;
                hero.Health = 0.0;
                hero.RespawnAt = 20.0;
            }
            var point = spell switch
            {
                "SummonerFlash" => new Position2(at.X + 200.0, at.Z + 150.0),
                "SummonerClairvoyance" => new Position2(at.X + 1000.0, at.Z + 1000.0),
                _ => new Position2(at.X + (target != 0 ? 300.0 : 0.0), at.Z),
            };
            TestAssert.True(Cast(runtime, hero, 0, point, target), $"{spell} cast");
            // Teleport's channel (its OnBuffActivate) writes the TeleportCancel placeholder's 1 s over the cast's 300, which the channel's end
            // writes back (spells-summoners-advanced-teleport).
            TestAssert.Near(spell == "SummonerTeleport" ? 1.0 : expected, hero.SummonerCooldowns[0], 1e-9, $"{spell}: cast cooldown");
            TestAssert.Equal(0, module.ScriptFailures.Count, $"{spell}: no script failure");
        }
    }

    /// <summary>
    /// 1.0.0.132 (batch 772, wiki 129.45): Fortify and Rally left the summoner pool. What still ships and runs is the tower buff's
    /// FortifyCheck for a seat that holds Fortify (APBonusDamageToTowers' OnBuffActivate, the FortifyCheck poll's FortifyBuff), but the casts
    /// are refused: no turret is fortified, no beacon is planted, no cooldown starts. 126's lifecycle lives on the 126 line.
    /// </summary>
    [Check("spells-summoners-advanced-fortify-rally")]
    public static void FortifyAndRallyLifecycle()
    {
        var runtime = CreateRuntime();
        var hero = Hero(runtime, Team.Order, SpawnPoint(),
            ["SummonerFortify", "SummonerRally"], level: 4);
        runtime.Spells.UnitScripts!.AddShippedBuff(hero, "APBonusDamageToTowers", BuffType.Internal, 25000.0);
        runtime.Spells.Tick(0.0);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "FortifyCheck"),
            "FortifyCheck on a hero that carries Fortify");
        runtime.Buffs.TickBuffs(hero, 3.0);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "FortifyBuff"),
            "ready Fortify marker");
        var turret = new Entity(runtime.World.Allocate(UnitType.Turret),
            EntityKind.Turret, UnitType.Turret, Team.Order,
            new Position2(hero.Position.X + 150.0, hero.Position.Z))
        {
            Name = "Turret_T1_C1", ChampionName = "Annie", Health = 1000.0,
            MaxHealth = 1000.0, Alive = true, Created = true,
            AttackPeriod = 1.0, AttackSpeedMod = 1.0,
        };
        runtime.World.Register(turret);
        TestAssert.False(Cast(runtime, hero, 0, hero.Position), "132 refuses Fortify");
        TestAssert.False(turret.Buffs.Any(buff => buff.Name == "SummonerFortify"), "no turret is fortified");
        TestAssert.Equal(0.0, hero.SummonerCooldowns[0], "the refused Fortify starts no cooldown");
        TestAssert.False(Cast(runtime, hero, 1, hero.Position), "132 refuses Rally");
        TestAssert.False(runtime.World.Entities.Values.Any(unit => unit.Name == "Beacon"), "no beacon is planted");
        TestAssert.Equal(0.0, hero.SummonerCooldowns[1], "the refused Rally starts no cooldown");
    }

    /// <summary>
    /// 1.0.0.132 (batch 772, wiki 129.45): no Fortify splash and no Rally aura can arise, because neither spell can be cast (126's Reinforce and
    /// Improved Rally keys are read by scripts no 132 seat may cast).
    /// </summary>
    [Check("spells-summoners-fortify-splash-and-rally-ap")]
    public static void FortifySplashAndRallyAbilityPower()
    {
        var runtime = CreateRuntime();
        var hero = Hero(runtime, Team.Order, SpawnPoint(), ["SummonerFortify", "SummonerRally"], level: 4);
        runtime.HeroStats.Refresh(hero);
        runtime.Spells.Tick(0.0);
        TestAssert.False(Cast(runtime, hero, 0, hero.Position), "132 refuses Fortify");
        TestAssert.False(Cast(runtime, hero, 1, hero.Position), "132 refuses Rally");
        TestAssert.False(runtime.World.Entities.Values.Any(unit => unit.Buffs.Any(buff => buff.Name is "SummonerFortify" or "BeaconAura" or "BeaconAuraAP")),
            "neither the Fortify buff nor a Rally aura exists");
    }

    /// <summary>
    /// 1.0.0.132 (batch 772, wiki 129.45): Fortify cannot be cast, so a turret's hit on an armoured minion splashes nothing (126's splash phase
    /// rule lives on the 126 line).
    /// </summary>
    [Check("spells-summoners-fortify-splash-reads-the-unmitigated-hit")]
    public static void FortifySplashReadsTheUnmitigatedHit()
    {
        var runtime = CreateRuntime();
        var hero = Hero(runtime, Team.Order, SpawnPoint(), ["SummonerFortify", "SummonerDot"], level: 4);
        runtime.HeroStats.Refresh(hero);
        runtime.Spells.Tick(0.0);
        Entity Unit(UnitType type, EntityKind kind, Team team, Position2 position, double armor)
        {
            var unit = new Entity(runtime.World.Allocate(type), kind, type, team, position)
            {
                Name = type == UnitType.Turret ? "Turret_T1_C1" : "FortifyTarget",
                ChampionName = "Annie", Health = 1000.0, MaxHealth = 1000.0,
                Armor = armor, Alive = true, Created = true,
            };
            runtime.World.Register(unit);
            return unit;
        }

        var turret = Unit(UnitType.Turret, EntityKind.Turret, Team.Order,
            new Position2(hero.Position.X + 150.0, hero.Position.Z), 0.0);
        TestAssert.False(Cast(runtime, hero, 0, hero.Position), "132 refuses Fortify");
        var primary = Unit(UnitType.Minion, EntityKind.Minion, Team.Chaos,
            new Position2(turret.Position.X + 150.0, turret.Position.Z), 100.0);
        var secondary = Unit(UnitType.Minion, EntityKind.Minion, Team.Chaos,
            new Position2(primary.Position.X + 100.0, primary.Position.Z), 0.0);
        TestAssert.True(runtime.Combat.ApplyHit(turret.NetId, primary.NetId, 100.0,
            DamageType.Physical, DamageSourceKind.Basic), "a turret hit on an armoured primary");
        TestAssert.Near(950.0, primary.Health, 1e-9, "the primary takes 100 through its 100 armour");
        TestAssert.Near(1000.0, secondary.Health, 1e-9, "an unfortified turret splashes nothing");
    }

    /// <summary>
    /// 1.0.0.132 (batch 772, wiki 129.48 / 129.49): Promote is <c>SummonerPromoteSR</c> on every map. Its record and script ship; 126's
    /// Summoner's Rift <c>SummonerPromote.luaobj</c> and the Dominion <c>SummonerOdinPromote</c> files are gone. The loadout reads "Promote"
    /// and the old <c>SummonerOdinPromote</c> (which the AIR offer still sends for id 18) as <c>SummonerPromoteSR</c>, and Surge is
    /// <c>SummonerBattleCry</c>. Every loadout spell has a spell record, the client's spell-name index (GameData.HasSpell).
    /// </summary>
    [Check("spells-summoners-promote-not-offered")]
    public static void PromoteIsTheSummonersRiftScript()
    {
        var paths = GameData.Default.Paths;
        foreach (var asset in new[] { "SummonerPromoteSR.luaobj", "SummonerPromoteSR.inibin", "PromoteMeBuff.luaobj" })
            TestAssert.True(File.Exists(paths.Data($"Spells/{asset}")), $"{asset} ships");
        foreach (var gone in new[] { "SummonerPromote.luaobj", "SummonerPromote.inibin", "SummonerOdinPromote.luaobj", "SummonerOdinPromote.inibin" })
            TestAssert.False(File.Exists(paths.Data($"Spells/{gone}")), $"{gone} is gone in 132");
        TestAssert.Equal("SummonerPromoteSR", SummonerLoadout.TryCanonicalFromDisplay("Promote"), "Promote is SummonerPromoteSR");
        TestAssert.Equal("SummonerPromoteSR", SummonerLoadout.TryCanonicalFromDisplay("SummonerOdinPromote"), "the AIR offer's old name too");
        TestAssert.Equal("Promote", SummonerLoadout.DisplayName("SummonerPromoteSR"), "and it shows as Promote");
        TestAssert.Equal("SummonerBattleCry", SummonerLoadout.TryCanonicalFromDisplay("Surge"), "Surge is SummonerBattleCry");
        foreach (var name in SummonerLoadout.CanonicalNames.Concat(SummonerLoadout.DominionNames))
            TestAssert.True(GameData.Default.HasSpell(name), $"{name} has a spell record");
        var records = Directory.EnumerateFiles(paths.Data("Spells"), "Summoner*.inibin")
            .Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal).ToArray();
        var expected = SummonerLoadout.CanonicalNames.Concat(SummonerLoadout.DominionNames)
            .Order(StringComparer.Ordinal).ToArray();
        TestAssert.Equal(expected, records, "the summoner spell records of this build are exactly the loadout's");
    }

    [Check("spells-summoners-advanced-dead-and-cc-gates")]
    public static void DeadAndCrowdControlGates()
    {
        var runtime = CreateRuntime();
        var hero = Hero(runtime, Team.Order, SpawnPoint(),
            ["SummonerTeleport", "SummonerFortify"]);
        var target = new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, Team.Order,
            new Position2(hero.Position.X + 300.0, hero.Position.Z))
        {
            Name = "Minion_Basic", ChampionName = "Annie", Health = 1000.0,
            MaxHealth = 1000.0, Alive = true, Created = true,
        };
        runtime.World.Register(target);
        hero.Alive = false;
        hero.RespawnAt = 10.0;
        var before = hero.SummonerCooldowns.ToArray();
        TestAssert.False(Cast(runtime, hero, 0, target.Position, target.NetId),
            "dead Teleport rejected");
        TestAssert.Equal(before[0], hero.SummonerCooldowns[0],
            "dead Teleport does not cool down");
        // The dead gate is the spell's own SpellData*IsDisabledWhileDead, which Fortify alone among the summoners ships as 0 (the unit's
        // cast check). 1.0.0.132 (batch 772, wiki 129.45): Fortify left the pool, so it is refused dead or alive.
        TestAssert.False(Cast(runtime, hero, 1, hero.Position),
            "dead Fortify is refused like a living one (132)");
    }

    private static MatchRuntime CreateRuntime() =>
        MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), GameData.Default, solo: true);

    private static Position2 SpawnPoint() =>
        new(MapDefinitions.HeroSpawn.X, MapDefinitions.HeroSpawn.Z);

    private static Entity Hero(MatchRuntime runtime, Team team, Position2 position,
        IReadOnlyList<string> spells, int level = 1)
    {
        var hero = new Entity(runtime.World.Allocate(UnitType.Hero), EntityKind.Hero,
            UnitType.Hero, team, position)
        {
            Name = team == Team.Order ? "OrderSeat" : "ChaosSeat",
            ChampionName = "Annie", Alive = true, Created = true, Visible = true,
            Health = 1000.0, MaxHealth = 1000.0, Mana = 1000.0, MaxMana = 1000.0,
            Level = level, Speed = 325.0, Radius = 35.0, AttackDamage = 100.0,
            AttackPeriod = 1.6, AttackWindup = 0.3,
        };
        hero.SummonerSpells = spells.ToList();
        runtime.World.Register(hero);
        return hero;
    }

    private static bool Cast(MatchRuntime runtime, Entity caster, int slot,
        Position2 point, uint target = 0) =>
        runtime.Spells.HandleCastRequest(caster,
            EncodeCast(slot, point, target), runtime.World.Clock.Now());

    /// <summary>
    /// One production simulation step (ProductionMatchComposition.Tick): the spell clock first, then every
    /// unit's buffs. A channel buff therefore runs its <c>OnBuffDeactivate</c> after the step's own cooldown
    /// decrement, and a spell tick alone never ends a buff.
    /// </summary>
    private static void Tick(MatchRuntime runtime, double seconds)
    {
        runtime.Spells.Tick(seconds);
        foreach (var unit in runtime.World.Entities.Values.ToArray()) runtime.Buffs.TickBuffs(unit, seconds);
    }

    /// <summary>
    /// Takes the running Teleport channel off its caster the way an external removal does, so the next cast
    /// starts from a clean slot. The shipped <c>OnBuffDeactivate</c> restores the slot and writes the
    /// interrupted cooldown as the buff leaves.
    /// </summary>
    private static void EndChannel(MatchRuntime runtime, Entity hero)
    {
        var channel = hero.Buffs.SingleOrDefault(buff => buff.Name == "SummonerTeleport");
        if (channel is not null) runtime.SpellBuffs.Remove(hero, channel.Slot);
    }

    private static byte[] EncodeCast(int slot, Position2 point, uint target)
    {
        var payload = new byte[34];
        payload[0] = 0xA3;
        payload[5] = checked((byte)((slot << 1) | 1));
        WriteSingle(payload, 6, (float)point.X);
        WriteSingle(payload, 10, 0.0f);
        WriteSingle(payload, 14, (float)point.Z);
        WriteSingle(payload, 18, (float)point.X);
        WriteSingle(payload, 22, 0.0f);
        WriteSingle(payload, 26, (float)point.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(30, 4), target);
        return payload;
    }

    private static void WriteSingle(byte[] payload, int offset, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4),
            BitConverter.SingleToInt32Bits(value));


}
