using Amberline.Core;
using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// The block natives the 1.0.0.132 scripts call that no 1.0.0.126 script did (the patch-132 script measurement, f-scripts "Missing block functions"):
/// the jungle monsters' growing rewards (V1.0.0.130: neutral monsters scale their experience and gold with game length), Graves' and Sejuani's
/// buff renewals, the new champions' slot icons, Twisted Fate's and Sona's acquisition switch, Viktor's starting item, Volibear's chain and his
/// Frenzy's castable bit. Each is bound from its body in the 1.0.0.132 client (lol132.exe), the
/// parameters from the shipped call sites.
/// </summary>
internal sealed partial class BbNativeHandlers
{
    private static readonly HashSet<string> Patch132Bound = new(StringComparer.Ordinal)
    {
        "BBIncPermanentExpReward", "BBIncPermanentGoldReward", "BBSetSlotSpellIcon", "BBSpellBuffRenew", "BBSetAutoAcquireTargets",
        "BBCreateItem", "BBForEachVisibleUnitInTargetAreaRandom", "BBUpdateCanCast",
    };

    private bool ExecutePatch132(BbCall call)
    {
        switch (call.Function)
        {
            case "BBIncPermanentExpReward": IncPermanentReward(call, gold: false); return true;
            case "BBIncPermanentGoldReward": IncPermanentReward(call, gold: true); return true;
            case "BBSetSlotSpellIcon": SetSlotSpellIcon(call); return true;
            case "BBSpellBuffRenew":
                _host.Buffs.LandHeldAdds();
                if (Unit(call, "TargetVar") is { } renewed && call.Value("BuffName") is string buff)
                    _host.Buffs.Renew(renewed, buff, Sum(call, "ResetDuration"));
                return true;
            case "BBSetAutoAcquireTargets": SetAutoAcquireTargets(call); return true;
            case "BBCreateItem": CreateItem(call); return true;
            // The Visible thunk pushes 0 to the shared random body the client, where the plain one pushes
            // 0x400000, IgnoreVisibilityCheck: the same pair as the other Visible variants (see ForEachUnitInTargetArea).
            case "BBForEachVisibleUnitInTargetAreaRandom": ForEachUnitInTargetArea(call, random: true, visibleOnly: true); return true;
            case "BBUpdateCanCast": UpdateCanCast(call); return true;
            default: return false;
        }
    }

    /// <summary>
    /// <c>BBIncPermanentExpReward</c> and <c>BBIncPermanentGoldReward</c> (the stock client's two routines): the unit parameter and
    /// <c>Delta</c> (the shared number getter); the body adds the delta to the unit's experience reward or gold reward,
    /// the amounts its death pays. Shipped callers: the camp monsters' character scripts (CharOnActivate: GiantWolf +14 XP +6 gold, Golem, SmallGolem,
    /// LesserWraith, Wraith, wolf), <c>GlobalMonsterBuff</c> (the reward of the monster's life so far at its activation, then expPerMinute and
    /// goldPerMinute every minute at full health) and <c>MonsterBankSmall</c>. A bare call in a stats sample follows the buff's mutation clock, like
    /// every permanent (<see cref="BbHookFrame.MutationSuppressed"/>). The gold is what the killer is paid (<c>CombatService</c>, the unit's
    /// <see cref="Entity.GoldReward"/>); the experience is <see cref="Entity.ExperienceReward"/>, which the 1.0.0.132 neutral death award reads.
    /// </summary>
    private void IncPermanentReward(BbCall call, bool gold)
    {
        if (_host.HasFrame && _host.Frame.MutationSuppressed) return;
        if (Unit(call, "TargetVar") is not { } unit) return;
        var delta = Sum(call, "Delta");
        if (gold) unit.GoldReward += delta;
        else unit.ExperienceReward += delta;
    }

    /// <summary>
    /// <c>BBSetSlotSpellIcon</c> (in the 126 client too, where no script called it): <c>Owner</c>, the slot
    /// parser's <c>SlotType</c>/<c>SlotNumber</c> (<see cref="StoredSlot"/>), <c>SpellbookType</c> (1: the summoner book) and <c>IconIndex</c> (the
    /// integer getter). The body stores <c>IconIndex - 1</c> as the slot's icon byte and sends <c>ChangeSlotSpellIcon</c> 0x36
    /// with it, owner-client only (<c>ReplicationDelivery.IsOwnerClientOnly</c>). Pure presentation: the server keeps the byte for the reconnect burst
    /// (<see cref="Entity.SpellSlotIcons"/>). Shipped: CharScriptFiddlesticks (skin 6, slots 2 and 3), FizzSeastonePassive (W while it lasts),
    /// ViktorAugmentQ and ViktorAugmentE.
    /// </summary>
    private void SetSlotSpellIcon(BbCall call)
    {
        if (Unit(call, "OwnerVar") is not { } unit) return;
        var slot = StoredSlot("BBSetSlotSpellIcon", SlotTypeOf(call, "BBSetSlotSpellIcon", required: false), (int)Sum(call, "SlotNumber"));
        var summoner = SummonerBook(call, "SpellbookType");
        var icon = unchecked((byte)((int)Sum(call, "IconIndex") - 1));
        unit.SpellSlotIcons[(byte)(slot | (summoner ? 0x80 : 0))] = icon;
        if (Spells.Effects.IsReferenceReady(unit)) Spells.Effects.SendSlotSpellIcon(unit, slot, icon, summoner);
    }

    /// <summary>
    /// <c>BBSetAutoAcquireTargets</c> (new in 1.0.0.132, a stock client routine): <c>Target</c> and <c>Value</c> (the boolean getter); the body
    /// writes the value into one byte of the unit. Reconstruction: that byte is read here as the hero's <c>mbCanAutoAcquireTargets</c>
    /// (the field name the stock PDB lists for the hero, and the switch the level scripts' <c>SetCanAutoAcquireTargets</c> sets), so it gates the idle
    /// acquisition scan (<see cref="Entity.CanAutoAcquireTargets"/>, <c>HeroLuaAiEngine.Acquire</c>). Twisted Fate holds a locked card without
    /// spending it on whatever walks by (PickACard: false when a card locks, true when the buff ends), and Sona's Power Chord the same way.
    /// </summary>
    private static void SetAutoAcquireTargets(BbCall call)
    {
        if (Unit(call, "TargetVar") is not { } unit || call.Value("Value") is not { } value) return;
        unit.CanAutoAcquireTargets = BbRuntime.Truthy(value);
    }

    /// <summary>
    /// <c>BBCreateItem</c>: <c>UnitVar</c> and <c>ItemID</c>. The client registers it as a bare return, so
    /// the grant is the server's: Reconstruction, the free grant the tutorial's <c>CreateItem</c> already uses (<c>ShopService.Grant</c>: the item
    /// takes the slot a purchase would, the gold is unchanged, the client learns it from the purchase's 0x72). The one 1.0.0.132 champion call is
    /// CharScriptViktor's CharOnActivate, the Prototype Hex Core (3200) every Viktor starts with. A refused grant (a full inventory) is a script error.
    /// </summary>
    private void CreateItem(BbCall call)
    {
        if (Unit(call, "UnitVar") is not { } unit) return;
        var grant = Spells.GrantItem ?? throw BbSpellHost.Unbound("BBCreateItem in a match without a shop", "b769");
        var item = (int)Sum(call, "ItemID");
        if (grant(unit, item) is { } refusal) throw new BbRuntimeException($"BBCreateItem {item}: {refusal}");
    }

    /// <summary>
    /// <c>BBUpdateCanCast</c> (new in 1.0.0.132): the unit parameter; for a hero the body
    /// calls the hero's can-cast update (virtual slot 0x2B4), which re-runs the spells' <c>CanCast</c> gates into the replicated castable bits.
    /// VolibearW calls it on each hit (BuffOnHitUnit) so his Frenzy lights up at the third stack instead of at the next stat update. This engine
    /// evaluates the gates when it builds the hero's stat block (<c>HeroStatsServices.CastGateOpen</c>), so the update is that block, pushed now.
    /// </summary>
    private void UpdateCanCast(BbCall call)
    {
        if (Unit(call, "TargetVar") is { UnitType: UnitType.Hero } hero) Spells.SendChampionStats(hero);
    }
}
