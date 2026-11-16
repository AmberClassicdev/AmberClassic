using System.Buffers.Binary;
using System.Globalization;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Diagnostics;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 807 (owner, 15 November 2026, native 126: Nunu's attacks after his spells; damage with no swing, a long pause after a cast). What
/// the stock client does with the packets that reach it during a swing: a 0x0D starts the swing's SpellCaster (the client,
/// HSM event 3); a 0x39 stops it (event 4); a new 0x0D or a cast with a SpellCaster replaces it; an accepted route (HSM event
/// 1, the client 142084) enters Move or Move_And_Attack, whose entries run ChannelingStop
/// (Cancel, Move), which stops the current SpellCaster only once its cast frame has run. So
/// a route in a swing's wind-up does not cut the swing; only a stop, another swing or a cast does. Ported to 132 (replication 0xCD, cast
/// request 0xA3, cast answer 0xBE); the client handlers were read from the 126 executable only and are inferred for 132.
/// </summary>
public static class AttackResumeChecks
{
    private static readonly (string Champion, double Range)[] Champions = [("Nunu", 150.0), ("Tristana", 450.0), ("Warwick", 150.0)];

    /// <summary>A match with <paramref name="champion"/> attacking the foe (an explicit attack order) at <paramref name="range"/>, both sides seen.</summary>
    private static Match Attacking(string champion, double range, out Entity foe, out WireCapture capture)
    {
        var match = new Match(champion, foeChampion: "Orianna");
        match.Hero.Level = 9;
        match.Learn(5);
        var hero = match.Hero;
        foe = match.Foe;
        foe.MaxHealth = foe.Health = 50000.0;
        foe.Position = new Position2(hero.Position.X + range, hero.Position.Z);
        match.Ally.Position = new Position2(hero.Position.X - 150.0, hero.Position.Z);
        match.Runtime.HeroStats.Refresh(hero, push: false);
        capture = Observe(match, Team.Order, hero);
        match.Advance(0.5);
        var view = match.Runtime.Session.Participants[0].View;
        view.Known.Add(foe.NetId);
        view.Visible.Add(foe.NetId);
        Order(match, 3, foe.Position, foe.NetId);
        return match;
    }

    /// <summary>Advances until the hero's next 0x0D has gone out, then <paramref name="steps"/> more steps; the 0x0D's time.</summary>
    private static double IntoSwing(Match match, WireCapture capture, int steps)
    {
        var before = Swings(capture, match.Hero).Count;
        for (var step = 0; step < 600 && Swings(capture, match.Hero).Count == before; step++) match.Advance(Step);
        var at = Swings(capture, match.Hero)[^1].At;
        match.Advance(steps * Step);
        return at;
    }

    private static void Order(Match match, byte order, Position2 at, uint target)
    {
        var packet = ClientGameplayPackets.MoveOrder(match.Hero.NetId, order, at, target);
        TestAssert.True(match.Runtime.Session.DispatchGameplay(1, 1, packet.Plaintext.Span, match.Runtime.Dependencies.Gameplay), $"order {order}");
    }

    /// <summary>The 0xA3 cast request the 132 client sends (126: 0xA2): actor, slot, point, end point, target.</summary>
    private static void Cast(Match match, int slot, Entity target, Position2? at = null)
    {
        var point = at ?? target.Position;
        var payload = new byte[34];
        payload[0] = 0xA3;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1, 4), match.Hero.NetId);
        payload[5] = checked((byte)(slot << 1));
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(6, 4), (float)point.X);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(14, 4), (float)point.Z);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(18, 4), (float)point.X);
        BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(26, 4), (float)point.Z);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(30, 4), target.NetId);
        TestAssert.True(match.Runtime.Session.DispatchGameplay(1, 1, payload, match.Runtime.Dependencies.Gameplay), $"cast request slot {slot}");
    }

    /// <summary>The hero's 0x0D: time, target, and the mAttackSpeedMod the client held at receipt.</summary>
    private static List<(double At, uint Target, double Speed)> Swings(WireCapture capture, Entity hero)
    {
        var result = new List<(double, uint, double)>();
        var speed = double.NaN;
        foreach (var (at, packet) in capture.Packets)
        {
            var span = packet.Plaintext.Span;
            if (span[0] == 0xCD)
            {
                var decoded = WirePacketDecoder.Decode(span, packet.Channel);
                if (decoded.Fields.TryGetValue("unit_netid", out var unit) && unit is uint id && id == hero.NetId &&
                    decoded.Fields.TryGetValue("fields", out var raw) && raw is Dictionary<string, object?> fields &&
                    fields.TryGetValue("mAttackSpeedMod", out var value))
                    speed = (double)value!;
                continue;
            }
            if (span[0] == 0x0D && BinaryPrimitives.ReadUInt32LittleEndian(span[1..]) == hero.NetId)
                result.Add((at, BinaryPrimitives.ReadUInt32LittleEndian(span[5..]), speed));
        }
        return result;
    }

    private static List<double> Hits(Match match) => match.Log.Hits
        .Where(line => line.Contains("->Foe ", StringComparison.Ordinal) && line.Contains(" Basic ", StringComparison.Ordinal))
        .Select(line => double.Parse(line.Split(' ')[0], CultureInfo.InvariantCulture)).ToList();

    /// <summary>The base attack's cast frame and period the client plays for a 0x0D at mAttackSpeedMod <paramref name="speed"/>.</summary>
    private static (double Windup, double Period) ClientSwing(string champion, double speed)
    {
        var raw = InibinReader.Load(GameData.Default.Paths.Data($"Characters/{champion}/{champion}.inibin"));
        float Read(string key) => raw.TryGetValue(InibinReader.KeyHash("Data*" + key), out var value) ? (float)value.AsClientFloat() : 0f;
        var period = Math.Clamp(1.6 * (1.0 + Read("AttackDelayOffsetPercent")) / speed, 0.4, 5.0);
        return (period * Math.Max(0.0, 0.3 + Read("AttackDelayCastOffsetPercent")), period);
    }

    /// <summary>
    /// Damage without a swing: between a 0x0D and the hit the server lands for it, the client must receive nothing that cuts that swing
    /// (a 0x39, another 0x0D of the hero, a cast packet of the hero); a route may come (the attack-move's stop path does: it does not cut an
    /// un-cast swing). A move order in the wind-up stops the swing (0x39 DestroyMissile) and lands nothing. Each champion, each order, in
    /// the wind-up and after the hit.
    /// </summary>
    [Check("attacks.no-swing-is-cut-before-a-hit-the-server-lands")]
    public static void NoSwingIsCutBeforeAHitTheServerLands()
    {
        foreach (var (champion, range) in Champions)
        foreach (var label in new[] { "move", "attack-move", "attack-again", "attack-move-spam" })
        foreach (var phase in new[] { 3, 12 })
        {
            using var match = Attacking(champion, range, out var foe, out var capture);
            var hero = match.Hero;
            for (var swing = 0; swing < 2; swing++) IntoSwing(match, capture, 0);
            var swingAt = IntoSwing(match, capture, phase);
            switch (label)
            {
                case "move": Order(match, 2, hero.Position + new Position2(-300, 0), 0); break;
                case "attack-move": Order(match, 7, foe.Position + new Position2(300, 0), 0); break;
                case "attack-again": Order(match, 3, foe.Position, foe.NetId); break;
                default:
                    for (var click = 0; click < 12; click++)
                    {
                        Order(match, 7, foe.Position + new Position2(300, 0), 0);
                        match.Advance(5 * Step);
                    }
                    break;
            }
            match.Advance(3.0);
            var context = $"{champion} {label} at {phase} steps into the swing of {swingAt:0.000}";
            var hits = Hits(match);
            var swings = Swings(capture, hero);
            foreach (var hit in hits.Where(at => at > swingAt - 1e-9))
            {
                var start = swings.Where(entry => entry.At < hit - 1e-9).Select(entry => entry.At).DefaultIfEmpty(double.NaN).Max();
                TestAssert.False(double.IsNaN(start), $"{context}: the hit at {hit:0.000} has a 0x0D");
                // From the packet after the 0x0D (its own flush included: a channel keeps its order) to the hit's flush.
                var first = capture.Packets.FindLastIndex(entry => entry.At == start && entry.Packet.Plaintext.Span[0] == 0x0D &&
                    BinaryPrimitives.ReadUInt32LittleEndian(entry.Packet.Plaintext.Span[1..]) == hero.NetId) + 1;
                foreach (var (at, packet) in capture.Packets.Skip(first).Where(entry => entry.At < hit - 1e-9))
                {
                    var span = packet.Plaintext.Span;
                    if (span.Length < 5 || BinaryPrimitives.ReadUInt32LittleEndian(span[1..]) != hero.NetId) continue;
                    TestAssert.False(span[0] is 0x39 or 0x0D or SpellPackets.CastSpellAnswerOpcode,
                        $"{context}: 0x{span[0]:X2} at {at:0.000} cuts the swing of {start:0.000} before its hit at {hit:0.000}");
                }
                if (champion != "Tristana")
                    TestAssert.True(hit <= start + ClientSwing(champion, swings.Last(entry => entry.At == start).Speed).Windup + Step + 1e-6,
                        $"{context}: the hit at {hit:0.000} is not at the cast frame of the swing of {start:0.000}");
            }
            if (label == "move" && phase == 3)
                TestAssert.False(hits.Any(at => at > swingAt && at < swingAt + 0.6), $"{context}: a move in the wind-up lands no hit");
        }
    }

    /// <summary>
    /// The long pause (batch 803 open 4): a spell with a cast time pressed while the hero attacks on the player's order took the order away and
    /// left the attack to idle acquisition after the cast gesture's whole 1 s hold, which also picks the nearest enemy rather than the ordered
    /// one. Retail keeps the order: Hero.luaobj's OnOrder (proto 0.2 pc 0084-0087) answers an order it does not handle (the engine's
    /// AI_TEMP_CASTSPELL, 4) with TimerCheckAttack and leaves the AI state and target alone, and the engine's IssueOrders record postpones the
    /// current order for the cast and executes it again (ORDER_STATUS_POSTPONED, the client, stubs in the
    /// client). The hero swings at the ordered target again once the spell's cast frame has run on its client (the cast packet's total time is
    /// its cast time, so the client's SpellCaster for it is over), in the wind-up and between swings alike. A move ordered while the spell
    /// winds up is the newer order: no swing follows (Disintegrate's CantCancelWhileWindingUp holds that move behind the wind-up and applies it
    /// at the instant of the hand-back; the others apply it at once).
    /// </summary>
    [Check("attacks.cast-in-a-swing-resumes-the-ordered-attack-after-its-cast-frame")]
    public static void CastInASwingResumesTheOrderedAttackAfterItsCastFrame()
    {
        TestAssert.True(GameData.Default.SpellHasUncancellableWindup("Disintegrate"), "Disintegrate holds orders behind its wind-up");
        foreach (var (champion, range, slot, spell) in new[]
                 { ("Nunu", 150.0, 2, "IceBlast"), ("Tristana", 450.0, 2, "DetonatingShot"), ("Warwick", 150.0, 0, "HungeringStrike"),
                   ("Annie", 450.0, 0, "Disintegrate") })
        foreach (var phase in new[] { 3, 12 })
        foreach (var moveAfter in new[] { false, true })
        {
            var data = GameData.Default.Spell(spell, 5);
            TestAssert.False(data.IsInstant, $"{spell} has a cast time");
            using var match = Attacking(champion, range, out var foe, out var capture);
            var hero = match.Hero;
            // A second enemy nearer than the ordered one: automatic acquisition would take it.
            var near = match.FarFoe;
            near.MaxHealth = near.Health = 50000.0;
            near.Position = new Position2(hero.Position.X - range + 20.0, hero.Position.Z);
            var view = match.Runtime.Session.Participants[0].View;
            view.Known.Add(near.NetId);
            view.Visible.Add(near.NetId);
            for (var swing = 0; swing < 2; swing++) IntoSwing(match, capture, 0);
            var swingAt = IntoSwing(match, capture, phase);
            var pressed = match.Now;
            var cooldownEnds = pressed + Math.Max(0.0, hero.AttackCooldown);
            var sent = capture.Packets.Count;
            Cast(match, slot, foe);
            var context = $"{champion} {spell} at {phase} steps into the swing of {swingAt:0.000}, pressed at {pressed:0.000}";
            if (moveAfter)
            {
                match.Advance(2 * Step);
                var start = hero.Position;
                Order(match, 2, hero.Position + new Position2(0, -400), 0);
                var castAt = capture.Packets.Skip(sent).First(entry => entry.Packet.Plaintext.Span[0] == SpellPackets.CastSpellAnswerOpcode &&
                    BinaryPrimitives.ReadUInt32LittleEndian(entry.Packet.Plaintext.Span[1..]) == hero.NetId).At;
                // Past the instant a postponed attack would come back (the cast frame, the settle margin, a few steps), still walking.
                while (match.Now < castAt + data.CastDelay + 0.1 + 6 * Step) match.Advance(Step);
                TestAssert.False(Swings(capture, hero).Any(entry => entry.At > pressed), $"{context}: a move in the wind-up is the newer order, no swing follows");
                TestAssert.True(hero.CastPostponedOrder is null && hero.Position.Z < start.Z - 30.0,
                    $"{context}: the move replaced the postponed attack and the hero walked it ({start} -> {hero.Position})");
                continue;
            }
            match.Advance(2.0);
            var cast = capture.Packets.Skip(sent).First(entry => entry.Packet.Plaintext.Span[0] == SpellPackets.CastSpellAnswerOpcode &&
                BinaryPrimitives.ReadUInt32LittleEndian(entry.Packet.Plaintext.Span[1..]) == hero.NetId).At;
            var next = Swings(capture, hero).FirstOrDefault(entry => entry.At > pressed);
            TestAssert.True(next.At > 0.0, $"{context}: the hero attacks again");
            TestAssert.Equal(foe.NetId, next.Target, $"{context}: the swing after the cast is at the ordered target");
            TestAssert.True(next.At >= cast + data.CastDelay - 1e-6, $"{context}: the swing at {next.At:0.000} comes before the cast frame {cast + data.CastDelay:0.000}");
            // The cast frame plus the settle margin of an order held behind a wind-up (SpellWindup.SettleSeconds), on the step grid; a swing
            // still cooling down waits for its period.
            var due = Math.Max(cast + data.CastDelay + 0.1, phase == 3 ? 0.0 : cooldownEnds);
            TestAssert.True(next.At <= due + 3 * Step + 1e-6,
                $"{context}: the next swing at {next.At:0.000} (+{next.At - pressed:0.000} after the press) waits past {due:0.000}");
        }
    }

    /// <summary>
    /// Batch 803 open 1, the other champions: an attack-speed buff cast in a swing's wind-up leaves that swing's period alone (the client
    /// stamps a swing's end once, at its 0x0D, the client; its own attack cadence adds the period computed at each
    /// attack's start, the client), so the next 0x0D waits the old period and the one after it the new one.
    /// </summary>
    [Check("attacks.attack-speed-buff-in-a-swing-keeps-that-swings-period")]
    public static void AttackSpeedBuffInASwingKeepsThatSwingsPeriod()
    {
        foreach (var (champion, range, slot) in new[] { ("Tristana", 450.0, 0), ("Warwick", 150.0, 1) })
        {
            using var match = Attacking(champion, range, out var foe, out var capture);
            var hero = match.Hero;
            for (var swing = 0; swing < 2; swing++) IntoSwing(match, capture, 0);
            var swingAt = IntoSwing(match, capture, 3);
            var before = Swings(capture, hero)[^1].Speed;
            Cast(match, slot, hero);
            match.Advance(4.0);
            var swings = Swings(capture, hero).Where(entry => entry.At >= swingAt - 1e-9).ToList();
            var context = $"{champion} slot {slot} in the swing of {swingAt:0.000}";
            TestAssert.True(swings.Count >= 3 && swings[1].Speed > before + 0.05, $"{context}: the buff raised the replicated attack speed ({before:0.0000} -> {swings.ElementAtOrDefault(1).Speed:0.0000})");
            var old = ClientSwing(champion, before).Period;
            var fresh = ClientSwing(champion, swings[1].Speed).Period;
            TestAssert.True(swings[1].At - swings[0].At >= old - 1e-6 && swings[1].At - swings[0].At <= old + Step + 1e-6,
                $"{context}: the swing after the buff waits the old period {old:0.000} (came after {swings[1].At - swings[0].At:0.000})");
            TestAssert.True(swings[2].At - swings[1].At <= fresh + Step + 1e-6,
                $"{context}: the next swing uses the new period {fresh:0.000} (came after {swings[2].At - swings[1].At:0.000})");
        }
    }
}
