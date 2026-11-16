namespace Amberline.Core;

/// <summary>
/// The 1.0.0.132 line's maps (batch 822): the shared ones and Summoner's Rift (Winter), LEVELS/Map6, which only the 132 data ships (owner
/// decision, 16 November 2026: "Add it", offered whatever the date). The 1.0.0.126 line keeps its own copy at this path.
/// </summary>
public static partial class GameMaps
{
    /// <summary>
    /// The maps this line loads and offers in custom games beyond the shared ones (PlatformStoreCalendar.OpenMapIds): the winter rift. NOT
    /// RETAIL: offered at all times (owner decision, 16 November 2026); retail showed it during its winter event.
    /// </summary>
    public static IReadOnlyList<int> LineMaps { get; } = [Winter];

    /// <summary>The ids <see cref="IsSupported"/> accepts, for the refusal messages of every <c>--map-id</c> gate.</summary>
    public const string SupportedText =
        "1 (Summoner's Rift), 2 (the Harrowing rift), 3 (the Basic Tutorial), 4 (Twisted Treeline), 6 (the winter rift) or 8 (The Crystal Scar)";

    /// <summary>
    /// Reconstruction: the lane minions of the winter rift wear MeshSkin2. Each lane minion character the LevelScript spawns
    /// (<c>{Blue,Red}_Minion_{Basic,Wizard,MechCannon,MechMelee}</c>) names <c>*_Snowdown.skn</c> as its <c>MeshSkin2</c> (MeshSkin1 is the
    /// Harrowing's, <see cref="HarrowingMinionSkinId"/>), and no shipped file selects a skin by map, so the server's minion SkinId carries it.
    /// </summary>
    public const int WinterMinionSkinId = 2;
}
