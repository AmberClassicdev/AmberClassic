using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Sessions;
using Amberline.Server;

namespace Amberline.Tests;

/// <summary>
/// Batch 822: Summoner's Rift (Winter), LEVELS/Map6, on the 132 line. Its scripts and rule files are Map1's byte for byte, so the checks
/// pin what differs: its own grid, its own scene positions (Turret_T1_C_04 17.4 units from Map1's, the Order spawn 17.9, __NAV_L02, C02
/// and R02 106 to 158), the Constants.var it reads (music cue 5, Map1's 3), and the lane minions' MeshSkin2. The expected positions are an
/// independent reading of the same meshes (<c>.artifacts/b822/scb_points.py</c>, <c>scb-points.txt</c>).
/// </summary>
public static class Map6WinterRiftChecks
{
    private static readonly Position2 Map6TurretT1C04 = new(4658.8408, 4592.3008);
    private static readonly Position2 Map1TurretT1C04 = new(4641.4419, 4591.9092);
    private static readonly Position2 Map6OrderSpawn = new(16.5942, 248.2209);
    private static readonly Position2 Map6NavL02 = new(1090.4604, 4378.9736);
    private static readonly Position2 Map6NavC02 = new(3210.9829, 3892.0610);
    private static readonly Position2 Map6NavR02 = new(4159.5962, 1337.5708);

    [Check("data.map6-winter-rift-reads-its-own-level-files")]
    public static void TheWinterRiftReadsItsOwnLevelFiles()
    {
        // The selector: every --map-id gate accepts 6, the SynchVersion map id the client loads LEVELS\Map6\ from carries it.
        TestAssert.True(GameMaps.IsSupported(GameMaps.Winter), "132 loads map 6");
        TestAssert.False(GameMaps.IsSupported(5) || GameMaps.IsSupported(7), "maps 5 and 7 are still refused");
        TestAssert.Equal(GameMaps.Winter, ServerOptions.Parse(["--map-id", "6"]).MapId, "--map-id 6 names the winter rift");
        var synch = LoadingPackets.SynchVersionS2C(mapId: GameMaps.Winter).Plaintext.Span;
        TestAssert.Equal(6, BitConverter.ToInt32(synch.Slice(6, 4)), "SynchVersion carries map id 6");

        var map6 = GameDataPaths.Default.ForMap(GameMaps.Winter);
        TestAssert.Equal("Map6", map6.MapName);
        TestAssert.Equal(GameMaps.Winter, map6.MapId);
        TestAssert.True(MapDefinitions.NavigationGridPath(map6).EndsWith(Path.Combine("LEVELS", "Map6", "AIPath.aimesh_ngrid")),
            "the winter rift's grid is LEVELS/Map6/AIPath.aimesh_ngrid");
        // Constants.var: every gameplay constant is Map1's; the music cue (5) and the ambience (map_snow) are the winter rift's own.
        var constants = new GameData(map6).LevelConstants;
        TestAssert.Near(5.0, constants.Values["aud_FMODMusicCueID"], 1e-9, "Map6 Constants.var music cue");
        TestAssert.Near(3.0, new GameData(GameDataPaths.Default.ForMap(GameMaps.Normal)).LevelConstants.Values["aud_FMODMusicCueID"], 1e-9,
            "Map1 Constants.var music cue");
        TestAssert.Near(1.05, constants.DamageRatios.BuildingToHero, 1e-9, "Map6's damage ratios are Summoner's Rift's");

        // The layout: its own scene, by Summoner's Rift's rules (three lanes, nineteen nodes each, Map1's approaches).
        var layout = MapLayout.For(map6);
        var rift = MapLayout.For(GameDataPaths.Default.ForMap(GameMaps.Normal));
        TestAssert.False(ReferenceEquals(layout, rift), "map 6 has a layout of its own");
        TestAssert.Equal(rift.Turrets.Count, layout.Turrets.Count, "map 6 creates Map1's turret rows");
        TestAssert.True(Distance(layout.TurretPositions["Turret_T1_C_04"], Map6TurretT1C04) < 1e-3, "Turret_T1_C_04 stands at Map6's mesh");
        TestAssert.True(Distance(rift.TurretPositions["Turret_T1_C_04"], Map1TurretT1C04) < 1e-3, "and Map1's at Map1's");
        TestAssert.True(Distance(layout.TeamSpawn(Team.Order), Map6OrderSpawn) < 1e-3, "the Order spawn is Map6's __Spawn_T1");
        TestAssert.Equal("L,C,R", string.Join(",", layout.Lanes), "the winter rift's three lanes");
        foreach (var lane in layout.Lanes)
        {
            TestAssert.Equal(19, layout.LanePaths[lane].Count, $"lane {lane} spine");
            TestAssert.Equal(rift.LaneSpawnApproaches[lane], layout.LaneSpawnApproaches[lane], $"lane {lane} approaches are Summoner's Rift's");
        }
        TestAssert.True(Distance(layout.LanePaths["L"][2], Map6NavL02) < 1e-3, "lane L node 2 is Map6's __NAV_L02");
        TestAssert.True(Distance(layout.LanePaths["C"][2], Map6NavC02) < 1e-3, "lane C node 2 is Map6's __NAV_C02");
        TestAssert.True(Distance(layout.LanePaths["R"][1], Map6NavR02) < 1e-3, "lane R node 1 is Map6's __NAV_R02");

        // On Map6's own grid (its structure and turret stamps on), every interior lane node is pathing floor and every leg has a path,
        // for Summoner's Rift's rules (the layout) and, for the record, the plain scene reading and Map1 by the same rules.
        var map1 = GameDataPaths.Default.ForMap(GameMaps.Normal);
        foreach (var (name, paths, variant) in new[] { ("rift-rules", map6, layout), ("scene", map6, MapLayout.FromScene(map6)), ("map1-rift-rules", map1, rift) })
        {
            var grid = NavigationGrid.Load(paths, new NavigationGridSettings { Turrets = variant.Turrets, Structures = variant.StructureStamps });
            foreach (var lane in variant.Lanes)
            {
                var spine = variant.LanePaths[lane];
                var (refused, pathless, length) = (0, 0, 0.0);
                var at = spine[0];
                for (var index = 1; index < spine.Count; index++)
                {
                    var interior = index < spine.Count - 1;
                    if (interior && !grid.IsPathingWalkable(spine[index])) refused++;
                    var node = interior ? grid.ProjectToPathingFloor(spine[index]) : spine[index];
                    var leg = grid.FindPath(at, node);
                    if (leg.Count == 0) pathless++;
                    for (var step = 1; step < leg.Count; step++) length += Distance(leg[step - 1], leg[step]);
                    at = node;
                }
                Console.WriteLine(FormattableString.Invariant(
                    $"{name} lane {lane}: {spine.Count} nodes, {refused} refused by the pathing grid, {pathless} legs without a path, route {length:0} units"));
                if (name != "rift-rules") continue;
                TestAssert.Equal(0, refused, $"map 6 lane {lane}: every interior node is pathing floor");
                TestAssert.Equal(0, pathless, $"map 6 lane {lane}: every leg has a path");
            }
        }
    }

    // Serial: it makes Map6 the process's match map, as a map 6 server process does (Program.cs, GameData.ConfigureDefault).
    [Check("server.map6-winter-rift-match-starts-and-runs-its-level-script", Serial = true)]
    public static void AWinterRiftMatchStartsAndRunsItsLevelScript()
    {
        var map6 = GameDataPaths.Default.ForMap(GameMaps.Winter);
        GameData.ConfigureDefault(map6);
        try
        {
            TestAssert.True(Distance(MapDefinitions.Layout.TurretPositions["Turret_T1_C_04"], Map6TurretT1C04) < 1e-3,
                "the match map's turrets are Map6's");
            TestAssert.True(Distance(MapDefinitions.TeamSpawn(Team.Order), Map6OrderSpawn) < 1e-3, "the match map's Order spawn is Map6's");
            var roster = new List<SessionParticipantDefinition>
            {
                new(1, 0, "Order", "Annie", Team.Order), new(2, 1, "Chaos", "Annie", Team.Chaos),
            };
            using var runtime = MatchRuntime.CreateProduction(NavigationGrid.Load(map6), GameData.Default,
                readySettleSeconds: 0.0, roster: roster, mapId: GameMaps.Winter);
            runtime.World.Clock.SetVirtual(0.0);
            runtime.Session.AfkTrackingDisabled = true;
            foreach (var id in new[] { 1L, 2L })
            {
                TestAssert.True(runtime.Session.Authenticate(id, runtime.Session.BuildKeyCheck((ulong)id)) is not null, "authenticate");
                runtime.Session.Spawn(id);
                TestAssert.True(runtime.Session.MarkReady(id), "ready");
            }
            runtime.Session.RunSchedulers();
            TestAssert.True(runtime.Session.Started, "the map 6 match starts");

            // The map script ran its start: Map6's LevelScript (the same bytes as Map1's) created its turret rows and armed its timers.
            var level = runtime.Waves?.LevelScript;
            TestAssert.True(level is not null, "the match runs a LevelScript");
            TestAssert.Equal(MapDefinitions.Turrets.Count, level!.LevelTurrets.Count, "CreateLevelProps created every turret row");
            TestAssert.True(level.Timers.Count > 0, $"OnLevelInit armed its timers ({string.Join(", ", level.Timers.Select(timer => timer.Name))})");

            // The first wave walks Map6's lanes in the winter skin.
            List<Entity> minions = [];
            for (var t = 0.0; t < 150.0 && minions.Count == 0; t += 0.25)
            {
                runtime.Tick(0.25);
                minions = runtime.World.Entities.Values.Where(unit => unit.MinionLane is not null).ToList();
            }
            TestAssert.True(minions.Count > 0, $"the first wave spawns (first spawn {level.FirstSpawnSeconds} s)");
            TestAssert.True(minions.All(minion => minion.SkinId == GameMaps.WinterMinionSkinId),
                $"every lane minion wears MeshSkin2 ({string.Join(", ", minions.Select(minion => minion.SkinId).Distinct())})");
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
