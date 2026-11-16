namespace Amberline.Core;

/// <summary>
/// Canonical summoner names shared by roster validation, entity state, loading packets, and the runtime spell dispatcher (patch 126's
/// thirteen, plus what 1.0.0.132 adds: Surge, and Promote under its 132 script).
/// </summary>
public static class SummonerLoadout
{
    public const string DefaultFirst = "SummonerFlash";
    public const string DefaultSecond = "SummonerDot";

    public static readonly IReadOnlyList<string> CanonicalNames =
    [
        "SummonerFlash", "SummonerDot", "SummonerHeal", "SummonerHaste",
        "SummonerExhaust", "SummonerSmite", "SummonerBoost",
        "SummonerClairvoyance", "SummonerMana", "SummonerTeleport",
        "SummonerRevive", "SummonerFortify", "SummonerRally",
        // 1.0.0.132 (batch 772, wiki 129.40 / 132.53): Surge is Spells/SummonerBattleCry.luaobj (Summoner_BattleCry.dds; TargetExecute adds the
        // SummonerBattleCry buff to the caster: 35 % attack speed and 10 ... 78 ability power by level for 12 s, AdjustCooldown 180).
        // Spells/PrimalSurge.luaobj is Nidalee's Primal Surge, not this spell.
        "SummonerBattleCry",
    ];

    /// <summary>
    /// 1.0.0.132 (batch 772, wiki 129.45): Fortify and Rally left the summoner pool. Their files still ship unchanged; the AIR offer no longer
    /// seats them (batch 798, AirGameDtos.SpellNamesFor), and the game server refuses their cast for any other roster
    /// (<c>SpellSystem.HandleSummonerCast</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> RetiredNames = ["SummonerFortify", "SummonerRally"];

    /// <summary>
    /// Batch 455: the Dominion summoners (Crystal Scar, mode ODIN; their inibins and luaobj ship under DATA/Spells): Garrison, and
    /// Batch 500: Promote. The Platform lets them onto map 8 only (AirGameDtos.SpellNamesFor).
    /// 1.0.0.132 (batch 772): Promote is Spells/SummonerPromoteSR.luaobj on every map (wiki 129.48: Promote added to Summoner's Rift). 132 removed
    /// SummonerOdinPromote's files; the 132 script keeps the Crystal Scar branch (OdinPlayerBuff: the nearest OdinSuperMinion) beside the
    /// Summoner's Rift one (the nearest allied minion holding PromoteMeBuff, which the siege minions' CharScript*_Minion_MechCannon add). The old
    /// name, which the AIR offer sent for id 18 until batch 798 (1.52 offers id 20, SummonerPromoteSR), is read as the new one (<see cref="TryCanonicalFromDisplay"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> DominionNames = ["SummonerOdinGarrison", "SummonerPromoteSR"];

    /// <summary>Promote's 126 script name (removed in 1.0.0.132), which the AIR offer sent until batch 798.</summary>
    private const string OdinPromote = "SummonerOdinPromote";

    public static readonly IReadOnlyList<string> DominionLauncherNames = ["Garrison", "Promote"];

    public static readonly IReadOnlyList<string> LauncherNames =
    [
        "Flash", "Ignite", "Heal", "Ghost", "Exhaust", "Smite", "Cleanse",
        "Clairvoyance", "Clarity", "Teleport", "Revive", "Fortify", "Rally", "Surge",
    ];

    /// <summary>
    /// The script name of a summoner spell this loadout knows ("Fortify" and "SummonerFortify" both answer
    /// "SummonerFortify"), or null when the name is not a summoner spell at all.
    /// </summary>
    public static string? TryCanonicalFromDisplay(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (string.Equals(value.Trim(), OdinPromote, StringComparison.OrdinalIgnoreCase)) return DominionNames[1];
        for (var index = 0; index < LauncherNames.Count; index++)
            if (string.Equals(value.Trim(), LauncherNames[index], StringComparison.OrdinalIgnoreCase))
                return CanonicalNames[index];
        for (var index = 0; index < CanonicalNames.Count; index++)
            if (string.Equals(value.Trim(), CanonicalNames[index], StringComparison.OrdinalIgnoreCase))
                return CanonicalNames[index];
        for (var index = 0; index < DominionNames.Count; index++)
            if (string.Equals(value.Trim(), DominionNames[index], StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value.Trim(), DominionLauncherNames[index], StringComparison.OrdinalIgnoreCase))
                return DominionNames[index];
        return null;
    }

    public static string CanonicalFromDisplay(string value) =>
        TryCanonicalFromDisplay(value) ?? throw new ArgumentException($"Unknown summoner spell '{value}'.", nameof(value));

    public static string DisplayName(string value)
    {
        var canonical = CanonicalFromDisplay(value);
        var index = Array.IndexOf(CanonicalNames.ToArray(), canonical);
        if (index < 0) return DominionLauncherNames[Array.IndexOf(DominionNames.ToArray(), canonical)];
        return LauncherNames[index];
    }

    public static (string First, string Second) ValidatePair(string first, string second)
    {
        var canonicalFirst = CanonicalFromDisplay(first);
        var canonicalSecond = CanonicalFromDisplay(second);
        if (string.Equals(canonicalFirst, canonicalSecond, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A player must choose two different summoner spells.");
        return (canonicalFirst, canonicalSecond);
    }
}
