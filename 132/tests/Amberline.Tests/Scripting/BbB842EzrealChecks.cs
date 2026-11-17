using static Amberline.Tests.BbM3;
using static Amberline.Tests.BbP132Pins;

namespace Amberline.Tests;

/// <summary>Batch 842 (17 November 2026): 1.0.0.132 Ezreal Essence Flux (change list 129.11, the area-effect flag). Pin on the shipped missile.</summary>
public static class BbB842EzrealChecks
{
    // 129.11: the missile's damage is area-effect spell damage on 132 (DAMAGESOURCE_SPELLAOE). The 126 script says DAMAGESOURCE_SPELL.
    [Check("scripting.p132-ezreal-essence-flux-missile-deals-area-spell-damage")]
    public static void EssenceFluxMissileIsAreaSpellDamage()
    {
        TestAssert.True(Calls(Script("EzrealEssenceFluxMissile"), "BBApplyDamage").Any(block => Named(block, "SourceDamageType") == "DAMAGESOURCE_SPELLAOE"),
            "the missile's damage is DAMAGESOURCE_SPELLAOE (shipped EzrealEssenceFluxMissile; 126 has DAMAGESOURCE_SPELL)");
    }
}
