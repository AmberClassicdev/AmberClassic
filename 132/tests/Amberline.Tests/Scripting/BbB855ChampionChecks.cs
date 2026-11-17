using static Amberline.Tests.BbM3;
using static Amberline.Tests.BbP132Pins;

namespace Amberline.Tests;

/// <summary>
/// Batch 855 (17 November 2026): one 1.0.0.132 champion row of the parity audit that no check pinned (130.03, Volibear's fling). The
/// shipped scripts carry it: the Q's next basic attack adds the VolibearQExtra stun, and that buff disables movement, attack and casting.
/// </summary>
public static class BbB855ChampionChecks
{
    // 130.03 (fling): the Q's next basic attack flings an AI target with a stun, and the knock-up disables its moves, attacks and casts.
    [Check("scripting.p132-volibear-q-attack-flings-with-a-stun-that-disables-move-attack-and-cast")]
    public static void VolibearQFling()
    {
        var attack = Script("VolibearQAttack");
        TestAssert.True(Calls(attack, "BBSpellBuffAdd").Any(block => Named(block, "BuffName") == "VolibearQExtra" && Named(block, "BuffType") == "BUFF_Stun"),
            "the Q's next attack adds the VolibearQExtra stun (shipped VolibearQAttack; change list 130.03)");
        var extra = Script("VolibearQExtra");
        foreach (var status in new[] { "SetCanMove", "SetCanAttack", "SetCanCast" })
        {
            TestAssert.True(Calls(extra, "BBSetStatus").Any(block => Named(block, "Status") == status && !Flag(block, "SrcValue")),
                $"the fling sets {status} off while it lasts (shipped VolibearQExtra)");
        }
    }
}
