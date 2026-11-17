using Amberline.Data;
using Amberline.Simulation;
using static Amberline.Tests.BbM3;
using static Amberline.Tests.BbP132Pins;

namespace Amberline.Tests;

/// <summary>
/// Batch 855 (17 November 2026): the 1.0.0.132 item rows of the change list that no check pinned (Stinger 132.51, Madred's Razors 130.27,
/// Wriggle's Lantern 130.28), the shop charge of Nashor's Tooth (132.49, batch 834 F2, driven through the production purchase), and the
/// untargetable part of Garrison (128.25: the 132 script has no targeted execute). Each reads the shipped 132 record or script; the 126 values
/// are named in each message.
/// </summary>
public static class BbB855ItemChecks
{
    private static int FullCost(ItemCatalog catalog, int itemId)
    {
        var item = catalog.Get(itemId)!;
        return item.Price + item.RecipeItems.Sum(part => FullCost(catalog, part));
    }

    // 132.51: Stinger combines for 250 (126: 300) and costs 1090 in full (126: 1140), with its two Daggers at 420 each.
    [Check("items.p132-stinger-combines-for-250-and-costs-1090-with-its-components")]
    public static void StingerCost()
    {
        var catalog = new ItemCatalog();
        TestAssert.Equal(250, catalog.Get(3101)!.Price, "Stinger's combine cost is 250 on 1.0.0.132 (126: 300; change list 132.51)");
        TestAssert.Equal(1090, FullCost(catalog, 3101), "Stinger costs 1090 with its components on 1.0.0.132 (126: 1140; change list 132.51)");
    }

    // 130.27: Madred's Razors procs on a basic attack 20% of the time (126: 15%) and deals 300 magic damage (126: 500) to a non-hero AI target.
    [Check("scripting.p132-madreds-razors-procs-twenty-percent-for-three-hundred-magic")]
    public static void MadredsRazorsProc()
    {
        var razors = Script("MadredsRazors");
        TestAssert.True(Calls(razors, "BBIf").Any(block => Named(block, "CompareOp") == "CO_RANDOM_CHANCE_LESS_THAN" && Is(Number(block, "Value1"), 0.2)),
            "the proc chance is 0.2 (shipped MadredsRazors; 126: 0.15; change list 130.27)");
        TestAssert.True(Calls(razors, "BBApplyDamage").Any(block => Is(Number(block, "Damage"), 300) && Named(block, "DamageType") == "MAGIC_DAMAGE"),
            "the proc deals 300 magic damage (shipped MadredsRazors; 126: 500; change list 130.27)");
    }

    // 130.28: Wriggle's Lantern's proc on a non-hero AI target deals 425 magic damage (126: 500); its 20% chance does not change.
    [Check("scripting.p132-wriggles-lantern-proc-deals-four-hundred-twenty-five-magic")]
    public static void WrigglesLanternProc()
    {
        var lantern = Script("WriggleLantern");
        TestAssert.True(Calls(lantern, "BBApplyDamage").Any(block => Is(Number(block, "Damage"), 425) && Named(block, "DamageType") == "MAGIC_DAMAGE"),
            "the proc deals 425 magic damage (shipped WriggleLantern; 126: 500; change list 130.28)");
        TestAssert.True(Calls(lantern, "BBIf").Any(block => Named(block, "CompareOp") == "CO_RANDOM_CHANCE_LESS_THAN" && Is(Number(block, "Value1"), 0.2)),
            "the proc chance stays 0.2 (shipped WriggleLantern)");
    }

    // 132.49 (batch 834 F2): the shop charges the recipe's full cost into an empty inventory, 2735 (126: 2885), through the production purchase.
    [Check("items.p132-nashors-tooth-shop-charges-the-full-cost-into-an-empty-inventory")]
    public static void NashorsToothFullCharge()
    {
        var catalog = new ItemCatalog();
        var result = Inventory.Buy(catalog, new InventorySlot?[Inventory.SlotCount], 3000.0, 3115);
        TestAssert.True(result.Ok, "the purchase is accepted with 3000 gold (reason: " + result.Reason + ")");
        TestAssert.Near(3000.0 - 2735.0, result.Gold, 1e-9, "Nashor's Tooth costs 2735 into an empty inventory on 1.0.0.132 (126: 2885; change list 132.49)");
    }

    // 132.49: with its Stinger already owned, the purchase pays Nashor's Tooth's own combine cost (400) and the Stinger's component is not charged again:
    // 400 plus the full cost of the other component (1245), so 1645 (126: 500 plus the same 1245).
    [Check("items.p132-nashors-tooth-shop-charges-the-combine-cost-when-stinger-is-owned")]
    public static void NashorsToothWithStinger()
    {
        var catalog = new ItemCatalog();
        var slots = new InventorySlot?[Inventory.SlotCount];
        slots[0] = new InventorySlot(3101);
        var result = Inventory.Buy(catalog, slots, 3000.0, 3115);
        TestAssert.True(result.Ok, "the purchase is accepted with a Stinger owned (reason: " + result.Reason + ")");
        TestAssert.Near(3000.0 - (400 + FullCost(catalog, 3108)), result.Gold, 1e-9,
            "with Stinger owned, Nashor's Tooth costs its combine cost plus the other component's full cost (1645 on 1.0.0.132)");
    }

    // 128.25: the 132 Garrison is cast without a target (its targeted execute is gone; 126 has one), and its cast gate is pinned elsewhere.
    [Check("scripting.p132-garrison-has-no-targeted-execute-so-the-cast-is-untargeted")]
    public static void GarrisonIsUntargeted()
    {
        var garrison = Script("SummonerOdinGarrison");
        TestAssert.True(garrison.Event("TargetExecute") is null,
            "the 132 Garrison has no TargetExecute hook (shipped SummonerOdinGarrison; 126 has one; change list 128.25)");
    }
}
