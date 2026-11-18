namespace Amberline.Data;

/// <summary>The release line's part of <see cref="RuneCatalog"/> (batch 861; the 1.0.0.126 line keeps its own copy of this file).</summary>
public sealed partial class RuneCatalog
{
    /// <summary>
    /// The rune stats this line no longer sells: a rune carrying one is retired (<see cref="IsRetired"/>). Patch 1.0.0.132 (17 January 2012)
    /// removed dodge (wiki V1.0.0.132: "Dodge global stat removal."), and with it the dodge runes: their owners got the IP they spent back and
    /// lost the runes (wiki Dodge: "Players were refunded the spent IP when they were removed in V1.0.0.132."; Riot, 24 November 2011, quoted
    /// by SurrenderAt20: "removing these runes from your runebooks"). The shipped 132 records (DATA/Items 5019 ... 5354, fontconfig
    /// "+0.21% dodge") and AIR 1.52's RuneGeneratedData still carry them; the store and the inventory come from the server, which drops them.
    /// </summary>
    private static readonly HashSet<string> RetiredStats = new(StringComparer.Ordinal) { "rFlatDodgeMod", "rFlatDodgeModPerLevel" };
}
