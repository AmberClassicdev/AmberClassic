using System;
using System.Collections.Generic;
using System.Linq;
using Amberline.Core;
using Amberline.Data;

namespace Amberline.Tests.Core;

/// <summary>
/// The 1.0.0.132 mastery pages the checks use as input (batch 770: patch 1.0.0.129 remade the trees, ids 211-463). Each full
/// page spends 30 points in a retail-style 21/9 split, legal under the 132 TalentTree.xml rules (row thresholds 0/4/8/12/16/20,
/// prerequisites at their MaxRank), and opens its 21-point tree with that tree's summoner talent.
/// </summary>
public static class MasteryTestPages
{
    // Utility 9: Expanded Mind 3, Summoner's Insight, Meditation 3, Swiftness 1, Greed 1.
    private static readonly Dictionary<int, int> UtilityNine = new() { [413] = 3, [411] = 1, [423] = 3, [422] = 1, [432] = 1 };
    // Defense 9: Hardiness 3, Summoner's Resolve, Durability 4, Veteran's Scars.
    private static readonly Dictionary<int, int> DefenseNine = new() { [313] = 3, [311] = 1, [322] = 4, [332] = 1 };

    /// <summary>
    /// 21/0/9 Offense: Summoner's Wrath, Butcher 2, Brute Force 1, Sorcery 4, Havoc 3, Arcane Knowledge, Blast 4, Archmage 4,
    /// Executioner; plus the utility nine.
    /// </summary>
    public static MasteryPage Offense(MasteryCatalog catalog) => Page(catalog, new Dictionary<int, int>
    {
        [211] = 1, [214] = 2, [212] = 1, [223] = 4, [234] = 3, [233] = 1, [243] = 4, [253] = 4, [262] = 1,
    }, UtilityNine);

    /// <summary>
    /// 0/21/9 Defense: Summoner's Resolve, Hardiness 3, Tough Skin 2, Durability 4, Indomitable 2, Veteran's Scars, Initiator 3,
    /// Honor Guard 3, Resistance 1, Juggernaut; plus the utility nine.
    /// </summary>
    public static MasteryPage Defense(MasteryCatalog catalog) => Page(catalog, new Dictionary<int, int>
    {
        [311] = 1, [313] = 3, [314] = 2, [322] = 4, [331] = 2, [332] = 1, [342] = 3, [352] = 3, [312] = 1, [362] = 1,
    }, UtilityNine);

    /// <summary>
    /// 0/9/21 Utility: Summoner's Insight, Good Hands 3, Swiftness 4, Greed 4, Wealth 2, Awareness 2, Strength of Spirit 3,
    /// Runic Affinity, Mastermind; plus the defense nine.
    /// </summary>
    public static MasteryPage Utility(MasteryCatalog catalog) => Page(catalog, new Dictionary<int, int>
    {
        [411] = 1, [412] = 3, [422] = 4, [432] = 4, [442] = 2, [443] = 2, [452] = 3, [434] = 1, [463] = 1,
    }, DefenseNine);

    /// <summary>One talent at the given rank (a first-row talent is legal alone): 211, 311 and 411 hold the summoner flags.</summary>
    public static MasteryPage Single(MasteryCatalog catalog, int id, int rank = 1) =>
        MasteryPage.Create(catalog, new Dictionary<int, int> { [id] = rank });

    private static MasteryPage Page(MasteryCatalog catalog, Dictionary<int, int> main, Dictionary<int, int> rest) =>
        MasteryPage.Create(catalog, main.Concat(rest).ToDictionary(pair => pair.Key, pair => pair.Value));
}

public static class MasteryChecks
{
    [Check("data.masteries.catalog")]
    public static void CatalogReadsShippedTree()
    {
        var catalog = new MasteryCatalog();
        TestAssert.Equal(49, catalog.Definitions.Count);
        TestAssert.Equal(
            new[] { 211, 212, 213, 214, 222, 223, 224, 231, 232, 233, 234, 241, 242, 243, 252, 253, 262,
                311, 312, 313, 314, 322, 323, 331, 332, 333, 334, 341, 342, 343, 352, 353, 362,
                411, 412, 413, 414, 422, 423, 424, 432, 433, 434, 442, 443, 444, 452, 453, 463 },
            catalog.Definitions.Select(definition => definition.Id).ToArray());
        TestAssert.Equal(17, catalog.Definitions.Count(definition => definition.GroupId == 1));
        TestAssert.Equal(16, catalog.Definitions.Count(definition => definition.GroupId == 2));
        TestAssert.Equal(16, catalog.Definitions.Count(definition => definition.GroupId == 3));

        var sorcery = catalog.Get(223);
        TestAssert.Equal("Sorcery", sorcery.DisplayName);
        TestAssert.Equal(1, sorcery.GroupId);
        TestAssert.Equal(2, sorcery.Row);
        TestAssert.Equal(4, sorcery.MaxRank);
        TestAssert.Equal(4, sorcery.LevelDescriptions.Count);

        var arcaneKnowledge = catalog.Get(233);
        TestAssert.Equal(223, arcaneKnowledge.PrerequisiteId);
        TestAssert.Equal(3, arcaneKnowledge.Row);
        TestAssert.True(arcaneKnowledge.LevelDescriptions[0].Contains("10%", StringComparison.Ordinal));
        TestAssert.True(catalog.TryGet(463, out var mastermind), "allocatable summoner capstone must be present");
        TestAssert.Equal("Mastermind", mastermind!.DisplayName);
        TestAssert.Equal(3, mastermind.GroupId);
        TestAssert.Equal("Summoner's Wrath", catalog.Get(211).DisplayName);
        TestAssert.Equal("Strength of Spirit", catalog.Get(452).DisplayName);
        TestAssert.True(catalog.TryGet(106, out _) == false, "the 1.0.0.126 ids are gone from the 132 tree");
        TestAssert.True(catalog.TryGet(120, out _) == false, "126's Presence of the Master is 132's Mastermind 463");
        TestAssert.Equal(4, catalog.Get(222).MaxRank);
        TestAssert.Equal(3, catalog.Get(234).MaxRank, "Havoc: three ranks (its script's fourth 1.02 is unreachable data)");
        TestAssert.Equal(2, catalog.Get(214).MaxRank);
        // Batch 770: the catalog's error names no patch any more.
        try
        {
            catalog.Get(106);
            TestAssert.True(false, "an unknown id must throw");
        }
        catch (KeyNotFoundException error)
        {
            TestAssert.False(error.Message.Contains("patch-126", StringComparison.Ordinal), error.Message);
        }
    }

    [Check("core-masteries.page-validation")]
    public static void PageRejectsInvalidAllocationsAndAcceptsEmpty()
    {
        var catalog = new MasteryCatalog();
        var empty = MasteryPage.Create(catalog);
        TestAssert.Equal(0, empty.TotalPoints);
        TestAssert.Equal(0, empty.Allocations.Count);

        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog,
            new Dictionary<int, int> { [999] = 1 }));
        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog,
            new Dictionary<int, int> { [213] = -1 }));
        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog,
            new Dictionary<int, int> { [213] = 5 }));
        // 31 points: a full page plus one.
        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog,
            MasteryTestPages.Offense(catalog).Allocations.Append(new KeyValuePair<int, int>(313, 1))
                .ToDictionary(pair => pair.Key, pair => pair.Value)));

        // Row 2 opens at 4 earlier points; Arcane Knowledge needs Sorcery at its MaxRank.
        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog,
            new Dictionary<int, int> { [223] = 1 }));
        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog,
            new Dictionary<int, int> { [213] = 4, [214] = 2, [212] = 2, [222] = 4, [233] = 1 }));

        var valid = MasteryPage.Create(catalog,
            new Dictionary<int, int> { [213] = 4, [223] = 4, [233] = 1 });
        TestAssert.Equal(9, valid.TotalPoints);
        TestAssert.Equal(1, valid.Rank(233));
        TestAssert.Equal(0, valid.Rank(212));
        TestAssert.True(MasteryPage.TryCreate(catalog, valid.Allocations, out _, out _));

        var source = new Dictionary<int, int> { [213] = 1 };
        var isolated = MasteryPage.Create(catalog, source);
        source[213] = 3;
        source[223] = 3;
        TestAssert.Equal(1, isolated.Rank(213));
        TestAssert.Equal(0, isolated.Rank(223));

        foreach (var page in new[] { MasteryTestPages.Offense(catalog), MasteryTestPages.Defense(catalog), MasteryTestPages.Utility(catalog) })
            TestAssert.Equal(30, page.TotalPoints, "the test pages spend the whole 30 points");
    }

    [Check("core-masteries.prerequisite-ranks")]
    public static void PageRequiresFullPrerequisiteRanks()
    {
        var catalog = new MasteryCatalog();

        // Arcane Knowledge (233) needs Sorcery (223) at 4.
        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog,
            new Dictionary<int, int> { [213] = 4, [214] = 2, [223] = 3, [233] = 1 }));
        var arcane = MasteryPage.Create(catalog, new Dictionary<int, int> { [213] = 4, [223] = 4, [233] = 1 });
        TestAssert.Equal(1, arcane.Rank(233));

        // Weapon Expertise (232) needs Alacrity (222) at 4.
        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog,
            new Dictionary<int, int> { [212] = 3, [211] = 1, [222] = 3, [213] = 1, [232] = 1 }));
        var expertise = MasteryPage.Create(catalog, new Dictionary<int, int> { [212] = 3, [211] = 1, [222] = 4, [232] = 1 });
        TestAssert.Equal(1, expertise.Rank(232));

        // Veteran's Scars (332) needs Durability (322) at 4.
        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog,
            new Dictionary<int, int> { [313] = 3, [311] = 1, [322] = 3, [312] = 1, [332] = 1 }));
        var scars = MasteryPage.Create(catalog, new Dictionary<int, int> { [313] = 3, [311] = 1, [322] = 4, [332] = 1 });
        TestAssert.Equal(1, scars.Rank(332));

        // Bladed Armor (334) needs Tough Skin (314) at 2.
        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog,
            new Dictionary<int, int> { [313] = 3, [314] = 1, [322] = 4, [334] = 1 }));
        var bladed = MasteryPage.Create(catalog, new Dictionary<int, int> { [313] = 3, [314] = 2, [322] = 3, [334] = 1 });
        TestAssert.Equal(1, bladed.Rank(334));
    }

    [Check("core-masteries.summoner-level-validation")]
    public static void PageValidatesSummonerLevelSeparately()
    {
        var catalog = new MasteryCatalog();
        TestAssert.Throws<ArgumentOutOfRangeException>(() => MasteryPage.Create(catalog, summonerLevel: 0));
        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog,
            new Dictionary<int, int> { [213] = 2 }, summonerLevel: 1));
        TestAssert.True(MasteryPage.TryCreate(catalog, null, out var empty, out var error, summonerLevel: 1));
        TestAssert.Equal(0, empty.TotalPoints);
        TestAssert.True(error is null);
    }

    [Check("core-masteries.effects-zero")]
    public static void EffectsKeepEmptyPageCompatible()
    {
        var effects = MasteryEffects.Calculate(MasteryPage.Empty, championLevel: 18);
        TestAssert.True(effects.IsEmpty);
        TestAssert.Equal(0, effects.Modifiers.Count);
        TestAssert.Equal(1.0, effects.Combat.NonTrueDamageDealtMultiplier);
        TestAssert.Equal(1.0, effects.Combat.NonTrueDamageTakenMultiplier);
        TestAssert.Equal(0.0, effects.Summoner.DefensiveMastery);
        TestAssert.Equal(0.0, effects.Economy.ExperiencePercentBonus);
    }

    /// <summary>The 21/0/9 offense page at champion level 18, every number the 132 scripts' own (and the tooltip's).</summary>
    [Check("core-masteries.effects-stat-formulas")]
    public static void EffectsUseShippedStatFormulas()
    {
        var effects = MasteryEffects.Calculate(MasteryTestPages.Offense(new MasteryCatalog()), championLevel: 18);

        TestAssert.Near(1.0, effects.Modifiers["FlatPhysicalDamageMod"], 1e-12, "Brute Force 1");
        TestAssert.Near(-0.04, effects.Modifiers["PercentCooldownMod"], 1e-12, "Sorcery 4");
        TestAssert.Near(0.1, effects.Modifiers["PercentMagicPenetrationMod"], 1e-12, "Arcane Knowledge");
        TestAssert.Near(18.0, effects.Modifiers["FlatMagicDamageMod"], 1e-12, "Blast 4: 1 AP per level");
        TestAssert.Near(12.0 * 18, effects.Modifiers["FlatMPPoolMod"], 1e-12, "Expanded Mind 3: 12 mana per level");
        TestAssert.Near(10.0, effects.Modifiers["FlatEnergyPoolMod"], 1e-12, "Expanded Mind 3: 10 energy, on the energy pool only");
        TestAssert.Near(0.6, effects.Modifiers["FlatMPRegenMod"], 1e-12, "Meditation 3: 3 mana per 5 s");
        TestAssert.Near(0.005, effects.Modifiers["PercentMovementSpeedMod"], 1e-12, "Swiftness 1");
        TestAssert.Near(0.5, effects.Economy.GoldPer10Bonus, 1e-12, "Greed 1");
        TestAssert.Near(1.015, effects.Combat.NonTrueDamageDealtMultiplier, 1e-12, "Havoc 3");
        TestAssert.Near(0.05, effects.Combat.ArchmageAbilityPowerPercent, 1e-12, "Archmage 4");
        TestAssert.Equal(25000.0, effects.Combat.ArchmageDurationSeconds);
        TestAssert.Near(4.0, effects.Combat.ButcherMinionDamageBonus, 1e-12, "Butcher 2");
        TestAssert.Equal(1.0, effects.Summoner.OffensiveMastery);
        TestAssert.Equal(1.0, effects.Summoner.UtilityMastery);
        TestAssert.Equal(0.0, effects.Summoner.DefensiveMastery);
        TestAssert.True(effects.Summoner.BurningEmbersWhenIgniteOnCooldown);
        TestAssert.Equal(5.0, effects.Summoner.BurningEmbersAbilityPowerBonus);
        TestAssert.Equal(5.0, effects.Summoner.BurningEmbersAttackDamageBonus);
    }

    /// <summary>The 0/21/9 defense page at champion level 1, and the 0/9/21 utility page's Strength of Spirit (126's 112 is 132's 452).</summary>
    [Check("core-masteries.effects-combat-hooks")]
    public static void EffectsExposeCombatHooksWithoutApplyingThem()
    {
        var catalog = new MasteryCatalog();
        var effects = MasteryEffects.Calculate(MasteryTestPages.Defense(catalog));

        TestAssert.Near(6.0, effects.Modifiers["FlatArmorMod"], 1e-12, "Hardiness 3");
        TestAssert.Near(2.0, effects.Modifiers["FlatSpellBlockMod"], 1e-12, "Resistance 1");
        TestAssert.Near(30.0 + 6.0, effects.Modifiers["FlatHPPoolMod"], 1e-12, "Veteran's Scars 30 + Durability 4 at level 1 (6)");
        TestAssert.Near(0.03, effects.Modifiers["PercentHPPoolMod"], 1e-12, "Juggernaut 3%");
        TestAssert.Near(2.0, effects.Combat.IndomitableDamageBlock, 1e-12, "Indomitable 2");
        TestAssert.Near(0.985, effects.Combat.NonTrueDamageTakenMultiplier, 1e-12, "Honor Guard 3");
        TestAssert.Near(2.0, effects.Combat.NonHeroAiDamageReduction, 1e-12, "Tough Skin 2");
        TestAssert.Equal(1.0, effects.Summoner.DefensiveMastery);
        TestAssert.Near(0.0, effects.Combat.StrengthOfSpiritHealthRegenPerMaxMana, 1e-12);
        TestAssert.False(effects.Modifiers.ContainsKey("FlatHPRegenMod"), "no flat regen row on the defense page");
        // Durability scales with the champion level.
        TestAssert.Near(30.0 + 6.0 * 18, MasteryEffects.Calculate(MasteryTestPages.Defense(catalog), 18).Modifiers["FlatHPPoolMod"], 1e-12);

        var utility = MasteryEffects.Calculate(MasteryTestPages.Utility(catalog));
        TestAssert.Near(0.002, utility.Combat.StrengthOfSpiritHealthRegenPerMaxMana, 1e-12,
            "Strength of Spirit 3: 1% of maximum mana per 5 s, per point of mana per second");
        TestAssert.False(utility.Modifiers.ContainsKey("FlatHPRegenMod"),
            "Strength of Spirit's per-mana factor is not a flat regen row");
    }

    /// <summary>
    /// Batch 551: a mastery's <c>IncFlatDodgeMod</c> composed with a script buff's dodge as remaining chance. Batch 770: the
    /// 1.0.0.132 tree has no dodge talent (126's Evasion 114 is gone; 132's Evasion 333 cuts area damage), so a full page adds
    /// nothing and the buff's dodge passes through alone.
    /// </summary>
    [Check("core-masteries.evasion-composes-with-script-dodge")]
    public static void EvasionComposesWithScriptDodge()
    {
        var catalog = new MasteryCatalog();
        var noItems = new Amberline.Simulation.InventorySlot?[] { null, null, null, null, null, null };
        var buff = new Amberline.Core.Buffs.BuffState("dodge-source",
            new Amberline.Core.Buffs.BuffPayload { Dodge = 0.20 }, expiresAt: 100.0);
        double Dodge(MasteryPage page, bool withBuff) => Amberline.Simulation.Stats.HeroStatCalculator.Calculate(
            GameData.Default, new ItemCatalog(GameData.Default.Paths),
            new Amberline.Simulation.Stats.HeroStatsInput("Annie", 6, noItems,
                withBuff ? [buff] : null, masteries: page)).Dodge;
        foreach (var page in new[] { MasteryTestPages.Offense(catalog), MasteryTestPages.Defense(catalog), MasteryTestPages.Utility(catalog) })
        {
            TestAssert.False(MasteryEffects.Calculate(page, 6).Modifiers.ContainsKey("FlatDodgeMod"), "no 132 talent writes dodge");
            TestAssert.Near(0.0, Dodge(page, withBuff: false), 1e-12, "the page alone");
            TestAssert.Near(0.20, Dodge(page, withBuff: true), 1e-12, "the buff alone");
        }
    }

    /// <summary>
    /// Batch 770: the stock 1.0.0.126 AIR client sends (and accounts store) its own tree's ids, which the 1.0.0.132 tree does not
    /// hold. The roster drops them with one stderr line per page and seats the rest, instead of stopping the match at
    /// "Unknown mastery id 106" (NOT RETAIL: retail's client and server shared one tree). A known page passes untouched.
    /// </summary>
    [Check("server.roster-skips-unknown-mastery-ids", Serial = true)]
    public static void RosterSkipsUnknownMasteryIdsWithOneLine()
    {
        var catalog = new MasteryCatalog();
        var known = new Dictionary<int, int> { [213] = 4, [223] = 4, [233] = 1 };
        TestAssert.True(ReferenceEquals(known, MasteryPage.KnownOnly(catalog, known, out var none)) && none.Count == 0,
            "a page of the data's own tree passes as it is");

        // Annie's 126 Easy bot page, as the 126 AIR client would send it, plus one 132 id.
        var page126 = new Dictionary<int, int> { [102] = 4, [103] = 1, [106] = 3, [107] = 1, [212] = 1 };
        var previous = Console.Error;
        var error = new System.IO.StringWriter();
        Console.SetError(error);
        try
        {
            var loadout = Amberline.Server.MatchRoster.ValidateLoadout(
                new Amberline.Server.MatchPlayer("Air126", "Annie", Team.Order, Masteries: page126));
            TestAssert.Equal(1, loadout.Masteries.TotalPoints, "only the known id is seated");
            TestAssert.Equal(1, loadout.Masteries.Rank(212));
            var lines = error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            TestAssert.Equal(1, lines.Length, "one line per page: " + error);
            TestAssert.True(lines[0].Contains("Air126", StringComparison.Ordinal) &&
                            lines[0].Contains("102, 103, 106, 107", StringComparison.Ordinal), lines[0]);
            // The production roster path (the platform's launch) seats the page instead of throwing.
            var seats = Amberline.Server.MatchRoster.ToParticipants([
                new Amberline.Server.MatchPlayer("Air126", "Annie", Team.Order, Masteries: page126),
            ]);
            TestAssert.Equal(1, seats[0].Masteries!.TotalPoints);
        }
        finally
        {
            Console.SetError(previous);
        }
        // An id this tree does not hold is still refused when a caller validates the page itself (the launcher's editor).
        TestAssert.Throws<ArgumentException>(() => MasteryPage.Create(catalog, page126));
    }

    /// <summary>
    /// Batch 770: the headless match seats its bots from the shipped <c>&lt;X&gt;BotMasteries.inibin</c>, which 1.0.0.132 ships
    /// byte for byte from 1.0.0.126 (ids its tree no longer holds). Batch 768 saw such a match stop at "Unknown mastery id 106";
    /// the reader now seats a legal page of the data's tree with the shipped points per tree, and the match starts.
    /// </summary>
    [Check("tools.headless-match-starts-with-132-bot-mastery-pages", Serial = true, TimeoutSeconds = 600)]
    public static void HeadlessMatchStartsWithBotMasteryPagesOfTheDataTree()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "league-b770-masteries-" + Guid.NewGuid().ToString("N"));
        var pages = new List<(string Champion, MasteryPage Page)>();
        try
        {
            // A 3v3 (the smallest ordinary roster) of bot champions, those that ship Bot*Masteries.inibin first.
            var supported = ChampionCatalog.PlayableNames.Where(Amberline.Simulation.Bots.BotController.SupportedChampions.Contains).ToArray();
            var withPages = supported.Where(name => BotLoadoutReader.Read(GameData.Default.Paths, name, 3) is { Masteries.Count: > 0 }).ToArray();
            TestAssert.True(withPages.Length > 0, "at least one playable bot champion ships a mastery section");
            var roster = withPages.Concat(supported.Except(withPages)).Take(6).ToArray();
            var report = Amberline.Tools.Diagnostics.HeadlessMatch.Run(new Amberline.Tools.Diagnostics.HeadlessMatchOptions(770, root, 1.0,
                roster)
            {
                OnStep = runtime =>
                {
                    if (!runtime.Session.Started || pages.Count > 0) return;
                    pages.AddRange(runtime.Session.Participants.Select(seat => (seat.ChampionName, seat.Hero?.Masteries ?? seat.Masteries)));
                },
                StopWhen = () => pages.Count > 0,
            });
            TestAssert.Equal("stopped", report.Outcome, report.Summary());
            var catalog = new MasteryCatalog();
            TestAssert.Equal(6, pages.Count, "every seat was created");
            foreach (var (champion, page) in pages.Where(seat => withPages.Contains(seat.Champion)))
            {
                TestAssert.Equal(30, page.TotalPoints, champion + ": the Hard section's 30 points");
                TestAssert.True(page.Allocations.Keys.All(id => catalog.TryGet(id, out _)), champion + ": every id is in the data's tree");
                TestAssert.True(MasteryPage.TryCreate(catalog, page.Allocations, out _, out var why), champion + ": " + why);
            }
        }
        finally
        {
            try { System.IO.Directory.Delete(root, recursive: true); } catch (Exception) { /* temp output only */ }
        }
    }

    /// <summary>The 0/9/21 utility page: the economy fields and the summoner flags (126's 120 is 132's 463).</summary>
    [Check("core-masteries.effects-summoner-economy")]
    public static void EffectsExposeSummonerAndEconomyValues()
    {
        var effects = MasteryEffects.Calculate(MasteryTestPages.Utility(new MasteryCatalog()));

        TestAssert.Near(0.025, effects.Economy.ExperiencePercentBonus, 1e-12, "Awareness 2");
        TestAssert.Equal(2.0, effects.Economy.GoldPer10Bonus, "Greed 4");
        TestAssert.Near(0.10, effects.Economy.RespawnTimePercent, 1e-12, "Good Hands 3");
        TestAssert.Near(0.02, effects.Modifiers["PercentMovementSpeedMod"], 1e-12, "Swiftness 4");
        TestAssert.Near(6.0, effects.Modifiers["FlatArmorMod"], 1e-12, "Hardiness 3");
        TestAssert.Near(0.15, effects.Summoner.SummonerCooldownBonus, 1e-12, "Mastermind");
        TestAssert.Equal(1.0, effects.Summoner.UtilityMastery);
        TestAssert.Equal(1.0, effects.Summoner.DefensiveMastery);
        TestAssert.Equal(0.0, effects.Summoner.OffensiveMastery);
        TestAssert.Near(0.20, effects.Combat.NeutralBuffDurationPercent, 1e-12, "Runic Affinity");
        TestAssert.True(effects.Combat.MonsterBuffs);
        TestAssert.False(effects.Combat.MonsterBuffs2, "no 132 talent adds MonsterBuffs2");
    }
}
