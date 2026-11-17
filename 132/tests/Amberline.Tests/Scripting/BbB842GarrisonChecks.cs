using static Amberline.Tests.BbM3;
using static Amberline.Tests.BbP132Pins;

namespace Amberline.Tests;

/// <summary>Batch 842 (17 November 2026): 1.0.0.132 Garrison placement (change list 128.25). Pin on the shipped spell's cast gate.</summary>
public static class BbB842GarrisonChecks
{
    // 128.25: the 132 Garrison has a cast gate: units with OdinGuardianBuff (AffectUseable) within 1250 of the caster.
    // 126 has no CanCast hook, so the check fails on 126. The untargetable part is not pinned (see the record).
    [Check("scripting.p132-garrison-can-be-cast-only-near-an-eligible-point")]
    public static void CastNeedsAnEligiblePoint()
    {
        var garrison = Script("SummonerOdinGarrison");
        TestAssert.True(garrison.Events.Any(hook => hook.Hook == "CanCast"), "the 132 Garrison has the CanCast gate (126 has none)");
        TestAssert.True(Blocks(garrison, "CanCast").Any(block => block.Function == "BBForEachUnitInTargetArea" && Is(Number(block, "Range"), 1250)
            && Named(block, "Flags").Contains("AffectUseable") && Named(block, "BuffNameFilter") == "OdinGuardianBuff"),
            "the gate scans within 1250 of the caster for AffectUseable units that carry OdinGuardianBuff");
    }
}
