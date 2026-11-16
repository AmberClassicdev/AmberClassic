using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Simulation.Stats;

public readonly record struct HeroAttackTiming(
    double Period,
    double Windup,
    double MissileSpeed)
{
    // Kept internal so the public three-field timing value remains source
    // compatible. Applying timing persists the unbounded values needed by
    // clone/pet consumers without making For/ForRefresh mutate an Entity.
    internal double EffectiveAttackSpeed { get; init; }
    internal double RawPeriod { get; init; }
    internal double CastFraction { get; init; }
}

/// <summary>
/// Shared patch-126 basic-attack timing used at spawn and stat refresh.
/// </summary>
public static class HeroAttackTimings
{
    // Map1 Constants.var; the stock helpers.
    private const double BasePeriod = 1.6;
    private const double BaseCastFraction = 0.3;
    private const double FallbackMissileSpeed = 1200.0;

    public static HeroAttackTiming For(Entity hero, GameData gameData)
    {
        ArgumentNullException.ThrowIfNull(hero);
        ArgumentNullException.ThrowIfNull(gameData);
        if (hero.UnitType != UnitType.Hero)
            throw new ArgumentException("Attack timing requires a hero entity.", nameof(hero));

        var dataName = CharacterName(hero);
        var attackSpeed = hero.AttackSpeedMod;
        if (!double.IsFinite(attackSpeed) || attackSpeed <= 0.0)
            throw new InvalidOperationException("Hero attack speed must be positive and finite before timing.");

        var champion = gameData.Champion(dataName);
        var rawPeriod = BasePeriod * (1.0 + Number(champion.Raw, "Data*AttackDelayOffsetPercent"));
        var period = rawPeriod / attackSpeed;
        if (!double.IsFinite(period) || period <= 0.0)
            throw new InvalidDataException($"Invalid attack period for {hero.ChampionName}.");
        period = Math.Clamp(period, 0.4, 5.0);
        var castFraction = Math.Max(0.0,
            BaseCastFraction + Number(champion.Raw, "Data*AttackDelayCastOffsetPercent"));
        var windup = period * castFraction;
        // Transformed Nidalee keeps the ordinary basic missile; her alternate
        // character data supplies the attack period and cast fraction.
        // The client flies a missile only for CastType 1, 2 and 3 (the cast builder: case 0 runs the effect at once), so the
        // MissileSpeed of a CastType 0 attack spell is unused data. 27 melee champions ship one (Xin Zhao, Jarvan IV, Wukong and Pantheon 20, Udyr
        // and Evelynn 467 ...): read as a flight, Xin Zhao's swing at 120 units landed six seconds after it started (19 October 2026, S-xinzhao).
        // The override path (BbNativeHandlers.AttackMissileSpeed) reads the same rule.
        var attackSpell = gameData.Spell(hero.ChampionName + "BasicAttack");
        var missile = BbChampionModule.CastType(attackSpell) is 1 or 2 or 3 ? attackSpell.MissileSpeed : 0.0;

        if (!double.IsFinite(period) || period <= 0.0 || !double.IsFinite(windup) || windup < 0.0 ||
            !double.IsFinite(missile) || missile < 0.0)
            throw new InvalidDataException($"Invalid attack timing for {hero.ChampionName}.");
        return new HeroAttackTiming(period, windup, missile)
        {
            EffectiveAttackSpeed = attackSpeed,
            RawPeriod = rawPeriod,
            CastFraction = castFraction
        };
    }

    public static HeroAttackTiming Apply(Entity hero, GameData gameData)
    {
        var timing = For(hero, gameData);
        hero.AttackPeriod = timing.Period;
        hero.AttackWindup = timing.Windup;
        hero.MissileSpeed = timing.MissileSpeed;
        hero.AttackTimingBaseline = new AttackTimingBaseline(
            timing.EffectiveAttackSpeed, timing.RawPeriod, timing.CastFraction);
        ApplyAnimationProbabilities(hero, gameData);
        return timing;
    }

    /// <summary>
    /// Refresh uses the same data and limits as spawn, preserving a runtime
    /// missile-speed override such as one supplied by a captured snapshot.
    /// </summary>
    public static HeroAttackTiming ForRefresh(Entity hero, GameData gameData)
    {
        return For(hero, gameData) with { MissileSpeed = ExistingMissile(hero) };
    }

    public static HeroAttackTiming ApplyRefresh(Entity hero, GameData gameData)
    {
        var timing = ForRefresh(hero, gameData);
        hero.AttackPeriod = timing.Period;
        hero.AttackWindup = timing.Windup;
        hero.MissileSpeed = timing.MissileSpeed;
        hero.AttackTimingBaseline = new AttackTimingBaseline(
            timing.EffectiveAttackSpeed, timing.RawPeriod, timing.CastFraction);
        ApplyAnimationProbabilities(hero, gameData);
        return timing;
    }

    /// <summary>The character attack slots in the order of the 0x0D slot byte (0x40 + index; <see cref="Combat.CombatRules.AttackSlot"/>).</summary>
    private static readonly string[] AttackSlotNames =
        ["BaseAttack", "ExtraAttack1", "ExtraAttack2", "ExtraAttack3", "CritAttack", "MissAttack", "DodgeAttack"];

    /// <summary>
    /// The period and wind-up of one swing of <paramref name="hero"/> in attack <paramref name="slot"/>. Batch 803: the 126 character loader
    /// reads <c>%s_AttackDelayCastOffsetPercent</c> and <c>%s_AttackDelayOffsetPercent</c> for each of
    /// ExtraAttack1-3, CritAttack, MissAttack and DodgeAttack, each defaulting to the base attack's value, and the 0x0D handler
    /// times the swing with the pair of the slot its slot byte names (the client index the tables by
    /// byte - 0x40). Nunu's CritAttack ships 0.12 against his base 0.02, so the client plays his crit about 10 percent longer than the server
    /// timed it: the hit landed before the crit's cast frame and the next 0x0D cut the crit's swing. A slot whose pair reads the base's as a
    /// float32 (the client's getter) keeps <see cref="Entity.AttackPeriod"/> and <see cref="Entity.AttackWindup"/>, so every buff path that
    /// scales them still holds; only a slot that ships its own offsets is computed here, from the same attack speed and limits as
    /// <see cref="For"/>. The 132 client ships the same two format strings. Shipped 132 data: only
    /// CritAttack_AttackDelayOffsetPercent differs from the base, for Akali, Brand, Gangplank, Malzahar, Nunu, Renekton, Rumble, Vladimir,
    /// Warwick and Xerath (attacks.every-champion-times-each-attack-slot-as-the-client pins the list), so the rule is the heroes' (batch 803).
    /// </summary>
    public static (double Period, double Windup) ForSwing(Entity hero, GameData gameData, int slot)
    {
        ArgumentNullException.ThrowIfNull(hero);
        ArgumentNullException.ThrowIfNull(gameData);
        var current = (hero.AttackPeriod, hero.AttackWindup);
        if (hero.UnitType != UnitType.Hero || slot <= 0 || slot >= AttackSlotNames.Length) return current;
        var raw = gameData.Champion(CharacterName(hero)).Raw;
        var baseOffset = (float)Number(raw, "Data*AttackDelayOffsetPercent");
        var baseCastOffset = (float)Number(raw, "Data*AttackDelayCastOffsetPercent");
        var offset = SlotNumber(raw, AttackSlotNames[slot] + "_AttackDelayOffsetPercent", baseOffset);
        var castOffset = SlotNumber(raw, AttackSlotNames[slot] + "_AttackDelayCastOffsetPercent", baseCastOffset);
        if (offset == baseOffset && castOffset == baseCastOffset) return current;
        var attackSpeed = hero.AttackSpeedMod;
        if (!double.IsFinite(attackSpeed) || attackSpeed <= 0.0) return current;
        var period = Math.Clamp(BasePeriod * (1.0 + offset) / attackSpeed, 0.4, 5.0);
        return (period, period * Math.Max(0.0, BaseCastFraction + castOffset));
    }

    // The client's float getter (a boolean or text value is a present number); absent, the base attack's value.
    private static float SlotNumber(InibinData data, string key, float fallback)
        => data.TryGetValue(InibinReader.KeyHash("Data*" + key), out var value) &&
           value.Kind is not (InibinValueKind.ByteVector or InibinValueKind.FloatVector)
            ? (float)value.AsClientFloat() : fallback;

    private static void ApplyAnimationProbabilities(Entity hero, GameData gameData)
        => hero.BasicAttackAnimationProbabilities = gameData.Champion(CharacterName(hero)).BasicAttackAnimationProbabilities;

    private static string CharacterName(Entity hero)
        => hero.StatCharacterName;

    private static double Number(InibinData data, string key)
        => data.TryGetValue(InibinReader.KeyHash(key), out var value) && value.IsNumeric
            ? value.AsDouble()
            : 0.0;

    private static double ExistingMissile(Entity hero) =>
        double.IsFinite(hero.MissileSpeed) && hero.MissileSpeed >= 0.0
            ? hero.MissileSpeed : FallbackMissileSpeed;
}
