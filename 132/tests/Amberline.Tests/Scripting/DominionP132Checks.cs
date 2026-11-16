using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Simulation.BehaviorTrees;
using Amberline.Simulation.Combat;

namespace Amberline.Tests;

/// <summary>
/// Batch 771: the Dominion rules the 1.0.0.132 line carries that its files do not (wiki, Patch History V1.0.0.127 and V1.0.0.128): the
/// personal score's 127/128 values and a capture's damage below the kill floor; and the two items that left the Crystal Scar's store, which
/// its files do carry.
/// </summary>
public static class DominionP132Checks
{
    /// <summary>
    /// V1.0.0.127: minion kill 2 (from 1), channel interrupt 5 (from 2), martyr 10 more than before (10 + 10 per enemy assistant), and a kill
    /// or assist on or near a point scores the "Kill on Point" / "Assist on Point" labels (the 132 client's ScoreEvent_e 10-13); V1.0.0.128:
    /// capture and neutralize assists 25 (from 10).
    /// </summary>
    [Check("dominion.p132-personal-score-values")]
    public static void PersonalScoreCarriesThe127And128Values()
    {
        TestAssert.Equal(2, DominionPlayerScore.MinionKill.Points, "V1.0.0.127 minion kill 2");
        TestAssert.Equal(5, DominionPlayerScore.Counter.Points, "V1.0.0.127 channel interrupt 5");
        TestAssert.Equal(25, DominionPlayerScore.NodeCaptureAssist.Points, "V1.0.0.128 capture assist 25");
        TestAssert.Equal(25, DominionPlayerScore.NodeNeutralizeAssist.Points, "V1.0.0.128 neutralize assist 25");

        // Points: x < 1000 is a point Order holds, 1000 <= x < 2000 a neutral point, elsewhere no point.
        static Team? PointOwnerAt(Position2 at) => at.X < 1000 ? Team.Order : at.X < 2000 ? Team.Neutral : null;
        var world = new WorldState();
        Entity Hero(uint id, Team team, double x) =>
            world.Register(new Entity(id, EntityKind.Hero, UnitType.Hero, team, new Position2(x, 0)) { Alive = true, Health = 100, MaxHealth = 100 });
        var killer = Hero(1, Team.Order, 500);       // on its own point: defensive kill
        var assistOnPoint = Hero(2, Team.Order, 1500); // on a neutral point: offensive assist
        var assistOff = Hero(3, Team.Order, 5000);     // on no point
        var victim = Hero(4, Team.Chaos, 5000);
        var martyr = Hero(5, Team.Order, 500);        // dies on its own point
        var sent = new List<PlaintextPacket>();
        var score = new DominionPlayerScore(world, PointOwnerAt, sent.Add);

        score.OnChampionKill(killer, victim, [assistOnPoint, assistOff], multiKill: 1, firstBlood: false);
        TestAssert.Equal(15 + 30, killer.OdinCombatScore, "a champion kill 15 and a kill on the scorer's own point 30");
        TestAssert.Equal(5 + 10, assistOnPoint.OdinCombatScore, "an assist 5 and an assist on a point 10");
        TestAssert.Equal(5, assistOff.OdinCombatScore, "an assist off every point scores the assist alone");
        var labels = sent.Select(packet => packet.Plaintext.Span[10]).ToArray();
        TestAssert.True(labels.Contains((byte)11), "the defensive kill label (DEFENSIVE_KILL, \"Kill on Point\")");
        TestAssert.True(labels.Contains((byte)12), "the offensive assist label (OFFENSIVE_ASSIST, \"Assist on Point\")");

        sent.Clear();
        var enemy = Hero(6, Team.Chaos, 500);
        score.OnChampionKill(enemy, martyr, [Hero(7, Team.Chaos, 5000), Hero(8, Team.Chaos, 5000)], multiKill: 1, firstBlood: false);
        TestAssert.Equal(10 + 2 * 10, martyr.OdinObjectiveScore, "a martyr: 10, and 10 more per enemy assistant");
        TestAssert.Equal(15 + 30, enemy.OdinCombatScore, "a kill on the enemy's point is an offensive kill on point");
        TestAssert.True(sent.Any(packet => packet.Plaintext.Span[10] == 13), "the offensive kill label (OFFENSIVE_KILL)");
        var martyrPacket = sent.Single(packet => packet.Plaintext.Span[10] == DominionPlayerScore.Martyr.Label);
        TestAssert.Near(30.0, BinaryPrimitives.ReadSingleLittleEndian(martyrPacket.Plaintext.Span[12..]), 0.0, "the martyr's 0xE2 carries 30");

        var lone = Hero(9, Team.Order, 500);
        score.OnChampionKill(Hero(10, Team.Chaos, 5000), lone, [], multiKill: 1, firstBlood: false);
        TestAssert.Equal(10, lone.OdinObjectiveScore, "a martyr with no enemy assistant still scores the 127 base 10");

        score.OnMinionKill(assistOff);
        TestAssert.Equal(5 + 2, assistOff.OdinCombatScore, "a minion kill scores 2");
    }

    /// <summary>
    /// V1.0.0.127: "Lowered the point where Nexus health will only be affected by holding capture points (and not by Champion Kills or
    /// Neutralizes) to 100 Nexus health from 125": a neutralise stops at 100 as a kill does; a capture still costs the nexus below it.
    /// </summary>
    [Check("dominion.p132-capture-costs-the-nexus-below-the-floor")]
    public static void CaptureCostsTheNexusBelowTheFloor()
    {
        GameData.ConfigureDefault(GameDataPaths.Default.ForMap(GameMaps.CrystalScar));
        try
        {
            TestAssert.Near(100.0, CombatService.DominionKillFloor, 0.0, "the 1.0.0.127 floor");
            foreach (var (captured, start, expected, context) in new[]
                     {
                         (false, 101.0, 100.0, "a neutralise at 101 stops at the floor"),
                         (false, 100.0, 100.0, "a neutralise at the floor takes nothing"),
                         (true, 101.0, 99.0, "a capture at 101 takes its 2"),
                         (true, 50.0, 48.0, "a capture below the floor takes its 2"),
                     })
            {
                var world = new WorldState();
                var nexus = world.Register(new Entity(world.Allocate(UnitType.Structure), EntityKind.Nexus, UnitType.Structure,
                    Team.Order, new Position2(1740.5343, 4480.0933)) { Health = start, MaxHealth = 500, Name = "LevelProp_Odin_SOG_Order_Crystal" });
                var combat = new CombatService(world, new CombatChecks.TestHooks(), new CombatChecks.TestTransport(), new SystemRandomSource());
                combat.DamageDominionNexusForPointEvent(Team.Order, captured ? 2.0 : 3.0, captured);
                TestAssert.Near(expected, nexus.Health, 1e-9, context);
            }
        }
        finally
        {
            GameData.ConfigureDefault(GameDataPaths.Default);
        }
    }

    /// <summary>
    /// V1.0.0.129: Priscilla's Blessing and Warmog's Armor can no longer be bought in Dominion. The 132 files carry it: Map8's Items.inibin
    /// lists 3083 among its UnpurchasableItemList, and item 3183's record sets InStore 0 (and Price 9999999).
    /// </summary>
    [Check("dominion.p132-priscilla-and-warmogs-leave-the-store")]
    public static void PriscillaAndWarmogsLeaveTheStore()
    {
        var map8 = GameDataPaths.Default.ForMap(GameMaps.CrystalScar);
        TestAssert.False(MapItemRules.Load(map8).AllowsPurchase(3083), "map 8's Items.inibin refuses Warmog's Armor");
        TestAssert.True(MapItemRules.Load(GameDataPaths.Default).AllowsPurchase(3083), "Summoner's Rift still sells it");
        TestAssert.False(new ItemCatalog(map8).Get(3183)!.InStore, "Priscilla's Blessing's record leaves the store");
    }
}
