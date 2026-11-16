using Amberline.Simulation.Spells.Bb;

namespace Amberline.Simulation.Spells;

public sealed partial class SpellSystem
{
    /// <summary>
    /// The pet services the BuildingBlocks spawn natives use (set by <c>ProductionMatchComposition</c> after the spell system is built);
    /// null in a graph that has none, where those natives raise an "unbound" error instead of spawning a parallel copy of a pet.
    /// </summary>
    internal BbPetServices? ScriptPets { get; set; }

    /// <summary>
    /// A <c>BBIssueOrder</c> to a pet: the pet AI host's <c>PetAiService.ScriptOrder</c> (set by <c>ProductionMatchComposition</c>), which runs
    /// the order in the pet's own script. Null in a graph without the pet host, where the order raises "unbound".
    /// </summary>
    internal Func<Core.Entity, int, Core.Position2, Core.Entity?, bool>? PetOrder { get; set; }

    /// <summary>
    /// The free item grant <c>BBCreateItem</c> calls (set by <c>ProductionMatchComposition</c> to <c>ShopService.Grant</c>, the tutorial's
    /// <c>CreateItem</c> grant): null when the item was placed, else the refusal. Null in a graph with no shop, where the native raises "unbound".
    /// </summary>
    internal Func<Core.Entity, int, string?>? GrantItem { get; set; }
}
