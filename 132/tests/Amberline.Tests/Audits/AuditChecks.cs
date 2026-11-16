using System.Text.Json;
using Amberline.Data;
using Amberline.Tools.Audits;

namespace Amberline.Tests;

public static class AuditChecks
{



    /// <summary>
    /// Batch 781: the 1.0.0.132 client ships one skin more than the 126 AIR client (AIR 1.45) listed for these 29 champions (the Harrowing
    /// 2011, Snowdown 2011 and Lunar Revel 2012 skins: one more <c>MeshSkin</c> section in
    /// <c>DATA/Characters/&lt;name&gt;/&lt;name&gt;.inibin</c>, named by <c>game_character_skin_displayname_&lt;name&gt;_&lt;n&gt;</c> in the 132
    /// <c>DATA/Menu/fontconfig_en_US.txt</c>). Batch 795: the AIR 1.52 client's rows, master's catalog since then, list them. The value is the
    /// 132 count, the skin it adds.
    /// </summary>
    private static readonly Dictionary<string, int> SkinsAddedBy132 = new(StringComparer.Ordinal)
    {
        ["Annie"] = 7, // FrankenTibbers Annie
        ["TwistedFate"] = 6, // Musketeer Twisted Fate
        ["Leblanc"] = 3, // Mistletoe LeBlanc
        ["Vladimir"] = 5, // Blood Lord Vladimir
        ["FiddleSticks"] = 6, // Surprise Party Fiddlesticks
        ["Tristana"] = 6, // Rocketeer Tristana
        ["Warwick"] = 7, // Hyena Warwick
        ["MissFortune"] = 6, // Mafia Miss Fortune
        ["Ashe"] = 5, // Amethyst Ashe
        ["Jax"] = 6, // Temple Jax
        ["Chogath"] = 4, // Jurassic Cho'Gath
        ["Shaco"] = 5, // Asylum Shaco
        ["Sona"] = 4, // Guqin Sona
        ["Janna"] = 3, // Frost Queen Janna
        ["Gangplank"] = 4, // Toy Soldier Gangplank
        ["Veigar"] = 6, // Superb Villain Veigar
        ["Blitzcrank"] = 5, // Definitely Not Blitzcrank
        ["Katarina"] = 6, // Sandstorm Katarina
        ["Nocturne"] = 4, // Haunting Nocturne
        ["Maokai"] = 3, // Festive Maokai
        ["MonkeyKing"] = 3, // Jade Dragon Wukong
        ["LeeSin"] = 3, // Dragon Fist Lee Sin
        ["Heimerdinger"] = 4, // Snowmerdinger
        ["Nidalee"] = 5, // Bewitching Nidalee
        ["Ezreal"] = 5, // Pulsefire Ezreal
        ["Akali"] = 6, // Silverfang Akali
        ["Kennen"] = 5, // Arctic Ops Kennen
        ["Talon"] = 3, // Dragonblade Talon
        ["Lux"] = 4, // Imperial Lux
    };


    private static void CompareStats(JsonElement source, ChampionStatsAudit actual)
    {
        FixtureNear(source.GetProperty("max_hp").GetDouble(), actual.MaxHp);
        FixtureNear(source.GetProperty("max_mp").GetDouble(), actual.MaxMp);
        FixtureNear(source.GetProperty("attack_damage").GetDouble(), actual.AttackDamage);
        FixtureNear(source.GetProperty("armor").GetDouble(), actual.Armor);
        FixtureNear(source.GetProperty("spell_block").GetDouble(), actual.SpellBlock);
        FixtureNear(source.GetProperty("hp_regen").GetDouble(), actual.HpRegen);
        FixtureNear(source.GetProperty("mp_regen").GetDouble(), actual.MpRegen);
        FixtureNear(source.GetProperty("attack_speed_mod").GetDouble(), actual.AttackSpeedMod);
        FixtureNear(source.GetProperty("move_speed").GetDouble(), actual.MoveSpeed);
        FixtureNear(source.GetProperty("attack_range").GetDouble(), actual.AttackRange);
    }

    private static void CompareRelated(JsonElement champion, IReadOnlyList<string>? actual)
    {
        var expected = champion.TryGetProperty("related_scripts", out var value)
            ? value.ValueKind == JsonValueKind.Object
                ? value.EnumerateObject().Select(item => Path.GetFileNameWithoutExtension(item.Name)).ToArray()
                : ArrayOfStrings(value)
            : Array.Empty<string>();
        var values = actual ?? Array.Empty<string>();
        TestAssert.Equal(expected.Length, values.Count);
        for (var index = 0; index < expected.Length; index++)
            TestAssert.Equal(expected[index], values[index]);
    }

    // A byte/10 inibin value is the client's float32 (float)b * 0.1f (the client's raw float lookup), which differs from the decimal
    // b/10 these fixtures recorded by at most two float32 roundings, under 2e-7 relative.
    private static void FixtureNear(double expected, double actual) =>
        TestAssert.Near(expected, actual, Math.Max(1e-9, Math.Abs(expected) * 2e-7));

    // Batch 187 (the cast-range reader, GameData.CastRange): the client lifts a shipped cast range at or below 1.0 to 1.0, and so does
    // the engine. The audit keeps the raw shipped entry in its fixture (source-backed, not re-recorded) and restates the
    // expectation as the floored read: ArchersMark, CardmasterStack and MoltenShield record 0 and read 1.
    private static double FlooredCastRange(JsonElement source) => Math.Max(1.0, source.GetProperty("cast_range").GetDouble());

    private static void CompareNumbers(JsonElement source, IReadOnlyList<double> actual)
    {
        var expected = source.ValueKind == JsonValueKind.Array
            ? source.EnumerateArray().Select(value => value.GetDouble()).ToArray()
            : [source.GetDouble()];
        TestAssert.Equal(expected.Length, actual.Count);
        for (var index = 0; index < expected.Length; index++)
            FixtureNear(expected[index], actual[index]);
    }

    private static string[] ArrayOfStrings(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? value.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray()
        : Array.Empty<string>();

    // Batch 64: an effect whose Effect{N}Level{R}Amount is a boolean-segment 0/1 used to vanish from the loader (the old reader treated the
    // key as missing); the recorded roster lacks those effects. An effect the fixture lacks must be explained that way (the same rule as
    // data.gamedata.corpus), and the count that is compared excludes only those.
    private static int ExtraEffects(ChampionSpellAudit spell, JsonElement recorded)
    {
        var recordedIds = recorded.EnumerateObject().Select(entry => int.Parse(entry.Name)).ToHashSet();
        var raw = GameData.Default.Spell(spell.Name).Raw;
        var extras = spell.Effects.Keys.Where(key => !recordedIds.Contains(key)).ToArray();
        foreach (var extra in extras)
            TestAssert.True(Enumerable.Range(1, spell.Effects[extra].Count).Any(rank =>
                    raw.TryGetValue(InibinReader.KeyHash($"SpellData*Effect{extra}Level{rank}Amount"), out var value) &&
                    value.Kind == InibinValueKind.Boolean),
                $"effect {extra} of {spell.Name} is new without a boolean-segment amount");
        return extras.Length;
    }

}
