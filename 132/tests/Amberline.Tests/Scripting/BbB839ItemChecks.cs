using Amberline.Core;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 839 (17 November 2026): the 1.0.0.132 item row of patch 1.0.0.127 (change list 127.21: Hextech Gunblade's spell vamp is Unique).
/// Items/3146 keeps one <c>HextechGunblade</c> buff (replace-existing), and that buff carries the spell vamp (Spells/HextechGunblade.luaobj
/// <c>BuffOnUpdateStats</c>: <c>IncPercentSpellVampMod</c> 0.2). A second Gunblade therefore adds no second 20 percent.
/// </summary>
public static class BbB839ItemChecks
{
    private static double OneGunbladeVamp()
    {
        // The shipped stat literal (Spells/HextechGunblade.luaobj BuffOnUpdateStats, IncPercentSpellVampMod Delta).
        return 0.2;
    }

    [Check("scripting.p132-hextech-gunblade-spell-vamp-does-not-stack-with-a-second-gunblade")]
    public static void GunbladeSpellVampIsUnique()
    {
        using var one = new Match("Annie");
        var oneHero = one.Hero;
        oneHero.Inventory = [new(3146), null, null, null, null, null];
        one.Runtime.HeroStats.Refresh(oneHero);
        // Items/3146's buff is set by its UpdateSelfBuffActions every nine seconds (ExecuteImmediately=false): wait one period.
        one.Advance(9.5);
        TestAssert.Equal(1, oneHero.Buffs.Count(buff => buff.Name == "HextechGunblade"), "one Gunblade carries its buff (positive control)");
        TestAssert.Near(OneGunbladeVamp(), oneHero.SpellVamp, 0.001, "one Gunblade gives the 20 percent spell vamp");

        using var two = new Match("Annie");
        var twoHero = two.Hero;
        twoHero.Inventory = [new(3146), new(3146), null, null, null, null];
        two.Runtime.HeroStats.Refresh(twoHero);
        two.Advance(9.5);
        TestAssert.Equal(1, twoHero.Buffs.Count(buff => buff.Name == "HextechGunblade"), "two Gunblades keep one HextechGunblade buff (replace-existing)");
        TestAssert.Near(OneGunbladeVamp(), twoHero.SpellVamp, 0.001, "and the spell vamp stays 20 percent, not 40 (Unique)");
    }
}
