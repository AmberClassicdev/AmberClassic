using Amberline.Scripting.BuildingBlocks;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>Batch 842 (17 November 2026): 1.0.0.132 Odyn's Veil (change list 129.30). Pin on the shipped spell.</summary>
public static class BbB842OdynsVeilChecks
{
    // 129.30: the Veil's cast does not trigger Sheen, Tear or Arcane Mastery. On 132 the spell is flagged DoesntTriggerSpellCasts;
    // the 126 spell carries no such flag (the flag is absent there, so the check fails on 126).
    [Check("scripting.p132-odyns-veil-cast-does-not-trigger-spell-cast-items")]
    public static void VeilCastTriggersNoSpellCastItems()
    {
        TestAssert.True(Script("OdynsVeil").Globals.Any(pair => pair.Key == "DoesntTriggerSpellCasts" && pair.Value is BbValue.Bool flag && flag.Value),
            "the Veil spell is DoesntTriggerSpellCasts (shipped OdynsVeil; 126 has no such flag)");
    }
}
