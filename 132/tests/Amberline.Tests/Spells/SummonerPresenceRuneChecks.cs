using System.Buffers.Binary;
using System.Text;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

/// <summary>
/// Rune 5378, Greater Quintessence of Presence ("UNIQUE: Reduces the cooldown of summoner spells by 4%"). In 1.0.0.126 the SummonerExhaust
/// and SummonerRevive AdjustCooldown scripts subtracted a further 0.04 while <c>AvatarVars.HasRune5378</c> was true. Batch 772: the 1.0.0.132
/// scripts dropped that branch (no 132 script, talent, buff or item reads a HasRune variable), so the rune changes no summoner cooldown and
/// only the Mastermind multiplier (SummonerCooldownBonus 0.15) remains.
/// </summary>
public static partial class ChampionLiveRegressionChecks
{
    private const int PresenceRuneId = 5378;

    /// <summary>
    /// The shipped data has no record for 5378 (Items stops at 5374, the AIR rune catalog too), so a page can
    /// hold it only through a host-supplied catalog, built here the way the launcher builds one.
    /// </summary>
    private static RunePage PresenceRunePage()
    {
        var shipped = new RuneCatalog(GameData.Default.Paths);
        var catalog = new RuneCatalog(shipped.All.Append(new RuneData(PresenceRuneId, RuneColor.Black,
            RuneTier.Three, "Greater Quintessence of Presence",
            "UNIQUE: Reduces the cooldown of summoner spells by 4%", string.Empty, [])));
        return RunePage.Create(catalog, black: [PresenceRuneId]);
    }

    /// <summary>
    /// Batch 770: 126's Presence of the Master (120) is 1.0.0.132's Mastermind (463, Utility row 6: 20 earlier Utility points;
    /// SummonerCooldownBonus 0.15). 132 has no flat Revive cooldown bonus (126's Preservation 129, 30 s): its Revive mastery is
    /// Summoner's Resolve (311), which changes no cooldown, so "with preservation" is the utility page that also holds 311.
    /// </summary>
    private static MasteryPage PresencePage(bool preservation)
    {
        var catalog = new MasteryCatalog();
        if (preservation) return Amberline.Tests.Core.MasteryTestPages.Utility(catalog);
        var allocation = Amberline.Tests.Core.MasteryTestPages.Utility(catalog).Allocations
            .Where(pair => pair.Key >= 400).ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var (id, rank) in new[] { (213, 4), (223, 4), (233, 1) }) allocation[id] = rank;
        return MasteryPage.Create(catalog, allocation);
    }

    private static double ShippedAdjustCooldown(string script, double presence, bool rune, double preservation)
    {
        var loaded = BbScriptLoader.Load(GameDataPaths.Default, $"Spells/{script}.luaobj");
        var blocks = loaded.Script!.Event("AdjustCooldown")!.Blocks;
        var run = new BbTest.Run(blocks, setup: scope =>
        {
            scope.AvatarVars!["SummonerCooldownBonus"] = presence;
            scope.AvatarVars["HasRune5378"] = rune;
            scope.AvatarVars["ReviveCooldownBonus"] = preservation;
        });
        return run.Scope.ReturnValue as double? ?? double.NaN;
    }

    /// <summary>One real summoner cast: the stored cooldown and the cooldown the HUD packet carries.</summary>
    private static (double Cooldown, float Hud) CastSummonerWith(string spell, MasteryPage? masteries, bool rune)
    {
        using var match = new Match("Annie", Team.Order);
        var hero = match.Hero(0);
        var enemy = match.Hero(1);
        hero.SummonerSpells[0] = spell;
        hero.SummonerCooldowns[0] = 0.0;
        if (masteries is not null) hero.Masteries = masteries;
        if (rune) hero.Runes = PresenceRunePage();
        match.Runtime.HeroStats.Refresh(hero);
        if (spell == "SummonerRevive")
        {
            TestAssert.True(match.Runtime.Combat.ApplyHit(enemy.NetId, hero.NetId, 1_000_000,
                DamageType.True), "lethal hit");
            TestAssert.False(hero.Alive, "Revive is dead-only");
        }
        match.Clear();
        match.Cast(0, 0, spell == "SummonerExhaust" ? enemy : hero, summoner: true);
        var hud = match.Captures[0].Packets.Where(packet => packet.Plaintext.Length == 10 &&
                Opcode(packet) == 0x8B && ReadUInt32(packet, 1) == hero.NetId && packet.Plaintext.Span[5] == 0x80)
            .Select(packet => BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[6..])).ToArray();
        TestAssert.Equal(1, hud.Length, $"{spell}: one HUD cooldown packet");
        return (hero.SummonerCooldowns[0], hud[0]);
    }

    [Check("spells-live-summoner-presence-rune-cooldowns")]
    public static void PresenceQuintessenceStacksWithPresenceInExhaustAndRevive()
    {
        var presence = PresencePage(preservation: false);
        var both = PresencePage(preservation: true);
        var preservationOnly = MasteryPage.Create(new MasteryCatalog(), new Dictionary<int, int> { [311] = 1 });

        // Exhaust: BaseCooldown = 210 * (1 - 0.15) with Mastermind, 210 without (nil ~= 0 takes the multiplier branch, batch 303); the
        // 132 script has no rune term.
        foreach (var (masteries, rune, expected, label) in new (MasteryPage?, bool, double, string)[]
        {
            (null, false, 210.0, "no page"),
            (null, true, 210.0, "rune alone: no 132 rune term"),
            (presence, false, 210.0 * 0.85, "Presence alone"),
            (presence, true, 210.0 * 0.85, "Presence and the rune"),
        })
        {
            var (cooldown, hud) = CastSummonerWith("SummonerExhaust", masteries, rune);
            TestAssert.Near(expected, cooldown, 1e-9, $"Exhaust, {label}");
            TestAssert.Near(expected, hud, 1e-3, $"Exhaust HUD cooldown, {label}");
        }

        // Revive: BaseCooldown = 540 * multiplier (the script's own 540 under nil, batch 303); 132 has no flat bonus and no rune term.
        foreach (var (masteries, rune, expected, label) in new (MasteryPage?, bool, double, string)[]
        {
            (null, false, 540.0, "no page"),
            (null, true, 540.0, "rune alone"),
            (presence, false, 459.0, "Presence alone"),
            (presence, true, 459.0, "Presence and the rune"),
            (preservationOnly, true, 540.0, "Summoner's Resolve and the rune, no Mastermind (132: no flat bonus)"),
            (both, false, 540.0 * 0.85, "Mastermind and Summoner's Resolve"),
            (both, true, 540.0 * 0.85, "Mastermind, Summoner's Resolve and the rune"),
        })
        {
            var (cooldown, hud) = CastSummonerWith("SummonerRevive", masteries, rune);
            TestAssert.Near(expected, cooldown, 1e-9, $"Revive, {label}");
            TestAssert.Near(expected, hud, 1e-3, $"Revive HUD cooldown, {label}");
        }

        // The other summoner scripts have no rune branch, so the rune changes nothing for them.
        foreach (var (spell, baseCooldown) in new[]
                 { ("SummonerHeal", 270.0), ("SummonerHaste", 210.0), ("SummonerBoost", 210.0), ("SummonerMana", 180.0) })
        {
            var (without, _) = CastSummonerWith(spell, presence, rune: false);
            var (with, hud) = CastSummonerWith(spell, presence, rune: true);
            TestAssert.Near(baseCooldown * 0.85, without, 1e-9, $"{spell} Presence");
            TestAssert.Near(without, with, 0.0, $"{spell} ignores the rune");
            TestAssert.Near(without, hud, 1e-3, $"{spell} HUD cooldown");
        }
    }

    [Check("spells-live-summoner-presence-rune-matches-shipped-script")]
    public static void PresenceQuintessenceCooldownsEqualTheShippedAdjustCooldownScripts()
    {
        var presence = PresencePage(preservation: false);
        var both = PresencePage(preservation: true);
        foreach (var rune in new[] { false, true })
        {
            // Exhaust returns BaseCooldown; the script's own value is the oracle, never current runtime output (132: no rune term).
            TestAssert.Near(210.0 * (1.0 - 0.15),
                ShippedAdjustCooldown("SummonerExhaust", 0.15, rune, 0.0), 1e-4, $"Exhaust script, rune {rune}");
            TestAssert.Near(ShippedAdjustCooldown("SummonerExhaust", 0.15, rune, 0.0),
                CastSummonerWith("SummonerExhaust", presence, rune).Cooldown, 1e-4, $"Exhaust cast, rune {rune}");

            // The 132 Revive returns BaseCooldown (no NewCooldown, no flat Preservation term).
            TestAssert.Near(540.0 * (1.0 - 0.15),
                ShippedAdjustCooldown("SummonerRevive", 0.15, rune, 30.0), 1e-4, $"Revive script, rune {rune}");
            TestAssert.Near(ShippedAdjustCooldown("SummonerRevive", 0.15, rune, 30.0),
                CastSummonerWith("SummonerRevive", both, rune).Cooldown, 1e-4, $"Revive cast, rune {rune}");
        }
        // A bonus written as 0 closes the branch and the script returns nothing. No feeder writes 0 (Presence's
        // SetVarsByLevel writes 0.15 and an unallocated talent writes nothing), so a hero without Presence reads nil and
        // takes the branch (spells-summoners-cast-cooldowns-from-scripts, batch 303).
        TestAssert.True(double.IsNaN(ShippedAdjustCooldown("SummonerExhaust", 0.0, true, 0.0)),
            "an explicit 0 bonus sets nothing in the Exhaust script");
    }

    /// <summary>
    /// Batch 841 (batch 834 F1): Smite and Clairvoyance on 1.0.0.132. Their inibins keep 126's cooldowns (75, 55) and nothing reads them: the
    /// cast's AdjustCooldown says 70, and 70 x (1 - SummonerCooldownBonus) under Mastermind (463, 0.15). Its <c>BBIf</c> takes the multiplier
    /// branch with no mastery too, because an unwritten AvatarVars entry is nil and nil is not 0. The stored cooldown and the HUD's 0x8B
    /// (summoner bit) carry it. The 132 tooltips read <c>@f2@</c> for the cooldown (fontconfig_en_US: "@f2@ second Cooldown"; 126's read
    /// the inibin's <c>@Cooldown@</c>), which the script's SpellUpdateTooltip writes with the same literal and branch; the server runs that hook
    /// and sends the row as a 0x84 entry of selector 0x78 + slot, the summoner book (SpellPackets.SummonerTooltipSlot).
    /// </summary>
    [Check("spells-live-p132-smite-and-clairvoyance-cooldown-and-tooltip-with-and-without-mastermind")]
    public static void SmiteAndClairvoyanceCooldownAndTooltipFollowTheirScripts()
    {
        foreach (var spell in new[] { "SummonerSmite", "SummonerClairvoyance" })
        foreach (var (masteries, expected, label) in new (MasteryPage?, double, string)[]
                 { (null, 70.0, "no page"), (PresencePage(preservation: false), 70.0 * 0.85, "Mastermind") })
        {
            using var match = new Match("Annie", Team.Order);
            var hero = match.Hero(0);
            // The other of the two in the second slot: its row is the summoner book's slot 1 (selector 0x79); both write the cooldown at @f2@.
            hero.SummonerSpells[0] = spell;
            hero.SummonerSpells[1] = spell == "SummonerSmite" ? "SummonerClairvoyance" : "SummonerSmite";
            hero.SummonerCooldowns[0] = 0.0;
            if (masteries is not null) hero.Masteries = masteries;
            match.Runtime.HeroStats.Refresh(hero);
            match.Clear();
            match.Runtime.Tick(1.05);
            match.Runtime.Delivery.Flush();

            for (var slot = 0; slot < 2; slot++)
            {
                var selector = 0x78 + slot;
                TestAssert.True(hero.ScriptSpellTooltips.TryGetValue(SpellPackets.SummonerTooltipSlot + slot, out var row),
                    $"{spell}, {label}: SpellUpdateTooltip wrote summoner slot {slot}'s row");
                TestAssert.Near(expected, row![1], 1e-3, $"{spell}, {label}: slot {slot}'s @f2@ cooldown");
                var wire = match.Captures[0].Packets.Where(packet => Opcode(packet) == 0x84).SelectMany(packet =>
                {
                    var bytes = packet.Plaintext.ToArray();
                    var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(5));
                    return Enumerable.Range(0, count).Select(index => 7 + 17 * index)
                        .Where(offset => ReadUInt32(packet, offset) == hero.NetId && bytes[offset + 4] == selector)
                        .Select(offset => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset + 9)));
                }).ToArray();
                TestAssert.True(wire.Length > 0 && Math.Abs(wire[^1] - expected) < 1e-3,
                    $"{spell}, {label}: the 0x84 row of selector 0x{selector:X2} carries f2 {expected} (got {string.Join(",", wire)})");
            }

            var target = hero;
            if (spell == "SummonerSmite")
            {
                target = new Entity(match.Runtime.World.Allocate(UnitType.Monster), EntityKind.Neutral, UnitType.Monster, Team.Neutral,
                    hero.Position + new Position2(0, 300))
                {
                    Name = "Wraith", Alive = true, Created = true, Visible = true, MaxHealth = 3000, Health = 3000, Radius = 35,
                };
                match.Runtime.World.Register(target);
                foreach (var seat in match.Runtime.Session.Participants)
                {
                    seat.View.Known.Add(target.NetId);
                    seat.View.Visible.Add(target.NetId);
                }
            }
            match.Clear();
            match.Cast(0, 0, target, summoner: true);
            var hud = match.Captures[0].Packets.Where(packet => packet.Plaintext.Length == 10 &&
                    Opcode(packet) == 0x8B && ReadUInt32(packet, 1) == hero.NetId && packet.Plaintext.Span[5] == 0x80)
                .Select(packet => BinaryPrimitives.ReadSingleLittleEndian(packet.Plaintext.Span[6..])).ToArray();
            TestAssert.Near(expected, hero.SummonerCooldowns[0], 1e-6, $"{spell}, {label}: the cast's cooldown");
            TestAssert.Equal(1, hud.Length, $"{spell}, {label}: one HUD cooldown packet");
            TestAssert.Near(expected, hud[0], 1e-3, $"{spell}, {label}: the HUD cooldown");
            TestAssert.Equal(0, match.Runtime.Spells.SummonerScripts!.ScriptFailures.Count, $"{spell}, {label}: no summoner script failed");
        }
    }

    /// <summary>
    /// Batch 841: every 1.0.0.132 summoner script with a SpellUpdateTooltip (14 of them; 126 has none) runs it in a live match without a
    /// script failure and writes the hero's summoner row, whose cooldown entry (the index the script writes its <c>BaseCooldown</c> to) is
    /// the script's own AdjustCooldown answer.
    /// </summary>
    [Check("spells-live-p132-every-summoner-tooltip-runs-without-failure")]
    public static void EverySummonerTooltipRunsWithoutFailure()
    {
        var withHook = new Amberline.Simulation.Spells.Bb.BbSummonerModule().ScriptNames
            .Where(name => BbScriptLoader.Load(GameDataPaths.Default, $"Spells/{name}.luaobj").Script?.Event("SpellUpdateTooltip") is not null)
            .ToArray();
        TestAssert.Equal(14, withHook.Length, "summoner scripts with SpellUpdateTooltip: " + string.Join(",", withHook));
        foreach (var spell in withHook)
        {
            using var match = new Match("Annie", Team.Order);
            var hero = match.Hero(0);
            hero.SummonerSpells[0] = spell;
            match.Runtime.Tick(1.05);
            var module = match.Runtime.Spells.SummonerScripts!;
            TestAssert.Equal("", string.Join(" | ", module.ScriptFailures.Select(failure => failure.Message)), $"{spell}: no script failure");
            TestAssert.True(hero.ScriptSpellTooltips.ContainsKey(SpellPackets.SummonerTooltipSlot), $"{spell}: the summoner row is written");
            var binding = module.Binding(hero, 0, match.Runtime.Spells.LoadoutHooks!.OwnerFor(hero)!)!;
            var index = (int)binding.Script.Event("SpellUpdateTooltip")!.Blocks.SelectMany(BbM3.Flatten)
                .First(block => block.Function == "BBSetSpellToolTipVar" && block.Text("ValueVar") == "BaseCooldown").Number("Index")!.Value;
            var cooldown = module.Router!.RunAdjustCooldown(binding.Script, hero, 0, binding.Rank, binding);
            TestAssert.Near(cooldown ?? double.NaN, hero.ScriptSpellTooltips[SpellPackets.SummonerTooltipSlot][index - 1], 1e-3,
                $"{spell}: the tooltip's cooldown (@f{index}@) is the cast's");
        }
    }

    [Check("data.summoner-scripts-rune-5378-readers")]
    public static void OnlyExhaustAndReviveReadTheUnsetPresenceRuneFlag()
    {
        // 1.0.0.132 (batch 772): no shipped script sets or reads a HasRune variable any more (126: SummonerExhaust and SummonerRevive).
        var paths = GameDataPaths.Default;
        var needle = Encoding.ASCII.GetBytes("HasRune");
        var found = new List<string>();
        foreach (var folder in new[] { "Spells", "Talents", "Buffs", "Items" })
            foreach (var file in Directory.EnumerateFiles(paths.Data(folder), "*.luaobj"))
                if (File.ReadAllBytes(file).AsSpan().IndexOf(needle) >= 0)
                    found.Add(Path.GetFileNameWithoutExtension(file));
        TestAssert.Equal(Array.Empty<string>(), found.Order().ToArray(),
            "scripts that mention a HasRune variable");
        TestAssert.False(new RuneCatalog(GameData.Default.Paths).Contains(PresenceRuneId),
            "the shipped rune records stop at 5374; the rune has no data record");
    }
}
