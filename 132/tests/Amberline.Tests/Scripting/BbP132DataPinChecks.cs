using Amberline.Data;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 834 (17 November 2026): the 1.0.0.132 data rows of the audit's batch B that no check pinned (DATA-ONLY, unpinned). Each reads the
/// shipped value through the production reader (the summoner spell data, the summoner scripts, the item catalogue, the summoner minimum levels)
/// and pins it at the 1.0.0.132 number of the change list (REPORT.md). Where a shipped file disagrees with the change list, the file is pinned.
/// </summary>
public static class BbP132DataPinChecks
{
    // 129.41 (Clairvoyance cooldown 70) and 129.52 (Smite cooldown 70) are pinned cast-driven in SummonerCoreChecks.cs, and 129.43 (Exhaust)
    // there too: the shipped inibins still hold the 126 cooldowns, so a literal read of the inibin would pin the wrong value.

    // 129.46: Garrison is learnt at level one (126: four).
    [Check("scripting.p132-garrison-is-learnt-at-level-one")]
    public static void GarrisonIsLevelOne() =>
        TestAssert.Equal(1, Amberline.Platform.AirDtos.SpellMinimumLevel("SummonerOdinGarrison"),
            "Garrison's minimum level is 1 on 1.0.0.132 (126: 4; change list 129.46)");

    // 132.49: Nashor's Tooth (item 3115, identified by its +65 ability power in the shipped description) costs 2735 with its components (126:
    // 2885). The record's own price is its combine cost (500 to 400 in the change list, pinned as 400 here).
    [Check("scripting.p132-nashors-tooth-costs-2735-and-gives-sixty-five-ability-power")]
    public static void NashorsToothCostsTwoThousandSevenHundredThirtyFive()
    {
        var catalog = new ItemCatalog();
        var tooth = catalog.Get(3115)!;
        TestAssert.True(tooth.Modifiers.Any(modifier => modifier.Value == 65.0), "it gives +65 ability power (126: 55; change list 132.49)");
        TestAssert.Equal(400, tooth.Price, "its combine cost is 400 on 1.0.0.132 (126: 500; change list 132.49)");
        TestAssert.Equal(2735, FullCost(catalog, 3115), "Nashor's Tooth costs 2735 on 1.0.0.132 with its components (126: 2885; change list 132.49)");
    }

    /// <summary>An item's full cost: its own price (its combine cost when it has a recipe) plus the full cost of each component.</summary>
    private static int FullCost(ItemCatalog catalog, int itemId)
    {
        var item = catalog.Get(itemId)!;
        return item.Price + item.RecipeItems.Sum(part => FullCost(catalog, part));
    }
}
