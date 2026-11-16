namespace Amberline.Core;

/// <summary>
/// The 1.0.0.126 line's maps (batch 822): only the shared ones. Its game data has no LEVELS/Map6, so <see cref="Winter"/> is neither
/// loaded nor offered here. The 1.0.0.132 line keeps its own copy at this path.
/// </summary>
public static partial class GameMaps
{
    /// <summary>The maps this line loads and offers in custom games beyond the shared ones: none.</summary>
    public static IReadOnlyList<int> LineMaps { get; } = [];

    /// <summary>The ids <see cref="IsSupported"/> accepts, for the refusal messages of every <c>--map-id</c> gate.</summary>
    public const string SupportedText =
        "1 (Summoner's Rift), 2 (the Harrowing rift), 3 (the Basic Tutorial), 4 (Twisted Treeline) or 8 (The Crystal Scar)";
}
