using Amberline.Core;
using Amberline.Data;
using Amberline.Simulation.Combat;
using Amberline.Tests.Core;

namespace Amberline.Tests;

public static partial class ChampionLiveRegressionChecks
{
    [Check("combat.live-nimbleness-capacity-dodge-renewal")]
    public static void NimblenessCapacityDodgeAndRenewal()
    {
        foreach (var team in new[] { Team.Order, Team.Chaos })
        foreach (var used in new[] { 64, 63 })
        {
            using var match = new Match("Annie", team);
            var target = match.Hero(0);
            var attacker = match.Hero(1);
            // Batch 770: 1.0.0.132 has no CharOnDodge talent (126's Nimbleness 127 is gone), so a full defense page
            // allocates nothing on a dodge, whether the champion's capacity is full or has one slot left.
            target.Masteries = MasteryTestPages.Defense(new MasteryCatalog());
            match.Runtime.HeroStats.Refresh(target);
            target.Dodge = 1.0;
            FillChampionCapacity(match.Runtime.Buffs, target, used);
            var before = target.Health;
            var roll = new AttackRolls(Crit: 1.0, Dodge: 0.0, Miss: 1.0);
            TestAssert.True(match.Runtime.Combat.BasicAttackDamage(attacker, target, roll).Dodged);
            TestAssert.Near(before, target.Health, 1e-8, "dodged attack still avoids damage");
            TestAssert.False(target.Buffs.Any(buff => buff.Name == "Nimbleness"),
                "no 132 talent adds a buff on a dodge");
            if (used == 64) continue;

            var count = target.Buffs.Count;
            TestAssert.True(match.Runtime.Combat.BasicAttackDamage(attacker, target, roll).Dodged);
            TestAssert.Equal(count, target.Buffs.Count, "a second dodge adds nothing either");
        }
    }
}
