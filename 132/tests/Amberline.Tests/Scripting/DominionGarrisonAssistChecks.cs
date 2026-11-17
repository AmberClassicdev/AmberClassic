using Amberline.Core;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells;

namespace Amberline.Tests;

/// <summary>Batch 833 (131.32): Garrison's assist, on the two-Chaos map 8 match of <see cref="DominionGuardianChecks"/> (point C).</summary>
public static partial class DominionGuardianChecks
{
    /// <summary>
    /// V1.0.0.131 (Garrison): "Now correctly applies assists to the caster." The 132 Spells/SummonerOdinGarrison.luaobj's
    /// BBApplyAssistMarker names the garrisoned guardian as its TargetVar and the caster as its SourceVar (pc 0108-0113; 126 had them the other
    /// way round), so the caster's support marker sits on the guardian (CombatService.RecordSupportAssist), and the guardian's hits on an enemy
    /// champion carry it to the victim (PropagateSupportCredits). The caster deals nothing; another Chaos champion takes the kill and the caster
    /// has the assist. Without Garrison the same kill gives the caster none.
    /// </summary>
    [Check("dominion.p132-garrison-credits-its-caster-with-an-assist", TimeoutSeconds = 300)]
    public static void GarrisonCreditsItsCasterWithAnAssist()
    {
        foreach (var garrisoned in new[] { false, true })
        {
            using var match = StartMatch(secondChaos: true);
            var (runtime, order, caster) = (match.Runtime, match.Order, match.Chaos);
            var killer = runtime.Session.Participants.Select(seat => seat.Hero!).Single(hero => hero.Team == Team.Chaos && hero != caster);
            Capture(match, caster);
            var guardian = match.Guardian;
            foreach (var hero in new[] { order, caster, killer }) hero.HeroAutoAcquire = false;
            order.MaxHealth = order.Health = 1e6;
            killer.Position = match.ChaosHome;
            caster.Position = Offset(guardian, -600.0);
            order.Position = match.OrderHome;
            if (garrisoned)
            {
                caster.SummonerSpells[0] = "SummonerOdinGarrison";
                caster.SummonerCooldowns[0] = 0.0;
                var at = new Position3(guardian.Position.X, 0.0, guardian.Position.Z);
                TestAssert.True(runtime.Spells.HandleCastRequest(caster, new SpellCastRequest(0, true, at, at, guardian.NetId), runtime.Combat.CurrentTime),
                    "Chaos casts Garrison on its point");
                TestAssert.True(Until(runtime, () => guardian.Buffs.Any(buff => buff.Name == "SummonerOdinGarrison"), 1.0), "the guardian is garrisoned");
            }
            order.Position = Offset(guardian, 300.0);
            var health = order.Health;
            TestAssert.True(Until(runtime, () => order.Health < health - 1.0, 3.0), $"the guardian hits the Order champion (garrisoned {garrisoned})");
            Advance(runtime, 1.0);
            runtime.Combat.ApplyHit(killer.NetId, order.NetId, 1e7, DamageType.True, DamageSourceKind.Spell);
            TestAssert.False(order.Alive, "the second Chaos champion kills the Order champion");
            TestAssert.Equal(1, killer.ChampionKills, "the killer has the kill");
            TestAssert.Equal(garrisoned ? 1 : 0, caster.ChampionAssists,
                garrisoned ? "the Garrison caster, who dealt nothing, has the assist" : "without Garrison the caster has no assist");
        }
    }
}
