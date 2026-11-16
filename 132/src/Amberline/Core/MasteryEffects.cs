using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using Amberline.Data;
using Amberline.Scripting.BuildingBlocks;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Core;

/// <summary>
/// Typed combat hooks emitted by the 1.0.0.132 mastery scripts (TalentTree ids 211-463). Values describe the script
/// output; event consumers still decide when to apply a hook. The damage hooks themselves run live from the same scripts
/// (<c>BbLoadoutHooks</c>); these fields are the report's reading of them.
/// </summary>
public sealed record MasteryCombatEffects(
    double ArchmageAbilityPowerPercent = 0.0,
    double ArchmageDurationSeconds = 0.0,
    double StrengthOfSpiritHealthRegenPerMaxMana = 0.0,
    double IndomitableDamageBlock = 0.0,
    double NonTrueDamageDealtMultiplier = 1.0,
    double NonTrueDamageTakenMultiplier = 1.0,
    double NonHeroAiDamageReduction = 0.0,
    double NeutralBuffDurationPercent = 0.0,
    bool MonsterBuffs = false,
    bool MonsterBuffs2 = false,
    double ButcherMinionDamageBonus = 0.0);

/// <summary>
/// Typed summoner-spell and avatar variables from the 1.0.0.132 mastery scripts. Patch 1.0.0.129 folded the twelve
/// per-spell feeders of 1.0.0.126 into three tier-1 talents that each write one <c>AvatarVars</c> flag at their rank, which
/// every 132 <c>Spells/Summoner*.luaobj</c> reads with <c>CO_EQUAL 1</c>: 211 Summoner's Wrath <c>offensiveMastery</c>
/// (Exhaust, Ignite, Surge, Ghost), 311 Summoner's Resolve <c>defensiveMastery</c> (Revive, Cleanse, Heal, Smite, Garrison),
/// 411 Summoner's Insight <c>utilityMastery</c> (Teleport, Flash, Clarity, Clairvoyance). 463 Mastermind writes the
/// shared <c>SummonerCooldownBonus</c> every script's <c>AdjustCooldown</c> reads.
/// </summary>
public sealed record MasterySummonerEffects(
    double SummonerCooldownBonus = 0.0,
    double OffensiveMastery = 0.0,
    double DefensiveMastery = 0.0,
    double UtilityMastery = 0.0,
    bool BurningEmbersWhenIgniteOnCooldown = false,
    double BurningEmbersAbilityPowerBonus = 0.0,
    double BurningEmbersAttackDamageBonus = 0.0);

/// <summary>Typed progression, economy and death-timer mastery effects.</summary>
public sealed record MasteryEconomyEffects(
    double ExperiencePercentBonus = 0.0,
    double GoldPer10Bonus = 0.0,
    double RespawnTimePercent = 0.0);

/// <summary>
/// Pure contribution from one already validated mastery page. The modifier
/// keys intentionally match RunePage and ItemData keys so the later stat
/// wiring can aggregate them without changing either owner.
/// </summary>
public sealed record MasteryContribution(
    IReadOnlyDictionary<string, double> Modifiers,
    MasteryCombatEffects Combat,
    MasterySummonerEffects Summoner,
    MasteryEconomyEffects Economy)
{
    public static MasteryContribution Empty { get; } = new(
        new ReadOnlyDictionary<string, double>(
            new Dictionary<string, double>(StringComparer.Ordinal)),
        new MasteryCombatEffects(),
        new MasterySummonerEffects(),
        new MasteryEconomyEffects());

    public bool IsEmpty => Modifiers.Count == 0 &&
                           Combat == new MasteryCombatEffects() &&
                           Summoner == new MasterySummonerEffects() &&
                           Economy == new MasteryEconomyEffects();
}

/// <summary>
/// The mastery page's contribution, read from the shipped <c>DATA/Talents/&lt;id&gt;.luaobj</c> scripts rather than
/// from a table of coefficients. Each allocated talent runs through <see cref="BbLoadoutReport"/> at its rank (batch
/// 140, step 1a: the same conversion the champions had in batches 86-112), and what its blocks wrote is landed on
/// this record: <c>UpdateSelfBuffStats</c>'s <c>BBIncStat</c> onto the modifiers and the economy fields, its
/// <c>BBInc*PAR*</c> and <c>BBIncMaxHealth</c> onto the pool modifiers, its <c>BBSpellBuffAdd</c> onto the combat fields
/// through the buff's own script, and the character hooks onto the damage fields through one synthetic hit.
/// Batch 770: the talent ids are the 1.0.0.132 tree's (211-463); the 1.0.0.126 ids (100-148) are gone from it.
/// </summary>
public static class MasteryEffects
{
    // The 1.0.0.132 talents a field below is read from (DATA/Talents/TalentTree.xml, names from Menu/fontconfig_en_US.txt).
    private const int SummonersWrath = 211, Butcher = 214, Havoc = 234, Archmage = 253;
    private const int SummonersResolve = 311, ToughSkin = 314, Indomitable = 331, HonorGuard = 352, Juggernaut = 362;
    private const int SummonersInsight = 411, RunicAffinity = 434, StrengthOfSpirit = 452, Mastermind = 463;

    // The three economy accessors are the only IncStat names that land on a typed field instead of a modifier key.
    private static readonly Dictionary<string, string> EconomyLanding = new(StringComparer.Ordinal)
    {
        ["IncPercentEXPBonus"] = nameof(MasteryEconomyEffects.ExperiencePercentBonus),
        ["IncFlatGoldPer10Mod"] = nameof(MasteryEconomyEffects.GoldPer10Bonus),
        ["IncPercentRespawnTimeMod"] = nameof(MasteryEconomyEffects.RespawnTimePercent),
    };

    // The PAR-typed natives that land on a modifier key, as a format over the pool's kind (MP, Energy, ...): the stat
    // calculator keys its pools by kind (Flat{MP,Energy,...}PoolMod), as BBIncPermanentFlatPARPoolMod's live handler does.
    private static readonly Dictionary<string, string> ResourceLanding = new(StringComparer.Ordinal)
    {
        ["BBIncPercentPARPoolMod"] = "Percent{0}PoolMod",
        ["BBIncFlatPARPoolMod"] = "Flat{0}PoolMod",
        ["BBIncFlatPARRegenMod"] = "Flat{0}RegenMod",
        ["BBIncPercentPARRegenMod"] = "Percent{0}RegenMod",
    };

    private static readonly ConcurrentDictionary<string, MasteryContribution> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// The neutral-buff markers a utility talent adds (132: Talents/434.luaobj Runic Affinity adds <c>MonsterBuffs</c>).
    /// Both shipped scripts are empty chunks: the factor belongs to the neutral death scripts that read the marker from the
    /// killer (see <c>Calculate</c>), so the loadout installs the marker as a live buff on its hero (<c>BbLoadoutHooks</c>).
    /// <c>MonsterBuffs2</c> stays: no 132 talent adds it, but dragonApplicator and the Odin relic scripts still read it.
    /// </summary>
    public static readonly IReadOnlySet<string> NeutralBuffMarkers =
        new HashSet<string>(StringComparer.Ordinal) { "MonsterBuffs", "MonsterBuffs2" };

    public static MasteryContribution Calculate(MasteryPage page, int championLevel = 1)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (championLevel is < 1 or > 18)
            throw new ArgumentOutOfRangeException(nameof(championLevel), "Champion level must be 1..18.");

        // Empty pages are the neutral default for synthetic/non-player heroes.
        // Keep the level validation above so malformed callers still fail, then
        // avoid walking the mastery definitions and allocating a dictionary.
        if (page.TotalPoints == 0)
            return MasteryContribution.Empty;

        // The report depends only on the allocation and the champion level, and HeroStats asks for it on every stat
        // refresh; the record is immutable, so the same page and level can share one answer.
        return Cache.GetOrAdd(Key(page, championLevel), _ => FromScripts(page, championLevel));
    }

    private static string Key(MasteryPage page, int championLevel) =>
        string.Join(",", page.Allocations.Select(pair =>
            pair.Key.ToString(CultureInfo.InvariantCulture) + ":" + pair.Value.ToString(CultureInfo.InvariantCulture))) +
        "@" + championLevel.ToString(CultureInfo.InvariantCulture);

    private static MasteryContribution FromScripts(MasteryPage page, int championLevel)
    {
        var talents = BbLoadoutReport.Talents(page.Allocations, championLevel);
        var modifiers = new Dictionary<string, double>(StringComparer.Ordinal);
        var economy = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var talent in talents.Values)
        {
            foreach (var (accessor, delta) in talent.Stats)
            {
                if (delta == 0.0) continue;
                // Strength of Spirit's IncFlatHPRegenMod is RegenPercent x GetMaxPAR(PAR_MANA) / 5 (Talents/452.luaobj
                // UpdateSelfBuffStats), read here with the probe's maximum mana of 1: a factor per point of mana, not a
                // flat row. It lands on the combat field the stat calculator multiplies by the final mana pool.
                if (talent.Id == StrengthOfSpirit && accessor == "IncFlatHPRegenMod") continue;
                if (EconomyLanding.TryGetValue(accessor, out var field)) Add(economy, field, delta);
                // Inc<Name> is the modifier key <Name>, the same names ItemData and RunePage use.
                else Add(modifiers, accessor.StartsWith("Inc", StringComparison.Ordinal) ? accessor[3..] : accessor, delta);
            }
            foreach (var ((native, type), delta) in talent.ResourceMods)
            {
                if (delta == 0.0) continue;
                if (ResourceLanding.TryGetValue(native, out var format))
                    Add(modifiers, string.Format(CultureInfo.InvariantCulture, format,
                        ((PrimaryResourceType)(int)type).StatKeyName()), delta);
            }
            // BBIncMaxHealth with IncCurrentHealth false (both 132 users). Durability (322) adds 1.5 x rank x level: a flat pool
            // row. Juggernaut (362) adds 0.03 x GetMaxHealth, which the probe reads as 1, so its number is the share of the maximum.
            // Reconstruction: the share lands on the percent pool step, (base + flat) x 1.03, which is the tooltip's "maximum
            // Health by 3%"; the script's own read of the maximum it is part of is a feedback the static report cannot run. Both
            // pools heal like any flat pool row here: the non-healing part (m_NonHealingFlatHPPoolMod) is a buff payload field
            // the static loadout contribution has no row for.
            if (talent.MaxHealth != 0.0)
                Add(modifiers, talent.Id == Juggernaut ? "PercentHPPoolMod" : "FlatHPPoolMod", talent.MaxHealth);
        }

        return new MasteryContribution(
            new ReadOnlyDictionary<string, double>(modifiers),
            Combat(talents),
            SummonerEffects(talents),
            new MasteryEconomyEffects(
                ExperiencePercentBonus: Take(economy, nameof(MasteryEconomyEffects.ExperiencePercentBonus)),
                GoldPer10Bonus: Take(economy, nameof(MasteryEconomyEffects.GoldPer10Bonus)),
                RespawnTimePercent: Take(economy, nameof(MasteryEconomyEffects.RespawnTimePercent))));
    }

    // ------------------------------------------------------------------------------------------------------------
    // Combat fields — each is one shipped talent's own hook, its marker buff's script or its AvatarVars write
    // ------------------------------------------------------------------------------------------------------------

    private static MasteryCombatEffects Combat(IReadOnlyDictionary<int, BbTalentReport> talents)
    {
        // Archmage (253, was 126's Ardor 111) asks for MasteryBlastBuff with NextBuffVars.PercentMod {0.0125 .. 0.05}; the
        // buff's own BuffOnUpdateActions scales the flat AP pool, so the report runs it with the holder's stats reading 1
        // and the captured add is the factor. The live buff is BbLoadoutHooks' (it keeps its own update loop).
        var archmage = Buff(talents, Archmage);

        // Runic Affinity (434, was 126's Utility Mastery 135) adds MonsterBuffs with BUFF_RENEW_EXISTING; the script is an
        // empty chunk, so the only thing it says is which marker it added.
        var markers = talents.TryGetValue(RunicAffinity, out var runic)
            ? runic.Buffs.Select(buff => buff.Name).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

        return new MasteryCombatEffects(
            ArchmageAbilityPowerPercent: Stat(archmage, "IncFlatMagicDamageMod"),
            ArchmageDurationSeconds: Duration(archmage),
            StrengthOfSpiritHealthRegenPerMaxMana: talents.TryGetValue(StrengthOfSpirit, out var spirit)
                ? spirit.Stats.GetValueOrDefault("IncFlatHPRegenMod") : 0.0,
            // Indomitable (331, was 126's Harden Skin 113): CharOnPreDamage takes TalentLevel x 1 off every non-true hit.
            IndomitableDamageBlock: Block(talents, Indomitable, "CharOnPreDamage", BbDamageProbe.Physical),
            // Havoc (234, 126's Havoc 123): CharOnPreDealDamage multiplies a non-true hit by {1.005, 1.01, 1.015, 1.02}.
            NonTrueDamageDealtMultiplier: Multiplier(talents, Havoc, "CharOnPreDealDamage", BbDamageProbe.Physical),
            // Honor Guard (352, was 126's Tenacity 125): CharOnPreDamage multiplies a non-true hit by 1 - 0.005 x TalentLevel.
            NonTrueDamageTakenMultiplier: Multiplier(talents, HonorGuard, "CharOnPreDamage", BbDamageProbe.Physical),
            // Tough Skin (314, was 126's Defensive Mastery 144): CharOnPreDamage blocks {1, 2} of an AI non-turret non-hero hit.
            NonHeroAiDamageReduction: Block(talents, ToughSkin, "CharOnPreDamage", BbDamageProbe.AiNonHero),
            // The 1.20 neutral-buff duration factor is the shipped death scripts' own: the 132 Spells/CrestoftheAncientGolem.luaobj
            // and BlessingoftheLizardElder.luaobj multiply the new duration by 1.2 when the killer holds MonsterBuffs (126 read
            // 1.15 / 1.3 for MonsterBuffs / MonsterBuffs2), which is Runic Affinity's "by 20%". Batch 196: BbLoadoutHooks installs
            // the marker live on its hero (NeutralBuffMarkers), so those scripts apply the factor; this is the report's reading.
            NeutralBuffDurationPercent: markers.Contains("MonsterBuffs") ? 0.20 : 0.0,
            MonsterBuffs: markers.Contains("MonsterBuffs"),
            MonsterBuffs2: markers.Contains("MonsterBuffs2"),
            // Butcher (214, was 126's Offensive Mastery 137) adds no buff: its UpdateSelfBuffActions writes AvatarVars.MasteryButcher
            // and MasteryButcherAmt {2, 4}, and the 132 Spells/APBonusDamageToTowers.luaobj every hero carries adds that amount
            // in its BuffOnHitUnit to a hit on an AI non-turret (it also reads Demolitionist 224, Bladed Armor 334, Siege
            // Commander 341, Initiator 342, Mercenary 353, Juggernaut 362 and Sage 444 the same way).
            ButcherMinionDamageBonus: talents.TryGetValue(Butcher, out var butcher) ? butcher.Avatar("MasteryButcherAmt") : 0.0);
    }

    /// <summary>
    /// The single marker buff the talent's self-buff hooks asked for (253 MasteryBlastBuff, 211 BurningEmbers). The
    /// probe world recorded the add when the script's own gate opened; when it never did (211 only adds BurningEmbers
    /// while a live <c>SummonerDot</c> slot is on cooldown, and the probe has no summoner spellbook), the buff comes
    /// from the <c>BBSpellBuffAdd</c> block the script carries, so the name and the numbers stay the shipped ones.
    /// </summary>
    private static BbLoadoutBuffAdd? Buff(IReadOnlyDictionary<int, BbTalentReport> talents, int id)
    {
        if (!talents.TryGetValue(id, out var talent)) return null;
        if (talent.Buffs.LastOrDefault() is { } captured) return captured;
        foreach (var (hook, block) in talent.Binding.Script.Walk())
        {
            if (hook is not ("UpdateSelfBuffActions" or "UpdateSelfBuffStats")) continue;
            if (!string.Equals(block.Function, "BBSpellBuffAdd", StringComparison.Ordinal)) continue;
            if (block.Text("BuffName") is not { Length: > 0 } name) continue;
            return new BbLoadoutBuffAdd(name, block.Number("Duration") ?? 0.0, new BbTable());
        }
        return null;
    }

    /// <summary>What the buff's own script adds to a stat, with the holder's stats reading 1 (a pure factor).</summary>
    private static double Stat(BbLoadoutBuffAdd? add, string accessor)
    {
        if (add is null) return 0.0;
        var stats = BbLoadoutReport.BuffStats(add);
        return stats.TryGetValue(accessor, out var value) ? value : 0.0;
    }

    private static double Duration(BbLoadoutBuffAdd? add) => add?.Duration ?? 0.0;

    /// <summary>The factor a damage hook leaves on a hit (Havoc 234, Honor Guard 352).</summary>
    private static double Multiplier(IReadOnlyDictionary<int, BbTalentReport> talents, int id, string hook,
        BbDamageProbe probe) =>
        talents.TryGetValue(id, out var talent) ? talent.Damage(hook, probe).Multiplier : 1.0;

    /// <summary>The flat block a damage hook takes off a hit (Tough Skin 314, Indomitable 331).</summary>
    private static double Block(IReadOnlyDictionary<int, BbTalentReport> talents, int id, string hook,
        BbDamageProbe probe) =>
        talents.TryGetValue(id, out var talent) ? talent.Damage(hook, probe).Block : 0.0;

    // ------------------------------------------------------------------------------------------------------------
    // Summoner fields — the AvatarVars the feeder scripts write
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The summoner fields are the <c>AvatarVars</c> the 132 feeders write in <c>SetVarsByLevel</c>, read back per talent:
    /// 211 <c>offensiveMastery</c>, 311 <c>defensiveMastery</c> and 411 <c>utilityMastery</c> (each <c>TalentLevel</c>, so 1),
    /// 463 <c>SummonerCooldownBonus</c> (0.15 x <c>TalentLevel</c>). Each probe owns its own loadout, so a key is read from
    /// the one feeder that writes it and an unallocated feeder's unwritten (nil) key reads 0 here. The improved values
    /// themselves are the summoner scripts' own branches on those flags (Heal x1.1, Ghost 0.35, Flash 250, ...), which
    /// <c>BbSummonerModule</c> runs against the same table.
    /// </summary>
    private static MasterySummonerEffects SummonerEffects(IReadOnlyDictionary<int, BbTalentReport> talents)
    {
        double Avatar(int id, string key) => talents.TryGetValue(id, out var talent) ? talent.Avatar(key) : 0.0;

        // Summoner's Wrath (211, was 126's Burning Embers 146) is the one feeder that also adds a buff: its
        // UpdateSelfBuffActions adds BurningEmbers while Ignite is on cooldown, and the 132 buff script holds the numbers
        // (BuffOnUpdateStats BBIncStat IncFlatMagicDamageMod 5 and IncFlatPhysicalDamageMod 5; 126's was 10 AP).
        var embers = Buff(talents, SummonersWrath);

        return new MasterySummonerEffects(
            SummonerCooldownBonus: Avatar(Mastermind, "SummonerCooldownBonus"),
            OffensiveMastery: Avatar(SummonersWrath, "offensiveMastery"),
            DefensiveMastery: Avatar(SummonersResolve, "defensiveMastery"),
            UtilityMastery: Avatar(SummonersInsight, "utilityMastery"),
            BurningEmbersWhenIgniteOnCooldown: embers is not null,
            BurningEmbersAbilityPowerBonus: Stat(embers, "IncFlatMagicDamageMod"),
            BurningEmbersAttackDamageBonus: Stat(embers, "IncFlatPhysicalDamageMod"));
    }

    private static void Add(IDictionary<string, double> modifiers, string name, double value)
    {
        if (value == 0.0) return;
        modifiers[name] = modifiers.TryGetValue(name, out var previous)
            ? previous + value
            : value;
    }

    private static double Take(IDictionary<string, double> values, string name) =>
        values.TryGetValue(name, out var value) ? value : 0.0;
}
