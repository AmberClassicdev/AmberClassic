namespace Amberline.Platform;

/// <summary>
/// The store home's content of this line (batch 812). The copy that compiles is the seam, like <see cref="AirLine"/>: the shared store
/// code reads these values and holds none of its own. Patch 1.0.0.126 shows the October 2011 store (the Xerath release); the values are
/// the ones the shared code held before batch 812, unchanged.
/// </summary>
internal static class StoreLine
{
    /// <summary>The NEW promo's product: its title and its button name it. Its subtitle, body and art are as printed.</summary>
    public const string PromoItemId = "champion:101";
    public const string PromoSubtitle = "The Magus Ascendant";
    public const string PromoBody = "A long-range mage who bombards his enemies from afar. New in this patch.";
    public static readonly IReadOnlyList<string> PromoArtKeys = ["champion-loading-101", "skin-loading-101001", "skin-loading-101002"];

    /// <summary>The New Skins promo. Its title is the slide's name; the store home drops the slide while RP is off.</summary>
    public const string SkinsTitle = "New Skins";
    public const string SkinsSubtitle = "Now in the store";
    public const string SkinsBody = "Runeborn Xerath, Battlecast Xerath, Cryocore Brand and Officer Caitlyn skins are here!";
    public static readonly IReadOnlyList<string> SkinsArtKeys = ["skin-loading-101001", "skin-loading-101002", "skin-loading-63003"];

    /// <summary>
    /// What's New, the newest first; the first one is the Featured Item.
    /// Reconstruction: what patch 1.0.0.126 added to the store on 5 October 2011 (the Xerath release: the champion and the skins Runeborn
    /// Xerath, Battlecast Xerath, Cryocore Brand and Officer Caitlyn). The Store Home lists them under "What's New" with a New badge. The
    /// Sep 25 screenshots fill the same panel with the then-newest items.
    /// </summary>
    public static readonly IReadOnlyList<string> NewItemIds = ["champion:101", "skin:101001", "skin:101002", "skin:63003", "skin:51005"];

    /// <summary>
    /// Top Sellers. Reconstruction: the server has no sales data; like the 25 September screenshot, where the list is the newest champion
    /// with its skins and the newest skins, it lists the newest items.
    /// </summary>
    public static readonly IReadOnlyList<string> TopSellerIds =
        ["champion:101", "skin:101001", "skin:101002", "skin:63003", "skin:51005", "champion:92"];
}
