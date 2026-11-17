using static Amberline.Tests.BbM3;
using static Amberline.Tests.BbP132Pins;

namespace Amberline.Tests;

/// <summary>Batch 840 (17 November 2026): the 1.0.0.132 Karthus row (130.10), read from the shipped scripts and driven through the match.</summary>
public static class BbP132KarthusChecks
{
    // 130.10: Wall of Pain hits each target once. The 132 beam skips a unit that already carries the wall's slow (BuffNameFilter), and it
    // marks each unit it has hit (WallOfPainMarker, BBIfHasBuff); 126 guards with BBIfNotHasBuff and has no marker, so these pins are 132-only.
    // The driven part: the foe carries one armour and resistance debuff application (its stack count stays at one for four seconds).
    [Check("scripting.p132-karthus-wall-of-pain-hits-each-target-once")]
    public static void WallOfPainHitsEachTargetOnce()
    {
        // The shipped wall script (WallOfPain.luaobj) holds the loop; it adds the marker as "WallofPainMarker" and tests "WallOfPainMarker".
        var wall = Script("WallOfPain");
        TestAssert.True(Calls(wall, "BBForEachUnitInTargetArea").Any(block => Named(block, "BuffNameFilter") == "WallofPainTarget"),
            "the wall's loop skips a unit that already carries the slow (shipped WallOfPain, 132)");
        TestAssert.True(Calls(wall, "BBIfHasBuff").Any(block => Named(block, "BuffName") == "WallOfPainMarker"),
            "a unit that has the marker is not hit again (shipped WallOfPain, 132)");
        using var match = new Match("Karthus", foeChampion: "Orianna");
        match.Learn(1);
        match.Refill();
        TestAssert.True(match.Cast(1, at: match.Foe.Position), "W casts the wall across the foe");
        match.Advance(1.0);
        TestAssert.True(Buff(match.Foe, "WallofPainExtra") is not null, "the foe carries the wall's debuff; buffs: " + Names(match.Foe));
        TestAssert.True(Buff(match.Foe, "WallofPainMarker") is not null, "the foe carries the wall's hit marker; buffs: " + Names(match.Foe));
        TestAssert.Equal(1, Buff(match.Foe, "WallofPainExtra")?.Stacks ?? -1, "one application after one second");
        match.Advance(3.0);
        TestAssert.Equal(1, Buff(match.Foe, "WallofPainExtra")?.Stacks ?? -1, "still one application after four seconds in the wall");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }
}
