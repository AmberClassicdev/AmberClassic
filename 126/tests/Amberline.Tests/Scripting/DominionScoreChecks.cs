using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.BehaviorTrees;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Stats;

namespace Amberline.Tests;

/// <summary>
/// Batch 447: the Dominion score (DualUpdateScoreMath, shipped in LEVELS/Map8/Scripts/OdinScriptBehaviorTrees.xml) sends 0xDD and
/// drains the trailing team's nexus; a champion kill takes 2 nexus health; each nexus and spawn stands at its map 8 place.
/// </summary>
public static class DominionScoreChecks
{
    private const double Step = 1.0 / 30.0;

    [Check("dominion.score-sends-both-teams-at-start")]
    public static void ScoreSendsBothTeamsAtTheStart()
    {
        var world = new WorldState();
        var sink = new RecordingScoreSink();
        var host = BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, world)
            ?? throw new InvalidOperationException("map 8 has a quest host");
        var score = host.AttachScore(new FixedControl(0, 0), sink);
        for (var tick = 1; tick <= 60; tick++)
            score.Tick(tick * Step, Step);
        // Batch 450: 0xDD is each team's whole nexus health (500 at the start, RecordingScoreSink's nexus), not the tree's total.
        TestAssert.True(sink.Scores.Contains((Team.Order, 500)) && sink.Scores.Contains((Team.Chaos, 500)),
            "0xDD carries both teams' whole nexus health (500) at the start of the game");
        TestAssert.Equal(2, sink.Scores.Count, "with no control the score does not change, so only the start is sent");
    }

    [Check("dominion.score-tie-drains-nothing")]
    public static void TiedControlCountsDrainNothing()
    {
        var world = new WorldState();
        var sink = new RecordingScoreSink();
        var host = BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, world)
            ?? throw new InvalidOperationException("map 8 has a quest host");
        var score = host.AttachScore(new FixedControl(2, 2), sink);
        for (var tick = 1; tick <= 600; tick++)
            score.Tick(tick * Step, Step);
        TestAssert.Equal(0, sink.Drains.Count, "a tie (2 points each) damages neither nexus");
        TestAssert.True(score.Total(Team.Order) > 0.0 && score.Total(Team.Order) == score.Total(Team.Chaos),
            "both teams still gain their points while tied");
    }

    [Check("dominion.score-leader-drains-trailing-nexus-to-zero")]
    public static void LeaderDrainsTheTrailingNexusToZeroAndOrderWins()
    {
        ConfigureMapEight();
        try
        {
            var world = new WorldState();
            var order = world.Register(new Entity(world.Allocate(UnitType.Structure), EntityKind.Nexus, UnitType.Structure,
                Team.Order, new Position2(1740.5343, 4480.0933)) { Health = 500, MaxHealth = 500, Name = "LevelProp_Odin_SOG_Order_Crystal" });
            var chaos = world.Register(new Entity(world.Allocate(UnitType.Structure), EntityKind.Nexus, UnitType.Structure,
                Team.Chaos, new Position2(12163.1846, 4568.9819)) { Health = 500, MaxHealth = 500, Name = "LevelProp_Odin_SOG_Chaos_Crystal" });
            var combat = new CombatService(world, new CombatChecks.TestHooks(), new CombatChecks.TestTransport(), new SystemRandomSource());
            var host = BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, world)
                ?? throw new InvalidOperationException("map 8 has a quest host");
            var score = host.AttachScore(new FixedControl(3, 1), new CombatScoreSink(combat));

            // Order holds 3 points against Chaos's 1: 3-1-1 takes 1.5 from Chaos's nexus every 2 s (DrainAmount(3, 1), DrainIntervalSeconds),
            // so it reaches 0 after 334 applications, about 668 s (batch 505).
            var tick = 0;
            while (!combat.GameOver && tick < 30000)
                score.Tick(++tick * Step, Step);

            TestAssert.True(combat.GameOver, "the drained nexus ends the match");
            TestAssert.Near(0.0, chaos.Health, 1e-9, "Chaos's nexus reaches 0 health");
            TestAssert.False(chaos.Alive, "the nexus death is the shipped building-death path");
            TestAssert.Near(500.0, order.Health, 1e-9, "Order's nexus is not drained while Order leads");
            TestAssert.Equal(Team.Order, combat.Winner, "the other team wins");
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    [Check("dominion.score-meter-follows-nexus-health")]
    public static void TheMeterCountsEachNexusDownByWholePoints()
    {
        var world = new WorldState();
        var sink = new RecordingScoreSink();
        var host = BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, world)
            ?? throw new InvalidOperationException("map 8 has a quest host");
        var score = host.AttachScore(new FixedControl(3, 1), sink);
        for (var tick = 1; tick <= 1830; tick++)
            score.Tick(tick * Step, Step);
        var chaos = sink.Scores.Where(entry => entry.Team == Team.Chaos).Select(entry => entry.Score).ToList();
        TestAssert.True(chaos.Count > 1 && chaos.SequenceEqual(chaos.OrderByDescending(value => value)) && chaos.Distinct().Count() == chaos.Count,
            "the trailing meter counts down, each whole value sent once (batch 450: 0xDD is nexus health)");
        TestAssert.True(chaos[^1] == 455, $"61 s at 1.5 per 2 s (3-1-1, batch 505: thirty applications) leaves Chaos's nexus at 455 on the meter ({chaos[^1]})");
        TestAssert.True(sink.Scores.All(entry => entry.Team != Team.Order || entry.Score == 500), "the leading team's meter stays at 500");
    }

    // Batch 505: the drain per tick is the forum post's table (15 February 2012, "You-Neutral-Enemy: nexus reduction per tick"), by both
    // teams' counts; 2-3-0 is the Reconstruction 1.5 (DominionScore.DrainAmount). A tie drains nothing (wiki, Dominion: "When both teams have the
    // same amount of capture points, neither nexus will be damaged"). Steps are 1/30 s; one application falls in the first 2.5 s (DrainIntervalSeconds).
    [Check("dominion.drain-follows-the-control-configuration")]
    public static void DrainFollowsTheControlConfiguration()
    {
        foreach (var (order, chaos, expected) in new[]
                 {
                     (1, 0, 1.0), (2, 0, 1.5), (2, 1, 1.0), (3, 0, 2.0), (3, 1, 1.5), (3, 2, 1.0), (4, 0, 3.0), (4, 1, 2.5), (5, 0, 5.0),
                     (0, 0, 0.0), (1, 1, 0.0), (2, 2, 0.0), (1, 4, 2.5),
                 })
            TestAssert.Near(expected, NexusLossAfter(order, chaos, 75), 1e-6,
                $"{order}-{5 - order - chaos}-{chaos} drains {expected} once per tick (the trailing team's nexus)");
    }

    // Batch 505 (two-second tick, Gamerevolution 4 August 2011: "every two seconds"): the drain is applied once per tick, not every step.
    // Before the first tick nothing is lost; the second tick takes a second application.
    [Check("dominion.drain-applies-once-every-two-seconds")]
    public static void DrainAppliesOnceEveryTwoSeconds()
    {
        TestAssert.Near(0.0, NexusLossAfter(2, 1, 45), 1e-6, "no drain before the first two seconds (1.5 s)");
        TestAssert.Near(1.0, NexusLossAfter(2, 1, 75), 1e-6, "one application at two seconds (2.5 s)");
        TestAssert.Near(2.0, NexusLossAfter(2, 1, 135), 1e-6, "two applications by 4.5 s");
    }

    /// <summary>Batch 478b: the nexus health the trailing team loses in the first <paramref name="steps"/> steps of 1/30 s under a fixed control split.</summary>
    private static double NexusLossAfter(int order, int chaos, int steps)
    {
        ConfigureMapEight();
        try
        {
            var world = new WorldState();
            world.Register(new Entity(world.Allocate(UnitType.Structure), EntityKind.Nexus, UnitType.Structure,
                Team.Order, new Position2(1740.5343, 4480.0933)) { Health = 500, MaxHealth = 500, Name = "LevelProp_Odin_SOG_Order_Crystal" });
            world.Register(new Entity(world.Allocate(UnitType.Structure), EntityKind.Nexus, UnitType.Structure,
                Team.Chaos, new Position2(12163.1846, 4568.9819)) { Health = 500, MaxHealth = 500, Name = "LevelProp_Odin_SOG_Chaos_Crystal" });
            var combat = new CombatService(world, new CombatChecks.TestHooks(), new CombatChecks.TestTransport(), new SystemRandomSource());
            var host = BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, world)
                ?? throw new InvalidOperationException("map 8 has a quest host");
            var score = host.AttachScore(new FixedControl(order, chaos), new CombatScoreSink(combat));
            for (var tick = 1; tick <= steps; tick++)
                score.Tick(tick * Step, Step);
            var trailing = order > chaos ? Team.Chaos : Team.Order;
            return 500.0 - combat.DominionNexusHealth(trailing);
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    // Batch 478b: a champion kill takes nexus health only down to the floor. The wiki (Dominion, Patch History V1.0.0.127): "Lowered the point
    // where Nexus health will only be affected by holding capture points (and not by Champion Kills or Neutralizes) to 100 Nexus health from
    // 125 Nexus health." So the 1.0.0.126 floor for kills is 125.
    [Check("dominion.champion-kill-stops-at-the-125-floor")]
    public static void ChampionKillStopsAtTheFloor()
    {
        TestAssert.Near(125.0, KillAtNexusHealth(126.0), 1e-9, "a kill at 126 takes the nexus down to the 125 floor and no further");
        TestAssert.Near(125.0, KillAtNexusHealth(125.0), 1e-9, "a kill at the floor takes nothing");
    }

    private static double KillAtNexusHealth(double health)
    {
        ConfigureMapEight();
        try
        {
            var world = new WorldState();
            var nexus = world.Register(new Entity(world.Allocate(UnitType.Structure), EntityKind.Nexus, UnitType.Structure,
                Team.Order, new Position2(1740.5343, 4480.0933)) { Health = health, MaxHealth = 500, Name = "LevelProp_Odin_SOG_Order_Crystal" });
            var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(7000, 7000))
            { ChampionName = "Yorick", Health = 1000, MaxHealth = 1000 });
            var combat = new CombatService(world, new CombatChecks.TestHooks(), new CombatChecks.TestTransport(), new SystemRandomSource());
            combat.Kill(hero);
            return nexus.Health;
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    [Check("dominion.champion-kill-takes-two-nexus-health")]
    public static void ChampionKillTakesTwoFromTheVictimTeamsNexus()
    {
        ConfigureMapEight();
        try
        {
            var world = new WorldState();
            var nexus = world.Register(new Entity(world.Allocate(UnitType.Structure), EntityKind.Nexus, UnitType.Structure,
                Team.Order, new Position2(1740.5343, 4480.0933)) { Health = 500, MaxHealth = 500, Name = "LevelProp_Odin_SOG_Order_Crystal" });
            var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(7000, 7000))
            { ChampionName = "Yorick", Health = 1000, MaxHealth = 1000 });
            var combat = new CombatService(world, new CombatChecks.TestHooks(), new CombatChecks.TestTransport(), new SystemRandomSource());
            combat.Kill(hero);
            TestAssert.Near(498.0, nexus.Health, 1e-9, "a champion kill takes 2 from the victim team's nexus (wiki: Dominion)");
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    [Check("dominion.nexuses-and-spawns-stand-at-map-8-bases")]
    public static void MapEightNexusesAndSpawnsStandAtTheirShippedPlaces()
    {
        ConfigureMapEight();
        try
        {
            var scene = Path.Combine(GameDataPaths.Default.ForMap(GameMaps.CrystalScar).Root, "LEVELS", "Map8", "Scene");
            var orderCrystal = SceneMesh.CentralPoint(Path.Combine(scene, "LevelProp_Odin_SOG_Order_Crystal.SCB"));
            var chaosCrystal = SceneMesh.CentralPoint(Path.Combine(scene, "LevelProp_Odin_SOG_Chaos_Crystal.SCB"));
            var orderSpawn = SceneMesh.CentralPoint(Path.Combine(scene, "__Spawn_T1.SCB"));
            var chaosSpawn = SceneMesh.CentralPoint(Path.Combine(scene, "__Spawn_T2.SCB"));

            var nexuses = MapDefinitions.Nexuses;
            TestAssert.Equal(2, nexuses.Count, "map 8 has one nexus per team");
            var order = nexuses.Single(nexus => nexus.Team == Team.Order);
            var chaos = nexuses.Single(nexus => nexus.Team == Team.Chaos);
            TestAssert.Near(orderCrystal.X, order.Position.X, 1e-3, "Order nexus X is the crystal prop's centre");
            TestAssert.Near(orderCrystal.Z, order.Position.Z, 1e-3, "Order nexus Z is the crystal prop's centre");
            TestAssert.Near(chaosCrystal.X, chaos.Position.X, 1e-3, "Chaos nexus X is the crystal prop's centre");
            TestAssert.Near(chaosCrystal.Z, chaos.Position.Z, 1e-3, "Chaos nexus Z is the crystal prop's centre");
            TestAssert.Near(500.0, order.MaxHealth, 1e-9, "a nexus starts at 500 health (wiki: Crystal Scar)");
            TestAssert.Near(500.0, chaos.MaxHealth, 1e-9, "a nexus starts at 500 health (wiki: Crystal Scar)");
            TestAssert.Near(0.0, order.HealthRegen, 1e-9, "no regeneration: it would cancel the drain (Reconstruction)");
            TestAssert.Equal(MapDefinitions.BuildingNetId("LevelProp_Odin_SOG_Order_Crystal"), order.NetId, "netid from the placement name");

            TestAssert.Near(orderSpawn.X, MapDefinitions.TeamSpawn(Team.Order).X, 1e-3, "Order spawn is __Spawn_T1 (Locations.dat OrderStartPoint)");
            TestAssert.Near(orderSpawn.Z, MapDefinitions.TeamSpawn(Team.Order).Z, 1e-3, "Order spawn is __Spawn_T1 (Locations.dat OrderStartPoint)");
            TestAssert.Near(chaosSpawn.X, MapDefinitions.TeamSpawn(Team.Chaos).X, 1e-3, "Chaos spawn is __Spawn_T2 (Locations.dat ChaosStartPoint)");
            TestAssert.Near(chaosSpawn.Z, MapDefinitions.TeamSpawn(Team.Chaos).Z, 1e-3, "Chaos spawn is __Spawn_T2 (Locations.dat ChaosStartPoint)");
            // The first Chaos hero of a one-champion team takes the shipped slot offset (HeroSpawnOffsets: Chaos1 is -10, 0) from the base.
            var initial = MapDefinitions.InitialHeroSpawn(Team.Chaos, 1, 0).Position;
            TestAssert.Near(chaosSpawn.X - 10.0, initial.X, 1e-3, "the first Chaos hero spawns at the map 8 base plus its slot offset");
            TestAssert.Near(chaosSpawn.Z, initial.Z, 1e-3, "the first Chaos hero's Z is the map 8 base's Z (slot offset 0)");
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    [Check("dominion.summoners-rift-spawns-unchanged")]
    public static void SummonersRiftSpawnsAreUnchanged()
    {
        GameData.ConfigureDefault(GameDataPaths.Default);
        try
        {
            TestAssert.Near(MapDefinitions.HeroSpawn.X, MapDefinitions.TeamSpawn(Team.Order).X, 1e-9, "Map 1 Order spawn is HeroSpawn");
            TestAssert.Near(13933.7890625, MapDefinitions.TeamSpawn(Team.Chaos).X, 1e-9, "Map 1 Chaos spawn X");
            TestAssert.Near(14170.09375, MapDefinitions.TeamSpawn(Team.Chaos).Z, 1e-9, "Map 1 Chaos spawn Z");
            TestAssert.Equal(2, MapDefinitions.Nexuses.Count, "Summoner's Rift keeps its two HQs");
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    [Check("dominion.hero-respawns-at-its-map-8-base")]
    public static void HeroRespawnsAtItsOwnMapEightBase()
    {
        ConfigureMapEight();
        try
        {
            var data = GameData.Default;
            var world = new WorldState();
            var hero = world.Register(new Entity(1, EntityKind.Hero, UnitType.Hero, Team.Chaos, new Position2(7000, 7000))
            { ChampionName = "Yorick", Health = 0, MaxHealth = 1000, Alive = false, RespawnAt = 10.0 });
            var stats = new HeroStatsService(data, new ItemCatalog(data.Paths), new CombatChecks.LifecycleStatsHooks(),
                new CombatChecks.LifecycleStatsTransport());
            var combat = new CombatService(world, new CombatChecks.TestHooks(), new CombatChecks.TestTransport(),
                new SystemRandomSource(), stats, data);
            TestAssert.True(combat.RespawnImmediately(hero), "the hero respawns");
            var spawn = MapDefinitions.TeamSpawn(Team.Chaos);
            TestAssert.Near(spawn.X, hero.Position.X, 1e-6, "a respawned Chaos hero stands at the Chaos base on map 8 (X)");
            TestAssert.Near(spawn.Z, hero.Position.Z, 1e-6, "a respawned Chaos hero stands at the Chaos base on map 8 (Z)");
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    /// <summary>
    /// Batch 747 (Reconstruction, DominionScore.PlayMusicPhase): the decoded 0xE5 is 10 bytes, the opcode, a hero's net id (the stock client routes
    /// the packet to a hero), the command at 5 (0 = PrepareMusicCue, 1 = BeginMusicCue) and the FMOD cue at 6. Nothing goes out at the start: the
    /// client begins its own phase 1 theme (Map_CrystalScar, cue 13) on StartGame. As the lower nexus of either team passes 375, 250 and 125,
    /// every client gets Global_Phase2, 3 and 4 (cues 15, 16, 17 of LoL_Audio.fev), once each; a fallen nexus sends none, since the client plays
    /// its own victory or defeat music on EndGame. Batch 804: each cue is a Prepare, then a Begin (the stock client drops a Begin before its first
    /// Prepare, <see cref="ClientMusic"/>), and the stream leaves the client playing each phase in turn.
    /// </summary>
    [Check("dominion.music-phase-follows-the-lower-nexus")]
    public static void MusicPhaseFollowsTheLowerNexus()
    {
        var (_, hero, packets, sink, host) = MusicMatch();
        var score = host.Score!;
        var tick = 0;
        void Run() { for (var step = 0; step < 30; step++) score.Tick(++tick * Step, Step); }
        List<byte[]> Music() => packets.Where(packet => packet.Plaintext.Span[0] == 0xE5).Select(packet => packet.Plaintext.ToArray()).ToList();
        void Phases(string what, params uint[] cues)
        {
            var expected = cues.SelectMany(cue => new[] { MusicCue(hero.NetId, 0, cue), MusicCue(hero.NetId, 1, cue) }).Select(Convert.ToHexString);
            TestAssert.True(Music().Select(Convert.ToHexString).SequenceEqual(expected),
                $"{what}: expected Prepare and Begin of cues [{string.Join(", ", cues)}], got [{string.Join(", ", Music().Select(Convert.ToHexString))}]");
            TestAssert.True(ClientMusic.Play(Music()).SequenceEqual(cues), $"{what}: the stock client begins cues [{string.Join(", ", cues)}]");
        }

        Run();
        Phases("the start sends nothing (the client starts its own theme)");
        sink.DamageNexus(Team.Order, 120.0);
        Run();
        Phases("380 is still phase 1");
        sink.DamageNexus(Team.Chaos, 125.0);
        Run();
        Phases("Chaos at 375: phase 2", 15);
        var first = packets.First(packet => packet.Plaintext.Span[0] == 0xE5);
        TestAssert.True(first.Channel == 3 && first.Reliability == LeaguePacketReliability.Reliable,
            $"0xE5 goes reliable on the S2C channel 3 (channel {first.Channel}, {first.Reliability})");
        sink.DamageNexus(Team.Chaos, 25.0);
        Run();
        Phases("Chaos at 350 stays in phase 2", 15);
        sink.DamageNexus(Team.Order, 140.0);
        Run();
        Phases("Order at 240 is now the lower nexus: phase 3", 15, 16);
        sink.DamageNexus(Team.Chaos, 300.0);
        Run();
        Phases("Chaos at 50: phase 4", 15, 16, 17);
        sink.DamageNexus(Team.Chaos, 50.0);
        Run();
        Phases("the fallen nexus sends nothing more", 15, 16, 17);
    }

    /// <summary>
    /// Batch 747: a returning client (BehaviorTreeQuestHost.ReconnectState) gets the current music phase addressed to its own hero, and nothing
    /// while the match is still in phase 1. Batch 804: as a Prepare and a Begin, the current phase's cue only (the restarted client's theme starts
    /// at its start segment, whose links in LoL_Audio.fev reach every later phase's loop on that phase's cue). Two thresholds passed in one step
    /// send both phases in order: the theme's phase 2 loop links on only the phase 3 cue (batch 747 sent only the deeper one).
    /// </summary>
    [Check("dominion.reconnecting-client-gets-the-current-music-phase")]
    public static void ReconnectingClientGetsTheCurrentMusicPhase()
    {
        var (world, hero, packets, sink, host) = MusicMatch();
        var returning = world.Register(new Entity(world.Allocate(UnitType.Hero), EntityKind.Hero, UnitType.Hero, Team.Chaos, new Position2(13000, 4500))
            { Name = "Annie", ChampionName = "Annie" });
        var known = new HashSet<uint> { hero.NetId, returning.NetId };
        List<byte[]> Music(IEnumerable<PlaintextPacket> stream) =>
            stream.Where(packet => packet.Plaintext.Span[0] == 0xE5).Select(packet => packet.Plaintext.ToArray()).ToList();
        var tick = 0;
        void Run() { for (var step = 0; step < 30; step++) host.Score!.Tick(++tick * Step, Step); }

        Run();
        TestAssert.Equal(0, Music(host.ReconnectState(returning.NetId, Team.Chaos, known)).Count,
            "in phase 1 the returning client gets no 0xE5 (its own StartGame began the theme)");
        sink.DamageNexus(Team.Chaos, 260.0);
        Run();
        var live = Music(packets);
        var first = world.Heroes()[0].NetId;
        TestAssert.True(live.Select(Convert.ToHexString).SequenceEqual(new[]
                { MusicCue(first, 0, 15), MusicCue(first, 1, 15), MusicCue(first, 0, 16), MusicCue(first, 1, 16) }.Select(Convert.ToHexString)),
            $"500 to 240 in one step sends phases 2 and 3 in order (cues 15, 16), got {live.Count} packets");
        var burst = Music(host.ReconnectState(returning.NetId, Team.Chaos, known));
        TestAssert.True(burst.Select(Convert.ToHexString).SequenceEqual(new[]
                { MusicCue(returning.NetId, 0, 16), MusicCue(returning.NetId, 1, 16) }.Select(Convert.ToHexString)),
            $"the returning client gets the current phase (cue 16) prepared and begun, addressed to its own hero, got {burst.Count} packets");
        TestAssert.True(ClientMusic.Play(burst).SequenceEqual([16u]), "a restarted client begins the current phase's cue");
    }

    /// <summary>
    /// Batch 821 (Reconstruction, DominionScore.PlayMusicPhase; the wiki's Nexus trivia: "a unique track began to play whenever both team's
    /// Nexai were at or below 70 health"): phase 5 (Global_Phase5, cue 18) goes out once, as a Prepare and a Begin, when the higher nexus too
    /// is at or below 70 on the meter; the lower one alone at 70 or below stays in phase 4. Several phases passed in one step go in order, the
    /// returning client gets cue 18 alone, and a fallen nexus sends nothing more.
    /// </summary>
    [Check("dominion.music-final-phase-waits-for-both-nexuses-at-70")]
    public static void MusicFinalPhaseWaitsForBothNexusesAt70()
    {
        var (world, hero, packets, sink, host) = MusicMatch();
        var returning = world.Register(new Entity(world.Allocate(UnitType.Hero), EntityKind.Hero, UnitType.Hero, Team.Chaos, new Position2(13000, 4500))
            { Name = "Annie", ChampionName = "Annie" });
        var known = new HashSet<uint> { hero.NetId, returning.NetId };
        var tick = 0;
        void Run() { for (var step = 0; step < 30; step++) host.Score!.Tick(++tick * Step, Step); }
        List<byte[]> Music(IEnumerable<PlaintextPacket> stream) =>
            stream.Where(packet => packet.Plaintext.Span[0] == 0xE5).Select(packet => packet.Plaintext.ToArray()).ToList();
        void Phases(string what, params uint[] cues)
        {
            var expected = cues.SelectMany(cue => new[] { MusicCue(hero.NetId, 0, cue), MusicCue(hero.NetId, 1, cue) }).Select(Convert.ToHexString);
            TestAssert.True(Music(packets).Select(Convert.ToHexString).SequenceEqual(expected),
                $"{what}: expected Prepare and Begin of cues [{string.Join(", ", cues)}], got [{string.Join(", ", Music(packets).Select(Convert.ToHexString))}]");
            TestAssert.True(ClientMusic.Play(Music(packets)).SequenceEqual(cues), $"{what}: the stock client begins cues [{string.Join(", ", cues)}]");
        }

        sink.DamageNexus(Team.Chaos, 380.0);
        Run();
        Phases("Chaos at 120: phases 2 to 4 in order", 15, 16, 17);
        sink.DamageNexus(Team.Chaos, 70.0);
        Run();
        Phases("Chaos at 50 with Order at 500 stays in phase 4", 15, 16, 17);
        sink.DamageNexus(Team.Order, 429.0);
        Run();
        Phases("Order at 71 stays in phase 4", 15, 16, 17);
        sink.DamageNexus(Team.Order, 1.0);
        Run();
        Phases("Order at 70 too: phase 5", 15, 16, 17, 18);
        Run();
        Phases("phase 5 goes out once", 15, 16, 17, 18);
        var burst = Music(host.ReconnectState(returning.NetId, Team.Chaos, known));
        TestAssert.True(burst.Select(Convert.ToHexString).SequenceEqual(new[]
                { MusicCue(returning.NetId, 0, 18), MusicCue(returning.NetId, 1, 18) }.Select(Convert.ToHexString)),
            $"the returning client gets phase 5 (cue 18) prepared and begun, addressed to its own hero, got {burst.Count} packets");
        sink.DamageNexus(Team.Chaos, 50.0);
        Run();
        Phases("the fallen nexus sends nothing more", 15, 16, 17, 18);

        var (_, other, otherPackets, otherSink, otherHost) = MusicMatch();
        var otherTick = 0;
        void RunOther() { for (var step = 0; step < 30; step++) otherHost.Score!.Tick(++otherTick * Step, Step); }
        otherSink.DamageNexus(Team.Order, 370.0);
        otherSink.DamageNexus(Team.Chaos, 370.0);
        RunOther();
        otherSink.DamageNexus(Team.Order, 60.0);
        otherSink.DamageNexus(Team.Chaos, 60.0);
        RunOther();
        var both = Music(otherPackets);
        TestAssert.True(both.Select(Convert.ToHexString).SequenceEqual(new uint[] { 15, 16, 17, 18 }
                .SelectMany(cue => new[] { MusicCue(other.NetId, 0, cue), MusicCue(other.NetId, 1, cue) }).Select(Convert.ToHexString)),
            $"130 each, then 70 each in one step: phases 2 and 3, then 4 and 5 in order, got {both.Count} packets");
    }

    /// <summary>
    /// Batch 804 (owner, 15 November 2026: natively the Dominion music never changes): on the production match path (MatchRuntime, the
    /// replication delivery, one observer per seat), a nexus passing 375 and then 250 begins the phase 2 and phase 3 cues on every client,
    /// replayed through the stock client's music state (<see cref="ClientMusic"/>), and each 0xE5 names a hero that client holds. Batch 747's
    /// lone Begin reached the clients and was dropped by them: no Prepare had created the audio manager's cue map. Batch 821: then one nexus
    /// at 70 begins phase 4, and the other at 70 too begins phase 5 (cue 18).
    /// </summary>
    [Check("dominion.music-phase-plays-on-every-client-of-a-match", Serial = true, TimeoutSeconds = 900)]
    public static void MusicPhasePlaysOnEveryClientOfAMatch()
    {
        ConfigureMapEight();
        try
        {
            var roster = new[]
            {
                new SessionParticipantDefinition(1, 0, "Order", "Annie", Team.Order),
                new SessionParticipantDefinition(2, 1, "Chaos", "Annie", Team.Chaos),
            };
            using var runtime = MatchRuntime.CreateProduction(NavigationGrid.Load(GameDataPaths.Default.ForMap(GameMaps.CrystalScar)),
                GameData.Default, readySettleSeconds: 0.0, roster: roster, mapId: GameMaps.CrystalScar);
            runtime.World.Clock.SetVirtual(0.0);
            runtime.Session.AfkTrackingDisabled = true;
            runtime.Session.CreateWorld();
            var seats = new List<(SessionParticipant Seat, List<PlaintextPacket> Packets)>();
            for (var id = 1; id <= roster.Length; id++)
            {
                TestAssert.True(runtime.Session.Authenticate(id, runtime.Session.BuildKeyCheck((ulong)id)) is not null, "authenticate");
                runtime.Session.Spawn(id);
                TestAssert.True(runtime.Session.MarkReady(id), "ready");
                var seat = runtime.Session.Participants[id - 1];
                var packets = new List<PlaintextPacket>();
                runtime.Delivery.AddObserver(new ReplicationObserver((ulong)id, seat.Team, seat.Hero!.NetId, new SeatCapture(packets),
                    ready: true, view: seat.View));
                seats.Add((seat, packets));
            }
            TestAssert.True(runtime.Session.Start(), "the map 8 match starts");
            void Run(int steps) { for (var step = 0; step < steps; step++) runtime.Tick(Step); }
            void Playing(string what, params uint[] cues)
            {
                foreach (var (seat, packets) in seats)
                {
                    var music = packets.Select(packet => packet.Plaintext.ToArray()).Where(packet => packet[0] == 0xE5).ToList();
                    var begun = ClientMusic.Play(music);
                    TestAssert.True(begun.SequenceEqual(cues),
                        $"{what}: the {seat.Team} client begins cues [{string.Join(", ", cues)}], got [{string.Join(", ", begun)}] from {music.Count} 0xE5");
                    TestAssert.True(music.All(packet => seat.View.Known.Contains(BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(1)))),
                        $"{what}: every 0xE5 the {seat.Team} client gets names a hero it holds");
                }
            }

            Run(60);
            Playing("the start: the client's own theme");
            runtime.Combat.DamageDominionNexus(Team.Chaos, 130.0); // DIAGNOSTICS: the nexus damage seam.
            Run(3);
            Playing("Chaos at 370: phase 2", 15);
            runtime.Combat.DamageDominionNexus(Team.Order, 260.0); // DIAGNOSTICS: the nexus damage seam.
            Run(3);
            Playing("Order at 240: phase 3", 15, 16);
            // Batch 821: phase 5 waits for both nexuses at 70 or below (DominionScore.PlayMusicPhase).
            runtime.Combat.DamageDominionNexus(Team.Chaos, 300.0); // DIAGNOSTICS: the nexus damage seam.
            Run(3);
            Playing("Chaos at 70: phase 4", 15, 16, 17);
            runtime.Combat.DamageDominionNexus(Team.Order, 170.0); // DIAGNOSTICS: the nexus damage seam.
            Run(3);
            Playing("Order at 70 too: phase 5", 15, 16, 17, 18);
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    /// <summary>
    /// Batch 804: the stock 126 client's music cue state, applied to its 0xE5 stream (handler: the command at 5, the cue at 6,
    /// then). The client creates the manager's cue map on its first call and adds the
    /// cue; BeginMusicCue returns at once while that map is null (<c>cmp [edi+0x14], 0; je</c>), else begins the cue
    /// (preparing it first when it is not in the map); EndMusicCue (2) is not modelled. Returns the cues begun, in order.
    /// </summary>
    internal static class ClientMusic
    {
        public static List<uint> Play(IEnumerable<byte[]> stream)
        {
            HashSet<uint>? cues = null;
            var begun = new List<uint>();
            foreach (var packet in stream.Where(packet => packet.Length == 10 && packet[0] == 0xE5))
            {
                var cue = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6));
                if (packet[5] == 0) (cues ??= []).Add(cue);
                else if (packet[5] == 1 && cues is not null) { cues.Add(cue); begun.Add(cue); }
            }
            return begun;
        }
    }

    private sealed class SeatCapture(List<PlaintextPacket> packets) : IReplicationTransport
    {
        public void Send(PlaintextPacket packet) => packets.Add(packet);
    }

    // Batch 747: map 8's score with a packet sender, one Order champion and a recorded nexus pair (500 each).
    private static (WorldState World, Entity Hero, List<PlaintextPacket> Packets, RecordingScoreSink Sink, BehaviorTreeQuestHost Host) MusicMatch()
    {
        var world = new WorldState();
        var hero = world.Register(new Entity(world.Allocate(UnitType.Hero), EntityKind.Hero, UnitType.Hero, Team.Order, new Position2(1000, 4500))
            { Name = "Annie", ChampionName = "Annie" });
        var packets = new List<PlaintextPacket>();
        var host = BehaviorTreeQuestHost.ForMap(GameMaps.CrystalScar, GameDataPaths.Default, world, hooks: new CapturePointHooks(Send: packets.Add))
            ?? throw new InvalidOperationException("map 8 has a quest host");
        var sink = new RecordingScoreSink();
        host.AttachScore(new FixedControl(0, 0), sink);
        return (world, hero, packets, sink, host);
    }

    // The decoded 0xE5 (command 0 Prepare, 1 Begin), written byte by byte (not through the builder).
    private static byte[] MusicCue(uint heroNetId, byte command, uint cue)
    {
        var bytes = new byte[10];
        bytes[0] = 0xE5;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), heroNetId);
        bytes[5] = command;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(6), cue);
        return bytes;
    }

    private static void ConfigureMapEight() =>
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));

    /// <summary>Records what the score trees send: 0xDD team scores and nexus drains.</summary>
    internal sealed class RecordingScoreSink : IDominionScoreSink
    {
        public List<(Team Team, int Score)> Scores { get; } = new();
        public List<(Team Team, double Amount)> Drains { get; } = new();
        private readonly Dictionary<Team, double> _nexus = new() { [Team.Order] = 500.0, [Team.Chaos] = 500.0 };
        public void SetGameScore(Team team, int score) => Scores.Add((team, score));
        public double NexusHealth(Team team) => _nexus[team];
        public void DamageNexus(Team team, double amount)
        {
            Drains.Add((team, amount));
            _nexus[team] = Math.Max(0.0, _nexus[team] - amount);
        }
    }

    /// <summary>The score's nexus drain goes through the combat service, so the nexus death reaches the shipped FinishMatch path.</summary>
    internal sealed class CombatScoreSink(CombatService combat) : IDominionScoreSink
    {
        public void SetGameScore(Team team, int score) { }
        public double NexusHealth(Team team) => combat.DominionNexusHealth(team);
        public void DamageNexus(Team team, double amount) => combat.DamageDominionNexus(team, amount);
    }

    /// <summary>Fixed control counts (batch 446's capture points replace this seed).</summary>
    internal sealed class FixedControl(int order, int chaos) : IDominionControl
    {
        public int ControlCount(Team team) => team == Team.Order ? order : chaos;
    }
}
