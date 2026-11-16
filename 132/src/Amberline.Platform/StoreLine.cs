namespace Amberline.Platform;

/// <summary>
/// The store home's content of this line (batch 812). The copy that compiles is the seam, like <see cref="AirLine"/>: the shared store
/// code reads these values and holds none of its own. Patch 1.0.0.132 (17 January 2012) shows the January 2012 store: the newest champion,
/// her skins, and the skins of December 2011 to January 2012 that this line's price list prices (the README's January table has the
/// sources). What the sources do not state is marked Reconstruction.
/// </summary>
internal static class StoreLine
{
    // The NEW promo: Sejuani, released 17 January 2012 in patch 1.0.0.132 (wiki V1.0.0.132 "Release Date (US)": January 17, 2012, and
    // "New Champion: Sejuani"; the Sejuani page: release 2012-01-17). The subtitle is her client title (fontconfig
    // game_character_description_Sejuani). The body is Reconstruction wording from her 1.52 tags (jungler, tank, melee) and her passive
    // (Frost slows the enemies she hits: fontconfig game_character_passiveDescription_Sejuani).
    public const string PromoItemId = "champion:113";
    public const string PromoSubtitle = "The Winter's Wrath";
    public const string PromoBody = "A melee tank who slows enemies with Frost. New in this patch.";
    public static readonly IReadOnlyList<string> PromoArtKeys = ["champion-loading-113", "skin-loading-113001", "skin-loading-113002"];

    // The New Skins promo: the four newest skins of the store (Reconstruction wording; the skins are the list below).
    public const string SkinsTitle = "New Skins";
    public const string SkinsSubtitle = "Now in the store";
    public const string SkinsBody = "Sabretusk Sejuani, Darkrider Sejuani, Full Machine Viktor and Prototype Viktor skins are here!";
    public static readonly IReadOnlyList<string> SkinsArtKeys = ["skin-loading-113001", "skin-loading-113002", "skin-loading-112001"];

    /// <summary>
    /// What's New, by store date, newest first; the first one is the Featured Item. Every entry is priced in this line's price list and
    /// has its art in the 1.52 client.
    /// 17 January 2012 (wiki V1.0.0.132: "Release Date (US)" January 17, 2012; the Sejuani page: 17.01.2012): Sejuani (113), Sabretusk
    /// Sejuani and Darkrider Sejuani, "released along with this patch".
    /// 10 January 2012 (V1.0.0.132, "New Skins in the Store": released on Monday, January 10): Dragon Fist Lee Sin (64003), Guqin Sona
    /// (37004), Dragonblade Talon (91003) and Jade Dragon Wukong (62003).
    /// 2 January 2012 (V1.0.0.131, "New Skins in the Store": not available for purchase until Monday, January 2): Sandstorm Katarina
    /// (55006) and Superb Villain Veigar (45006).
    /// 29 December 2011 (V1.0.0.131: "New Champions: Viktor (December 29th)" and "not available for purchase until Thursday, December 29"):
    /// Viktor (112), Full Machine Viktor and Prototype Viktor.
    /// The skins' prices are the ones their wiki skin pages print (Reconstruction: the January prices were not read; prices.json).
    /// </summary>
    public static readonly IReadOnlyList<string> NewItemIds =
    [
        "champion:113", "skin:113001", "skin:113002",
        "skin:64003", "skin:37004", "skin:91003", "skin:62003",
        "skin:55006", "skin:45006",
        "champion:112", "skin:112001", "skin:112002",
    ];

    /// <summary>
    /// Top Sellers. Reconstruction: the server has no sales data behind it, and the sources name no sale in January 2012. Like the 2011 list
    /// (the newest champion with its skins, the newest skins and the previous champion), it lists the January champion and her two skins,
    /// then the previous champion, Viktor (29 December 2011), and his two skins.
    /// </summary>
    public static readonly IReadOnlyList<string> TopSellerIds =
        ["champion:113", "skin:113001", "skin:113002", "champion:112", "skin:112001", "skin:112002"];
}
