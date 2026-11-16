using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    // Batch 802 (owner report, 15 November 2026: Zilean's Time Bomb on a unit that leaves sight and comes back still explodes, but its particle
    // is gone). The production composition: Zilean (seat 0) bombs the enemy (seat 1) in sight, the enemy steps out of Zilean's sight for a second
    // and comes back. The server leaves the bomb to the client's own copy through the fog trip: one create (0x8C, TimeBomb_green for Zilean's
    // team) at the landing, no kill (0x3D) and no second create at the leave (0x54) or the reveal (0xC2), no re-add of the TimeBomb buff at the
    // reveal (the client kept it; ReplicationView.Buffs), and the kill only when the bomb blows. The stock client keeps a bound particle while
    // its unit is fogged and draws it again once the unit is seen (the client and OnVisibleEnterClient touch no particle,
    // AB 2394059 and 2398734; the emitter's Draw skips a particle only while both its bind and its target attachment are hidden, retail
    // The client). What it does lose is a system that leaves the camera view, and
    // TimeBomb_green's sphere is a single particle whose emitter lives 1 s (TimeBomb*e-life 1, single-particle, p-life 4, no
    // SimulateWhileOffScreen): see docs/maintenance/2026-11-15-batch-802-resend.md.
    [Check("replay.time-bomb-survives-a-fog-trip-without-a-resend")]
    public static void TimeBombSurvivesAFogTripWithoutAResend()
    {
        using var match = new Match("Zilean", Team.Order);
        var zilean = match.Hero(0);
        var foe = match.Hero(1);
        zilean.Position = new Position2(7000, 7000);
        foe.Position = new Position2(7300, 7000);
        match.Runtime.Delivery.Flush(); match.Clear();
        var wire = match.Captures.Select(_ => new Capture()).ToArray();
        void Run(double seconds)
        {
            for (var elapsed = 0.0; elapsed < seconds - 1e-9; elapsed += 0.1)
            {
                match.Runtime.Tick(0.1);
                for (var seat = 0; seat < wire.Length; seat++)
                {
                    wire[seat].Packets.AddRange(match.Captures[seat].Packets);
                    match.Captures[seat].Packets.Clear();
                }
            }
        }
        bool Names(PlaintextPacket packet, byte opcode, uint netId) =>
            Opcode(packet) == opcode && BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == netId;

        match.Cast(0, 0, foe);
        Run(1.0);
        var bomb = wire[0].Packets.Where(packet => Opcode(packet) == FxPackets.CreateGroupOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[6..]) == SpellPackets.BuffNameHash("TimeBomb_green"))
            .Select(packet => packet.EntityReferences.First(reference => reference.Role == LeaguePacketEntityRole.Create).NetId).SingleOrDefault();
        TestAssert.True(bomb != 0, "the bomb landed: Zilean's client got TimeBomb_green");
        foe.Position = new Position2(11000, 11000);
        Run(1.0);
        TestAssert.False(match.Runtime.Session.Participants[0].View.Visible.Contains(foe.NetId), "Zilean lost sight of the carrier");
        foe.Position = new Position2(7300, 7000);
        Run(1.0);
        TestAssert.True(match.Runtime.Session.Participants[0].View.Visible.Contains(foe.NetId), "Zilean sees the carrier again");
        var zileanWire = wire[0];
        TestAssert.True(zileanWire.Packets.Any(packet => Names(packet, 0x54, foe.NetId)) &&
                        zileanWire.Packets.Any(packet => Names(packet, VisionPackets.EnterVisibilityOpcode, foe.NetId)),
            "Zilean's client was told of the leave and the reveal");
        for (var seat = 0; seat < wire.Length; seat++)
        {
            TestAssert.Equal(1, wire[seat].Packets.Count(packet => Opcode(packet) == FxPackets.CreateGroupOpcode &&
                packet.EntityReferences.Any(reference => reference.NetId == bomb && reference.Role == LeaguePacketEntityRole.Create)),
                $"seat {seat}: one create of the bomb, none at the leave or the reveal");
            TestAssert.Equal(0, CountKill(wire[seat], bomb), $"seat {seat}: the bomb is not killed before it blows");
        }
        var timeBomb = SpellPackets.BuffNameHash("TimeBomb");
        // 0xBF: the holder at 1, the name hash at 9 (target, slot, type, count, hidden, hash, running time, duration, caster).
        TestAssert.Equal(1, zileanWire.Packets.Count(packet => Opcode(packet) == 0xBF && packet.Plaintext.Length >= 13 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[1..]) == foe.NetId &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Plaintext.Span[9..]) == timeBomb), "the TimeBomb buff is added once, not again at the reveal");
        Run(3.0);
        TestAssert.Equal(1, CountKill(wire[0], bomb), "the bomb's particle is killed when it blows");
    }
}
