using Amberline.Simulation.Buffs;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 834 (17 November 2026): 1.0.0.132 champion rows of the audit's batch B that the existing champion checks did not measure at their exact
/// value. Each drives the champion through the production script engine (<see cref="BbM3.Match"/>).
/// </summary>
public static class BbP132ChampionPinChecks
{
    // 132.47: Xerath's Mage Chains stun lasts 1.5 seconds (126: two). The existing checks bound the stun between one and 2.6 seconds; this one
    // measures it from the moment the root buff appears (to within one step).
    [Check("scripting.p132-xerath-mage-chains-stun-lasts-one-and-a-half-seconds")]
    public static void MageChainsStunLastsOneAndAHalfSeconds()
    {
        using var match = new Match("Xerath", foeChampion: "Orianna");
        match.Learn(1);
        match.Refill();
        var foe = match.Foe;
        TestAssert.True(match.Cast(2, foe), "E marks the foe");
        match.Advance(1.0);
        TestAssert.True(Buff(foe, "XerathMageChains") is not null, "the foe carries the mark; buffs: " + Names(foe));
        match.Refill();
        TestAssert.True(match.Cast(0, at: foe.Position), "Q is cast at the marked foe");
        // Presence is read every step: the root's first and last observations bound its life (one step of error each side).
        double? on = null, off = null;
        for (var waited = 0.0; off is null && waited < 3.0; waited += Step)
        {
            match.Advance(Step);
            var present = Buff(foe, "XerathMageChainsRoot") is not null;
            if (present && on is null)
            {
                on = match.Now;
                TestAssert.False(CharacterState.Of(foe).CanMove, "the stun holds him in place while the root is on");
            }
            else if (!present && on is not null) off = match.Now;
        }
        TestAssert.True(on is not null && off is not null, "the next damaging spell stuns the foe and the stun ends; buffs: " + Names(foe));
        TestAssert.Near(1.5, off!.Value - on!.Value, 0.1, "the stun lasts 1.5 seconds on 1.0.0.132 (126: 2; change list 132.47)");
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }
}
