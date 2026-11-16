using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amberline.Data;
using Amberline.Platform;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;
using static Amberline.Tests.Air.AirPartyQueueChecks;
using static Amberline.Tests.Air.AirQueueChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 799 (AIR 132 batch D): the 1.52 line's data and rules. Rune pages twenty (wiki 129.56), the ranked champion requirement of sixteen
/// (the 1.52 bundle), the skins on sale (the price list's own, Inventory.updateChampionSkins), the rune effect categories of the 1.52
/// catalogue, and the price list of patch 1.0.0.132 (assets/air-store-2012, operator content seeded by install-132.ps1 -Air).
/// </summary>
public static class Air132StoreDataChecks
{
    // The seven champions the patches 1.0.0.127 to 1.0.0.132 added to the 1.45 roster (ChampionGeneratedData.as of 1.52: 91 champions, 84 in 1.45).
    private static readonly int[] NewChampions = [102, 103, 104, 105, 106, 112, 113];

    // Their fourteen skins, skinIndex 1 and 2 of each (ChampionGeneratedData.as; the 43 skin rows 1.52 adds are these and 29 more).
    private static readonly int[] NewChampionSkins =
        [102001, 102002, 103001, 103002, 104001, 104002, 105001, 105002, 106001, 106002, 112001, 112002, 113001, 113002];

    // Batch 812: six January 2012 skins, each at the price its wiki skin page prints (Reconstruction; the January price was not read).
    private static readonly (int Id, int Rp)[] JanuarySkins = [(55006, 975), (45006, 975), (64003, 975), (37004, 975), (91003, 975), (62003, 975)];

    [Check("server.air132-rune-pages-twenty")]
    public static void RunePagesTwenty()
    {
        TestAssert.Equal(20, AirLine.MaximumRunePages, "1.0.0.132 allows twenty rune pages (wiki 129.56)");
        var root = TemporaryDirectory("league-air132-rune-pages");
        try
        {
            using var accounts = new PlatformAccounts(root);
            TestAssert.Equal(20, accounts.EconomyRules.MaximumRunePages, "the default economy rule is the line's cap");
            TestAssert.Equal(Enumerable.Range(3, 18).ToArray(),
                accounts.StoreCatalog.Items.Where(item => item.Kind == "SPELL_BOOK_PAGE").Select(item => item.ContentId).Order().ToArray(),
                "the store sells the extra pages 3 to 20");
            var owner = accounts.RegisterPublic("pagestwenty", "testing-password", "Pages Twenty", maxed: true);
            TestAssert.Equal(20, accounts.GetRunePages(owner.Id).Length, "an all-unlocked account owns every page");
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air132-ranked-needs-sixteen-champions")]
    public static void RankedNeedsSixteenChampions() => RankedChampionsAsync().GetAwaiter().GetResult();

    private static async Task RankedChampionsAsync()
    {
        TestAssert.Equal(16, PlatformGames.RankedMinChampions, "the 1.52 bundle: 'must have at least 16 champions available'");
        var root = TemporaryDirectory("league-air132-ranked-champions");
        try
        {
            // A fresh summoner is level 1 (RANKED_MIN_LEVEL) and reaches level 30 only through games, so the champion count is read from
            // the reasons that remain: fifteen champions are refused for both, sixteen only for the level.
            await using var rig = new Rig(root);
            var player = rig.SignIn("rankchampions", maxed: false);
            rig.Accounts.GrantCurrency(player.Id, 1_000_000, 0, Guid.NewGuid());
            var playable = AirGameDtos.ChampionNames.Keys.Order().ToArray();
            HashSet<int> Owned() => (rig.Accounts.Get(player.Id).Economy?.OwnedChampions ?? Array.Empty<int>()).ToHashSet();
            string[] Refusals() => (((ArrayCollection?)rig.Attach(player, "attachToQueue", 4, null, null)["playerJoinFailures"])?
                .Cast<AsObject>().Select(row => (string)row["reasonFailed"]).ToArray()) ?? Array.Empty<string>();

            while (Owned().Count < 15)
                rig.Accounts.Purchase(player.Id, $"champion:{playable.First(id => !Owned().Contains(id))}", "IP", Guid.NewGuid());
            TestAssert.Equal(15, Owned().Count, "fifteen champions are owned");
            TestAssert.Equal(new[] { "RANKED_MIN_LEVEL", "RANKED_NUM_CHAMPS" }, Refusals(),
                "fifteen champions are one short of the ranked requirement");

            rig.Accounts.Purchase(player.Id, $"champion:{playable.First(id => !Owned().Contains(id))}", "IP", Guid.NewGuid());
            TestAssert.Equal(16, Owned().Count, "sixteen champions are owned");
            TestAssert.Equal(new[] { "RANKED_MIN_LEVEL" }, Refusals(), "sixteen champions meet the champion requirement; the level one remains");
        }
        finally { DeleteDirectory(root); }
    }

    [Check("server.air132-obtainable-skins")]
    public static void ObtainableSkinsAreTheSkinsTheStoreSells()
    {
        var root = TemporaryDirectory("league-air132-obtainable-skins");
        try
        {
            using var accounts = new PlatformAccounts(root);
            var player = accounts.RegisterPublic("skinsonsale", "testing-password", "Skins On Sale", maxed: false);
            var states = (AsObject)AirDtos.LoginData(accounts.Get(player.Id), rules: accounts.EconomyRules)["clientSystemStates"];
            var listed = ((ArrayCollection)states["obtainableChampionSkinIDList"]).Cast<object>().Select(id => Convert.ToInt32(id)).ToHashSet();
            var unlisted = ((ArrayCollection)states["unobtainableChampionSkinIDList"]).Cast<object>().Select(id => Convert.ToInt32(id)).ToHashSet();

            // The skin rows every champion sends (ChampionDTO.championSkins), each with its stillObtainable flag.
            var rows = AirDtos.Champions().Cast<AsObject>()
                .SelectMany(champion => ((ArrayCollection)champion["championSkins"]).Cast<AsObject>()).ToArray();
            var skins = rows.Select(row => Convert.ToInt32(row["skinId"])).ToArray();
            TestAssert.True(skins.Length > 0, "the champions send their skins");
            TestAssert.Equal(skins.Length, skins.Distinct().Count(), "each skin is sent once");

            // The store sells what its price list prices (PlatformStorePrices.Default; the store lists a skin only when it is priced).
            var sold = skins.Where(id => PlatformStorePrices.Default.SkinRp(id) is not null).ToHashSet();
            TestAssert.True(listed.SetEquals(sold), $"obtainable: {listed.Count} listed, {sold.Count} sold");
            TestAssert.True(unlisted.SetEquals(skins.Where(id => !sold.Contains(id))), "the unobtainable list is every other skin");
            TestAssert.False(listed.Overlaps(unlisted), "no skin is in both lists");
            foreach (var row in rows)
            {
                var id = Convert.ToInt32(row["skinId"]);
                TestAssert.Equal(sold.Contains(id), row["stillObtainable"] is true, $"skin {id}: stillObtainable follows the price list");
            }
        }
        finally { DeleteDirectory(root); }
    }


    // The store home's January 2012 content, newest first: the README's table by store date (its sources are there). A skin is listed on
    // the home only when the price list prices it and the 1.52 client has its art.
    private static readonly string[] JanuaryWhatsNew =
    [
        "Sejuani", "Sabretusk Sejuani", "Darkrider Sejuani",
        "Dragon Fist Lee Sin", "Guqin Sona", "Dragonblade Talon", "Jade Dragon Wukong",
        "Sandstorm Katarina", "Superb Villain Veigar",
        "Viktor", "Full Machine Viktor", "Prototype Viktor",
    ];

    private static readonly string[] JanuaryTopSellers = ["Sejuani", "Sabretusk Sejuani", "Darkrider Sejuani", "Viktor", "Full Machine Viktor", "Prototype Viktor"];

    // The October 2011 products (the Xerath release): none may show on the 1.0.0.132 store home.
    private static readonly string[] OctoberContent = ["Xerath", "Runeborn", "Battlecast", "Cryocore", "Officer Caitlyn"];


    // The text from a marker up to the next end marker (the store home's sections hold no nested section).
    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        if (from < 0) return string.Empty;
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        return to < 0 ? text[from..] : text[from..to];
    }

    private static List<string> Values(string text, string pattern) =>
        Regex.Matches(text, pattern).Select(match => match.Groups[1].Value).ToList();

    private static void Expect(List<string> problems, string what, List<string> actual, params string[] expected)
    {
        if (!actual.SequenceEqual(expected))
            problems.Add($"{what} is {string.Join(" | ", actual)}, not {string.Join(" | ", expected)}");
    }

    [Check("server.air132-rune-effect-categories-from-1-52")]
    public static void RuneEffectCategoriesAreTheCatalogues()
    {
        // The 1.52 catalogue (AirGeneratedContent RuneGeneratedData): categoryId and runeType of every effect name.
        // Each name has one pair in every rune that carries it (41 names).
        (string Name, int Category, int Type)[] catalogue =
        [
            ("FlatHPPoolMod", 1, 3), ("rFlatHPModPerLevel", 1, 3), ("FlatHPRegenMod", 1, 3), ("rFlatHPRegenModPerLevel", 1, 3),
            ("FlatMPPoolMod", 1, 5), ("rFlatMPModPerLevel", 2, 5), ("FlatMPRegenMod", 2, 3), ("rFlatMPRegenModPerLevel", 2, 3),
            ("FlatPhysicalDamageMod", 3, 1), ("rFlatPhysicalDamageModPerLevel", 3, 1), ("PercentAttackSpeedMod", 3, 1),
            ("rPercentAttackSpeedModPerLevel", 3, 1), ("FlatCritChanceMod", 3, 1), ("rFlatCritChanceModPerLevel", 3, 1),
            ("FlatCritDamageMod", 3, 1), ("rFlatCritDamageModPerLevel", 3, 1), ("rFlatArmorPenetrationMod", 3, 1),
            ("rFlatArmorPenetrationModPerLevel", 3, 1), ("FlatMagicDamageMod", 4, 5), ("rFlatMagicDamageModPerLevel", 4, 5),
            ("rFlatMagicPenetrationMod", 4, 1), ("rFlatMagicPenetrationModPerLevel", 4, 1), ("FlatArmorMod", 5, 3),
            ("rFlatArmorModPerLevel", 5, 3), ("FlatSpellBlockMod", 5, 5), ("rFlatSpellBlockModPerLevel", 5, 5), ("rFlatDodgeMod", 5, 3),
            ("rFlatDodgeModPerLevel", 5, 3), ("PercentMovementSpeedMod", 6, 7), ("rPercentCooldownMod", 6, 5),
            ("rPercentCooldownModPerLevel", 6, 5), ("rFlatGoldPer10Mod", 6, 7), ("PercentEXPBonus", 6, 7), ("rPercentTimeDeadMod", 6, 7),
            ("FlatEnergyPoolMod", 2, 5), ("rFlatEnergyModPerLevel", 2, 5), ("FlatEnergyRegenMod", 2, 3), ("rFlatEnergyRegenModPerLevel", 2, 3),
            ("PercentHPPoolMod", 2, 3), ("PercentLifeStealMod", 3, 1), ("PercentSpellVampMod", 4, 1),
        ];
        TestAssert.Equal(41, catalogue.Length, "the catalogue's effect names");
        var root = TemporaryDirectory("league-air132-rune-categories");
        try
        {
            using var accounts = new PlatformAccounts(root);
            var owner = accounts.RegisterPublic("runecategories", "testing-password", "Rune Categories", maxed: true);
            var effects = ((ArrayCollection)AirDtos.RuneInventory(accounts.Get(owner.Id))["summonerRunes"]).Cast<AsObject>()
                .SelectMany(rune => ((ArrayCollection)((AsObject)rune["rune"])["itemEffects"]).Cast<AsObject>()).ToArray();
            TestAssert.True(effects.Length > 0, "the regular runes send their effects");
            foreach (var effect in effects)
            {
                var name = (string)effect["name"];
                var expected = catalogue.FirstOrDefault(row => row.Name == name);
                TestAssert.True(expected.Name is not null, $"{name} is an effect of the 1.52 catalogue");
                TestAssert.Equal((expected.Category, expected.Type), ((int)effect["effectCategory"], (int)effect["effectType"]),
                    $"{name}: categoryId and runeType");
            }
        }
        finally { DeleteDirectory(root); }
    }

    private static string BasisOf(JsonElement root, string section, int id)
    {
        foreach (var entry in root.GetProperty(section).EnumerateArray())
            if (entry.GetProperty("id").GetInt32() == id)
                return entry.TryGetProperty("basis", out var basis) ? basis.GetString() ?? "" : "";
        return "";
    }
}
