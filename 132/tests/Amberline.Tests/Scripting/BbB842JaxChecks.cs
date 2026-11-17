using static Amberline.Tests.BbM3;
using static Amberline.Tests.BbP132Pins;

namespace Amberline.Tests;

/// <summary>
/// Batch 842 (17 November 2026): 1.0.0.132 Jax, the active of Grandmaster's Might (change list 132.28; the wiki's V1.0.0.132 page).
/// The third-hit magic is already pinned by the driven check scripting.bb-jax-relentless-assault-hits-the-third-time-for-bonus-magic-damage
/// (its name is stale: the ultimate is shipped as JaxRelentlessAssault). This file pins the active the ultimate adds.
/// </summary>
public static class BbB842JaxChecks
{
    // 132.28: the active lasts six seconds and adds 25/45/65 AD and AP by the ultimate's rank, plus 20 percent of the bonus AD and AP.
    // The buff's shipped name is RelentlessBarrier. On 126 the ultimate has no JaxRelentlessAssault script, so this fails there too.
    [Check("scripting.p132-jax-grandmasters-might-active-adds-25-45-65-ad-and-ap-for-six-seconds")]
    public static void ActiveAddsAdAndApByRank()
    {
        var speed = Script("JaxRelentlessAssaultSpeed");
        TestAssert.True(LevelsAre(Sets(speed, "BonusADAP").Single(), "SrcValueByLevel", 25, 45, 65),
            "BonusADAP is 25/45/65 by the ultimate's rank (shipped JaxRelentlessAssaultSpeed)");
        TestAssert.True(LevelsAre(Sets(speed, "Multiplier").Single(), "SrcValueByLevel", 0.2, 0.2, 0.2),
            "the bonus AD and AP scale 20 percent (shipped JaxRelentlessAssaultSpeed)");
        TestAssert.True(Calls(speed, "BBIncStat").Any(block => Named(block, "Stat") == "IncFlatPhysicalDamageMod")
            && Calls(speed, "BBIncStat").Any(block => Named(block, "Stat") == "IncFlatMagicDamageMod"),
            "the buff adds flat AD and flat AP (IncFlatPhysicalDamageMod, IncFlatMagicDamageMod)");
        // The add reads its duration from the Duration variable (SelfExecute sets it to 6 at every rank), not from a literal.
        var ultimate = Script("JaxRelentlessAssault");
        TestAssert.True(Sets(ultimate, "Duration").Any(block => LevelsAre(block, "SrcValueByLevel", 6, 6, 6)),
            "the buff lasts six seconds at every rank (shipped JaxRelentlessAssault SelfExecute)");
        TestAssert.True(Calls(ultimate, "BBSpellBuffAdd").Any(block => Named(block, "BuffName") == "JaxRelentlessAssaultSpeed"),
            "the ultimate adds the buff (shipped JaxRelentlessAssault)");
    }
}
