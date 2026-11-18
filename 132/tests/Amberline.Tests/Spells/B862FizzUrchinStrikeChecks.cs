using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 862 (17 November 2026, owner: "Fizz Q sometimes skips its dash animation and teleports through the target", 1.0.0.132). The shipped
/// <c>FizzPiercingStrike</c> <c>TargetExecute</c> starts the dash (<c>BBMove</c>, the 0x68), plays Spell1, adds its buff and then, for a champion only
/// (<c>BBIf CO_IS_TYPE_HERO</c>), orders Fizz <c>AI_ATTACKTO</c> it while the dash runs. Out of Fizz's attack reach that order closed on the target through
/// the hero order's walk, which the running dash refuses, and the refusal's one-point route at the dash's start went out after the 0x68 in the same flush:
/// every client stopped the dash there, and the landing's stop 0.4 s later moved Fizz across. A champion in reach (the order stands still) and a minion
/// (no order) were the cases that looked right.
/// </summary>
public static class B862FizzUrchinStrikeChecks
{
    /// <summary>A target <paramref name="Along"/> units east of Fizz (and <paramref name="Side"/> north), and what Fizz is busy with when he casts.</summary>
    private sealed record Case(string Name, bool Champion, double Along, double Side = 0.0, Action<Match, WireCapture>? Busy = null);

    private static readonly Case[] Cases =
    [
        new("a standing champion out of reach", true, 450.0),
        new("a champion out of cast range (walk, then cast)", true, 800.0),
        new("a champion out of reach while Fizz walks away", true, 300.0, Busy: (match, _) =>
        {
            match.Runtime.Spells.HeroOrders!.StartMove(match.Hero, new Position2(match.Hero.Position.X - 600.0, match.Hero.Position.Z));
            match.Advance(0.2);
        }),
        new("a champion out of reach in the windup of an attack on a minion", true, 400.0, 150.0, (match, own) =>
        {
            var minion = LaneMinion(match, new Position2(match.Hero.Position.X + 120.0, match.Hero.Position.Z));
            minion.MaxHealth = minion.Health = 50000.0;
            See(match, minion);
            var order = ClientGameplayPackets.MoveOrder(match.Hero.NetId, 3, minion.Position, minion.NetId);
            TestAssert.True(match.Runtime.Session.DispatchGameplay(1, 1, order.Plaintext.Span, match.Runtime.Dependencies.Gameplay), "the attack order");
            for (var step = 0; step < 90 && !own.Packets.Any(item => item.Packet.Plaintext.Span[0] == 0x0D && About(item.Packet) == match.Hero.NetId); step++)
                match.Advance(Step);
        }),
        new("a champion in reach (control)", true, 150.0),
        new("a minion (control: no order)", false, 450.0),
    ];

    /// <summary>The 0xA3 cast request of the 132 client: actor, slot, point, end point, target.</summary>
    private static bool Cast(Match match, int slot, Entity target)
    {
        var payload = new byte[34];
        payload[0] = 0xA3;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1, 4), match.Hero.NetId);
        payload[5] = checked((byte)(slot << 1));
        foreach (var offset in new[] { 6, 18 })
        {
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(offset, 4), (float)target.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(offset + 8, 4), (float)target.Position.Z);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(30, 4), target.NetId);
        return match.Runtime.Session.DispatchGameplay(1, 1, payload, match.Runtime.Dependencies.Gameplay);
    }

    private static void See(Match match, Entity unit)
    {
        var view = match.Runtime.Session.Participants[0].View;
        view.Known.Add(unit.NetId);
        view.Visible.Add(unit.NetId);
    }

    /// <summary>The net id a packet is about: the sender of a unit packet, the first entry's unit of a 0x68 group.</summary>
    private static uint About(PlaintextPacket packet)
    {
        var span = packet.Plaintext.Span;
        if (span.Length < 5) return 0u;
        return span[0] == VisionPackets.DashOpcode && span.Length >= 19
            ? BinaryPrimitives.ReadUInt32LittleEndian(span[15..])
            : BinaryPrimitives.ReadUInt32LittleEndian(span[1..]);
    }

    /// <summary>The points of a 0xC2 route (net id, sync id, then float pairs).</summary>
    private static Position2[] Points(PlaintextPacket packet)
    {
        var span = packet.Plaintext.Span;
        var points = new Position2[(span.Length - 9) / 8];
        for (var index = 0; index < points.Length; index++)
            points[index] = new Position2(BinaryPrimitives.ReadSingleLittleEndian(span[(9 + index * 8)..]),
                BinaryPrimitives.ReadSingleLittleEndian(span[(13 + index * 8)..]));
        return points;
    }

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Z - b.Z, 2));

    [Check("spells-live-fizz-urchin-strike-on-a-champion-out-of-reach-keeps-its-dash")]
    public static void UrchinStrikeOnAChampionKeepsItsDash()
    {
        foreach (var item in Cases)
        {
            using var match = new Match("Fizz", foeChampion: "Orianna");
            var hero = match.Hero;
            hero.Level = 9;
            match.Learn(5);
            match.Refill();
            match.Runtime.HeroStats.Refresh(hero, push: false);
            match.Ally.Position = new Position2(hero.Position.X - 400.0, hero.Position.Z);
            var own = Observe(match, Team.Order, hero);
            var enemy = Observe(match, Team.Chaos, match.Foe, 9002);
            var target = new Position2(hero.Position.X + item.Along, hero.Position.Z + item.Side);
            Entity victim;
            if (item.Champion)
            {
                victim = match.Foe;
                victim.Position = target;
            }
            else
            {
                match.Foe.Position = new Position2(hero.Position.X, hero.Position.Z + 1500.0);
                victim = LaneMinion(match, target);
            }
            victim.MaxHealth = victim.Health = 50000.0;
            See(match, victim);
            item.Busy?.Invoke(match, own);
            match.Advance(Step);
            var start = new[] { own.Packets.Count, enemy.Packets.Count };
            TestAssert.True(Cast(match, 0, victim), $"{item.Name}: Urchin Strike is accepted");
            for (var step = 0; step < 45 && hero.ForcedMove is null; step++) match.Advance(Step);
            TestAssert.True(hero.ForcedMove is { Effect: "FizzPiercingStrike" }, $"{item.Name}: the dash runs");
            var landing = hero.ForcedMove!.Destination;
            for (var step = 0; step < 45 && hero.ForcedMove is not null; step++) match.Advance(Step);
            TestAssert.True(hero.ForcedMove is null && Distance(hero.Position, landing) <= 4.0, $"{item.Name}: Fizz lands at the dash's end");
            var landed = match.Now;
            if (item.Champion)
            {
                TestAssert.Equal(victim.NetId, hero.AttackTarget ?? 0u, $"{item.Name}: the script's attack order holds through the dash");
                for (var step = 0; step < 30 && !own.Packets.Any(entry => entry.At >= landed && entry.Packet.Plaintext.Span[0] == 0x0D &&
                         About(entry.Packet) == hero.NetId); step++)
                    match.Advance(Step);
                TestAssert.True(own.Packets.Any(entry => entry.At >= landed && entry.Packet.Plaintext.Span[0] == 0x0D && About(entry.Packet) == hero.NetId &&
                                                         BinaryPrimitives.ReadUInt32LittleEndian(entry.Packet.Plaintext.Span[5..]) == victim.NetId),
                    $"{item.Name}: Fizz attacks the champion within a second of landing");
            }
            for (var view = 0; view < 2; view++)
            {
                var (who, packets) = view == 0 ? ("Fizz's client", own.Packets) : ("the enemy's client", enemy.Packets);
                var after = packets.Skip(start[view]).Select(entry => entry.Packet).Where(packet => About(packet) == hero.NetId).ToList();
                var dashes = after.Select((packet, index) => (packet, index)).Where(entry => entry.packet.Plaintext.Span[0] == VisionPackets.DashOpcode).ToList();
                TestAssert.Equal(1, dashes.Count, $"{item.Name}: {who} gets one 0x68 dash");
                var route = after.Skip(dashes[0].index + 1).FirstOrDefault(packet => packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode);
                var points = route is null ? Array.Empty<Position2>() : Points(route);
                TestAssert.True(points.Length == 1 && Distance(points[0], landing) <= 4.0,
                    $"{item.Name}: the first route {who} gets after the 0x68 is the landing's stop at the dash's end, not one that cuts the dash: " +
                    string.Join(">", points.Select(point => $"({point.X:0},{point.Z:0})")));
            }
            TestAssert.Equal(0, match.Module.ScriptFailures.Count,
                $"{item.Name}: no script failed: " + string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)));
        }
    }
}
