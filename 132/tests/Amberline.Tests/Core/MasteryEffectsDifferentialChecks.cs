using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;

namespace Amberline.Tests.Core;

/// <summary>
/// The differential oracle of the mastery conversion (batch 140 step 1a, batch 142 step 1c). <see
/// cref="MasteryEffects.Calculate"/> reads every number from the shipped <c>DATA/Talents/&lt;id&gt;.luaobj</c> scripts; this
/// file holds an independent table and compares the two on the pages the mastery checks pin. Batch 770: the 1.0.0.126
/// hand-written tables are gone with the 126 tree; the oracle is now the 1.0.0.132 tooltips
/// (<c>Menu/fontconfig_en_US.txt</c>, <c>game_mastery_level&lt;N&gt;desc_&lt;id&gt;</c>), a second shipped source the scripts
/// were never read from. A difference is a conversion bug unless it is one of the classified findings.
/// </summary>
public static class MasteryEffectsDifferentialChecks
{
    /// <summary>
    /// The 1.0.0.132 tooltip numbers, written as the stat rows the calculator keys them by. They are not production code and
    /// nothing else may read them. A per-5-seconds tooltip is the per-second row divided by 5.
    /// </summary>
    public static class Tooltips
    {
        public static MasteryContribution Calculate(MasteryPage page, int championLevel = 1)
        {
            if (page.TotalPoints == 0) return MasteryContribution.Empty;

            var modifiers = new Dictionary<string, double>(StringComparer.Ordinal);
            var rank = page.Rank;
            double Of(int id, params double[] byRank) => rank(id) == 0 ? 0.0 : byRank[rank(id) - 1];

            Add(modifiers, "FlatPhysicalDamageMod", rank(212));                         // Brute Force +1/2/3 AD
            Add(modifiers, "FlatMagicDamageMod", rank(213));                            // Mental Force +1..4 AP
            Add(modifiers, "PercentAttackSpeedMod", rank(222) * 0.01);                  // Alacrity +1..4%
            Add(modifiers, "PercentCooldownMod", rank(223) * -0.01);                    // Sorcery 1..4% CDR
            Add(modifiers, "FlatCritChanceMod", rank(231) * 0.01);                      // Deadliness 1..4%
            Add(modifiers, "PercentArmorPenetrationMod", Of(232, 0.10));               // Weapon Expertise 10%
            Add(modifiers, "PercentMagicPenetrationMod", Of(233, 0.10));               // Arcane Knowledge 10%
            Add(modifiers, "FlatCritDamageMod", Of(241, 0.10));                        // Lethality +10%
            Add(modifiers, "PercentLifeStealMod", rank(242) * 0.01);                    // Vampirism 1..3%
            Add(modifiers, "FlatMagicDamageMod", rank(243) * 0.25 * championLevel);     // Blast 0.25..1 AP per level
            Add(modifiers, "FlatArmorPenetrationMod", rank(252) * 2.0);                 // Sunder 2/4/6
            Add(modifiers, "FlatSpellBlockMod", rank(312) * 2.0);                       // Resistance 2/4/6
            Add(modifiers, "FlatArmorMod", rank(313) * 2.0);                            // Hardiness 2/4/6
            Add(modifiers, "FlatHPPoolMod", rank(322) * 1.5 * championLevel);           // Durability 1.5..6 per level
            Add(modifiers, "FlatHPRegenMod", rank(323) / 5.0);                          // Vigor 1/2/3 per 5 s
            Add(modifiers, "FlatHPPoolMod", Of(332, 30.0));                            // Veteran's Scars +30
            Add(modifiers, "PercentCooldownMod", rank(343) * -0.0015 * championLevel);  // Enlightenment 0.15% per level
            Add(modifiers, "PercentHPPoolMod", Of(362, 0.03));                         // Juggernaut 3% maximum health
            Add(modifiers, "FlatMPPoolMod", Of(413, 4.0, 8.0, 12.0) * championLevel);   // Expanded Mind 4/8/12 mana per level
            Add(modifiers, "FlatEnergyPoolMod", Of(413, 4.0, 7.0, 10.0));              // ... or +4/7/10 energy
            Add(modifiers, "PercentMovementSpeedMod", rank(422) * 0.005);               // Swiftness 0.5..2%
            Add(modifiers, "FlatMPRegenMod", rank(423) / 5.0);                          // Meditation 1/2/3 per 5 s
            Add(modifiers, "PercentSpellVampMod", rank(433) * 0.01);                    // Transmutation 1/2/3%
            Add(modifiers, "PercentCooldownMod", rank(453) * -0.02);                    // Intelligence 2/4/6%

            var combat = new MasteryCombatEffects(
                ArchmageAbilityPowerPercent: rank(253) * 0.0125,                          // Archmage 1.25..5% AP
                ArchmageDurationSeconds: rank(253) > 0 ? 25000.0 : 0.0,                   // (the talent's own permanent add)
                StrengthOfSpiritHealthRegenPerMaxMana: Of(452, 0.004, 0.007, 0.01) / 5.0, // 0.4/0.7/1% of max mana per 5 s
                IndomitableDamageBlock: rank(331),                                        // Indomitable 1/2
                NonTrueDamageDealtMultiplier: 1.0 + rank(234) * 0.005,                    // Havoc 0.5..2%
                NonTrueDamageTakenMultiplier: 1.0 - rank(352) * 0.005,                    // Honor Guard 0.5..1.5%
                NonHeroAiDamageReduction: rank(314),                                      // Tough Skin 1/2
                NeutralBuffDurationPercent: Of(434, 0.20),                               // Runic Affinity 20%
                MonsterBuffs: rank(434) > 0,
                MonsterBuffs2: false,
                ButcherMinionDamageBonus: rank(214) * 2.0);                               // Butcher 2/4

            var summoner = new MasterySummonerEffects(
                SummonerCooldownBonus: Of(463, 0.15),                                    // Mastermind 15%
                OffensiveMastery: rank(211),
                DefensiveMastery: rank(311),
                UtilityMastery: rank(411),
                // Summoner's Wrath: Ignite "Increases Ability Power and Attack Damage by 5 while on cooldown".
                BurningEmbersWhenIgniteOnCooldown: rank(211) > 0,
                BurningEmbersAbilityPowerBonus: Of(211, 5.0),
                BurningEmbersAttackDamageBonus: Of(211, 5.0));

            var economy = new MasteryEconomyEffects(
                ExperiencePercentBonus: rank(443) * 0.0125,                               // Awareness 1.25..5%
                GoldPer10Bonus: rank(432) * 0.5,                                          // Greed 0.5..2 per 10 s
                RespawnTimePercent: Of(412, 0.04, 0.07, 0.10));                          // Good Hands 4/7/10%

            return new MasteryContribution(
                new ReadOnlyDictionary<string, double>(modifiers), combat, summoner, economy);
        }

        private static void Add(IDictionary<string, double> modifiers, string name, double value)
        {
            if (value == 0.0) return;
            modifiers[name] = modifiers.TryGetValue(name, out var previous) ? previous + value : value;
        }
    }

    [Check("core-masteries.effects-summoner-avatar-vars")]
    public static void SummonerFieldsAreTheFeedersAvatarVars()
    {
        // The summoner fields are the AvatarVars the 132 feeder scripts write in SetVarsByLevel, and the flags the other 132
        // talents write in UpdateSelfBuffActions for Spells/APBonusDamageToTowers.luaobj to read. Pin the writes key by key.
        var catalog = new MasteryCatalog();
        var all = BbLoadoutReport.Talents(new Dictionary<int, int>
        {
            [211] = 1, [311] = 1, [411] = 1, [463] = 1,
            [214] = 2, [224] = 1, [334] = 1, [341] = 1, [342] = 3, [353] = 3, [362] = 1, [424] = 1, [444] = 1,
        }, 1);

        TestAssert.Near(1.0, all[211].Avatar("offensiveMastery"), 1e-12, "211");
        TestAssert.Near(1.0, all[311].Avatar("defensiveMastery"), 1e-12, "311");
        TestAssert.Near(1.0, all[411].Avatar("utilityMastery"), 1e-12, "411");
        TestAssert.Near(0.15, all[463].Avatar("SummonerCooldownBonus"), 1e-12, "463");
        // The flags and amounts Spells/APBonusDamageToTowers.luaobj reads. Each talent writes them in UpdateSelfBuffActions; the
        // live poll runs that hook on the match clock, which Demolitionist's own 3 s BBExecutePeriodically (ExecuteImmediately
        // false) needs before its first write, so the hook is polled once more at 3 s.
        foreach (var (id, rank, flag, amount, value) in new[]
                 {
                     (214, 2, "MasteryButcher", "MasteryButcherAmt", 4.0), (224, 1, "MasteryDemolitionist", "MasteryDemolitionistAmt", 10.0),
                     (334, 1, "MasteryBladedArmor", "MasteryBladedArmorAmt", 6.0), (341, 1, "MasterySeigeCommander", "", 0.0),
                     (342, 3, "MasteryInitiate", "MasteryInitiateAmt", 0.03), (353, 3, "MasteryBounty", "MasteryBountyAmt", 24.0),
                     (362, 1, "MasteryJuggernaut", "", 0.0), (424, 1, "Scout", "", 0.0), (444, 1, "MasteryScholar", "", 0.0),
                 })
        {
            // The flags are Lua booleans, which Avatar (a number reader) answers 0: read the owner's table itself.
            var owner = new BbLoadoutOwner();
            var report = BbLoadoutReport.BindTalent(owner, id, rank, 1);
            TestAssert.True(report is not null, "talent " + id + " binds");
            report!.HookBuffs("UpdateSelfBuffActions", now: 3.0);
            TestAssert.True(Equals(owner.AvatarVars[flag], true), id + " writes AvatarVars." + flag);
            if (amount.Length > 0) TestAssert.Near(value, report.Avatar(amount), 1e-12, id + " writes AvatarVars." + amount);
        }
        TestAssert.True(all[224].Avatar("MasteryDemolitionistAmt") == 0.0,
            "Demolitionist's periodic does not run at once (the static report's clock reads 0)");

        // The four feeders are MaxRank 1, so the summoner scripts' CO_EQUAL 1 tests read exactly their one legal rank.
        foreach (var id in new[] { 211, 311, 411, 463 })
        {
            TestAssert.True(catalog.TryGet(id, out var definition), "catalog knows talent " + id);
            TestAssert.Equal(1, definition!.MaxRank, "talent " + id + " must stay MaxRank 1");
        }
        // Each probe owns its own loadout: one feeder's report never sees another's writes.
        TestAssert.Near(0.0, all[211].Avatar("defensiveMastery"), 1e-12);
    }

    [Check("core-masteries.effects-script-matches-hand-written")]
    public static void ScriptsAgreeWithTheHandWrittenTables()
    {
        var catalog = new MasteryCatalog();
        foreach (var (name, page) in new[]
                 {
                     ("empty", MasteryPage.Empty),
                     ("offense", MasteryTestPages.Offense(catalog)),
                     ("defense", MasteryTestPages.Defense(catalog)),
                     ("utility", MasteryTestPages.Utility(catalog)),
                     // 21/9/0 AD carry: Brute Force, Alacrity, Deadliness, Lethality, Vampirism, Sunder, Executioner; Vigor.
                     ("ad-carry", MasteryPage.Create(catalog, new Dictionary<int, int>
                     {
                         [212] = 3, [211] = 1, [222] = 4, [231] = 4, [241] = 1, [242] = 3, [252] = 3, [234] = 1, [262] = 1,
                         [313] = 3, [311] = 1, [323] = 3, [312] = 2,
                     })),
                     // 9/0/21 caster: Mental Force, Sorcery, Arcane Knowledge; Transmutation, Intelligence, Mastermind.
                     ("caster", MasteryPage.Create(catalog, new Dictionary<int, int>
                     {
                         [213] = 4, [223] = 4, [233] = 1,
                         [413] = 3, [411] = 1, [423] = 3, [422] = 1, [432] = 4, [433] = 3, [442] = 1, [453] = 3, [434] = 1, [463] = 1,
                     })),
                     // 9/21/0 bruiser: Weapon Expertise; Vigor, Evasion, Enlightenment, Mercenary, Juggernaut.
                     ("bruiser", MasteryPage.Create(catalog, new Dictionary<int, int>
                     {
                         [212] = 3, [211] = 1, [222] = 4, [232] = 1,
                         [313] = 3, [311] = 1, [323] = 3, [322] = 4, [333] = 3, [343] = 3, [353] = 3, [362] = 1,
                     })),
                 })
        {
            foreach (var level in new[] { 1, 6, 12, 18 })
            {
                var script = MasteryEffects.Calculate(page, level);
                var tooltip = Tooltips.Calculate(page, level);
                Compare(name + "@" + level, tooltip, script);
            }
        }

        // A mastery script may only ask the probe world for things the conversion understands. Anything else would
        // have been dropped silently and would show up here rather than as a wrong number.
        TestAssert.Equal(0, BbLoadoutReport.IgnoredNatives.Count,
            "mastery scripts called: " + string.Join(", ", BbLoadoutReport.IgnoredNatives));
    }

    private static void Compare(string where, MasteryContribution expected, MasteryContribution actual)
    {
        TestAssert.Equal(expected.Modifiers.Count, actual.Modifiers.Count,
            where + ": modifier keys " + string.Join(",", actual.Modifiers.Keys.Order()));
        foreach (var (key, value) in expected.Modifiers)
        {
            TestAssert.True(actual.Modifiers.TryGetValue(key, out var found), where + ": missing modifier " + key);
            TestAssert.Near(value, found, 1e-12, where + ": modifier " + key);
        }

        Compare(where, expected.Combat, actual.Combat);
        TestAssert.Near(expected.Economy.ExperiencePercentBonus, actual.Economy.ExperiencePercentBonus, 1e-12, where + ": XP");
        TestAssert.Near(expected.Economy.GoldPer10Bonus, actual.Economy.GoldPer10Bonus, 1e-12, where + ": gold");
        TestAssert.Near(expected.Economy.RespawnTimePercent, actual.Economy.RespawnTimePercent, 1e-12, where + ": respawn");
        TestAssert.Near(expected.Summoner.SummonerCooldownBonus, actual.Summoner.SummonerCooldownBonus, 1e-12, where + ": Mastermind");
        TestAssert.Equal(expected.Summoner with { SummonerCooldownBonus = 0.0 }, actual.Summoner with { SummonerCooldownBonus = 0.0 },
            where + ": summoner");
    }

    private static void Compare(string where, MasteryCombatEffects expected, MasteryCombatEffects actual)
    {
        TestAssert.Near(expected.ArchmageAbilityPowerPercent, actual.ArchmageAbilityPowerPercent, 1e-12, where + ": Archmage");
        TestAssert.Equal(expected.ArchmageDurationSeconds, actual.ArchmageDurationSeconds, where + ": Archmage duration");
        TestAssert.Near(expected.StrengthOfSpiritHealthRegenPerMaxMana, actual.StrengthOfSpiritHealthRegenPerMaxMana,
            1e-12, where + ": Strength of Spirit");
        TestAssert.Near(expected.IndomitableDamageBlock, actual.IndomitableDamageBlock, 1e-12, where + ": Indomitable");
        TestAssert.Near(expected.NonTrueDamageDealtMultiplier, actual.NonTrueDamageDealtMultiplier, 1e-12, where + ": Havoc");
        TestAssert.Near(expected.NonTrueDamageTakenMultiplier, actual.NonTrueDamageTakenMultiplier, 1e-12, where + ": Honor Guard");
        TestAssert.Near(expected.NonHeroAiDamageReduction, actual.NonHeroAiDamageReduction, 1e-12, where + ": Tough Skin");
        TestAssert.Near(expected.NeutralBuffDurationPercent, actual.NeutralBuffDurationPercent, 1e-12,
            where + ": neutral buff duration");
        TestAssert.Equal(expected.MonsterBuffs, actual.MonsterBuffs, where + ": MonsterBuffs");
        TestAssert.Equal(expected.MonsterBuffs2, actual.MonsterBuffs2, where + ": MonsterBuffs2");
        TestAssert.Near(expected.ButcherMinionDamageBonus, actual.ButcherMinionDamageBonus, 1e-12, where + ": Butcher");
    }

    [Check("core-masteries.effects-script-damage-probes")]
    public static void DamageHooksLeaveTrueDamageAloneAndKnowTheirOwnShape()
    {
        // The 132 damage hooks: 234 Havoc and 262 Executioner (CharOnPreDealDamage, both gated on not-TRUE), 331 Indomitable,
        // 333 Evasion and 352 Honor Guard (CharOnPreDamage, gated on not-TRUE), 314 Tough Skin (CharOnPreDamage, gated on the
        // attacker's kind only). A true-damage hit from a hero must come back untouched, and each hook's shape must be exact.
        var catalog = new MasteryCatalog();
        var defense = BbLoadoutReport.Talents(MasteryTestPages.Defense(catalog).Allocations, 18);
        var offense = BbLoadoutReport.Talents(MasteryTestPages.Offense(catalog).Allocations, 18);
        foreach (var (talents, id, hook) in new[]
                 {
                     (offense, 234, "CharOnPreDealDamage"), (offense, 262, "CharOnPreDealDamage"),
                     (defense, 314, "CharOnPreDamage"), (defense, 331, "CharOnPreDamage"), (defense, 352, "CharOnPreDamage"),
                 })
        {
            var trueDamage = talents[id].Damage(hook, BbDamageProbe.TrueDamage);
            TestAssert.Near(1.0, trueDamage.Multiplier, 1e-12, "talent " + id + " must not touch true damage (factor)");
            TestAssert.Near(0.0, trueDamage.Block, 1e-12, "talent " + id + " must not touch true damage (block)");
        }

        // Each hook's own shape, read on the axis that carries its number: Indomitable (331) and Tough Skin (314) are pure
        // flat blocks, so Block is exact; Havoc (234) and Honor Guard (352) are pure factors, so Multiplier is.
        TestAssert.Near(2.0, defense[331].Damage("CharOnPreDamage", BbDamageProbe.Physical).Block, 1e-12);
        TestAssert.Near(2.0, defense[314].Damage("CharOnPreDamage", BbDamageProbe.AiNonHero).Block, 1e-12);
        TestAssert.Near(1.015, offense[234].Damage("CharOnPreDealDamage", BbDamageProbe.Physical).Multiplier, 1e-12);
        TestAssert.Near(0.985, defense[352].Damage("CharOnPreDamage", BbDamageProbe.Magic).Multiplier, 1e-12);
        // A hook that does not fire leaves the hit alone, and that reads as the neutral (1, 0) on both axes.
        var silent = defense[314].Damage("CharOnPreDamage", BbDamageProbe.Physical);
        TestAssert.Near(1.0, silent.Multiplier, 1e-12, "Tough Skin only fires against an AI non-turret non-hero");
        TestAssert.Near(0.0, silent.Block, 1e-12, "Tough Skin only fires against an AI non-turret non-hero");
        // Executioner reads the target's health percent, which the probe world answers 1 (full health): it never fires there.
        var executioner = offense[262].Damage("CharOnPreDealDamage", BbDamageProbe.Physical);
        TestAssert.Near(1.0, executioner.Multiplier, 1e-12, "Executioner is silent on a full-health probe target");
    }
}
