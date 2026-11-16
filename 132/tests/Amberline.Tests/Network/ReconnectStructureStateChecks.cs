using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation;
using Amberline.Simulation.Combat;

namespace Amberline.Tests.Network;

/// <summary>
/// Batch 802 (owner report, 15 November 2026, Twisted Treeline: after a reconnect, with the lane's first turrets down, Zilean had to stand
/// almost inside the next turret to attack it). A production match on Summoner's Rift, Twisted Treeline and the Crystal Scar: the Order seat
/// kills the Chaos structures of one lane until an inhibitor is down (respawning), then its client restarts and reconnects. What the
/// returning client is sent must leave it holding the server's state of every structure: a living lane turret's protection in a packet that
/// lands after the client's map script has re-protected its non-front towers (the 10 s <c>AllowDamageOnBuildings</c> timer fires on the
/// first frame after the reconnect's clock sync, 126's <c>0xC9</c> and 132's <c>0xCA</c>: the client passes the unclamped
/// clock jump to <c>UpdateLuaTimers</c>, AB 2690165), with the highest sync id it holds for that turret; a destroyed turret bound and
/// force-killed; a dead inhibitor's remaining respawn time; a living building's protection; every living structure's health (one turret hit
/// and the Chaos nexus lowered before the reconnect). The seat that never left holds the same protection.
/// 132: the IsInvulnerable block is 0xCD and the targetability is 0xF4 (CombatPackets.BuildingStats, TurretProtectionFlags); the client's
/// opcodes are 132's (0xC6/0xC7 handshake, 0xA5 load progress, 0x56 ready, 0x76 order).
/// </summary>
public static class ReconnectStructureStateChecks
{
    private const long OrderSeat = 11, ChaosSeat = 12, Returning = 15;

    private sealed class Capture(long connectionId, List<Sent> sink, Func<double> now) : IReplicationTransport
    {
        public void Send(PlaintextPacket packet) => sink.Add(new Sent(now(), connectionId, packet));
    }

    private readonly record struct Sent(double At, long Connection, PlaintextPacket Packet)
    {
        public byte Opcode => Packet.Plaintext.Span[0];
    }

    [Check("replay.reconnect-replays-every-structure-state", Serial = true)] // Serial: each match reads the process's map.
    public static void ReconnectReplaysEveryStructureState()
    {
        var failures = new List<string>();
        foreach (var mapId in new[] { GameMaps.Normal, GameMaps.TwistedTreeline, GameMaps.CrystalScar })
        {
            try { failures.AddRange(Run(mapId).Select(failure => $"map {mapId}: {failure}")); }
            catch (Exception failure) { failures.Add($"map {mapId}: {failure.Message}"); }
        }
        TestAssert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static List<string> Run(int mapId)
    {
        var paths = GameDataPaths.Default.ForMap(mapId);
        GameData.ConfigureDefault(paths);
        try
        {
            using var runtime = MatchRuntime.CreateProduction(NavigationGrid.Load(paths), GameData.Default, orderChampion: "Zilean",
                readySettleSeconds: 0.0, mapId: mapId);
            runtime.World.Clock.SetVirtual(0.0);
            var session = runtime.Session;
            var wire = new List<Sent>();
            double Now() => runtime.World.Clock.Now();
            void Collect(IEnumerable<SessionOutboundPacket> items)
            {
                foreach (var item in items) wire.Add(new Sent(Now(), item.ConnectionId, item.Packet));
            }
            void Advance(double seconds)
            {
                for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += 1.0 / 30.0)
                {
                    runtime.Tick(1.0 / 30.0);
                    Collect(session.RunSchedulers());
                }
            }
            foreach (var (connectionId, playerId) in new[] { (OrderSeat, 1UL), (ChaosSeat, 2UL) })
            {
                session.Receive(connectionId, 0, session.BuildKeyCheck(playerId));
                session.Receive(connectionId, 1, [0xC6, 0, 0, 0, 0]);
                session.Receive(connectionId, 1, [0xC7, 0, 0, 0, 0]);
            }
            for (var guard = 0; !session.Started && guard < 6000; guard++)
            {
                runtime.World.Clock.Advance(1.0 / 30.0);
                Collect(session.RunSchedulers());
            }
            TestAssert.True(session.Started, "the scheduler starts the match");
            foreach (var connectionId in new[] { OrderSeat, ChaosSeat })
            {
                var seat = session.Participant(connectionId)!;
                runtime.Delivery.AddObserver(new ReplicationObserver(seat.PlayerId, seat.Team, seat.Hero!.NetId,
                    new Capture(connectionId, wire, Now), ready: true, view: seat.View, clientId: seat.ClientId));
            }
            Advance(12.0);

            // The Order hero takes one Chaos lane down: each structure of that lane the ladder leaves unprotected, until its inhibitor falls.
            var zilean = session.Participant(OrderSeat)!.Hero!;
            var lane = session.MapEntities.Where(entity => entity.Team == Team.Chaos && entity.Name.Contains("_L", StringComparison.Ordinal) &&
                (entity.Kind == EntityKind.Inhibitor || entity.UnitType == UnitType.Turret && !TurretRules.IsShrineTurret(entity))).ToList();
            for (var kill = 0; kill < 6 && lane.Any(entity => entity.Alive && entity.Kind == EntityKind.Inhibitor); kill++)
            {
                var next = lane.FirstOrDefault(entity => entity.Alive && !entity.Protected);
                if (next is null) break;
                TestAssert.True(runtime.Combat.ApplyHit(zilean.NetId, next.NetId, 1.0e7) && !next.Alive, $"{next.Name} dies");
                Advance(1.0);
            }
            if (mapId != GameMaps.CrystalScar)
                TestAssert.True(lane.Any(entity => entity.Kind == EntityKind.Inhibitor && !entity.Alive), "the lane's inhibitor is down");
            // A released Chaos turret (the nexus tower the dead inhibitor released, else another lane's outer one) takes a partial hit, and the
            // Chaos nexus has lost health, so the reconnect must carry live health too.
            var released = session.MapEntities.Where(entity => entity.Team == Team.Chaos && entity.Alive && !entity.Protected &&
                    entity.UnitType == UnitType.Turret && !TurretRules.IsShrineTurret(entity))
                .OrderByDescending(entity => MapDefinitions.TurretTiers.TryGetValue(entity.Name, out var tier) && tier == TurretTier.Nexus).FirstOrDefault();
            if (released is not null)
                TestAssert.True(runtime.Combat.ApplyHit(zilean.NetId, released.NetId, 300.0) && released.Health < released.MaxHealth, $"{released.Name} is hit");
            if (session.MapEntities.FirstOrDefault(entity => entity.Kind == EntityKind.Nexus && entity.Team == Team.Chaos) is { } nexus)
                nexus.Health -= 500.0;

            // The Order client restarts and reconnects (ReconnectStateChecks' handshake), its new transport observed as production attaches it.
            zilean.Position = MapDefinitions.TeamSpawn(Team.Order);
            Advance(1.0);
            var mark = wire.Count;
            TestAssert.True(session.Disconnect(OrderSeat), "the Order seat drops");
            runtime.Delivery.RemoveObserver(session.Participants.Single(seat => seat.Team == Team.Order).PlayerId);
            session.Receive(Returning, 0, session.BuildKeyCheck(1));
            session.Receive(Returning, 1, [0xC6]);
            session.Receive(Returning, 1, [0xC7, 0, 0, 0, 0]);
            for (var guard = 0; guard < 900 && Now() < (session.Participant(Returning)!.SpawnAt ?? 0.0); guard++) Advance(1.0 / 30.0);
            Collect(session.RunSchedulers());
            session.Receive(Returning, 1, [0xA5, 0, 0, 0, 0, 1]);
            session.Receive(Returning, 1, [0x56]);
            session.Receive(Returning, 1, [0xC7, 0, 0, 0, 0]);
            Collect(session.RunSchedulers());
            var returning = session.Participant(Returning)!;
            TestAssert.True(returning is { Ready: true, Spawned: true }, "the returning seat is ready");
            var respawnLeft = lane.Where(entity => entity.Kind == EntityKind.Inhibitor)
                .ToDictionary(entity => entity.NetId, entity => entity.RespawnAt - runtime.Combat.CurrentTime);
            // The burst's health is the server's at the reconnect; later regeneration reaches a client that sees the structure (fog).
            var readyAt = Now();
            var healthAtReady = session.MapEntities.ToDictionary(entity => entity.NetId, entity => entity.Health);
            runtime.Delivery.AddObserver(new ReplicationObserver(returning.PlayerId, returning.Team, returning.Hero!.NetId,
                new Capture(Returning, wire, Now), ready: true, view: returning.View, clientId: returning.ClientId));
            Advance(8.0);

            var failures = new List<string>();
            var back = wire.Skip(mark).Where(sent => sent.Connection == Returning).ToList();
            // 132 opcodes: the clock sync is 0xCA (SpawnPackets.GameClock), the turret bind 0xA6 (SpawnPackets.CreateTurret).
            var sync = back.FindIndex(sent => sent.Opcode == 0xCA);
            if (sync < 0) return ["the returning client got no 0xCA"];
            var syncAt = back[sync].At;
            foreach (var structure in session.MapEntities.Where(entity =>
                         entity.UnitType == UnitType.Turret && !TurretRules.IsShrineTurret(entity) || entity.Kind is EntityKind.Inhibitor or EntityKind.Nexus))
            {
                var label = structure.Name;
                if (structure.UnitType == UnitType.Turret && !structure.Alive)
                {
                    var bind = back.FindIndex(sent => sent.Opcode == 0xA6 && Unit(sent, 5) == structure.NetId);
                    var dead = back.FindIndex(sent => sent.Opcode == 0x1E && Unit(sent, 1) == structure.NetId);
                    if (bind < 0 || dead < bind) failures.Add($"{label}: the destroyed turret is not bound then force-killed");
                    continue;
                }
                if (structure.Kind == EntityKind.Inhibitor && !structure.Alive)
                {
                    var state = back.LastOrDefault(sent => sent.Opcode == 0x2D && Unit(sent, 1) == structure.NetId);
                    if (state.Packet is null) { failures.Add($"{label}: the dead inhibitor's state is not sent"); continue; }
                    var bytes = state.Packet.Plaintext.Span;
                    var seconds = BinaryPrimitives.ReadUInt16LittleEndian(bytes[5..]);
                    if (bytes[7] != 0 || Math.Abs(seconds - respawnLeft[structure.NetId]) > 1.0)
                        failures.Add($"{label}: dead inhibitor sent alive={bytes[7]} respawn {seconds} s, server {respawnLeft[structure.NetId]:0.0} s");
                    continue;
                }
                var health = back.Where(sent => sent.At <= readyAt + 1e-9 && HealthOf(sent.Packet, structure.NetId) is not null).ToList();
                if (health.Count == 0) failures.Add($"{label}: the reconnect burst carries no health");
                else if (HealthOf(health.MaxBy(sent => SyncId(sent.Packet)).Packet, structure.NetId) is float hp &&
                         Math.Abs(hp - healthAtReady[structure.NetId]) > 1.0)
                    failures.Add($"{label}: the reconnect burst carries {hp:0} health, the server held {healthAtReady[structure.NetId]:0}");
                var expected = (Invulnerable: structure.Protected ? 1u : 0u,
                    SFlags: structure.Protected ? CombatPackets.SFlagNontargetableEnemy : CombatPackets.SFlagTargetableToAll);
                foreach (var (seat, stream, lua) in new[] { (Returning, back, structure.UnitType == UnitType.Turret),
                             (ChaosSeat, wire.Where(sent => sent.Connection == ChaosSeat).ToList(), false) })
                {
                    // 132 splits the state: IsInvulnerable in a sync-gated 0xCD block (the highest sync id stands), the targetability in 0xF4
                    // (the last one stands).
                    var blocks = stream.Where(sent => InvulnerableOf(sent.Packet, structure.NetId) is not null).ToList();
                    var flags = stream.Where(sent => sent.Opcode == CombatPackets.UnitTargetableStatusOpcode && Unit(sent, 1) == structure.NetId).ToList();
                    if (blocks.Count == 0 || flags.Count == 0) { failures.Add($"seat {seat} {label}: no protection state"); continue; }
                    var block = blocks.MaxBy(sent => SyncId(sent.Packet));
                    var flag = flags[^1];
                    var state = (Invulnerable: InvulnerableOf(block.Packet, structure.NetId)!.Value,
                        SFlags: BinaryPrimitives.ReadUInt32LittleEndian(flag.Packet.Plaintext.Span[6..]));
                    if (state != expected)
                        failures.Add($"seat {seat} {label}: holds invulnerable {state.Invulnerable} sflags 0x{state.SFlags:X}, server {expected.Invulnerable} 0x{expected.SFlags:X}");
                    // The client's map script re-protects its lane towers on its first frame after the clock sync; only a later packet stands.
                    if (lua && Math.Min(block.At, flag.At) < syncAt + 1.0)
                        failures.Add($"seat {seat} {label}: the last protection state lands {Math.Min(block.At, flag.At) - syncAt:0.00} s after the 0xCA, before the map script has run");
                }
            }
            // The owner's case on Twisted Treeline: the returning Zilean's attack on the released turret lands from his own range.
            if (mapId == GameMaps.TwistedTreeline && released is { Alive: true } target)
            {
                var start = MapDefinitions.LanePaths.Values.SelectMany(points => points)
                    .OrderBy(point => Math.Abs(Distance(point, target.Position) - 900.0)).First();
                zilean.Position = start;
                Advance(0.5);
                var order = new byte[22]; order[0] = 0x76; order[5] = 3;
                BinaryPrimitives.WriteUInt32LittleEndian(order.AsSpan(1), zilean.NetId);
                BinaryPrimitives.WriteSingleLittleEndian(order.AsSpan(6), (float)target.Position.X);
                BinaryPrimitives.WriteSingleLittleEndian(order.AsSpan(14), (float)target.Position.Z);
                BinaryPrimitives.WriteUInt32LittleEndian(order.AsSpan(18), target.NetId);
                TestAssert.True(session.DispatchGameplay(Returning, 1, order, runtime.Dependencies.Gameplay), "the returning seat's attack order is taken");
                var health = target.Health;
                var hitFrom = -1.0;
                for (var step = 0; step < 180 && hitFrom < 0.0; step++)
                {
                    Advance(1.0 / 30.0);
                    if (target.Health < health) hitFrom = Distance(zilean.Position, target.Position);
                }
                if (hitFrom < zilean.AttackRange)
                    failures.Add($"the returning Zilean's attack on {target.Name} lands from {hitFrom:0}, inside his {zilean.AttackRange} range");
            }
            return failures;
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    private static uint Unit(Sent sent, int offset) => sent.Packet.Plaintext.Length >= offset + 4
        ? BinaryPrimitives.ReadUInt32LittleEndian(sent.Packet.Plaintext.Span[offset..]) : 0u;

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static uint SyncId(PlaintextPacket packet) => BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[5..]);

    /// <summary>
    /// The mHP a 132 0xCD sets for one structure: a 23-byte health block (turret LOCAL1 bit 8, building bit 0), a turret's 83-byte stats block
    /// (field 8) or a building's 27-byte stats block (field 0); null for any other packet (CombatPackets.ReplicationHp, TurretStats, BuildingStats).
    /// </summary>
    private static float? HealthOf(PlaintextPacket packet, uint netId)
    {
        var span = packet.Plaintext.Span;
        if (span.Length < 23 || span[0] != 0xCD || span[10] != 0x02 && span[10] != 0x0A ||
            BinaryPrimitives.ReadUInt32LittleEndian(span[11..]) != netId) return null;
        var mask = BinaryPrimitives.ReadUInt32LittleEndian(span[15..]);
        return span.Length switch
        {
            23 when mask is 0x100 or 0x01 => BinaryPrimitives.ReadSingleLittleEndian(span[19..]),
            83 when mask == 0x3FFF => BinaryPrimitives.ReadSingleLittleEndian(span[51..]),
            27 when mask == 0x03 => BinaryPrimitives.ReadSingleLittleEndian(span[19..]),
            _ => null,
        };
    }

    /// <summary>
    /// The IsInvulnerable a 132 0xCD sets for one unit: a turret's 23-byte flags block (LOCAL1 bit 12) or 83-byte stats block (field 12), or a
    /// building's 27-byte stats block (field 1); null for any other packet (CombatPackets.TurretProtectionFlags, TurretStats, BuildingStats).
    /// </summary>
    private static uint? InvulnerableOf(PlaintextPacket packet, uint netId)
    {
        var span = packet.Plaintext.Span;
        if (span.Length < 19 || span[0] != 0xCD || BinaryPrimitives.ReadUInt32LittleEndian(span[11..]) != netId) return null;
        var mask = BinaryPrimitives.ReadUInt32LittleEndian(span[15..]);
        return span.Length switch
        {
            23 when mask == 0x1000 => BinaryPrimitives.ReadUInt32LittleEndian(span[19..]),
            83 when mask == 0x3FFF => BinaryPrimitives.ReadUInt32LittleEndian(span[67..]),
            27 when mask == 0x03 => BinaryPrimitives.ReadUInt32LittleEndian(span[23..]),
            _ => null,
        };
    }
}
