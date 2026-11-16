using Amberline.Core.Buffs;

namespace Amberline.Simulation.Stats;

/// <summary>Composes the shared Classic movement modifier stages.</summary>
public static class MovementSpeedCalculator
{
    public static double Calculate(
        double baseSpeed,
        double flatBonus,
        double additivePercent,
        double multiplicativePercent,
        IEnumerable<BuffPayload> payloads,
        bool applyHeroSoftCaps)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        if (!double.IsFinite(baseSpeed) || !double.IsFinite(flatBonus) ||
            !double.IsFinite(additivePercent) || !double.IsFinite(multiplicativePercent))
            throw new ArgumentOutOfRangeException(nameof(baseSpeed));

        var positiveAdditive = additivePercent;
        var positiveMultiplicative = Math.Max(0.0, 1.0 + multiplicativePercent);
        var slows = new List<double>();
        var floor = 0.0;
        foreach (var payload in payloads)
        {
            ArgumentNullException.ThrowIfNull(payload);
            floor = Math.Max(floor, payload.MoveSpeedFloor);
            flatBonus += payload.MoveSpeedFlat;
            if (payload.MoveSpeedPercent >= 0.0)
                positiveAdditive += payload.MoveSpeedPercent;

            if (payload.MoveSpeedMult >= 0.0)
                positiveMultiplicative *= Math.Max(0.0, 1.0 + payload.MoveSpeedMult);

            // Batch 407 (Reconstruction): the two negative stages of one buff are one slow. The shipped Slow sets MoveSpeedMod on both
            // IncPercentMovementSpeedMod and IncPercentMultiplicativeMovementSpeedMod; counting both as slows took one 25 percent Slow to
            // 68.4 percent speed. The larger of the buff's own slows enters the stacked list once.
            var slow = Math.Max(-Math.Min(payload.MoveSpeedPercent, 0.0), -Math.Min(payload.MoveSpeedMult, 0.0));
            if (slow > 0.0) slows.Add(slow);
        }

        var speed = (baseSpeed + flatBonus) * (1.0 + positiveAdditive) *
                    positiveMultiplicative;
        slows.Sort((left, right) => right.CompareTo(left));
        for (var index = 0; index < slows.Count; index++)
        {
            var strength = index == 0 ? slows[index] : slows[index] * 0.35;
            speed *= Math.Max(0.0, 1.0 - strength);
        }

        // IncMoveSpeedFloorMod (1.0.0.132): the holder moves at least this fast, slowed or not. Reconstruction: the floor's place among the stages
        // (after the slows, before the hero soft caps) is not in the shipped files; its one shipped holder is Viktor's storm, a non-hero.
        speed = Math.Max(floor, Math.Max(0.0, speed));
        return applyHeroSoftCaps ? HeroStatCalculator.ApplyMovementSoftCaps(speed) : speed;
    }
}
