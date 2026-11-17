using static Amberline.Tests.BbM3;
using static Amberline.Tests.BbP132Pins;

namespace Amberline.Tests;

/// <summary>
/// Batch 840 (17 November 2026): the 1.0.0.132 Viktor rule of the Gravity Field stun (131.05, the per-rule check for Viktor). The shipped
/// debuff adds the stun when its third stack lands (both of its branches add it; the W augment changes only the other stack handling).
/// </summary>
public static class BbP132ViktorChecks
{
    // 131.05 (per-rule): Gravity Field's Crushing Gravity stun is added when the field's debuff reaches three stacks.
    [Check("scripting.p132-viktor-gravity-field-stuns-at-three-stacks")]
    public static void GravityFieldStunsAtThreeStacks()
    {
        var debuff = Script("ViktorGravitonFieldDebuff");
        TestAssert.True(Calls(debuff, "BBIf").Any(block => Named(block, "Src1Var") == "Count" && Is(Number(block, "Value2"), 3)),
            "the stun is checked at three stacks of the field's debuff (shipped ViktorGravitonFieldDebuff)");
        TestAssert.True(Calls(debuff, "BBSpellBuffAdd").Any(block => Named(block, "BuffName") == "ViktorGravitonFieldStun" && Named(block, "BuffType") == "BUFF_Stun"),
            "the stun is the ViktorGravitonFieldStun buff (shipped ViktorGravitonFieldDebuff)");
        using var match = new Match("Viktor", foeChampion: "Orianna");
        match.Learn(1);
        match.Refill();
        TestAssert.True(match.Cast(1, at: match.Foe.Position), "W is cast on the foe");
        double? stunned = null;
        for (var waited = 0.0; stunned is null && waited < 6.0; waited += Step)
        {
            match.Advance(Step);
            if (Buff(match.Foe, "ViktorGravitonFieldStun") is not null) stunned = match.Now;
        }
        TestAssert.True(stunned is not null, "the foe is stunned within six seconds in the field; buffs: " + Names(match.Foe));
        TestAssert.Equal(0, match.Module.ScriptFailures.Count, "no script failed");
    }
}
