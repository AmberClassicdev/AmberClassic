using static Amberline.Tests.BbM3;
using static Amberline.Tests.BbP132Pins;

namespace Amberline.Tests;

/// <summary>Batch 842 (17 November 2026): 1.0.0.132 Shaco's Hallucinate clone and Two-Shiv Poison (change list 128.16). Pins on the shipped Backstab.</summary>
public static class BbB842ShacoChecks
{
    // 128.16: the clone's hit triggers Two-Shiv Poison. Backstab's on-hit (Spells/Backstab.luaobj) finds the Shaco champion by skin name,
    // reads his Two-Shiv rank from slot 2 and makes him the poison's attacker. The 126 copy reads the clone's own slot and makes the
    // clone the attacker, so these pins fail on 126.
    [Check("scripting.p132-shaco-backstab-takes-two-shiv-from-the-champion-so-the-clone-triggers-it")]
    public static void CloneTriggersTwoShiv()
    {
        var backstab = Script("Backstab");
        TestAssert.True(Blocks(backstab, "BuffOnHitUnit").Any(block => block.Function == "BBGetChampionBySkinName"
            && Named(block, "Skin") == "Shaco" && Named(block, "DestVar") == "Attacker"),
            "the on-hit finds the Shaco champion by skin and names him Attacker (shipped Backstab BuffOnHitUnit)");
        TestAssert.True(Blocks(backstab, "BuffOnHitUnit").Any(block => block.Function == "BBGetSlotSpellInfo"
            && Named(block, "OwnerVar") == "Attacker" && Is(Number(block, "SpellSlotValue"), 2)),
            "the Two-Shiv rank is read from the champion's slot 2");
        TestAssert.True(Blocks(backstab, "BuffOnHitUnit").Any(block => block.Function == "BBSpellBuffAdd"
            && Named(block, "BuffName") == "TwoShivPoison" && Named(block, "AttackerVar") == "Attacker"),
            "the poison's attacker is the champion");
    }
}
