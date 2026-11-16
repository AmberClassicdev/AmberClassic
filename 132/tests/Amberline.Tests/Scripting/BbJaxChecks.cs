using Amberline.Core;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Simulation;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Phase 3, wave 3 (batch 273, 26 October 2026): Jax plays from his shipped scripts in a production <c>MatchRuntime</c>. Expected numbers are the
/// literals of the shipped inibins (decoded by the batch's standalone reader, the audit entry) and the scripts; the scenario <c>S-Jax</c> shows the same
/// facts on a headless match. Shipped names: the passive is Equipment Mastery, Relentless Assault is his passive (basic attacks) and his ultimate (the
/// active magic-resist buff, rank three); the brief's "Grandmaster's Might" has no shipped name. Jax is not a retail bot.
/// 1.0.0.132 (batch 789, wiki 132.22-132.28: Jax remade): the kit is JaxLeapStrike, JaxEmpowerTwo, JaxCounterStrike, JaxRelentlessAssault
/// (Characters/Jax/Jax.inibin) with the passive JaxPassive. Each check asserts the 132 scripts' numbers and names the 126 value it replaces.
/// </summary>
public static class BbJaxChecks
{
    // Rank one of each spell (132 scripts): Leap Strike 70 (JaxLeapStrikeAttack BonusDamage), Empower 40 (JaxEmpowerTwo DamageByLevel),
    // Counter Strike's counter 40 (JaxCounterStrikeAttack BaseDmg), Relentless Assault's third hit 100 (JaxRelentlessAttack DamageByLevel).
    private static Match Arena(int rank, double foeDistance)
    {
        var match = new Match("Jax", foeChampion: "Ezreal");
        match.Learn(rank);
        match.Foe.SpellBlock = 0.0;
        match.Foe.Armor = 0.0;
        match.Foe.Position = new Position2(match.Hero.Position.X + foeDistance, match.Hero.Position.Z);
        match.Refill();
        return match;
    }

    private static double Gap(Entity a, Entity b) => Math.Sqrt(Math.Pow(a.Position.X - b.Position.X, 2) + Math.Pow(a.Position.Z - b.Position.Z, 2));

    // ------------------------------------------------------------------------------------------------------------
    // Closure, registration, ranks
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-jax-module-binds-the-shipped-closure")]
    public static void ModuleReadsTheClosureAndTheRankRules()
    {
        var module = new BbChampionModule("Jax");
        // 132 (bb-coverage, batch 789): the closure of CharScriptJax. 126 listed LeapStrike, EmpowerTwo, CounterStrike, RelentlessAssault,
        // EquipmentMastery, ArmsmasterLeapStrikeAttack, ArmsmasterEmpowerAttack, ArmsmasterRelentlessAttack, CounterStrikeDodgeUp, CounterStrikeCanCast,
        // LeapStrikeSpeed, RelentlessAssaultMarker and ArmsmasterRelentlessMR; 132 drops those and runs the JaxCounterStrikeAttack family instead.
        foreach (var name in new[]
                 {
                     "CharScriptJax", "JaxLeapStrike", "JaxLeapStrikeAttack", "JaxEmpowerTwo", "JaxCounterStrike", "JaxCounterStrikeAttack",
                     "JaxRelentlessAssault", "JaxRelentlessAttack", "JaxRelentlessAssaultAS", "JaxRelentlessAssaultDebuff",
                     "JaxRelentlessAssaultSpeed", "JaxPassive", "LeapStrike",
                 })
            TestAssert.True(module.ScriptNames.Contains(name, StringComparer.OrdinalIgnoreCase), $"{name} is in the closure");
        var closure = new BbScriptResolver(GameData.Default.Paths).Resolve("Jax");
        TestAssert.Equal(0, closure.Diagnostics.Count, "the closure resolves cleanly");
        TestAssert.Equal(34, closure.ScriptNames.Count(), "the closure is 34 scripts (132 bb-coverage, batch 789; 126: 29)");

        var hero = new Entity { ChampionName = "Jax" };
        // 132 Characters/Jax/Jax.inibin names JaxLeapStrike, JaxEmpowerTwo, JaxCounterStrike, JaxRelentlessAssault (126: LeapStrike, EmpowerTwo, ...).
        TestAssert.Equal(["JaxLeapStrike", "JaxEmpowerTwo", "JaxCounterStrike", "JaxRelentlessAssault"],
            Enumerable.Range(0, 4).Select(slot => module.SpellName(hero, slot)).ToArray(), "spell names come from Jax.inibin");
        TestAssert.Equal([5, 5, 5, 3], Enumerable.Range(0, 4).Select(slot => module.MaxRank(hero, slot)).ToArray(),
            "three spells of five ranks, the ultimate of three");

        var info = ChampionCatalog.ByName("Jax")!;
        TestAssert.True(info is { Playable: true, Role: "top", DefaultIp: 1350, Id: 24, RetailBot: false },
            "playable as a top laner for 1350 IP, not a retail bot (no JaxBot inibin, no game_bot_Jax)");
        TestAssert.Equal(1, ChampionCatalog.PlayableNames.Count(name => name == "Jax"), "listed once");
    }

    [Check("scripting.bb-jax-rank-gates-follow-the-stock-table")]
    public static void RankGatesAreTheStockTable()
    {
        var module = new BbChampionModule("Jax");
        var hero = new Entity { ChampionName = "Jax" };
        TestAssert.Equal([1, 3, 5, 7, 9], Enumerable.Range(1, 5).Select(rank => module.RankGate(hero, 0, rank)).ToArray(),
            "Q is learnt at 1, 3, 5, 7, 9");
        TestAssert.Equal([6, 11, 16], Enumerable.Range(1, 3).Select(rank => module.RankGate(hero, 3, rank)).ToArray(),
            "R is learnt at 6, 11, 16 (the stock table)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Leap Strike (Q): the leap, the magic hit, the ally leap
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-jax-leap-strike-hits-for-effect-plus-total-attack-damage-and-ability-power")]
    public static void LeapStrikeHitsForItsEffectPlusTotalAttackDamageAndAbilityPower()
    {
        var match = Arena(1, 600.0);
        match.GiveAbilityPower();
        match.Refill();
        var attackDamage = match.Hero.AttackDamage;
        var baseAttackDamage = GameData.Default.StatsAtLevel(GameData.Default.Champion("Jax"), match.Hero.Level).AttackDamage;
        var abilityPower = match.Hero.AbilityPower;
        TestAssert.True(match.Cast(0, match.Foe), "Leap Strike is accepted against an enemy 600 units away");
        match.Advance(0.6);
        var first = HitsOn(match, "Foe").First();
        TestAssert.Equal("Spell", first.Kind, "the leap's hit is a spell hit (DAMAGESOURCE_SPELL)");
        // 132 JaxLeapStrikeAttack.luaobj TargetExecute: BonusDamage SrcValueByLevel 70 at rank one (126: 20), plus (TotalAD - BaseAD) x 1.0 (the
        // bonus attack damage, not the total: 126 added the total), plus GetFlatMagicDamageMod x 0.6 (126: 0.7 of ability power). The leap's
        // own hit is PHYSICAL_DAMAGE with DAMAGESOURCE_SPELL; the magic part of the 132 kit is JaxEmpowerTwo's bonus, not this hit.
        TestAssert.Near(70.0 + (attackDamage - baseAttackDamage) + 0.6 * abilityPower, first.Raw, 0.01,
            "132 damage = 70 + bonus attack damage + 0.6 ability power");
        TestAssert.Near(first.Raw, first.Dealt, 0.01, "a zero-resistance victim takes the raw amount");
        TestAssert.True(Gap(match.Hero, match.Foe) < 150.0, "Jax lands beside the enemy");
        // JaxLeapStrikeAttack's TargetExecute ends with BBIf Target CO_IS_TYPE_HERO, BBIf CO_DIFFERENT_TEAM: BBIssueOrder(Owner, AI_ATTACKTO,
        // Target). The swing after a leap onto an enemy champion is the script's own order (the batch's first version blamed the hero AI).
        TestAssert.Equal((uint?)match.Foe.NetId, match.Hero.AttackTarget, "the leap orders Jax to attack the champion he landed on");
        match.Advance(1.0);
        TestAssert.True(HitsOn(match, "Foe").Any(hit => hit.Kind == "Basic"), "and he swings at it");
    }

    [Check("scripting.bb-jax-leap-strike-to-an-ally-moves-without-damage")]
    public static void LeapStrikeToAnAllyMovesJaxAndHurtsNobody()
    {
        var match = Arena(1, 600.0);
        var ally = match.Ally;
        ally.Position = new Position2(match.Hero.Position.X + 500.0, match.Hero.Position.Z);
        var allyHealth = ally.Health;
        TestAssert.True(match.Cast(0, ally), "Leap Strike is accepted on an allied champion");
        match.Advance(1.5);
        TestAssert.Equal(allyHealth, ally.Health, "the ally takes no damage");
        TestAssert.True(HitsOn(match, "Ally").Count == 0, "no hit is recorded on the ally");
        TestAssert.True(Gap(match.Hero, ally) < 150.0, "Jax lands beside the ally");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Empower (W): the next attack only
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-jax-empower-adds-magic-damage-to-the-next-attack-only")]
    public static void EmpowerAddsMagicDamageToTheNextAttackOnly()
    {
        var match = Arena(1, 110.0);
        var abilityPower = match.Hero.AbilityPower;
        TestAssert.True(match.Cast(1, match.Hero), "Empower is accepted on Jax");
        match.Advance(0.4);
        // 132 JaxEmpowerTwo.luaobj SelfExecute: BBSpellBuffAdd JaxEmpowerTwo for 6 s (126: buff EmpowerTwo).
        TestAssert.True(Buff(match.Hero, "JaxEmpowerTwo") is not null, "the Empower buff is on Jax");
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe), "Jax's next attack is queued");
        match.Advance(1.2);
        var spells = HitsOn(match, "Foe").Where(hit => hit.Kind == "Spell").ToList();
        TestAssert.Equal(1, spells.Count, "the empowered attack adds one magic hit");
        // 132 JaxEmpowerTwo.luaobj BuffOnHitUnit: DamageByLevel 40 at rank one (126: 60) magic, SpellDamageRatio 0.6 (126: 0.4) of ability power.
        TestAssert.Near(40.0 + 0.6 * abilityPower, spells[0].Raw, 0.01, "the bonus is 40 + 0.6 ability power, magic");
        TestAssert.True(Buff(match.Hero, "JaxEmpowerTwo") is null, "that attack spends the buff");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Counter Strike (E): the dodge window, the stun, the damage
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-jax-counter-strike-waits-for-a-dodge-then-stuns-for-a-second")]
    public static void CounterStrikeNeedsADodgeThenStunsAndHurts()
    {
        var match = Arena(1, 110.0);
        match.Advance(0.2);
        // 132 JaxCounterStrike.luaobj has no gate: it is castable before any dodge (126: sealed until CounterStrikeCanCast opened on a dodge).
        TestAssert.True(match.CanCast(2, match.Hero).Allowed, "132: Counter Strike is castable before any dodge");
        TestAssert.True(match.Cast(2, match.Hero), "Counter Strike is accepted");
        match.Advance(0.2);
        // JaxCounterStrike.luaobj: TargetExecute adds the dodge buff for 1.5 s (DurationByLevel). The add has no BuffName, so the buff carries the
        // script's name, JaxCounterStrike (the script's BuffName JaxEvasion is its display name). BuffOnUpdateStats adds IncFlatDodgeMod 100.
        TestAssert.True(Buff(match.Hero, "JaxCounterStrike") is not null, $"the 132 dodge buff is on Jax (buffs: {Names(match.Hero)})");
        TestAssert.Near(1.0, match.Hero.Dodge, 0.0001, "every basic attack is dodged while it runs (IncFlatDodgeMod 100)");

        // A forced dodge: the foe's attack is queued with a roll that dodges.
        TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Foe, match.Hero, new AttackRolls(1.0, 0.0, 1.0)), "the foe's attack is queued");
        match.Advance(0.6);
        TestAssert.True(HitsOn(match, "Hero").Count == 0, "the dodged attack deals nothing");
        TestAssert.True(Buff(match.Foe, "Stun") is null, "no stun while the dodge window runs");

        // The window closes at 1.5 s and JaxCounterStrikeAttack (cast from extra slot 3 by the buff's deactivation) stuns the attacker for one second.
        for (var step = 0; step < 90 && Buff(match.Foe, "Stun") is null; step++) match.Advance(1.0 / 30.0);
        TestAssert.True(Buff(match.Foe, "Stun") is not null, "the enemy is stunned when the window closes");
        var hit = HitsOn(match, "Foe").First(item => item.Kind == "SpellAoe");
        // JaxCounterStrikeAttack.luaobj: BaseDmg SrcValueByLevel 40 at rank one (126: 80 magic + 0.6 ability power), plus 0.8 of the bonus attack
        // damage (BonusAD x 0.8, the 132 script's own), plus NumCounter: the one dodge added 10 at rank one (JaxCounterStrike BuffOnDodge).
        var bonusAttackDamage = match.Hero.AttackDamage - GameData.Default.StatsAtLevel(GameData.Default.Champion("Jax"), match.Hero.Level).AttackDamage;
        TestAssert.Near(40.0 + 0.8 * bonusAttackDamage + 10.0, hit.Raw, 0.01, "the counter is 40 + 0.8 bonus attack damage + 10 for the dodge, physical");
        match.Advance(1.2);
        TestAssert.True(Buff(match.Foe, "Stun") is null, "the stun is over after its one second");
    }

    [Check("scripting.bb-jax-counter-strike-window-closes-after-seven-seconds")]
    public static void CounterStrikeWindowClosesAfterSevenSeconds()
    {
        var match = Arena(1, 110.0);
        match.Advance(0.2);
        TestAssert.True(match.Cast(2, match.Hero), "Counter Strike is accepted");
        // Poll for the window's appearance (the cast), then read its length from the moment it opens. BuffState.ExpiresAt is the time left,
        // not a clock instant (batch 273 read it as a clock and restated it).
        for (var step = 0; step < 60 && Buff(match.Hero, "JaxCounterStrike") is null; step++) match.Advance(1.0 / 30.0);
        var window = Buff(match.Hero, "JaxCounterStrike");
        TestAssert.True(window is not null, $"the cast opens the dodge window (buffs: {Names(match.Hero)})");
        // 132 JaxCounterStrike.luaobj: DurationByLevel 1.5 at every rank, so 1.5 seconds are left when the window opens (126: CounterStrikeCanCast,
        // a seven-second window that a dodge opened, CharScriptJax CharOnDodge).
        TestAssert.Near(1.5, window!.ExpiresAt, 1.0 / 30.0 + 1e-6, $"1.5 seconds are left when the window opens (read {window.ExpiresAt:0.###})");
        var opened = match.Now;
        for (var step = 0; step < 300 && Buff(match.Hero, "JaxCounterStrike") is not null; step++) match.Advance(1.0 / 30.0);
        TestAssert.Near(1.5, match.Now - opened, 0.1, $"the window closes 1.5 seconds after it opens (open for {match.Now - opened:0.###} s)");
    }

    [Check("scripting.bb-jax-counter-strike-dodge-grows-with-its-rank")]
    public static void CounterStrikeDodgeGrowsWithItsRank()
    {
        // 132 (126: CounterStrikeDodgeUp added 10, 12, 14, 16, 18 percent dodge by rank). The 132 dodge is flat: JaxCounterStrike's BuffOnUpdateStats
        // adds IncFlatDodgeMod 100 at every rank. What grows with the rank is the counter's base (JaxCounterStrikeAttack BaseDmg 40, 70, 100,
        // 130, 160) and the step one dodge adds to NumCounter (JaxCounterStrike BuffOnDodge: MinionBonus and ChampionBonus 10, 15, 20, 25, 30).
        var baseDamage = new[] { 40.0, 70.0, 100.0, 130.0, 160.0 };
        var stepPerDodge = new[] { 10.0, 15.0, 20.0, 25.0, 30.0 };
        for (var rank = 1; rank <= 5; rank++)
        {
            var match = Arena(rank, 110.0);
            match.Advance(0.2);
            TestAssert.True(match.Cast(2, match.Hero), $"rank {rank}: Counter Strike is accepted");
            match.Advance(0.2);
            TestAssert.Near(1.0, match.Hero.Dodge, 0.0001, $"rank {rank} of Counter Strike dodges every basic attack (IncFlatDodgeMod 100, flat)");
            TestAssert.True(match.Runtime.Combat.QueueBasicAttack(match.Foe, match.Hero, new AttackRolls(1.0, 0.0, 1.0)),
                $"rank {rank}: the foe's attack is queued");
            for (var step = 0; step < 150 && Buff(match.Foe, "Stun") is null; step++) match.Advance(1.0 / 30.0);
            var hit = HitsOn(match, "Foe").First(item => item.Kind == "SpellAoe");
            var bonusAttackDamage = match.Hero.AttackDamage - GameData.Default.StatsAtLevel(GameData.Default.Champion("Jax"), match.Hero.Level).AttackDamage;
            TestAssert.Near(baseDamage[rank - 1] + 0.8 * bonusAttackDamage + stepPerDodge[rank - 1], hit.Raw, 0.01,
                $"rank {rank}: the counter is {baseDamage[rank - 1]} plus {stepPerDodge[rank - 1]} for one dodge");
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Relentless Assault (R, and its passive JaxPassive): the attack-speed stacks, the third hit, the 132 damage buff (126: the magic-resist buff)
    // ------------------------------------------------------------------------------------------------------------

    [Check("scripting.bb-jax-relentless-assault-hits-the-third-time-for-bonus-magic-damage")]
    public static void RelentlessAssaultHitsTheThirdTimeForItsBonusMagicDamage()
    {
        var match = Arena(1, 110.0);
        match.Hero.Level = 6;
        match.Learn(1);
        match.Refill();
        TestAssert.True(match.Cast(3, match.Hero), "Relentless Assault is accepted at level six");
        match.Advance(0.4);
        // 132 JaxRelentlessAssault.luaobj SelfExecute: BBSpellBuffAdd JaxRelentlessAssaultSpeed for 6 s (126: buff RelentlessAssault).
        TestAssert.True(Buff(match.Hero, "JaxRelentlessAssaultSpeed") is not null, "the ultimate's buff is on Jax");
        // The buff adds 25 ability power at rank one (JaxRelentlessAssaultSpeed: 0.2 x ability power + BonusADAP 25), and the third hit's 0.7
        // ratio reads the ability power the hit sees, so the expectation is taken after the buff is up.
        var abilityPower = match.Hero.AbilityPower;
        for (var swing = 1; swing <= 3; swing++)
        {
            // An attack is refused while the attack timer runs (QueueBasicAttack): retry in steps until the swing is accepted.
            var queued = false;
            for (var step = 0; step < 40 && !queued; step++)
            {
                queued = match.Runtime.Combat.QueueBasicAttack(match.Hero, match.Foe);
                if (!queued) match.Advance(0.1);
            }
            TestAssert.True(queued, $"swing {swing} is queued within four seconds");
            match.Advance(1.0);
        }
        var spells = HitsOn(match, "Foe").Where(hit => hit.Kind == "Spell").ToList();
        TestAssert.Equal(1, spells.Count, "the third consecutive hit adds one magic hit: " + string.Join(" | ", HitsOn(match, "Foe").Select(hit => $"{hit.At:0.00} {hit.Kind} {hit.Raw:0.###}")));
        // 132 JaxRelentlessAttack.luaobj: the third hit's magic part is DamageByLevel 100 at rank one (126: 140), SpellDamageRatio 0.7 of ability
        // power (unchanged); its physical part is the base attack damage.
        TestAssert.Near(100.0 + 0.7 * abilityPower, spells[0].Raw, 0.01, "the bonus is 100 + 0.7 ability power, magic");
        // JaxPassive's BuffOnHitUnit adds one JaxRelentlessAssaultAS stack per landed hit (126: ArmsmasterRelentlessCounter, the same role).
        var stacks = Buff(match.Hero, "JaxRelentlessAssaultAS")?.Stacks ?? 0;
        TestAssert.True(stacks >= 3, $"the attack-speed stacks grow with each hit (saw {stacks})");
    }

    [Check("scripting.bb-jax-relentless-assault-adds-magic-resist-while-active")]
    public static void RelentlessAssaultAddsMagicResistWhileActive()
    {
        var match = Arena(1, 110.0);
        match.Hero.Level = 6;
        match.Learn(1);
        match.Refill();
        match.Advance(0.2);
        var resistBefore = match.Hero.SpellBlock;
        var attackDamageBefore = match.Hero.AttackDamage;
        var abilityPowerBefore = match.Hero.AbilityPower;
        TestAssert.True(match.Cast(3, match.Hero), "Relentless Assault is accepted at level six");
        match.Advance(0.4);
        // 132: no magic resist. 126 (ArmsmasterRelentlessMR) gave 20 plus the dodge percent; 132's Jax closure has no script that adds it.
        var gainedResist = match.Hero.SpellBlock - resistBefore;
        TestAssert.Near(0.0, gainedResist, 0.0001, $"132: Relentless Assault adds no magic resist (gained {gainedResist})");
        // 132 JaxRelentlessAssaultSpeed.luaobj (BuffName RelentlessBarrier, 6 s): InstanceVars BonusAD = 0.2 x bonus attack damage + BonusADAP, and
        // BonusAP = 0.2 x ability power + BonusADAP, with BonusADAP SrcValueByLevel 25, 45, 65 by the ultimate's rank. BuffOnUpdateStats adds them as
        // IncFlatPhysicalDamageMod and IncFlatMagicDamageMod.
        TestAssert.True(Buff(match.Hero, "JaxRelentlessAssaultSpeed") is not null, "the 132 ultimate buff, JaxRelentlessAssaultSpeed, is on Jax");
        TestAssert.Near(25.0, match.Hero.AttackDamage - attackDamageBefore, 0.01, "rank one adds 25 attack damage (BonusADAP 25)");
        TestAssert.Near(25.0 + 0.2 * abilityPowerBefore, match.Hero.AbilityPower - abilityPowerBefore, 0.01,
            "and 25 plus 0.2 of ability power as magic damage");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Equipment Mastery (passive): health from items
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// 1.0.0.132 (batch 772, wiki 132.22-132.28: Jax remade): Equipment Mastery is gone. Spells/EquipmentMastery.luaobj still ships, but
    /// CharScriptJax's CharOnActivate adds JaxPassive (Relentless Assault, 132.23) instead, so ability power gives Jax no health.
    /// </summary>
    [Check("scripting.bb-jax-equipment-mastery-gives-health-from-ability-power")]
    public static void AbilityPowerGivesJaxNoHealth()
    {
        var match = Arena(1, 300.0);
        var before = match.Hero.MaxHealth;
        match.GiveAbilityPower();
        match.Advance(1.0);
        TestAssert.Near(before, match.Hero.MaxHealth, 0.5,
            $"132: ability power adds no health (ability power {match.Hero.AbilityPower})");
        TestAssert.True(Buff(match.Hero, "EquipmentMastery") is null, "no Equipment Mastery buff on Jax");
        TestAssert.True(Buff(match.Hero, "JaxPassive") is not null, "the 132 innate, JaxPassive, is on Jax");
    }
}
