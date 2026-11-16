using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Buffs;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network.Neutral;

// Batch 434: the Autumn rift's Urf camp. Map2's NeutralMinionSpawn.luaobj adds camp 13: PredefinedCamps[11] = {'Urf'} (the main chunk
// pc 0600-0603), one spawn point and one face point Make3DPoint(2532.0, 44.4, 9940.0) (pc 0310-0316, 0534-0540), GroupDelaySpawnTime 0,
// GroupsRespawnTime 100 and CampLevel 1 (NeutralMinionInit pc 0284-0301). DefaultNeutralMinionValues['Urf'] pays 50 experience (pc 0087-0091).
// Summoner's Rift (LEVELS/Map1) keeps its twelve camps and has no camp 13.
// 1.0.0.132 (batch 788): the Harrowing's ghost is gone. The 132 Map2 NeutralMinionSpawn.luaobj ends PredefinedCamps at [10] (main chunk
// pc 0595-0601; 126 added [11] = {'Urf'} at pc 0600-0603), while NeutralMinionInit still gives camp 13 Groups = {PredefinedCamps[11]}
// (pc 0283-0289), a list of nil, with GroupsRespawnTime 100 and its grave point Make3DPoint(2532.0, 44.4, 9940.0) (pc 0313-0315, 0537-0539).
// Camp 13 so never spawns a unit. The Urf character, CharScriptUrf, HalloweenUrfAppear, HalloweenUrfWarwick and UrfSpell still ship, but
// nothing on the map uses them. The five Urf checks assert that 132 behaviour: no ghost on the Harrowing rift, at 100 s or later, whoever
// walks by the grave, and nothing of his reaches a client.
public static class HarrowingUrfCampChecks
{
    private static readonly Position3 ShippedUrfPoint = new(2532.0, 44.4, 9940.0);

    [Check("neutral.harrowing-urf-camp-spawns-on-map-two")]
    public static void UrfCampSpawnsOnTheHarrowingRiftAtItsShippedTimeAndPlace()
    {
        TestAssert.False(NeutralDefinitions.Camps.ContainsKey(13), "Summoner's Rift (Map1) has no camp 13");
        var mapOneJungle = new JungleScheduler(new WorldState(), GameData.Default, navigation: NavigationGrid.LoadDefault());
        mapOneJungle.Start();
        mapOneJungle.Tick(1000.0);
        TestAssert.Equal(12, mapOneJungle.Camps.Count, "Summoner's Rift keeps its twelve camps");
        TestAssert.False(mapOneJungle.Members.Values.Any(member => member.Name == "Urf"), "no Urf on Summoner's Rift");

        var paths = GameDataPaths.Default.ForMap(GameMaps.Harrowing);
        var data = new GameData(paths);
        var grid = NavigationGrid.Load(paths);
        var jungle = new JungleScheduler(new WorldState(), data, navigation: grid);
        jungle.Start();
        jungle.Tick(99.0);
        TestAssert.False(jungle.Members.Values.Any(member => member.Name == "Urf"), "the Urf camp is not up before its 100 s first spawn");
        jungle.Tick(100.0);
        TestAssert.False(jungle.Members.Values.Any(member => member.Name == "Urf" || member.CampId == 13),
            "132: camp 13 spawns nothing at its 100 s (its group, PredefinedCamps[11], is gone)");
        jungle.Tick(1000.0);
        TestAssert.True(jungle.Members.Values.Any(member => member.CampId is >= 1 and <= 12), "the rift's other camps are up");
        TestAssert.False(jungle.Members.Values.Any(member => member.Name == "Urf" || member.CampId == 13), "nor later: no ghost on the 132 Harrowing rift");
        TestAssert.False(jungle.Camps.TryGetValue(13, out var grave) && grave.Definition.Roster.Count > 0, "camp 13 has no member to spawn");
        // The ghost's own files still ship; only the map no longer places him.
        TestAssert.True(File.Exists(paths.Data("Characters/Urf/Urf.inibin")), "Urf.inibin still ships");
        TestAssert.True(File.Exists(paths.Data("Spells/HalloweenUrfAppear.luaobj")), "and his appear buff");
    }

    [Check("neutral.harrowing-urf-binds-its-shipped-character-script")]
    public static void UrfBindsItsShippedCharacterScriptOnTheHarrowingRift()
    {
        // The production binder (runtime.Spells.UnitScripts, as a match's jungle uses it) binds each camp member's character script. 132: no
        // camp member is the Urf, so CharScriptUrf is never bound on the map; the binder still runs for the camps that spawn.
        var paths = GameDataPaths.Default.ForMap(GameMaps.Harrowing);
        var data = new GameData(paths);
        var grid = NavigationGrid.Load(paths);
        using var runtime = MatchRuntime.CreateProduction(grid, data);
        runtime.World.Clock.SetVirtual();
        runtime.Session.CreateWorld();
        var jungle = new JungleScheduler(runtime.World, data, navigation: grid);
        var bound = new List<string>();
        jungle.BindUnitCharacter = (unit, character) =>
        {
            runtime.Spells.UnitScripts!.BindUnit(unit, character);
            bound.Add(character);
        };
        jungle.Start();
        jungle.Tick(100.0);
        jungle.Tick(200.0);
        TestAssert.True(bound.Count > 0, "the production binder runs for the camps that spawn");
        TestAssert.False(bound.Contains("Urf"), $"132: CharScriptUrf is never bound on the map (bound [{string.Join(",", bound.Distinct())}])");
        TestAssert.False(jungle.Members.Values.Any(member => member.Name == "Urf"), "no Urf member");
    }

    [Check("neutral.harrowing-urf-reveals-when-a-champion-walks-within-300")]
    public static void UrfRevealsWhenAChampionWalksWithin300()
    {
        // 126 (batch 440): the Urf's UpdateSelfBuffActions found a champion within 300 and added HalloweenUrfAppear. 132: there is no ghost on
        // the grave, so a champion walking within 300 of it reveals nothing and no unit gets the appear buff.
        var paths = GameDataPaths.Default.ForMap(GameMaps.Harrowing);
        var data = new GameData(paths);
        var grid = NavigationGrid.Load(paths);
        using var runtime = MatchRuntime.CreateProduction(grid, data);
        runtime.World.Clock.SetVirtual();
        runtime.Session.CreateWorld();
        var jungle = new JungleScheduler(runtime.World, data, navigation: grid);
        jungle.BindUnitCharacter = (unit, character) => runtime.Spells.UnitScripts!.BindUnit(unit, character);
        jungle.Start();
        jungle.Tick(100.0);
        var grave = new Position2(ShippedUrfPoint.X, ShippedUrfPoint.Z);
        var champion = runtime.World.Register(new Entity(runtime.World.Allocate(UnitType.Hero), EntityKind.Hero, UnitType.Hero, Team.Order,
            grave + new Position2(250, 0)) { Health = 1000, MaxHealth = 1000, Created = true, ChampionName = "Annie", Radius = 55 });
        // The spell system's own tick: its clock and the bound units' self-update poll.
        for (var t = 0.0; t < 3.0; t += 0.25)
        {
            runtime.World.Clock.Advance(0.25);
            runtime.Spells.Tick(0.25);
        }
        var failures = string.Join(" | ", runtime.Spells.UnitScripts!.ScriptFailures.Select(f => $"{f.Script}/{f.Hook}@{f.BlockPath}: {f.Message}"));
        TestAssert.False(runtime.World.Entities.Values.Any(unit => unit.Name == "Urf"), "132: no ghost stands on the grave");
        TestAssert.False(runtime.World.Entities.Values.Any(unit =>
                unit.Buffs.Any(buff => string.Equals(buff.Name, "HalloweenUrfAppear", StringComparison.OrdinalIgnoreCase))),
            $"so a champion within 300 makes nothing appear; failures [{failures}]");
        TestAssert.Equal("", failures, "no unit script failed");
        TestAssert.True(champion.Alive, "the walking champion is still alive");
    }

    // Batch 667 (owner, 3 November 2026: "URF attacking warwick with skin is bugged, URF invisible, hit sound plays, no damage is dealt to
    // warwick"). In 126 CharScriptUrf's scan (pc 0034-0096) cast UrfSpell at an Urf-skin Warwick (skin 2) within 300 and showed the ghost with
    // HalloweenUrfWarwick. 132: no ghost spawns, so the Urf-skin Warwick waiting by the grave from the camp's 100 s is neither slapped nor shown
    // anything, and his client gets no unit named Urf.
    [Check("neutral.harrowing-urf-shows-itself-and-slaps-urf-skin-warwick")]
    public static void UrfShowsItselfAndSlapsUrfSkinWarwick()
    {
        var paths = GameDataPaths.Default.ForMap(GameMaps.Harrowing);
        var grid = NavigationGrid.Load(paths);
        using var runtime = MatchRuntime.CreateProduction(grid, new GameData(paths), readySettleSeconds: 0.0,
            mapId: GameMaps.Harrowing,
            roster: [new(1, 0, "Manatee", "Warwick", Team.Order, SkinId: 2), new(2, 1, "Wolf", "Warwick", Team.Chaos)]);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.AfkTrackingDisabled = true;
        foreach (var id in new[] { 1L, 2L })
        {
            TestAssert.True(runtime.Session.Authenticate(id, runtime.Session.BuildKeyCheck((ulong)id)) is not null, "authenticate");
            runtime.Session.Spawn(id);
            TestAssert.True(runtime.Session.MarkReady(id), "ready");
        }
        runtime.Session.RunSchedulers();
        TestAssert.True(runtime.Session.Started, "the map 2 match starts");
        var manatee = runtime.Session.Participants[0].Hero!;
        var wolf = runtime.Session.Participants[1].Hero!;
        manatee.HeroAutoAcquire = wolf.HeroAutoAcquire = false;
        TestAssert.Equal(2, manatee.SkinId, "the Order Warwick wears skin 2 (Urf the Manatee)");
        TestAssert.True(manatee.Buffs.Any(buff => buff.Name == "EternalThirstIcon"), "Warwick's character script adds EternalThirstIcon");
        var capture = new UrfCapture();
        runtime.Delivery.AddObserver(MatchRuntimeNetwork.NewObserver(runtime, runtime.Session.Participants[0], capture));
        for (var t = 0.0; t < 99.0; t += 0.25) runtime.Tick(0.25);
        // The Order Warwick waits by the grave (CampSpawnPoints[13]) when the camp's first spawn would come at 100 s, on its south side.
        manatee.Position = new Position2(ShippedUrfPoint.X, ShippedUrfPoint.Z - 200.0);
        TestAssert.True(grid.IsMovementWalkable(manatee.Position) &&
                        grid.HasVisionLos(manatee.Position, new Position2(ShippedUrfPoint.X, ShippedUrfPoint.Z)), "a walkable spot that sees the grave");
        var health = manatee.Health;
        for (var t = 0.0; t < 6.0; t += 0.25) runtime.Tick(0.25);
        TestAssert.False(runtime.World.Entities.Values.Any(unit => unit.Name == "Urf"), "132: the Urf camp spawns nothing at 100 s");
        TestAssert.False(runtime.World.Entities.Values.Any(unit => unit.Buffs.Any(buff => buff.Name is "HalloweenUrfWarwick" or "HalloweenUrfAppear")),
            "no ghost buff on any unit");
        var urfSpell = BitConverter.GetBytes(Amberline.Simulation.Spells.SpellSystem.ProtocolHash("UrfSpell"));
        TestAssert.False(capture.Packets.Any(packet => packet.Plaintext.Span.IndexOf(urfSpell) >= 0), "the Order client is sent no UrfSpell slap");
        TestAssert.Near(health, manatee.Health, 1e-9, "and the Urf-skin Warwick is not hurt");
    }

    // Batch 672 (owner, 3 November 2026: the Ghost of Urf "spins in place, continuously"). The client's 0x66 handler (the client case 0x66,
    // -) takes the spawn facing as normalize(face point (+0x22) - position (+0x0a)) in 3D, falls back to (0, 0, 1) only when the
    // two points are exactly equal, and stores it as the unit's target facing. The
    // per-frame turn yaws a standing unit toward that target and never reaches a vertical one, so a face point straight above or
    // below the position spins the unit for ever; the 0xC2 stop (SetStoppedPosition) sets only the matrix, not the target.
    // Camp 13's script puts its spawn and face point on the same Make3DPoint(2532.0, 44.4, 9940.0); batch 588 grounded the spawn Y only.
    [Check("neutral.camp-spawn-facing-is-never-vertical")]
    public static void CampSpawnFacingIsNeverVertical()
    {
        var failures = new List<string>();
        foreach (var map in new[] { GameMaps.Normal, GameMaps.Harrowing, GameMaps.TwistedTreeline })
        {
            var paths = GameDataPaths.Default.ForMap(map);
            var jungle = new JungleScheduler(new WorldState(), new GameData(paths), navigation: NavigationGrid.Load(paths));
            jungle.Start();
            jungle.Tick(1000.0);
            TestAssert.True(jungle.Members.Count > 0, $"map {map} spawns its camps");
            foreach (var member in jungle.Members.Values)
            {
                // The wire's float32 points, as the client subtracts them.
                var dx = (float)member.FacePosition.X - (float)member.Home.X;
                var dy = (float)member.FacePosition.Y - (float)member.Home.Y;
                var dz = (float)member.FacePosition.Z - (float)member.Home.Z;
                if ((dx != 0f || dy != 0f || dz != 0f) && dx == 0f && dz == 0f)
                    failures.Add($"map {map} {member.Name} camp {member.CampId}: face - home = (0, {dy}, 0)");
            }
        }
        TestAssert.True(failures.Count == 0, $"a camp's 0x66 face point is straight above or below it: {string.Join("; ", failures)}");
    }

    // Batch 672 reproduction (brief step 2): everything a map 2 match sends the Order client over the ghost's first 30 s, with a default-skin
    // Warwick (the 126 appear branch) and an Urf-skin Warwick (the 126 slap branch) by the grave. 132: no ghost, so no creation, no route and no
    // facing of his reach the client; the facing of every camp that does spawn is neutral.camp-spawn-facing-is-never-vertical's.
    [Check("neutral.harrowing-urf-stands-facing-a-horizontal-direction")]
    public static void UrfStandsFacingAHorizontalDirection()
    {
        foreach (var skin in new[] { 0, 2 })
        {
            var (urfs, nearGrave, packets) = RunUrfWindow(skin, 30.0);
            TestAssert.Equal(0, urfs, $"skin {skin}: 132: no Urf is ever created on the Harrowing rift");
            TestAssert.Equal(0, nearGrave, $"skin {skin}: no neutral unit stands on the grave");
            TestAssert.True(packets > 0, $"skin {skin}: the Order client was sent the match (the window ran)");
        }
    }

    private static (int Urfs, int NearGrave, int Packets) RunUrfWindow(int skinId, double seconds)
    {
        var paths = GameDataPaths.Default.ForMap(GameMaps.Harrowing);
        var grid = NavigationGrid.Load(paths);
        using var runtime = MatchRuntime.CreateProduction(grid, new GameData(paths), readySettleSeconds: 0.0,
            mapId: GameMaps.Harrowing,
            roster: [new(1, 0, "Grave", "Warwick", Team.Order, SkinId: skinId), new(2, 1, "Wolf", "Warwick", Team.Chaos)]);
        runtime.World.Clock.SetVirtual(0.0);
        runtime.Session.AfkTrackingDisabled = true;
        foreach (var id in new[] { 1L, 2L })
        {
            TestAssert.True(runtime.Session.Authenticate(id, runtime.Session.BuildKeyCheck((ulong)id)) is not null, "authenticate");
            runtime.Session.Spawn(id);
            TestAssert.True(runtime.Session.MarkReady(id), "ready");
        }
        runtime.Session.RunSchedulers();
        var hero = runtime.Session.Participants[0].Hero!;
        hero.HeroAutoAcquire = runtime.Session.Participants[1].Hero!.HeroAutoAcquire = false;
        var capture = new UrfCapture();
        runtime.Delivery.AddObserver(MatchRuntimeNetwork.NewObserver(runtime, runtime.Session.Participants[0], capture));
        for (var t = 0.0; t < 99.0; t += 0.25) runtime.Tick(0.25);
        hero.Position = new Position2(ShippedUrfPoint.X, ShippedUrfPoint.Z - 200.0);
        var urfs = new HashSet<uint>();
        var nearGrave = new HashSet<uint>();
        for (var t = 0.0; t < 1.0 + seconds; t += 0.25)
        {
            runtime.Tick(0.25);
            foreach (var unit in runtime.World.Entities.Values)
            {
                if (unit.Name == "Urf") urfs.Add(unit.NetId);
                if (unit.Kind == EntityKind.Neutral && Math.Abs(unit.Position.X - ShippedUrfPoint.X) < 300.0 &&
                    Math.Abs(unit.Position.Z - ShippedUrfPoint.Z) < 300.0) nearGrave.Add(unit.NetId);
            }
        }
        return (urfs.Count, nearGrave.Count, capture.Packets.Count);
    }

    private sealed class UrfCapture : IReplicationTransport
    {
        public List<PlaintextPacket> Packets { get; } = [];

        public void Send(PlaintextPacket packet) => Packets.Add(packet);
    }
}
