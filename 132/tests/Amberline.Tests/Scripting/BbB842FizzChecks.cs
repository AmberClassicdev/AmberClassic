using static Amberline.Tests.BbM3;
using static Amberline.Tests.BbP132Pins;

namespace Amberline.Tests;

/// <summary>
/// Batch 842 (17 November 2026): the 1.0.0.132 Fizz rules of change list 129.04 (the six rules) and the Chum the Waters countdown of
/// 132.18. Fizz is new in 129, so 126 has no Fizz scripts and the "fails on 126" test does not apply. The ghosted rule is driven
/// (the buff's status reaches the hero); the others are pins of the shipped values.
/// </summary>
public static class BbB842FizzChecks
{
    // 129.04: Fizz ignores unit collision (the passive sets SetGhosted on its owner). Driven: the status reaches the hero.
    [Check("scripting.p132-fizz-passive-ghosts-her-so-units-do-not-collide")]
    public static void PassiveGhostsHer()
    {
        using var match = new Match("Fizz");
        var hero = match.Hero;
        TestAssert.True(!match.Ally.AnyBuffOrStatus(buff => buff.Payload.Ghosted), "control: an Annie ally is not ghosted");
        TestAssert.True(Buff(hero, "FizzPassive") is not null, "Fizz carries her passive from spawn; buffs: " + Names(hero));
        TestAssert.True(hero.AnyBuffOrStatus(buff => buff.Payload.Ghosted), "the passive ghosts Fizz; buffs: " + Names(hero));
        TestAssert.Equal("", string.Join(" | ", match.Module.ScriptFailures.Select(failure => failure.Message)), "no script failure");
    }

    // 129.04: reduced damage taken. The passive subtracts Defense from every physical hit; the table is per level.
    [Check("scripting.p132-fizz-passive-takes-four-to-sixteen-off-each-physical-hit-by-level")]
    public static void PassiveCutsPhysicalDamage()
    {
        var passive = Script("FizzPassive");
        var levels = Sets(passive, "Defense").Where(block => HasLevels(block, "SrcValueByLevel")).ToList();
        TestAssert.Equal(1, levels.Count, "one per-level Defense table (BuffOnLevelUp)");
        TestAssert.True(LevelsAre(levels[0], "SrcValueByLevel", 4, 4, 4, 6, 6, 6, 8, 8, 8, 10, 10, 10, 12, 12, 12, 14, 14, 14, 16),
            "Defense by level 4 to 16 (shipped FizzPassive)");
        TestAssert.True(Calls(passive, "BBMath").Any(block => Named(block, "DestVar") == "DamageAmount" && Named(block, "Src2Var") == "Defense"
            && Named(block, "MathOp") == "MO_SUBTRACT"), "the hit's damage has Defense subtracted (BuffOnBeingHit)");
        TestAssert.True(Calls(passive, "BBIf").Any(block => Named(block, "Src1Var") == "DamageType" && Named(block, "Value2") == "PHYSICAL_DAMAGE"),
            "only physical damage is reduced (basic attacks are physical)");
    }

    // 129.04: rend. The passive's hit adds the trident buff (FizzMalison), which ticks magic damage from missing health for 3.1 s.
    [Check("scripting.p132-fizz-rend-ticks-magic-damage-from-missing-health-for-three-seconds")]
    public static void RendTicksOnMissingHealth()
    {
        var trident = Script("FizzSeastoneTrident");
        TestAssert.True(LevelsAre(Sets(trident, "BaseMagic").Single(), "SrcValueByLevel", 30, 40, 50, 60, 70),
            "base magic 30 to 70 (shipped FizzSeastoneTrident)");
        TestAssert.True(LevelsAre(Sets(trident, "BasePercent").Single(), "SrcValueByLevel", 0.04, 0.05, 0.06, 0.07, 0.08),
            "missing-health percent 4 to 8 (shipped FizzSeastoneTrident)");
        TestAssert.True(Calls(trident, "BBApplyDamage").Any(block => Named(block, "DamageType") == "MAGIC_DAMAGE"), "the tick is magic damage");
        TestAssert.True(Calls(Script("FizzPassive"), "BBSpellBuffAdd").Any(block => Named(block, "BuffName") == "FizzSeastoneTrident"
            && Is(Number(block, "Duration"), 3.1)), "the passive's hit adds the trident buff for 3.1 s (shipped FizzPassive)");
    }

    // 129.04: the Playful Trickster hop is untargetable while Fizz is in the air; the landing makes her targetable again.
    [Check("scripting.p132-fizz-hop-is-untargetable-until-she-lands")]
    public static void HopIsUntargetable()
    {
        TestAssert.True(Calls(Script("FizzJump"), "BBSetStatus").Any(block => Named(block, "Status") == "SetTargetable" && !Flag(block, "SrcValue")),
            "the hop turns targetable off (shipped FizzJump)");
        TestAssert.True(Calls(Script("FizzJumpTwo"), "BBSetStatus").Any(block => Named(block, "Status") == "SetTargetable" && Flag(block, "SrcValue")),
            "the landing turns targetable back on (shipped FizzJumpTwo)");
    }

    // 129.04: the shark's knock-back. The blast moves the other units back (stun 0.25 s, FizzMoveback) and knocks its victim up (stun 0.75 s, FizzKnockup).
    [Check("scripting.p132-fizz-shark-knock-back-stuns-for-a-quarter-and-three-quarters-of-a-second")]
    public static void SharkKnockBackStuns()
    {
        var bomb = Script("FizzMarinerDoomBomb");
        TestAssert.True(Calls(bomb, "BBSpellBuffAdd").Any(block => Named(block, "BuffName") == "FizzMoveback" && Named(block, "BuffType") == "BUFF_Stun"
            && Is(Number(block, "Duration"), 0.25)), "the other units in the blast are moved back and stunned 0.25 s (FizzMoveback)");
        TestAssert.True(Calls(bomb, "BBSpellBuffAdd").Any(block => Named(block, "BuffName") == "FizzKnockup" && Named(block, "BuffType") == "BUFF_Stun"
            && Is(Number(block, "Duration"), 0.75)), "the bomb's victim is knocked up and stunned 0.75 s (FizzKnockup)");
    }

    // 129.04: fish binds. The missile's hit destroys the missile and attaches FizzMarinerDoomBomb to the target for 1.5 s.
    [Check("scripting.p132-fizz-chum-missile-binds-the-target-to-the-bomb-for-one-and-a-half-seconds")]
    public static void MissileBindsTheTarget()
    {
        TestAssert.True(Blocks(Script("FizzMarinerDoomMissile"), "TargetExecute").Any(block => block.Function == "BBSpellBuffAdd"
            && Named(block, "BuffName") == "FizzMarinerDoomBomb" && Is(Number(block, "Duration"), 1.5)),
            "the missile's hit adds the bomb to the target for 1.5 s (shipped FizzMarinerDoomMissile TargetExecute)");
    }

    // 132.18 (part): Chum the Waters starts with three ticks, one every 0.5 s, and blasts 350 wide only when it expires. The drop
    // rules (untargetable, immune, QSS) are not pinned here: the early-end branch is not driven (see the record).
    [Check("scripting.p132-fizz-chum-ticks-three-times-then-blasts-in-three-fifty-only-when-it-expires")]
    public static void ChumCountdownBlastsOnExpiry()
    {
        var bomb = Script("FizzMarinerDoomBomb");
        TestAssert.True(Calls(bomb, "BBSetVarInTable").Any(block => Named(block, "DestVar") == "TickDamage" && Is(Number(block, "SrcValue"), 3)),
            "the countdown starts at three ticks (shipped FizzMarinerDoomBomb)");
        TestAssert.True(Calls(bomb, "BBExecutePeriodically").Any(block => Is(Number(block, "TimeBetweenExecutions"), 0.5)),
            "the ticks come every 0.5 s");
        TestAssert.True(Calls(bomb, "BBApplyDamage").Any(block => LevelsAre(block, "DamageByLevel", 200, 325, 450)),
            "the blast deals 200/325/450");
        var expiry = Calls(bomb, "BBIf").Single(block => Named(block, "Src1Var") == "Expired" && Flag(block, "Value2"));
        TestAssert.True(expiry.SubBlocks.Any(block => block.Function == "BBForEachUnitInTargetArea" && Is(Number(block, "Range"), 350)),
            "the 350 blast sits inside the expired branch only");
    }
}
