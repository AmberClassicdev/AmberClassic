using System.IO;

namespace Amberline.Data;

/// <summary>
/// One retail 1.0.0.126 bot loadout: summoner spells, the ordered rune list and
/// the mastery ranks stored for a champion and bot difficulty section.
/// </summary>
public sealed record BotLoadoutData(
    string? SummonerSpell1,
    string? SummonerSpell2,
    IReadOnlyList<int> Runes,
    IReadOnlyDictionary<int, int> Masteries);

/// <summary>
/// Reads <c>Characters/&lt;X&gt;/Scripts/&lt;X&gt;Bot{SummonerSpells,Runes,Masteries}.inibin</c>.
/// Key names, recovered from the shipped files by hash match: every section
/// is <c>Bot{Spell|Rune|Mastery}Data</c> plus a difficulty suffix. The
/// summoner-spell section and suffix list are the client's own
/// (<c>PlayerConnectionInfo</c> builds <c>BotSummonerSpellData</c> + eNEWBIE,
/// eINTERMEDIATE, eADVANCED, eUBER or eTUTORIAL). Keys inside a section are
/// <c>SummonerSpell1/2</c>, <c>Rune1..30</c> and <c>MasteryN</c> (mastery id)
/// with <c>MasteryNLevel</c> (rank; stored as a boolean when 1).
/// </summary>
public static class BotLoadoutReader
{
    public static readonly IReadOnlyList<string> Difficulties =
        ["eNEWBIE", "eINTERMEDIATE", "eADVANCED", "eUBER", "eTUTORIAL"];

    private const int MaxRuneSlots = 30;
    private const int MaxMasteryEntries = 64;

    /// <summary>Returns null when the champion ships no bot loadout files.</summary>
    public static BotLoadoutData? Read(GameDataPaths paths, string champion, int difficulty)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(champion);
        if ((uint)difficulty >= (uint)Difficulties.Count)
            throw new ArgumentOutOfRangeException(nameof(difficulty));
        var stem = $"Characters/{champion}/Scripts/{champion}Bot";
        var spellsPath = paths.Data(stem + "SummonerSpells.inibin");
        if (!File.Exists(spellsPath)) return null;
        var suffix = Difficulties[difficulty];

        var spells = InibinReader.Load(spellsPath);
        var first = Text(spells, $"BotSummonerSpellData{suffix}*SummonerSpell1");
        var second = Text(spells, $"BotSummonerSpellData{suffix}*SummonerSpell2");
        if (first is null || second is null) first = second = null;

        var runes = new List<int>();
        var runesPath = paths.Data(stem + "Runes.inibin");
        if (File.Exists(runesPath))
        {
            var data = InibinReader.Load(runesPath);
            for (var slot = 1; slot <= MaxRuneSlots; slot++)
                if (Number(data, $"BotRuneData{suffix}*Rune{slot}") is { } id && id > 0) runes.Add(id);
        }

        var masteries = new Dictionary<int, int>();
        var masteriesPath = paths.Data(stem + "Masteries.inibin");
        if (File.Exists(masteriesPath))
        {
            var data = InibinReader.Load(masteriesPath);
            for (var index = 1; index <= MaxMasteryEntries; index++)
            {
                if (Number(data, $"BotMasteryData{suffix}*Mastery{index}") is not { } id) break;
                if (Number(data, $"BotMasteryData{suffix}*Mastery{index}Level") is { } rank && rank > 0)
                    masteries[id] = rank;
            }
        }
        if (masteries.Count > 0)
        {
            var tree = new MasteryCatalog(paths);
            if (masteries.Keys.Any(id => !tree.TryGet(id, out _))) masteries = BotPage132(masteries);
        }
        return new BotLoadoutData(first, second, runes, masteries);
    }

    // The 1.0.0.126 tree's groups (that release's DATA/Talents/TalentTree.xml): every other 126 id is Utility.
    private static readonly HashSet<int> Offense126 = [100, 101, 102, 103, 104, 105, 106, 107, 123, 126, 136, 137, 143, 146];
    private static readonly HashSet<int> Defense126 = [108, 109, 110, 111, 112, 113, 114, 125, 127, 129, 131, 133, 144, 145];
    // The caster line of the 126 Offense tree (Sorcery, Archmage's Savvy, Archaic Knowledge) against its attack line.
    private static readonly HashSet<int> Caster126 = [102, 106, 107];
    private static readonly HashSet<int> Attack126 = [100, 101, 103, 104, 105];

    // One fill order per 1.0.0.132 tree (ids 211-463): taken from the front, any prefix is a legal page (each row's
    // pointsToActivate is met by the earlier rows and every prerequisite is at its MaxRank before its dependant). A talent may
    // appear twice; its ranks add.
    private static readonly (int Id, int Ranks)[] AttackOffense =
        [(212, 3), (211, 1), (222, 4), (231, 4), (241, 1), (242, 3), (252, 3), (234, 1), (262, 1), (234, 2), (232, 1), (214, 2), (213, 4)];
    private static readonly (int Id, int Ranks)[] CasterOffense =
        [(213, 3), (211, 1), (223, 4), (233, 1), (234, 3), (243, 4), (253, 4), (262, 1), (213, 1), (222, 4), (212, 3), (214, 1)];
    private static readonly (int Id, int Ranks)[] Defense =
        [(313, 3), (311, 1), (322, 4), (332, 1), (331, 2), (323, 1), (341, 1), (342, 3), (352, 3), (312, 1), (362, 1), (312, 2), (353, 3), (323, 2), (314, 2)];
    private static readonly (int Id, int Ranks)[] Utility =
        [(413, 3), (411, 1), (423, 3), (422, 1), (432, 4), (442, 2), (443, 2), (453, 3), (434, 1), (463, 1), (452, 3), (412, 3), (443, 2), (422, 2)];

    /// <summary>
    /// Reconstruction (batch 770): the 1.0.0.132 client ships the 1.0.0.126 bot mastery pages byte for byte
    /// (<c>&lt;X&gt;BotMasteries.inibin</c>), whose ids its own TalentTree no longer holds (patch 1.0.0.129 remade the trees),
    /// and what the 132 server seated for them is not in the shipped files. The page keeps the shipped section's points in each
    /// tree (Annie's Hard 9/0/21, Cho'Gath's 0/21/9, Ashe's Easy 15/0/0) and spends them down the fixed 132 order of that tree; the
    /// Offense points follow the caster order when the 126 page put more of them into Sorcery, Archmage's Savvy and Archaic
    /// Knowledge than into the attack line. The orders are retail-style 21-point lines (an AD carry's Brute Force, Alacrity,
    /// Deadliness, Lethality, Vampirism, Sunder and Executioner; a caster's Mental Force, Sorcery, Arcane Knowledge, Havoc, Blast,
    /// Archmage; a tank's Hardiness, Durability, Veteran's Scars, Indomitable, Honor Guard, Juggernaut; a support's Expanded
    /// Mind, Meditation, Greed, Wealth, Awareness, Intelligence, Mastermind), each opening with its tree's summoner talent.
    /// </summary>
    private static Dictionary<int, int> BotPage132(IReadOnlyDictionary<int, int> page126)
    {
        int Points(Func<int, bool> where) => page126.Where(pair => where(pair.Key)).Sum(pair => pair.Value);
        var offense = Points(Offense126.Contains);
        var defense = Points(Defense126.Contains);
        var utility = Points(id => !Offense126.Contains(id) && !Defense126.Contains(id));
        var page = new Dictionary<int, int>();
        void Spend((int Id, int Ranks)[] order, int points)
        {
            foreach (var (id, ranks) in order)
            {
                if (points <= 0) return;
                var taken = Math.Min(ranks, points);
                page[id] = page.GetValueOrDefault(id) + taken;
                points -= taken;
            }
        }
        Spend(Points(Caster126.Contains) > Points(Attack126.Contains) ? CasterOffense : AttackOffense, offense);
        Spend(Defense, defense);
        Spend(Utility, utility);
        return page;
    }

    private static string? Text(InibinData data, string key) =>
        data.TryGetValue(InibinReader.KeyHash(key), out var value) && value.Kind == InibinValueKind.String
            ? value.AsString() : null;

    // Values are stored as u8/u16, as a bit-packed boolean (rank 1) or as a
    // numeric string, which InibinReader coerces to an integer.
    private static int? Number(InibinData data, string key)
    {
        if (!data.TryGetValue(InibinReader.KeyHash(key), out var value)) return null;
        if (value.Kind == InibinValueKind.Boolean) return value.Boolean ? 1 : 0;
        return value.IsNumeric ? checked((int)value.AsDouble()) : null;
    }
}
