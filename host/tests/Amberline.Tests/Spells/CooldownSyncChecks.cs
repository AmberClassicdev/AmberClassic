using System.Buffers.Binary;
using System.Globalization;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 801 (owner, 15 November 2026, native 126): a cooldown the server and the owner's client disagree on. Zilean's Q, then Rewind (W) inside
/// Time Bomb's windup, then Q again was refused while the HUD showed Q ready: the client's cast handler runs a pending windup's cast frame when
/// the next cast answer arrives (<c>ForceStop</c>), the server ran it at the end of its own windup, after Rewind had read and rewritten Q's
/// cooldown. These checks replay the client's two cooldown writers off the wire the hero's client receives and compare them with the server.
/// </summary>
public static class CooldownSyncChecks
{
    /// <summary>
    /// The owner's client's record of the champion slots, replayed off the wire it receives (zero latency: the flush time is the arrival).
    /// The two writers of a slot's record in the stock client: <c>0x8A</c> sets the time left (<c>SetTimeForCooldown</c>: expiry = now + seconds),
    /// and a cast answer (<c>0xBD</c>) raises the slot's time left to the cast's cooldown at its cast frame, <c>ExtraCastTime + DesignerCastTime</c>
    /// after it arrives, only when it is shorter (<c>AdjustVarsAfterSpellCast</c>). The cast handler (<c>Client_DoCastSpell</c>, the client
    /// 883180-883221) runs a pending cast frame at once when any answer arrives that is not an extra-slot cast without isAutoAttack and
    /// forceCastingOrChannelling (<c>ForceStop(false, true, false)</c>). The checks below toggle no slot, so the toggle exception is not modelled.
    /// </summary>
    internal sealed class ClientCooldowns(uint hero)
    {
        // CharSetCooldown's opcode, read off the line's own builder.
        private static readonly byte SetCooldown = SpellPackets.SetCooldown(0, 0, 0f).Plaintext.Span[0];
        private readonly double[] _expiry = new double[4];
        private readonly List<(double At, int Slot, double Cooldown)> _castFrames = [];
        private int _read;

        public void Feed(WireCapture wire)
        {
            for (; _read < wire.Packets.Count; _read++)
            {
                var (at, packet) = wire.Packets[_read];
                ApplyCastFrames(at);
                var span = packet.Plaintext.Span;
                if (span.Length >= 10 && span[0] == SetCooldown && BinaryPrimitives.ReadUInt32LittleEndian(span[1..]) == hero && (span[5] & 0x80) == 0 &&
                    (span[5] & 0x7F) < 4)
                    _expiry[span[5] & 0x7F] = at + BinaryPrimitives.ReadSingleLittleEndian(span[6..]);
                else if (span.Length >= 9 + 48 && span[0] == SpellPackets.CastSpellAnswerOpcode && BinaryPrimitives.ReadUInt32LittleEndian(span[1..]) == hero)
                {
                    var info = span[9..];
                    var tail = 48 + 17 * info[47];
                    if (info.Length < tail + 22) continue;
                    var slot = info[tail + 21];
                    var flags = info[tail + 20];
                    if (!((flags & 0x05) == 0 && slot > 0x29 && slot != 0x33))
                    {
                        for (var i = 0; i < _castFrames.Count; i++) _castFrames[i] = _castFrames[i] with { At = at };
                        ApplyCastFrames(at);
                    }
                    if (slot >= 4) continue;
                    var frame = at + BinaryPrimitives.ReadSingleLittleEndian(info[tail..]) + BinaryPrimitives.ReadSingleLittleEndian(info[(tail + 4)..]);
                    _castFrames.Add((frame, slot, BinaryPrimitives.ReadSingleLittleEndian(info[(tail + 12)..])));
                    ApplyCastFrames(at);
                }
            }
        }

        private void ApplyCastFrames(double now)
        {
            foreach (var frame in _castFrames.Where(frame => frame.At <= now + 1e-9).OrderBy(frame => frame.At).ToArray())
            {
                if (_expiry[frame.Slot] - frame.At < frame.Cooldown) _expiry[frame.Slot] = frame.At + frame.Cooldown;
                _castFrames.Remove(frame);
            }
        }

        public double Remaining(int slot, double now)
        {
            ApplyCastFrames(now);
            return Math.Max(0.0, _expiry[slot] - now);
        }
    }

    private static string F(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

    private static string Failures(Match match) => string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message));

    private static Match Arena(string champion, int level = 1)
    {
        var match = new Match(champion);
        match.Hero.Level = level;
        match.Learn(5);
        match.Hero.Position = new Position2(7000, 7000);
        match.Foe.Position = new Position2(7300, 7000);
        match.Ally.Position = new Position2(6000, 6000);
        match.Refill();
        match.Advance(0.2);
        return match;
    }

    /// <summary>
    /// Every champion slot's time left, the server's and the client's, within two steps of each other: the step grid alone puts a windup's cast
    /// frame up to that far apart (the answer goes out with the step's flush, the server's windup ends on a step and its cooldown is counted
    /// down in that same step).
    /// </summary>
    private static void Agree(Match match, ClientCooldowns client, WireCapture wire, string label)
    {
        client.Feed(wire);
        for (var slot = 0; slot < 4; slot++)
        {
            var server = match.Hero.SpellCooldowns[slot];
            var seen = client.Remaining(slot, match.Now);
            TestAssert.True(Math.Abs(server - seen) <= 2 * Step + 1e-6,
                $"{label}: slot {slot} has {F(server)} s left on the server and {F(seen)} s on the client");
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Zilean: Rewind inside Time Bomb's windup
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The owner's finding: Q, Rewind inside or after Q's 0.25 s windup, then Q again. Rewind (<c>Rewind.luaobj</c> TargetExecute) takes ten
    /// seconds off Q, E and R with a floor of 0 and writes them (<c>BBSetSlotSpellCooldownTimeVer2</c>); Time Bomb's cooldown is 10, so Q is
    /// ready again on both sides, and the second press is accepted. Red before batch 801 for every press inside the windup: the client 0, the
    /// server 8.5 to 8.7.
    /// </summary>
    [Check("cooldowns.rewind-in-time-bombs-windup-leaves-q-ready-on-both-sides")]
    public static void RewindInsideTimeBombsWindup()
    {
        var windupSteps = (int)Math.Ceiling(CastFrame("TimeBomb") / Step);
        for (var k = 0; k <= windupSteps + 2; k++)
        {
            using var match = Arena("Zilean");
            var hero = match.Hero;
            var wire = Observe(match, hero.Team, hero);
            var client = new ClientCooldowns(hero.NetId);
            TestAssert.True(match.Cast(0, match.Foe), $"k={k}: Time Bomb casts");
            match.Advance(k * Step);
            TestAssert.True(match.Cast(1, hero), $"k={k}: Rewind casts {F(k * Step)} s after Time Bomb");
            match.Advance(0.3);
            Agree(match, client, wire, $"k={k}");
            TestAssert.Near(0.0, hero.SpellCooldowns[0], 1e-6, $"k={k}: Time Bomb is ready on the server (10 s less 10)");
            TestAssert.True(Buff(match.Foe, "TimeBomb") is not null, $"k={k}: the first bomb is on the foe");
            TestAssert.True(match.Cast(0, match.Foe), $"k={k}: the second Time Bomb is accepted");
            TestAssert.Equal(0, match.Module.ScriptFailures.Count, $"k={k}: no script failed: " + Failures(match));
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // The shared rule: any cast inside another spell's windup
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The same rule without a cooldown script: Master Yi's Highlander (R, an instacast) pressed inside Alpha Strike's windup. The client starts
    /// Alpha Strike's cooldown when R's answer arrives; the server started it at the end of its own windup, up to the windup later, so a Q press
    /// the client sent the moment its HUD showed Q ready was refused. Red before batch 801 (0.2 s apart).
    /// </summary>
    [Check("cooldowns.an-instacast-in-a-windup-starts-the-windups-cooldown-when-the-client-does")]
    public static void InstacastInsideAWindup()
    {
        var windupSteps = (int)Math.Ceiling(CastFrame("AlphaStrike") / Step);
        for (var k = 0; k <= windupSteps; k++)
        {
            using var match = Arena("MasterYi", level: 18);
            var hero = match.Hero;
            var wire = Observe(match, hero.Team, hero);
            var client = new ClientCooldowns(hero.NetId);
            TestAssert.True(match.Cast(0, match.Foe), $"k={k}: Alpha Strike casts");
            match.Advance(k * Step);
            TestAssert.True(match.Cast(3, hero), $"k={k}: Highlander casts {F(k * Step)} s after Alpha Strike");
            match.Advance(1.0);
            Agree(match, client, wire, $"k={k}");
            TestAssert.True(hero.SpellCooldowns[0] > 1.0, $"k={k}: Alpha Strike is on cooldown");
            TestAssert.Equal(0, match.Module.ScriptFailures.Count, $"k={k}: no script failed: " + Failures(match));
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Batch 823: an item's active inside a windup
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>The hero's press of an item slot (4 to 9) through the production dispatch, the line's own cast request (0xA2 / 0xA3).</summary>
    private static bool UseItem(Match match, int slot)
    {
        var hero = match.Hero;
        var height = match.Runtime.Navigation.HeightAt(hero.Position);
        var payload = Amberline.Tools.Scenarios.SeatOrders.CastPayload(hero.NetId, slot, false, hero.Position, height, hero.Position, height, 0);
        return match.Runtime.Session.DispatchGameplay(1, 1, payload, match.Runtime.Dependencies.Gameplay);
    }

    /// <summary>The slots of the cast answers (0xBD) the hero's client received for the hero.</summary>
    private static List<int> CastAnswerSlots(WireCapture wire, uint hero)
    {
        var slots = new List<int>();
        foreach (var (_, packet) in wire.Packets)
        {
            var span = packet.Plaintext.Span;
            if (span.Length < 9 + 48 || span[0] != SpellPackets.CastSpellAnswerOpcode || BinaryPrimitives.ReadUInt32LittleEndian(span[1..]) != hero)
                continue;
            var info = span[9..];
            var tail = 48 + 17 * info[47];
            if (info.Length >= tail + 22) slots.Add(info[tail + 21]);
        }
        return slots;
    }

    /// <summary>
    /// Batch 823: an item's cast answer names slot 4 to 9 of the champion's spellbook (the item spells' <c>BelongsToAvatar</c> is clear), so the
    /// client's cast handler force-stops a pending windup for it as for a champion spell (batch 801). Master Yi's Meditate (W: a 3.25-frame windup
    /// whose cast frame opens the five second channel), then Youmuu's Ghostblade (an instacast self active) inside that windup: Meditate's cast
    /// frame runs with the item's answer (W's cooldown starts there and then) and the stop of its caster cuts the channel. Red before batch 823:
    /// the server ran Meditate's cast frame at the end of its own windup and channelled on.
    /// </summary>
    [Check("cooldowns.an-item-active-in-a-windup-lands-the-windup-and-cuts-its-channel")]
    public static void ItemActiveInsideAWindup()
    {
        var windupSteps = (int)Math.Ceiling(CastFrame("Meditate") / Step);
        for (var k = 0; k < windupSteps; k++)
        {
            using var match = Arena("MasterYi", level: 18);
            var hero = match.Hero;
            hero.Inventory = [new InventorySlot(3142), null, null, null, null, null];
            match.Runtime.HeroStats.Refresh(hero, push: false);
            var wire = Observe(match, hero.Team, hero);
            var client = new ClientCooldowns(hero.NetId);
            TestAssert.True(match.Cast(1, hero), $"k={k}: Meditate casts");
            match.Advance(k * Step);
            TestAssert.Near(0.0, hero.SpellCooldowns[1], 1e-6, $"k={k}: Meditate's cast frame has not run {F(k * Step)} s after the press");
            TestAssert.True(UseItem(match, 4), $"k={k}: Youmuu's Ghostblade is used");
            TestAssert.True(hero.SpellCooldowns[1] > 1.0, $"k={k}: Meditate's cast frame ran with the item's answer (W has {F(hero.SpellCooldowns[1])} s)");
            match.Advance(0.5);
            Agree(match, client, wire, $"k={k}");
            TestAssert.True(Buff(hero, "Meditate") is null && hero.ActiveScriptChannels == 0, $"k={k}: Meditate's channel is cut");
            TestAssert.True(Buff(hero, "SpectralFury") is not null, $"k={k}: Youmuu's Ghostblade's own cast ran");
            TestAssert.Equal(0, match.Module.ScriptFailures.Count, $"k={k}: no script failed: " + Failures(match));
        }
    }

    /// <summary>
    /// The negative: a consumable's use sends no cast answer (<c>ItemService.UseConsumable</c>), so the client force-stops nothing and the windup
    /// runs on, on both sides. A Health Potion drunk inside Meditate's windup leaves its cast frame and its channel to their own time.
    /// </summary>
    [Check("cooldowns.a-potion-in-a-windup-sends-no-cast-answer-and-leaves-the-windup")]
    public static void PotionInsideAWindup()
    {
        using var match = Arena("MasterYi", level: 18);
        var hero = match.Hero;
        hero.Inventory = [new InventorySlot(2003), null, null, null, null, null];
        match.Runtime.HeroStats.Refresh(hero, push: false);
        hero.Health = hero.MaxHealth * 0.5;
        var wire = Observe(match, hero.Team, hero);
        var client = new ClientCooldowns(hero.NetId);
        TestAssert.True(match.Cast(1, hero), "Meditate casts");
        TestAssert.True(UseItem(match, 4), "the Health Potion is drunk in the same step");
        TestAssert.True(Buff(hero, "RegenerationPotion") is not null, "the potion's own cast ran");
        TestAssert.Near(0.0, hero.SpellCooldowns[1], 1e-6, "Meditate's cast frame has not run");
        TestAssert.False(CastAnswerSlots(wire, hero.NetId).Contains(4), "no cast answer for the potion's slot reaches the client");
        match.Advance(0.5);
        Agree(match, client, wire, "potion");
        TestAssert.True(Buff(hero, "Meditate") is not null, "Meditate channels on");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Master Yi: Highlander's kill and assist hooks through Alpha Strike
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>Master Yi at level 18 with every spell spent but Meditate: Highlander up (12 s at rank three), Wuju Style cast.</summary>
    private static (Match Match, WireCapture Wire, ClientCooldowns Client) Highlander()
    {
        var match = Arena("MasterYi", level: 18);
        var hero = match.Hero;
        var wire = Observe(match, hero.Team, hero);
        var client = new ClientCooldowns(hero.NetId);
        TestAssert.True(match.Cast(3, hero), "Highlander casts");
        TestAssert.True(match.Cast(2, hero), "Wuju Style casts");
        match.Advance(0.5);
        TestAssert.True(Buff(hero, "Highlander") is not null, "Highlander is up");
        return (match, wire, client);
    }

    /// <summary>
    /// <c>Highlander.luaobj</c> BuffOnKill: a champion kill while Highlander is up sets Q, W, E and R to 0 (<c>BBSetSlotSpellCooldownTime</c>). Alpha
    /// Strike's damage is <c>AlphaStrikeTarget</c>'s OnBuffDeactivate on each unit it hit, with Yi as the attacker, so a kill by a later bounce is
    /// his kill too.
    /// </summary>
    [Check("cooldowns.highlander-a-kill-by-an-alpha-strike-bounce-readies-every-spell-on-both-sides")]
    public static void HighlanderKillByABounce()
    {
        var (match, wire, client) = Highlander();
        using var owned = match;
        var hero = match.Hero;
        match.Foe.MaxHealth = match.Foe.Health = 50000.0;
        match.FarFoe.Position = new Position2(7400, 7150);
        match.FarFoe.Health = 50.0;
        TestAssert.True(match.Cast(0, match.Foe), "Alpha Strike casts at the healthy foe");
        for (var i = 0; i < 60 && match.FarFoe.Alive; i++) match.Advance(Step);
        TestAssert.False(match.FarFoe.Alive, "the bounce kills the second champion");
        match.Advance(0.1);
        Agree(match, client, wire, "bounce kill");
        TestAssert.True(hero.SpellCooldowns.Take(4).All(cooldown => cooldown <= 1e-6), "every spell is ready on the server");
        TestAssert.True(match.Foe.Alive, "the marked foe lives");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// <c>Highlander.luaobj</c> BuffOnAssist: Yi's Alpha Strike hits a champion an ally then kills; the slots lose AlphaStrikeCD (9/8/7/6/5 by the
    /// buff's level, Highlander's rank three reads 7), MeditateCD 22.5, WujuStyleCD 12.5 and HighlanderCD 37.5 (<c>MO_SUBTRACT</c>, no floor in
    /// the script; the slot record takes no negative time), on both sides.
    /// </summary>
    [Check("cooldowns.highlander-an-assist-takes-the-scripts-seconds-off-on-both-sides")]
    public static void HighlanderAssist()
    {
        var (match, wire, client) = Highlander();
        using var owned = match;
        var hero = match.Hero;
        match.Foe.MaxHealth = match.Foe.Health = 50000.0;
        TestAssert.True(match.Cast(0, match.Foe), "Alpha Strike casts");
        match.Advance(1.5);
        TestAssert.True(match.Foe.Alive, "the foe survives the strike");
        var before = hero.SpellCooldowns.Take(4).ToArray();
        TestAssert.True(before[0] > 7.0 && before[2] > 12.5 && before[3] > 37.5, $"Q, E and R run ({string.Join(", ", before.Select(F))})");
        match.Runtime.Combat.Kill(match.Foe, match.Ally.NetId);
        match.Advance(Step);
        Agree(match, client, wire, "assist");
        TestAssert.Near(before[0] - 7.0, hero.SpellCooldowns[0], Step + 1e-6, "Alpha Strike loses 7 s (Highlander's rank three)");
        TestAssert.Near(before[2] - 12.5, hero.SpellCooldowns[2], Step + 1e-6, "Wuju Style loses 12.5 s");
        TestAssert.Near(before[3] - 37.5, hero.SpellCooldowns[3], Step + 1e-6, "Highlander loses 37.5 s");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }

    /// <summary>
    /// A kill credited inside Alpha Strike's windup: BuffOnKill readies Q before its cast frame has paid, and the cast frame then starts Q's
    /// cooldown (raise-only, <c>AdjustVarsAfterSpellCast</c>). The client gets the 0x8A before its own cast frame, so both end with Q running.
    /// </summary>
    [Check("cooldowns.highlander-a-kill-inside-alpha-strikes-windup-leaves-q-running-on-both-sides")]
    public static void HighlanderKillInsideTheWindup()
    {
        var (match, wire, client) = Highlander();
        using var owned = match;
        var hero = match.Hero;
        match.Foe.MaxHealth = match.Foe.Health = 50000.0;
        TestAssert.True(match.Cast(0, match.Foe), "Alpha Strike casts");
        match.Advance(Step);
        match.Runtime.Combat.Kill(match.FarFoe, hero.NetId);
        match.Advance(1.0);
        Agree(match, client, wire, "kill in the windup");
        TestAssert.True(hero.SpellCooldowns[0] > 1.0, "Alpha Strike's own cast frame started its cooldown after the reset");
        TestAssert.True(hero.SpellCooldowns[2] <= 1e-6 && hero.SpellCooldowns[3] <= 1e-6, "Wuju Style and Highlander are ready");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed: " + Failures(match));
    }
}
