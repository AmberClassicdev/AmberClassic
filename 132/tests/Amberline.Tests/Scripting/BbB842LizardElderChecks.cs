using static Amberline.Tests.BbM3;
using static Amberline.Tests.BbP132Pins;

namespace Amberline.Tests;

/// <summary>Batch 842 (17 November 2026): 1.0.0.132 Lizard Elder (change list 132.55: no longer on spell damage). Pin on the shipped buff.</summary>
public static class BbB842LizardElderChecks
{
    // 132.55: the burn runs only on the basic attack. The 132 hook (BuffOnPreDealDamage) tests the damage source against
    // DAMAGESOURCE_ATTACK. 126 applied its burn in BuffOnHitUnit with no damage-source test, so this check fails on 126.
    [Check("scripting.p132-lizard-elder-blessing-burns-on-basic-attacks-only")]
    public static void BurnOnBasicAttacksOnly()
    {
        var blessing = Script("BlessingoftheLizardElder");
        TestAssert.True(blessing.Events.Any(hook => hook.Hook == "BuffOnPreDealDamage"), "the rule sits in BuffOnPreDealDamage (126: BuffOnHitUnit)");
        TestAssert.True(Blocks(blessing, "BuffOnPreDealDamage").Any(block => block.Function == "BBIf" && Named(block, "Value1") == "DAMAGESOURCE_ATTACK"
            && Named(block, "CompareOp") == "CO_DAMAGE_SOURCETYPE_IS"), "the hook tests that the damage source is an attack (shipped BlessingoftheLizardElder)");
    }
}
