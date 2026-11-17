using static Amberline.Tests.BbM3;
using static Amberline.Tests.BbP132Pins;

namespace Amberline.Tests;

/// <summary>Batch 840 (17 November 2026): the 1.0.0.132 Fizz row (130.04), shipped values read by the engine's loader.</summary>
public static class BbP132FizzChecks
{
    // 130.04: Urchin Strike's damage has an undodgeable part (BBSetDodgePiercing on the physical hit).
    [Check("scripting.p132-fizz-urchin-strike-physical-part-ignores-dodge")]
    public static void UrchinStrikePhysicalPartIgnoresDodge()
    {
        var strike = Script("FizzPiercingStrike");
        TestAssert.True(Calls(strike, "BBSetDodgePiercing").Any(block => Flag(block, "Value")),
            "the strike turns dodge-piercing on for its damage (shipped FizzPiercingStrike)");
        TestAssert.True(Calls(strike, "BBApplyDamage").Any(block => Named(block, "DamageType") == "PHYSICAL_DAMAGE"),
            "the piercing hit is physical damage (shipped FizzPiercingStrike)");
    }
}
