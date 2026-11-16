using System;
using System.Collections.Generic;
using System.Linq;
using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Stats;
using Amberline.Tests.Core;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("combat.live-loadout-mastery-hooks")]
    public static void LiveLoadoutMasteryHooks()
    {
        // Batch 770: the pages are the 1.0.0.132 tree's (Core/MasteryChecks.cs MasteryTestPages). 126's Ardor (111), Harden
        // Skin (113), Nimbleness (127), Tenacity (125), Defensive (144) and Offensive Mastery (137) are 132's Archmage (253),
        // Indomitable (331), nothing, Honor Guard (352), Tough Skin (314) and Butcher (214, through APBonusDamageToTowers).
        using var match = new Match("Annie", Team.Order, ally: true);
        var runtime = match.Runtime;
        var catalog = new MasteryCatalog();
        var target = match.Hero(1);
        target.Masteries = MasteryTestPages.Utility(catalog);
        target.Level = 18;
        // A synthetic source buff has no lifetime of its own: the default ExpiresAt of 0 ends it on the first buff
        // update, which the batch-144 live loop now runs. Give them the long finite life the other live fixtures use.
        target.Buffs.Add(new BuffState("regen-one", new BuffPayload { HpRegen = 1.0 }, expiresAt: 100.0));
        target.Buffs.Add(new BuffState("regen-two", new BuffPayload { HpRegen = 1.0 }, expiresAt: 100.0));
        runtime.HeroStats.Refresh(target);

        var itemCatalog = new ItemCatalog(runtime.GameData.Paths);
        var noPenetration = HeroStatCalculator.Calculate(runtime.GameData, itemCatalog,
            new HeroStatsInput(target.ChampionName, target.Level,
                new InventorySlot?[] { null, null, null, null, null, null },
                masteries: MasteryPage.Empty));
        // Arcane Knowledge (233) needs Sorcery (223) at 4, which needs 4 points in the first row.
        var arcanePage = MasteryPage.Create(catalog, new Dictionary<int, int> { [213] = 4, [223] = 4, [233] = 1 });
        var arcaneOnly = HeroStatCalculator.Calculate(runtime.GameData, itemCatalog,
            new HeroStatsInput(target.ChampionName, target.Level,
                new InventorySlot?[] { null, null, null, null, null, null },
                masteries: arcanePage));
        TestAssert.Near(0.0, noPenetration.MagicPenPercent, 1e-12,
            "no loadout has no magic penetration");
        TestAssert.Near(0.10, arcaneOnly.MagicPenPercent, 1e-12,
            "Arcane Knowledge contributes its native 10 percent");
        // Batch 144: Void Staff's 40 percent is the VoidStaff buff its own script keeps alive, so the pair is read off
        // a live hero; neither the inventory aggregation nor the calculator carries it any more.
        TestAssert.Near(1.0 - 0.6 * 0.9,
            Live(target.ChampionName, target.Level, [3135], arcanePage, hero => hero.MagicPenetrationPercent), 1e-12,
            "Void Staff and Arcane Knowledge compose as native remaining resistance");

        var withMastery = HeroStatCalculator.Calculate(runtime.GameData, itemCatalog,
            new HeroStatsInput(target.ChampionName, target.Level, target.Inventory,
                target.Buffs, target.ItemState, target.Runes, target.Masteries));
        var withoutMastery = HeroStatCalculator.Calculate(runtime.GameData, itemCatalog,
            new HeroStatsInput(target.ChampionName, target.Level, target.Inventory,
                target.Buffs, target.ItemState, target.Runes, MasteryPage.Empty));
        TestAssert.Near(withMastery.HpRegen, target.HealthRegen, 1e-9,
            "production refresh applies loadout regen");
        TestAssert.Near(withMastery.HpRegen - withoutMastery.HpRegen,
            withMastery.MaxMp * 0.002, 1e-9,
            "Strength of Spirit (452 at 3: 1% of maximum mana per 5 s) is added once with multiple regen buffs");

        // Archmage (253) is a live buff, MasteryBlastBuff: its own BuffOnUpdateActions takes its prior share back off the
        // flat AP pool and keeps PercentMod of the rest, so it settles on the pool times 1.05. With Deathcap the two loops
        // read each other: ZhonyasRing divides the pool by 1.04 when MasteryBlastBuff is on (its shipped constant, not
        // Archmage's 1.05) and MasteryBlastBuff divides it by 1.3 when ZhonyasRing is, so the steady state is
        // B / (1 - 0.3 / (1.04 x 1.3) - 0.05 / (1.05 x 1.3)) over the plain pool B. Measured on a live hero.
        var offense = MasteryTestPages.Offense(catalog);
        var blastAp = MasteryEffects.Calculate(offense, target.Level).Modifiers["FlatMagicDamageMod"];
        var deathcapFlatAp = itemCatalog.Get(3089)!.Modifier("FlatMagicDamageMod");
        var flatApBasis = Live(target.ChampionName, target.Level, [], MasteryPage.Empty,
            hero => hero.AbilityPower, 20.0);
        TestAssert.Near((flatApBasis + blastAp) * 1.05,
            Live(target.ChampionName, target.Level, [], offense, hero => hero.AbilityPower, 20.0), 1e-6,
            "Archmage applies to the complete active flat-AP pool");
        TestAssert.Near((flatApBasis + deathcapFlatAp) * 1.30,
            Live(target.ChampionName, target.Level, [3089], MasteryPage.Empty, hero => hero.AbilityPower, 20.0), 1e-6,
            "Deathcap applies its native multiplier to the complete flat-AP pool");
        TestAssert.Near((flatApBasis + deathcapFlatAp + blastAp) / (1.0 - 0.3 / (1.04 * 1.3) - 0.05 / (1.05 * 1.3)),
            Live(target.ChampionName, target.Level, [3089], offense, hero => hero.AbilityPower, 20.0), 1e-4,
            "Deathcap and Archmage settle on the two scripts' joint steady state");

        target.Masteries = MasteryTestPages.Defense(catalog);
        runtime.HeroStats.Refresh(target);
        TestAssert.Near(2.0, target.LoadoutMasteries.Combat.IndomitableDamageBlock, 1e-12);
        target.Health = target.MaxHealth = 10000.0;
        target.Protected = false;
        var aiSource = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, Team.Order, target.Position)
        { Health = 1000.0, MaxHealth = 1000.0, Armor = 0.0, SpellBlock = 0.0 });
        var before = target.Health;
        TestAssert.True(runtime.Combat.ApplyHit(aiSource.NetId, target.NetId, 100.0,
            DamageType.Physical, DamageSourceKind.Basic));
        var expectedTaken = (100.0 * CombatRules.ResistanceMultiplier(target.Armor) - 2.0 - 2.0) * 0.985;
        TestAssert.Near(expectedTaken, before - target.Health, 1e-8,
            "the CharOnPreDamage talents in id order after resistance: 314 Tough Skin (-2), 331 Indomitable (-2), 352 Honor Guard (x0.985)");

        target.Health = 10000.0;
        var aiSpellBefore = target.Health;
        TestAssert.True(runtime.Combat.ApplyHit(aiSource.NetId, target.NetId, 100.0,
            DamageType.Physical, DamageSourceKind.Spell));
        TestAssert.Near((100.0 * CombatRules.ResistanceMultiplier(target.Armor) - 4.0) * 0.985,
            aiSpellBefore - target.Health, 1e-8,
            "Tough Skin sees AI spell damage too");

        target.Health = 10000.0;
        var aiTrueBefore = target.Health;
        TestAssert.True(runtime.Combat.ApplyHit(aiSource.NetId, target.NetId, 100.0,
            DamageType.True, DamageSourceKind.Spell));
        TestAssert.Near(98.0, aiTrueBefore - target.Health, 1e-8,
            "Tough Skin (CharOnPreDamage, no damage-type test) reduces true damage too; Indomitable and Honor Guard do not");

        var attacker = match.Hero(0);
        attacker.Masteries = offense;
        runtime.HeroStats.Refresh(attacker);
        // Butcher's +4 is the attacker's own APBonusDamageToTowers BuffOnHitUnit (it runs with the attack's on-hit scripts,
        // before the loadout's pre-mitigation stage), reading AvatarVars.MasteryButcherAmt from the loadout's table.
        runtime.Spells.LoadoutHooks?.OwnerFor(attacker);
        TestAssert.True(attacker.Buffs.Any(buff => buff.Name == "APBonusDamageToTowers"),
            "the hero carries the shipped APBonusDamageToTowers that reads the 132 masteries");
        var minion = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, Team.Chaos, attacker.Position)
        { Health = 1000.0, MaxHealth = 1000.0, Armor = 0.0 });
        var minionBefore = minion.Health;
        TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, minion.NetId, 100.0,
            DamageType.Physical, DamageSourceKind.Basic));
        TestAssert.Near((100.0 + 4.0) * 1.015, minionBefore - minion.Health, 1e-8,
            "Butcher (+4) on the basic hit, then Havoc (x1.015) on a hero versus a minion");

        var spellVictim = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, Team.Chaos, attacker.Position)
        { Health = 1000.0, MaxHealth = 1000.0, SpellBlock = 0.0 });
        var spellBefore = spellVictim.Health;
        TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, spellVictim.NetId, 100.0,
            DamageType.Magic, DamageSourceKind.Spell));
        TestAssert.Near(101.5, spellBefore - spellVictim.Health, 1e-8,
            "Butcher is an on-hit branch, not spell damage; Havoc still applies");

        var trueVictim = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, Team.Chaos, attacker.Position)
        { Health = 1000.0, MaxHealth = 1000.0, Armor = 100.0 });
        var trueBefore = trueVictim.Health;
        TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, trueVictim.NetId, 100.0,
            DamageType.True, DamageSourceKind.Basic));
        TestAssert.Near(104.0, trueBefore - trueVictim.Health, 1e-8,
            "Butcher's on-hit bonus also applies to true damage; Havoc does not");

        target.Health = 10000.0;
        var heroSpellBefore = target.Health;
        TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, target.NetId, 100.0,
            DamageType.Magic, DamageSourceKind.Spell));
        // The offense page's Arcane Knowledge (233) is 10% magic penetration: the attacker's own, read off the hero.
        TestAssert.Near(0.10, attacker.MagicPenetrationPercent, 1e-12, "Arcane Knowledge on the attacker");
        TestAssert.Near((101.5 * CombatRules.ResistanceMultiplier(target.SpellBlock * 0.9) - 2.0) * 0.985,
            heroSpellBefore - target.Health, 1e-8,
            "Havoc, Arcane Knowledge's penetration, then the target's Indomitable and Honor Guard on non-true hero damage");
        target.Health = 10000.0;
        var heroTrueBefore = target.Health;
        TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, target.NetId, 100.0,
            DamageType.True, DamageSourceKind.Spell));
        TestAssert.Near(100.0, heroTrueBefore - target.Health, 1e-8,
            "Havoc, Indomitable and Honor Guard exclude true damage");

        var turret = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Turret),
            EntityKind.Turret, UnitType.Turret, Team.Chaos, attacker.Position)
        { Health = 1000.0, MaxHealth = 1000.0, Armor = 0.0 });
        target.Health = 10000.0;
        var turretPhysicalBefore = target.Health;
        TestAssert.True(runtime.Combat.ApplyHit(turret.NetId, target.NetId, 100.0,
            DamageType.Physical, DamageSourceKind.Basic));
        TestAssert.Near((100.0 * CombatRules.ResistanceMultiplier(target.Armor) - 2.0) * 0.985,
            turretPhysicalBefore - target.Health, 1e-8,
            "Tough Skin excludes turrets; Indomitable and Honor Guard reduce turret damage");

        var clone = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Monster),
            EntityKind.ShacoClone, UnitType.Monster, Team.Order, attacker.Position)
        { OwnerNetId = attacker.NetId, Health = 1000.0, MaxHealth = 1000.0, AttackDamage = 100.0 });
        var cloneVictim = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
            EntityKind.Minion, UnitType.Minion, Team.Chaos, attacker.Position)
        { Health = 1000.0, MaxHealth = 1000.0, Armor = 0.0 });
        var cloneBefore = cloneVictim.Health;
        TestAssert.True(runtime.Combat.ApplyHit(clone.NetId, cloneVictim.NetId, 100.0,
            DamageType.Physical, DamageSourceKind.Basic));
        TestAssert.Near(100.0, cloneBefore - cloneVictim.Health, 1e-8,
            "clone damage does not inherit the owner's masteries");

        target.Dodge = 1.0;
        var dodge = runtime.Combat.BasicAttackDamage(attacker, target,
            new AttackRolls(Crit: 1.0, Dodge: 0.0, Miss: 1.0));
        TestAssert.True(dodge.Dodged, "target dodge is rolled");
        TestAssert.False(target.Buffs.Any(buff => buff.Name == "Nimbleness"),
            "no 1.0.0.132 talent has a CharOnDodge (126's Nimbleness 127 is gone)");
    }

    /// <summary>
    /// One hero of a throwaway match that bought <paramref name="ids"/> and wears <paramref name="masteries"/>, read
    /// after the purchase and loadout scripts have run (batch 144). The flat-AP composition is the scripts' own
    /// feedback loops — <c>ZhonyasRing</c> keeps 30 percent of the flat pool beside its own share, <c>MasteryBlastBuff</c> (Archmage)
    /// PercentMod of the pool with its own share taken back off — so it is measured on a live hero instead of by
    /// <c>HeroStatCalculator.Calculate</c> over a synthetic inventory. <paramref name="flatAp"/> is one plain buff
    /// payload of flat ability power, the "active" pool the two loops scale.
    /// </summary>
    private static double Live(string champion, int level, int?[] ids, MasteryPage masteries,
        Func<Entity, double> read, double flatAp = 0.0)
    {
        using var match = new Match(champion, Team.Order, ally: true);
        var hero = match.Hero(0);
        hero.Level = level;
        hero.Masteries = masteries;
        var slots = new InventorySlot?[Amberline.Simulation.Inventory.SlotCount];
        for (var i = 0; i < ids.Length && i < slots.Length; i++)
            slots[i] = ids[i] is { } id ? new InventorySlot(id) : null;
        hero.Inventory = slots;
        if (flatAp != 0.0)
            hero.Buffs.Add(new BuffState("flat-ap-stat-test", new BuffPayload { AbilityPower = flatAp }, expiresAt: 100.0));
        // The loadout is on the hero before the loops start reading it back: without this refresh the first samples see a
        // pool that does not have the purchase in it yet and walk up to the multiply from there (ZhonyasRing's own loop
        // contracts by about 0.23 a sample, so three seconds is not enough to erase that start).
        match.Runtime.HeroStats.Refresh(hero);
        // The loops read back through BuffOnUpdateStats on the 0.25 s grid, so their multiply is only on the hero once a
        // few samples have run (same ramp as the attack-speed pool above: 3 s is past it).
        Advance(match, 3.0);
        match.Runtime.HeroStats.Refresh(hero);
        return read(hero);
    }

    [Check("combat.live-loadout-progression-hooks")]
    public static void LiveLoadoutProgressionHooks()
    {
        using var match = new Match("Annie", Team.Order);
        var runtime = match.Runtime;
        var hero = match.Hero(0);
        hero.Masteries = MasteryTestPages.Utility(new MasteryCatalog());
        runtime.HeroStats.Refresh(hero);

        hero.Experience = 0.0;
        TestAssert.True(runtime.Combat.AwardHeroProgress(hero, experience: 100.0));
        TestAssert.Near(102.5, hero.Experience, 1e-9,
            "Awareness (443 at 2: 2.5%) applies to production hero XP");

        // Playtest B3 #12 (26 Sept 2026): loadout gold-per-10 now shares the
        // ambient 90-second start gate. Measure just past it (batch 161: ambient
        // income is continuous at 1.3 g/s from the gate — retail footage, the
        // Xerath champion spotlight, October 2011 — so its 0.325 of the quarter
        // second is part of the window and Greed's 0.05 sits beside it: 432 at 4 is 2 gold per 10 s).
        var goldBefore = hero.Gold;
        runtime.Combat.CombatTick(1.0, regenerate: false);
        TestAssert.Near(0.0, hero.Gold - goldBefore, 1e-9,
            "Greed waits for the 90-second income gate");
        runtime.Combat.CombatTick(CombatService.PassiveGoldDelay - runtime.Combat.CurrentTime,
            regenerate: false);
        goldBefore = hero.Gold;
        runtime.Combat.CombatTick(0.25, regenerate: false);
        TestAssert.Near(0.05 + 1.3 * 0.25, hero.Gold - goldBefore, 1e-9,
            "Greed is continuous and remains separate from item GP10");

        hero.Alive = false;
        hero.Health = 0.0;
        var now = runtime.Combat.CurrentTime;
        runtime.Combat.ScheduleRespawn(hero);
        TestAssert.Near(now + ProgressionRules.DeathTime(hero.Level, now) * 0.90,
            hero.RespawnAt, 1e-9,
            "Good Hands (412 at 3: 10%) reduces the authoritative death timer");

        // The three shipped black records are independent loadout inputs:
        // 5366 reduces death time by 5%, 5367 contributes one gold-per-10
        // stat, and 5368 contributes 2% XP. Keep this production path
        // separate from the mastery-only assertions above.
        // 5366 is a Float32 DATA value promoted to this exact double.
        const double rune5366RespawnReduction = 0.05000000074505806;
        hero.Alive = true;
        hero.Health = hero.MaxHealth;
        hero.Runes = RunePage.Create(new RuneCatalog(runtime.GameData.Paths),
            black: new[] { 5366, 5367, 5368 });
        hero.Masteries = MasteryPage.Empty;
        runtime.HeroStats.Refresh(hero);
        TestAssert.Near(1.0, hero.LoadoutGoldPer10, 1e-9,
            "Greed-like rune gold stat is cached separately from item income");
        TestAssert.Near(0.02, hero.LoadoutExperiencePercent, 1e-9,
            "the 5368 rune contributes its shipped XP bonus");
        TestAssert.Near(rune5366RespawnReduction, hero.LoadoutRespawnTimePercent, 1e-12,
            "the 5366 rune reduces death time with the shipped positive modifier");
        hero.Experience = 0.0;
        TestAssert.True(runtime.Combat.AwardHeroProgress(hero, experience: 100.0));
        TestAssert.Near(102.0, hero.Experience, 1e-6,
            "the 5368 rune is applied by the production XP hook");
        var runeGoldBefore = hero.Gold;
        runtime.Combat.CombatTick(0.5, regenerate: false);
        TestAssert.Near(0.05 + 1.3 * 0.5, hero.Gold - runeGoldBefore, 1e-9,
            "rFlatGoldPer10 follows the existing continuous loadout-income path beside the continuous ambient rate");
        hero.Experience = 0.0;
        hero.Alive = false;
        hero.Health = 0.0;
        now = runtime.Combat.CurrentTime;
        hero.RespawnAt = 0.0;
        runtime.Combat.ScheduleRespawn(hero);
        TestAssert.Near(now + ProgressionRules.DeathTime(hero.Level, now) *
            (1.0 - rune5366RespawnReduction),
            hero.RespawnAt, 1e-9,
            "the 5366 rune combines as a positive death-time reduction");
    }

    /// <summary>
    /// The step-1a report and the step-1b live hooks are two independent runs of the same shipped scripts: the report
    /// reads the numbers off a probe world (batch 140 step 1a), the live hooks run the character hooks inside a real hit
    /// (step 1b). This check holds them against each other, so a hook that is dispatched under the wrong name, at the
    /// wrong gate, or with the wrong <c>TalentLevel</c>, shows up as a wrong hit rather than as a plausible number.
    /// </summary>
    [Check("combat.live-loadout-mastery-hooks-match-contribution")]
    public static void LiveLoadoutMasteryHooksMatchTheContributionRecord()
    {
        var catalog = new MasteryCatalog();
        foreach (var (name, page) in new[]
                 {
                     ("defense", MasteryTestPages.Defense(catalog)), ("offense", MasteryTestPages.Offense(catalog)),
                     ("utility", MasteryTestPages.Utility(catalog)),
                 })
        {
            var combat = MasteryEffects.Calculate(page, 18).Combat;
            using var match = new Match("Annie", Team.Order, ally: true);
            var runtime = match.Runtime;
            var attacker = match.Hero(0);
            var target = match.Hero(1);
            attacker.Level = target.Level = 18;
            attacker.Masteries = target.Masteries = page;
            runtime.HeroStats.Refresh(attacker);
            runtime.HeroStats.Refresh(target);
            // The page is bound with the character, not on the first damage it takes (ProductionMatchComposition's
            // HeroCreated runs OwnerFor for exactly that reason), and re-made the first time a hook asks for it after
            // the page or the level changed (BbLoadoutHooks.PageFor). This fixture hands the page in after creation,
            // so that first ask is the first hit below — and the bind installs its live marker buffs (Archmage's
            // MasteryBlastBuff, Runic Affinity's MonsterBuffs), whose add runs a hero-stats refresh that takes a pool pinned above the hero's
            // computed maximum back down to it (HeroStatsServices.Refresh's clamp). Bind the way production does
            // first, so the pool the hits measure is the talents' alone.
            runtime.Spells.LoadoutHooks?.OwnerFor(attacker);
            runtime.Spells.LoadoutHooks?.OwnerFor(target);
            target.Health = target.MaxHealth = 100000.0;
            target.Protected = false;

            // A minion source has no mastery of its own, so every change is the target's page. The resistance is the
            // only other factor and it is derived here, not pinned: what is under test is the mastery fields.
            var aiSource = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
                EntityKind.Minion, UnitType.Minion, Team.Order, target.Position)
            { Health = 1000.0, MaxHealth = 1000.0, Armor = 0.0, SpellBlock = 0.0 });
            foreach (var (type, kind) in new[]
                     {
                         (DamageType.Physical, DamageSourceKind.Basic),
                         (DamageType.Physical, DamageSourceKind.Spell),
                         (DamageType.Magic, DamageSourceKind.Spell),
                         (DamageType.True, DamageSourceKind.Spell),
                     })
            {
                target.Health = 10000.0;
                var before = target.Health;
                TestAssert.True(runtime.Combat.ApplyHit(aiSource.NetId, target.NetId, 100.0, type, kind));
                TestAssert.Near(Expected(new MasteryCombatEffects(), combat, 100.0, type, kind,
                        targetIsHero: true, sourceIsAiNonHero: true, targetIsAiNonHero: false,
                        target.Armor, target.SpellBlock),
                    before - target.Health, 1e-8,
                    name + " takes " + type + " " + kind + " from an AI non-hero");
            }

            // The attacker's page alone: a minion target has no masteries and no resistance, so the hit is the
            // attacker's CharOnPreDealDamage and APBonusDamageToTowers' Butcher branch and nothing else.
            foreach (var (type, kind) in new[]
                     {
                         (DamageType.Physical, DamageSourceKind.Basic),
                         (DamageType.Magic, DamageSourceKind.Spell),
                         (DamageType.True, DamageSourceKind.Basic),
                     })
            {
                var victim = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Minion),
                    EntityKind.Minion, UnitType.Minion, Team.Chaos, attacker.Position)
                { Health = 1000.0, MaxHealth = 1000.0, Armor = 0.0, SpellBlock = 0.0 });
                var before = victim.Health;
                TestAssert.True(runtime.Combat.ApplyHit(attacker.NetId, victim.NetId, 100.0, type, kind));
                TestAssert.Near(Expected(combat, new MasteryCombatEffects(), 100.0, type, kind,
                        targetIsHero: false, sourceIsAiNonHero: false, targetIsAiNonHero: true,
                        victim.Armor, victim.SpellBlock),
                    before - victim.Health, 1e-8,
                    name + " deals " + type + " " + kind + " to an AI non-hero");
            }
        }
    }

    /// <summary>
    /// The mastery application, written as one formula: the attacker's Butcher bonus on a basic hit of an AI non-turret
    /// non-hero (APBonusDamageToTowers' BuffOnHitUnit, with the attack's on-hit scripts) and its non-true factor (Havoc) before
    /// mitigation, then resistance, then the target's CharOnPreDamage talents in id order: Tough Skin's block of an AI
    /// non-hero hit, Indomitable's block of a non-true hit, Honor Guard's non-true factor. This is the order
    /// <c>combat.live-loadout-mastery-hooks</c> pins with literals. Resistance is derived here from the target's own armor
    /// and spell block, and true damage takes neither. Executioner never fires: every target here is at full health.
    /// </summary>
    private static double Expected(MasteryCombatEffects attacker, MasteryCombatEffects target, double amount,
        DamageType type, DamageSourceKind kind, bool targetIsHero, bool sourceIsAiNonHero, bool targetIsAiNonHero,
        double armor, double spellBlock)
    {
        if (amount <= 0.0) return 0.0;
        if (kind == DamageSourceKind.Basic && targetIsAiNonHero)
            amount += attacker.ButcherMinionDamageBonus;
        if (type != DamageType.True) amount *= attacker.NonTrueDamageDealtMultiplier;
        if (type == DamageType.Physical) amount *= CombatRules.ResistanceMultiplier(armor);
        else if (type == DamageType.Magic) amount *= CombatRules.ResistanceMultiplier(spellBlock);
        if (targetIsHero && sourceIsAiNonHero)
            amount = Math.Max(0.0, amount - target.NonHeroAiDamageReduction);
        if (targetIsHero && type != DamageType.True)
            amount = Math.Max(0.0, amount - target.IndomitableDamageBlock) * target.NonTrueDamageTakenMultiplier;
        return amount;
    }
}
