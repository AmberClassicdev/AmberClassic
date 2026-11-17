using System.Buffers.Binary;
using System.Text;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Spectator;
using Amberline.Server;
using Amberline.Tools.Diagnostics;

namespace Amberline.Tests.Network;

/// <summary>
/// Batch 847 (spectating S2): the recorder's stream from a headless ten-bot match, read back with S1's decoder (written from the client's
/// block reader, <see cref="SpectatorChunkChecks.Client"/>), against reference views that share no code with the spectator view: the ten seats' own
/// views (what the players' clients hold) and the packet-stream checker's rules. The packet numbers are the line's own builders'.
/// </summary>
public static class SpectatorRecorderChecks
{
    // The stock client's gameplay channel to the server (DuelSession's ClientGameplayChannel).
    private const byte ClientChannel = 1;

    [Check("network.spectate-recorder-stream")]
    public static void Stream()
    {
        var root = Directory.CreateTempSubdirectory("league-spectate-recorder-").FullName;
        try
        {
            var key = SpectatorChunk.NewChunkKey();
            var folder = Path.Combine(root, "spectator");
            long knownChecks = 0, knownFaults = 0, maskChecks = 0, maskFaults = 0;
            var faults = new List<string>();
            uint stealthed = 0, chatter = 0;
            float stealthedAt = 0, surrenderedAt = 0;
            double? pausedAt = null;
            bool chatted = false, resumed = false;
            var report = HeadlessMatch.Run(new HeadlessMatchOptions(847, Path.Combine(root, "match"), 6.0)
            {
                SpectatorStream = folder,
                SpectatorKey = key,
                OnStep = runtime =>
                {
                    if (runtime.Session.Spectator is not { Started: true, Ended: false, Stopped: false } recorder ||
                        runtime.Session.PlaytestHudTime is not double now) return;
                    // A seat's client, as the network shell hands its packets to the session: all chat, team chat and a command at 20 s, a pause
                    // at 30 s and its resume 40 s of wall time later (then the 5 s countdown).
                    var seat = runtime.Session.Participants[0];
                    var wall = runtime.World.Clock.Monotonic();
                    void Wire(byte channel, ReadOnlySpan<byte> payload)
                    {
                        var blocks = payload.Length - payload.Length % 8;
                        runtime.Session.Receive(seat.ConnectionId!.Value, channel,
                            [.. runtime.Cipher.Encrypt(payload[..blocks]), .. payload[blocks..]], runtime.Dependencies.Gameplay);
                    }
                    if (!chatted && now >= 20.0)
                    {
                        chatted = true;
                        chatter = seat.ClientId;
                        Wire(ChatPackets.Channel, ChatPackets.Envelope(seat.ClientId, false, "gl hf"u8).Plaintext.Span);
                        Wire(ChatPackets.Channel, ChatPackets.Envelope(seat.ClientId, true, "push mid"u8).Plaintext.Span);
                        Wire(ChatPackets.Channel, ChatPackets.Envelope(seat.ClientId, false, "/cheats"u8).Plaintext.Span);
                    }
                    if (pausedAt is null && now >= 30.0)
                    {
                        Wire(ClientChannel, PausePackets.Pause(seat.ClientId, 1).Plaintext.Span);
                        pausedAt = wall;
                    }
                    if (pausedAt is double paused && !resumed && wall >= paused + 40.0)
                    {
                        Wire(ClientChannel, PausePackets.Resume(seat.ClientId, delayed: true).Plaintext.Span);
                        resumed = true;
                    }
                    var spectator = recorder.Observer.View;
                    var seats = runtime.Delivery.Observers.Where(observer => !observer.Spectator && observer.Ready).ToArray();
                    // The spectator learns no unit that no seat holds: every unit it names reached a player's client the same way.
                    var held = seats.SelectMany(seat => seat.View.Known).ToHashSet();
                    foreach (var id in spectator.Known)
                    {
                        knownChecks++;
                        if (held.Contains(id)) continue;
                        knownFaults++;
                        if (faults.Count < 10) faults.Add($"{now:0.0}s: the spectator holds {id} ({runtime.World.Get(id)?.Name}) that no seat holds");
                    }
                    // A unit's team bit is set exactly while a seat of that team sees it (a shrine's sight has its own phase; a corpse keeps its bits).
                    foreach (var (id, mask) in spectator.TeamMasks)
                    {
                        if (runtime.World.Get(id) is not { Alive: true } unit || VisibilityRules.IsShrine(unit)) continue;
                        foreach (var team in new[] { Team.Order, Team.Chaos })
                        {
                            maskChecks++;
                            var seen = seats.Any(seat => seat.Team == team && seat.View.Visible.Contains(id));
                            if (seen == ((mask >> (int)team & 1) == 1)) continue;
                            maskFaults++;
                            if (faults.Count < 10) faults.Add($"{now:0.0}s: {unit.Name} ({id}) mask {mask}, {team} seats see it: {seen}");
                        }
                    }
                    // 129.60: a champion that stealths where the enemy saw it stays in the stream with its own team's bit only.
                    if (stealthed == 0 && now >= 60.0 && runtime.World.Heroes().FirstOrDefault(hero => hero.Team == Team.Chaos && hero.Alive &&
                            spectator.TeamMasks.GetValueOrDefault(hero.NetId) == 3) is { } target)
                    {
                        target.StealthHidden = true;
                        stealthed = target.NetId;
                        stealthedAt = (float)now;
                    }
                    // The end of the game: Chaos gives up; the ceremony's EndGame closes the stream.
                    if (stealthed != 0 && surrenderedAt == 0 && now >= stealthedAt + 20.0 && runtime.Combat.Surrender(Team.Chaos))
                        surrenderedAt = (float)now;
                },
            });
            TestAssert.Equal("natural", report.Outcome, "the match ends by the surrender");
            TestAssert.True(stealthed != 0, "a Chaos champion seen by both teams was stealthed");
            TestAssert.True(knownChecks > 0 && maskChecks > 0, "the reference checks ran");
            TestAssert.Equal(0L, knownFaults, "the spectator holds only units a seat holds: " + string.Join("; ", faults));
            TestAssert.Equal(0L, maskFaults, "the masks follow each team's sight: " + string.Join("; ", faults));

            // The stream on disk: start-up chunk(s), then game chunks, the last one ending the game.
            var index = SpectatorRecording.Read(folder);
            TestAssert.True(index.Chunks.Count >= 4 && index.Chunks[0].Startup, "start-up first, then game chunks");
            TestAssert.Equal(index.Chunks[^1].Id, index.EndChunkId, "the last chunk ends the game");
            var chunkKey = Encoding.ASCII.GetBytes(key);
            var stream = new List<(int Chunk, float Time, int Channel, byte[] Packet)>();
            var largest = 0;
            var pause = PausePackets.Pause(0, 1);
            bool IsPause(byte[] packet, int channel) => packet.Length == pause.Plaintext.Length && packet[0] == pause.Plaintext.Span[0] && channel == pause.Channel;
            foreach (var chunk in index.Chunks)
            {
                var block = SpectatorChunkChecks.Client.Open(File.ReadAllBytes(SpectatorRecording.ChunkPath(folder, chunk.Id)), chunkKey);
                largest = Math.Max(largest, block.Length);
                var packets = SpectatorChunkChecks.Client.Unpack(block);
                TestAssert.True(packets.Count > 0, $"chunk {chunk.Id} holds a packet");
                stream.AddRange(packets.Select(packet => (chunk.Id, packet.Time, packet.Channel, packet.Packet)));
                // Cut on the server's monotonic clock, so a chunk covers at most 30 s of game time: a few steps less for the first one (it starts
                // with the start-up flushes), less again where the pause holds the game.
                if (!chunk.Startup && chunk.Id != index.EndChunkId)
                    TestAssert.True(chunk.DurationMs <= 30_500 && (chunk.DurationMs >= 29_000 || packets.Any(packet => IsPause(packet.Packet, packet.Channel))),
                        $"chunk {chunk.Id} covers 30 s of game: {chunk.DurationMs} ms");
            }
            TestAssert.True(largest <= SpectatorRecorder.ChunkBudget, $"the largest block ({largest} bytes) is inside the budget");

            var startup = stream.Where(packet => packet.Chunk <= index.Chunks.Count(chunk => chunk.Startup)).ToArray();
            TestAssert.Equal(LoadingPackets.SynchVersionS2C().Plaintext.Span[0], stream[0].Packet[0], "132.07: SynchVersionS2C opens the stream");
            TestAssert.True(startup.Any(packet => packet.Packet[0] == SpawnPackets.StartGame().Plaintext.Span[0] && packet.Channel ==
                SpawnPackets.StartGame().Channel), "132.07: StartGame is in the start-up chunk");
            TestAssert.True(startup.All(packet => packet.Time == 0f), "the start-up chunk plays at time 0");
            TestAssert.True(stream.All(packet => packet.Packet[0] != 0xFF), "no batched packet");
            for (var at = 1; at < stream.Count; at++)
                TestAssert.True(stream[at].Time >= stream[at - 1].Time, $"packet {at}'s time does not go back");

            var endGame = CombatPackets.EndGame(true);
            var ends = stream.Where(packet => packet.Packet.Length == endGame.Plaintext.Length && packet.Packet[0] == endGame.Plaintext.Span[0] &&
                packet.Channel == endGame.Channel).ToArray();
            TestAssert.True(ends.Length == 1 && ends[0].Chunk == index.EndChunkId, "the end of the game is in the last chunk, once");
            TestAssert.True(stream[^1].Time - surrenderedAt < 30f, "the end chunk closes with the ceremony, not a chunk later");

            // Spectators see all chat but not team chat (the 2011 FAQ), and no command reply.
            var chat = stream.Where(packet => packet.Channel == ChatPackets.Channel).ToArray();
            TestAssert.Equal(1, chat.Length, "one chat line in the stream");
            TestAssert.Equal(Convert.ToHexString(ChatPackets.Envelope(chatter, false, "gl hf"u8).Plaintext.Span), Convert.ToHexString(chat[0].Packet),
                "the all-chat line, as the seats got it");

            // The pause: game time stands still, the pause comes back every 5 s (the replay's heartbeat), a chunk closes inside it, and the
            // delayed resume, the final resume and the clock follow.
            var pauses = stream.Where(packet => IsPause(packet.Packet, packet.Channel)).ToArray();
            TestAssert.True(pauses.Length >= 8, $"the pause and a heartbeat every 5 s of a 45 s pause: {pauses.Length}");
            TestAssert.True(pauses.Select(packet => packet.Chunk).Distinct().Count() >= 2, "a chunk closes inside the pause");
            TestAssert.True(pauses[^1].Time - pauses[0].Time < 0.01f, "game time stands still in the pause");
            var resume = PausePackets.Resume(0, false);
            var resumes = stream.Select((packet, at) => (packet, at)).Where(entry => entry.packet.Packet.Length == resume.Plaintext.Length &&
                entry.packet.Packet[0] == resume.Plaintext.Span[0] && entry.packet.Channel == resume.Channel).ToArray();
            TestAssert.Equal(2, resumes.Length, "the delayed resume, then the final one");
            TestAssert.Equal(1, resumes[0].packet.Packet[9], "the first resume counts down");
            TestAssert.Equal(0, resumes[1].packet.Packet[9], "the second one ends the pause");
            var clock = SpawnPackets.GameClock();
            TestAssert.True(stream.Skip(resumes[1].at + 1).Take(4).Any(packet => packet.Packet.Length == clock.Plaintext.Length &&
                packet.Packet[0] == clock.Plaintext.Span[0] && packet.Channel == clock.Channel), "the clock follows the final resume");
            TestAssert.True(pauses.All(packet => Math.Abs(packet.Time - resumes[1].packet.Time) < 0.01f), "the resume plays at the paused time");

            // The client's per-unit mask as the stream sets it, and the stealthed champion's bits after its stealth.
            var enter = VisionPackets.EnterTeamVisibility(1, 0).Plaintext.Span[0];
            var leave = VisionPackets.LeaveTeamVisibility(1, 0).Plaintext.Span[0];
            var masks = new Dictionary<uint, int>();
            var orderLost = false;
            foreach (var (_, time, _, packet) in stream)
            {
                if (packet.Length != 6 || packet[0] != enter && packet[0] != leave) continue;
                var unit = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(1));
                TestAssert.True(packet[5] < 2, "a team mask names Order or Chaos");
                var bit = 1 << packet[5];
                masks[unit] = packet[0] == enter ? masks.GetValueOrDefault(unit) | bit : masks.GetValueOrDefault(unit) & ~bit;
                // The stealth is set before a step; the flush that ended the previous step carries that step's time (rebuilt within 0.5 ms).
                if (unit != stealthed || time <= stealthedAt + 0.01f) continue;
                if (packet[0] == leave && packet[5] == 0) orderLost = true;
                TestAssert.False(packet[0] == enter && packet[5] == 0,
                    $"129.60: Order never sees the stealthed champion again (at {time:0.000}, stealthed at {stealthedAt:0.000}, surrender at {surrenderedAt:0.0})");
            }
            TestAssert.True(orderLost, "129.60: the stealth clears the stealthed champion's Order bit");
            TestAssert.Equal(2, masks.GetValueOrDefault(stealthed), "129.60: the stealthed champion stays in the stream, seen by Chaos only");

            // The packet-stream rules a player's stream keeps (units created before use, no duplicate creation, sane replication).
            var checker = new PacketStreamChecker(1);
            foreach (var (_, time, channel, packet) in stream)
                checker.Observe(new PacketObservation(time, 0, (byte)channel,
                    !PacketCatalog.Default.TryGet(packet[0], (byte)channel, out var spec) || spec.Reliable, packet));
            var violations = checker.Finish();
            TestAssert.Equal(0, violations.Count, "the stream keeps the packet-stream rules: " +
                string.Join("; ", violations.Take(8).Select(violation => $"{violation.Kind} 0x{violation.Opcode:X2} {violation.NetId}: {violation.Detail}")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("network.spectate-recorder-off-by-default")]
    public static void OffByDefault()
    {
        TestAssert.Equal<string?>(null, ServerOptions.Parse([]).SpectatorStream, "the server records nothing without the switch");
        TestAssert.Equal("stream", ServerOptions.Parse([SpectatorRecording.StreamSwitch, "stream"]).SpectatorStream, "the switch names the folder");
        var root = Directory.CreateTempSubdirectory("league-spectate-off-").FullName;
        try
        {
            var spectators = 0;
            var report = HeadlessMatch.Run(new HeadlessMatchOptions(8471, root, 0.5)
            {
                OnStep = runtime =>
                {
                    if (runtime.Session.Spectator is not null || runtime.Delivery.Observers.Any(observer => observer.Spectator)) spectators++;
                },
            });
            TestAssert.Equal("cap", report.Outcome, "the match ran");
            TestAssert.Equal(0, spectators, "no recorder and no spectator view");
            TestAssert.Equal<string?>(null, report.Spectator, "nothing recorded");
            TestAssert.False(Directory.EnumerateDirectories(root, SpectatorRecording.FolderName, SearchOption.AllDirectories).Any(), "no stream folder");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("network.spectate-recorder-stops-without-folder")]
    public static void StopsWithoutFolder()
    {
        var root = Directory.CreateTempSubdirectory("league-spectate-gone-").FullName;
        try
        {
            var folder = Path.Combine(root, "spectator");
            SpectatorRecorder? seen = null;
            ReplicationDelivery? delivery = null;
            var report = HeadlessMatch.Run(new HeadlessMatchOptions(8472, Path.Combine(root, "match"), 1.25)
            {
                SpectatorStream = folder,
                SpectatorKey = SpectatorChunk.NewChunkKey(),
                OnStep = runtime =>
                {
                    seen = runtime.Session.Spectator;
                    delivery = runtime.Delivery;
                    // A Platform restart's leftover sweep beside a running game: the folder goes after the first game chunk.
                    if (runtime.Session.PlaytestHudTime is >= 40.0 && Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                },
            });
            TestAssert.Equal("cap", report.Outcome, "the game plays on to its cap");
            TestAssert.True(seen is { Stopped: true, Ended: false }, "the recorder stopped");
            TestAssert.False(Directory.Exists(folder), "and wrote nothing more");
            TestAssert.False(delivery!.Observers.Any(observer => observer.Spectator), "its view left the delivery");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
