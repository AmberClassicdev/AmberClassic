using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Scripting.BuildingBlocks.Runtime;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Spells.Bb;

namespace Amberline.Tests;

/// <summary>
/// Batch 771: the zombie life's death timer on the 1.0.0.132 Crystal Scar (V1.0.0.131), from DeathTimes.inibin's new
/// DeathTimeSettings*StartDeathTimerForZombies.
/// </summary>
public static class DominionZombieDeathTimerChecks
{
    /// <summary>
    /// DeathTimes.inibin's DeathTimeSettings section (both keys default 1): the 132 Crystal Scar ships
    /// AllowDeathTimeMods 1 and the new StartDeathTimerForZombies 0; Summoner's Rift ships neither (both on).
    /// </summary>
    [Check("progression.p132-death-time-settings")]
    public static void DeathTimeSettingsAreTheMapsOwn()
    {
        var map8 = MapProgressionRules.Load(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        TestAssert.True(map8.AllowDeathTimeMods, "map 8 ships DeathTimeSettings*AllowDeathTimeMods 1");
        TestAssert.False(map8.StartDeathTimerForZombies, "map 8 ships DeathTimeSettings*StartDeathTimerForZombies 0");
        var map1 = MapProgressionRules.Load(GameDataPaths.Default.ForMap(GameMaps.Normal));
        TestAssert.True(map1.AllowDeathTimeMods, "Summoner's Rift leaves AllowDeathTimeMods absent (on)");
        TestAssert.True(map1.StartDeathTimerForZombies, "Summoner's Rift leaves StartDeathTimerForZombies absent (on)");
    }

    /// <summary>
    /// V1.0.0.131: "zombie-state units start respawn timers when the zombie state ends" (map 8's StartDeathTimerForZombies 0). An ally under
    /// Yorick's Omen of Death dies into its zombie life with no death timer; the life's end starts it, the map's 26 s from then.
    /// </summary>
    [Check("dominion.p132-zombie-death-timer-starts-when-the-life-ends", TimeoutSeconds = 300)]
    public static void ZombieDeathTimerStartsWhenTheLifeEnds()
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Order", "Yorick", Team.Order),
            new SessionParticipantDefinition(2, 1, "Order", "Annie", Team.Order),
            new SessionParticipantDefinition(3, 2, "Chaos", "Orianna", Team.Chaos),
        };
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.Load(GameDataPaths.Default.ForMap(GameMaps.CrystalScar)), GameData.Default,
            readySettleSeconds: 0.0, roster: roster, mapId: GameMaps.CrystalScar);
        try
        {
            runtime.World.Clock.SetVirtual(0.0);
            runtime.Session.AfkTrackingDisabled = true;
            runtime.Session.CreateWorld();
            for (var index = 0; index < roster.Length; index++)
            {
                var connection = 100L + index;
                TestAssert.True(runtime.Session.Authenticate(connection, runtime.Session.BuildKeyCheck(roster[index].PlayerId)) is not null,
                    $"seat {index} authenticates");
                runtime.Session.Spawn(connection);
                TestAssert.True(runtime.Session.MarkReady(connection), $"seat {index} is ready");
            }
            runtime.Session.RunSchedulers();
            TestAssert.True(runtime.Session.Started, "the map 8 match starts");
            runtime.Tick(30.0);
            var heroes = runtime.Session.Participants.Select(participant => participant.Hero!).ToArray();
            var yorick = heroes.Single(hero => hero.ChampionName == "Yorick");
            var ally = heroes.Single(hero => hero.ChampionName == "Annie");
            var killer = heroes.Single(hero => hero.Team == Team.Chaos);
            var module = runtime.Dependencies.ChampionDispatcher.FindModule("Yorick") as BbChampionModule
                ?? throw new InvalidOperationException("the match has no script-driven Yorick");
            TestAssert.True(module.Buffs!.Add(new BbBuffRequest(ally, yorick, "YorickReviveAllySelf", (int)BbBuffAddType.BUFF_REPLACE_EXISTING,
                (int)BbBuffType.BUFF_CombatEnchancer, 1, 10.0, 0.0, false, new BbTable())) is not null, "the Omen of Death marker is on the ally");

            runtime.Combat.ApplyHit(killer.NetId, ally.NetId, 100000, DamageType.True, DamageSourceKind.Spell);
            TestAssert.True(ally.YorickRevival is not null && ally.Alive, "the ally's death became a zombie life");
            TestAssert.Near(0.0, ally.RespawnAt, 0.0, "map 8 starts no death timer at the death of a unit that becomes a zombie");

            runtime.Tick(1.0);
            var end = runtime.Combat.CurrentTime;
            TestAssert.True(runtime.Combat.Kill(ally, killer.NetId) is false && !ally.Alive && ally.YorickRevival is null,
                "a kill in the zombie life ends it (no second death)");
            TestAssert.Near(end + 26.0, ally.RespawnAt, 1e-6, "the death timer starts at the life's end: map 8's 26 s");
        }
        finally
        {
            runtime.Dispose();
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }
}
