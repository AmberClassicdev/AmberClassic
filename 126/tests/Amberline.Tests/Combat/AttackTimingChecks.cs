using System.Buffers.Binary;
using System.Globalization;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Stats;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 803 (owner, 15 November 2026, native 126: Nunu's basic attacks feel wrong, worst after Blood Boil). The stock client times every
/// 0x0D itself, at receipt, from the attacker's replicated mAttackSpeedMod and the delay offsets of the attack slot the packet's slot byte
/// names (the character loader reads <c>%s_AttackDelayOffsetPercent</c> and <c>%s_AttackDelayCastOffsetPercent</c> for ExtraAttack1-3,
/// CritAttack, MissAttack and DodgeAttack, each defaulting to the base value; 126 the client, -). These checks
/// time the server's swings against that client formula, read independently from the character inibin.
/// </summary>
public static class AttackTimingChecks
{
    private static readonly string[] SlotNames = ["BaseAttack", "ExtraAttack1", "ExtraAttack2", "ExtraAttack3", "CritAttack", "MissAttack", "DodgeAttack"];

    /// <summary>The playable champions' attack slots whose shipped offsets differ from the base attack's (the batch 803 sweep of the 126 data).</summary>
    private static readonly string[] ExpectedDifferingSlots =
        ["Akali/CritAttack", "Brand/CritAttack", "Gangplank/CritAttack", "Malzahar/CritAttack", "Nunu/CritAttack", "Renekton/CritAttack",
         "Rumble/CritAttack", "Vladimir/CritAttack", "Warwick/CritAttack", "Xerath/CritAttack"];

    /// <summary>What the client plays for a 0x0D of <paramref name="character"/> in attack slot <paramref name="slot"/> at mAttackSpeedMod <paramref name="speed"/>.</summary>
    private static (double Period, double Windup) ClientSwing(string character, int slot, double speed)
    {
        var raw = InibinReader.Load(GameData.Default.Paths.Data($"Characters/{character}/{character}.inibin"));
        float Read(string key, float fallback) =>
            raw.TryGetValue(InibinReader.KeyHash("Data*" + key), out var value) ? (float)value.AsClientFloat() : fallback;
        var offset = Read("AttackDelayOffsetPercent", 0f);
        var castOffset = Read("AttackDelayCastOffsetPercent", 0f);
        if (slot > 0)
        {
            offset = Read(SlotNames[slot] + "_AttackDelayOffsetPercent", offset);
            castOffset = Read(SlotNames[slot] + "_AttackDelayCastOffsetPercent", castOffset);
        }
        // Map1 gcd_AttackDelay 1.6 and gcd_AttackDelayCastPercent 0.3; the period is clamped to the shipped 0.4-5 s band.
        var period = Math.Clamp(1.6 * (1.0 + offset) / speed, 0.4, 5.0);
        return (period, period * Math.Max(0.0, 0.3 + castOffset));
    }

    private static Match Nunu(out Entity foe)
    {
        var match = new Match("Nunu", foeChampion: "Orianna");
        match.Hero.Level = 9;
        match.Learn(5);
        foe = match.Foe;
        foe.MaxHealth = foe.Health = 50000.0;
        foe.Position = new Position2(match.Hero.Position.X + 150.0, match.Hero.Position.Z);
        match.Ally.Position = new Position2(match.Hero.Position.X - 150.0, match.Hero.Position.Z);
        return match;
    }

    /// <summary>
    /// Nunu's CritAttack ships its own AttackDelayOffsetPercent (0.12, the base attack 0.02), so the client plays his crit about 10 percent
    /// longer than his other swings. The server timed every swing with the base attack's period and wind-up: the crit's hit landed before
    /// the client's cast frame and the next 0x0D came before the client's crit had ended.
    /// </summary>
    [Check("attacks.crit-swing-times-its-own-attack-slot")]
    public static void CritSwingTimesItsOwnAttackSlot()
    {
        using var match = Nunu(out var foe);
        var hero = match.Hero;
        var capture = Observe(match, Team.Order, hero);
        match.Advance(Step);
        hero.CritChance = 1.0;
        var speed = hero.AttackSpeedMod;
        var basePeriod = hero.AttackPeriod;

        hero.AttackCooldown = 0.0;
        var sent = capture.Packets.Count;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, foe, new AttackRolls(0.0, 1.0, 1.0)), "the crit swing starts");
        var crit = ClientSwing("Nunu", 4, speed);
        TestAssert.True(crit.Period > basePeriod + 0.05, $"the shipped crit slot plays longer than the base attack ({crit.Period:0.000} vs {basePeriod:0.000})");
        TestAssert.Near(crit.Period, hero.AttackCooldown, 1e-6, "the next swing waits the crit slot's period");
        TestAssert.Near(crit.Windup, match.Runtime.Combat.PendingHits.Single(hit => hit.SourceNetId == hero.NetId).WindupRemaining, 1e-6,
            "the crit lands at the crit slot's cast frame");
        match.Advance(Step);
        var swing = capture.Packets.Skip(sent).Select(entry => entry.Packet).Single(packet => packet.Plaintext.Span[0] == 0x0D);
        TestAssert.Equal((byte)0x44, swing.Plaintext.Span[14], "the 0x0D names the crit slot");

        match.Advance(2.0);
        hero.AttackCooldown = 0.0;
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(hero, foe, new AttackRolls(1.0, 1.0, 1.0)), "the control swing starts");
        var normal = ClientSwing("Nunu", 0, speed);
        TestAssert.Near(normal.Period, hero.AttackCooldown, 1e-6, "an ordinary swing keeps the base attack's period");
        TestAssert.Near(normal.Windup, match.Runtime.Combat.PendingHits.Single(hit => hit.SourceNetId == hero.NetId).WindupRemaining, 1e-6,
            "an ordinary swing keeps the base attack's cast frame");
    }

    /// <summary>
    /// Every playable champion: the server's swing time in each attack slot is the client loader's, the per-slot offsets where a champion
    /// ships them and the base offsets elsewhere (the client's float getter, so a float32 0.08 and the base's 0.0799999982 are one value).
    /// </summary>
    [Check("attacks.every-champion-times-each-attack-slot-as-the-client")]
    public static void EveryChampionTimesEachAttackSlotAsTheClient()
    {
        var data = GameData.Default;
        var differing = new List<string>();
        foreach (var champion in ChampionCatalog.PlayableNames)
        {
            var hero = new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(0, 0))
            {
                ChampionName = champion,
                AttackSpeedMod = 1.25,
            };
            HeroAttackTimings.Apply(hero, data);
            for (var slot = 0; slot < SlotNames.Length; slot++)
            {
                var client = ClientSwing(champion, slot, hero.AttackSpeedMod);
                var server = HeroAttackTimings.ForSwing(hero, data, slot);
                TestAssert.Near(client.Period, server.Period, 1e-6, $"{champion} {SlotNames[slot]} period");
                TestAssert.Near(client.Windup, server.Windup, 1e-6, $"{champion} {SlotNames[slot]} wind-up");
                if (Math.Abs(server.Period - hero.AttackPeriod) > 1e-6) differing.Add($"{champion}/{SlotNames[slot]}");
            }
        }
        TestAssert.Equal(string.Join(", ", ExpectedDifferingSlots), string.Join(", ", differing.Order(StringComparer.Ordinal)), "the slots that ship their own delay offsets");
    }

    /// <summary>
    /// The owner's case on the wire: Nunu attacks an enemy champion, every swing a crit or a normal attack as the rolls fall, and casts Blood
    /// Boil (an instacast: +65 percent attack speed at rank five, replicated in the same flush) in the wind-up of a swing. For every 0x0D the
    /// client receives, the server's hit lands at the client's cast frame for that slot and attack speed (within one 30 Hz step), every
    /// 0x0D is followed by its hit, and no 0x0D arrives before the client's previous swing has ended.
    /// </summary>
    [Check("attacks.nunu-blood-boil-swings-land-on-the-clients-cast-frames")]
    public static void NunuBloodBoilSwingsLandOnTheClientsCastFrames()
    {
        using var match = Nunu(out var foe);
        var hero = match.Hero;
        // Half the swings crit: the crit slot is the one Nunu ships his own timing for.
        hero.Buffs.Add(new BuffState("b803-crit", new BuffPayload { CritChance = 0.5 }) { ExpiresAt = 10000.0 });
        match.Runtime.HeroStats.Refresh(hero, push: false);
        var capture = Observe(match, Team.Order, hero);
        match.Advance(0.5);
        var view = match.Runtime.Session.Participants[0].View;
        view.Known.Add(foe.NetId);
        view.Visible.Add(foe.NetId);
        var order = ClientGameplayPackets.MoveOrder(hero.NetId, 3, foe.Position, foe.NetId);
        TestAssert.True(match.Runtime.Session.DispatchGameplay(1, 1, order.Plaintext.Span, match.Runtime.Dependencies.Gameplay), "the attack order");
        for (var step = 0; step < 600 && Swings(capture, hero).Count < 4; step++) match.Advance(Step);
        match.Advance(0.05);
        TestAssert.True(match.Runtime.Session.DispatchGameplay(1, 1, CastRequest(hero.NetId, 1, hero.Position, hero.NetId),
            match.Runtime.Dependencies.Gameplay), "Blood Boil in the wind-up");
        match.Advance(10.0);
        TestAssert.True(Buff(hero, "BloodBoil") is not null, "Blood Boil holds");

        var swings = Swings(capture, hero);
        var hits = match.Log.Hits.Where(line => line.Contains(" Nunu->Foe ", StringComparison.Ordinal) && line.Contains(" Basic ", StringComparison.Ordinal))
            .Select(line => double.Parse(line.Split(' ')[0], CultureInfo.InvariantCulture)).ToList();
        TestAssert.True(swings.Count >= 12, $"Nunu keeps swinging ({swings.Count})");
        TestAssert.True(swings.Any(swing => swing.Slot == 4) && swings.Any(swing => swing.Slot != 4),
            $"the rolls gave crits and other swings (slots {string.Join(",", swings.Select(swing => swing.Slot))}, crit chance {hero.CritChance})");
        TestAssert.True(swings.Select(swing => swing.Speed).Distinct().Count() == 2, "Blood Boil changed the replicated attack speed once");
        for (var index = 0; index < swings.Count; index++)
        {
            var swing = swings[index];
            var client = ClientSwing("Nunu", swing.Slot, swing.Speed);
            var label = $"swing {index} at {swing.At:0.000} (slot {swing.Slot}, attack speed {swing.Speed:0.0000})";
            if (index + 1 < swings.Count)
                TestAssert.True(swings[index + 1].At >= swing.At + client.Period - 1e-6,
                    $"{label}: the next 0x0D at {swings[index + 1].At:0.000} comes before the client's swing ends at {swing.At + client.Period:0.000}");
            var hit = hits.Where(at => at > swing.At + 1e-9).DefaultIfEmpty(double.NaN).First();
            if (double.IsNaN(hit) && index == swings.Count - 1) continue;
            TestAssert.True(index + 1 == swings.Count || hit < swings[index + 1].At + 1e-9, $"{label}: no hit before the next swing");
            TestAssert.True(hit >= swing.At + client.Windup - 1e-6 && hit <= swing.At + client.Windup + Step + 1e-6,
                $"{label}: the hit at +{hit - swing.At:0.000} is not at the client's cast frame +{client.Windup:0.000}");
        }
    }

    /// <summary>Every 0x0D of <paramref name="hero"/> the client received, with its slot and the mAttackSpeedMod the client held at receipt.</summary>
    private static List<(double At, int Slot, double Speed)> Swings(WireCapture capture, Entity hero)
    {
        var result = new List<(double, int, double)>();
        var speed = double.NaN;
        foreach (var (at, packet) in capture.Packets)
        {
            var span = packet.Plaintext.Span;
            if (span[0] == 0xCC)
            {
                var decoded = WirePacketDecoder.Decode(span, 3);
                if (decoded.Fields.TryGetValue("unit_netid", out var unit) && unit is uint id && id == hero.NetId &&
                    decoded.Fields["fields"] is Dictionary<string, object?> fields && fields.TryGetValue("mAttackSpeedMod", out var value))
                    speed = (double)value!;
                continue;
            }
            if (span[0] != 0x0D || BinaryPrimitives.ReadUInt32LittleEndian(span[1..]) != hero.NetId) continue;
            var slot = span[14] - 0x40 is >= 0 and < 7 ? span[14] - 0x40 : 0;
            result.Add((at, slot, speed));
        }
        return result;
    }

    /// <summary>The 0xA2 cast request the client sends (<c>SpellSystem.ParseCastRequest</c> reads it): actor, slot, point, end point, target.</summary>
    private static byte[] CastRequest(uint actorNetId, int slot, Position2 point, uint targetNetId)
    {
        var payload = new byte[34];
        payload[0] = 0xA2;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1, 4), actorNetId);
        payload[5] = checked((byte)(slot << 1));
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(6, 4), (float)point.X);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(14, 4), (float)point.Z);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(18, 4), (float)point.X);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(26, 4), (float)point.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(30, 4), targetNetId);
        return payload;
    }
}
