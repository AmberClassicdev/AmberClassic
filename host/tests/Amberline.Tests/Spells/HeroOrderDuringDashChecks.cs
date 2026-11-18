using System.Buffers.Binary;
using Amberline.Core;
using Amberline.Network.Protocol;
using static Amberline.Tests.BbM3;

namespace Amberline.Tests;

/// <summary>
/// Batch 862 (Fizz's Urchin Strike, 1.0.0.132; the engine is shared): a hero order given while a forced move runs (a script's <c>BBIssueOrder</c>:
/// <c>FizzPiercingStrike</c> orders <c>AI_ATTACKTO</c> right after its <c>BBMove</c>) publishes no route. The walk is refused while the move owns the
/// hero, and the refusal's one-point route at the hero's spot stopped the dash on every client (the 0x68 and every route share the client's install,
/// <c>ClientFollowServerPath</c>). The attack order itself stands. A melee champion of both lines with its target out of reach, then a move order.
/// </summary>
public static class HeroOrderDuringDashChecks
{
    private static int Routes(WireCapture capture, int from, Entity hero) => capture.Packets.Skip(from).Count(entry =>
        entry.Packet.Plaintext.Span[0] == VisionPackets.WaypointListOpcode &&
        BinaryPrimitives.ReadUInt32LittleEndian(entry.Packet.Plaintext.Span[1..]) == hero.NetId);

    [Check("movement.a-hero-order-during-a-forced-move-publishes-no-route")]
    public static void HeroOrderDuringAForcedMovePublishesNoRoute()
    {
        using var match = new Match("Warwick");
        var hero = match.Hero;
        var foe = match.Foe;
        foe.Position = new Position2(hero.Position.X + 450.0, hero.Position.Z);
        match.Ally.Position = new Position2(hero.Position.X - 400.0, hero.Position.Z);
        var own = Observe(match, Team.Order, hero);
        match.Advance(Step);
        TestAssert.True(match.Runtime.Movement.StartForcedMove(hero.NetId, new Position2(hero.Position.X, hero.Position.Z + 400.0), 800.0, 1.0,
            hero.NetId, "B862", cancelOrder: true), "the forced move starts");
        var orders = match.Runtime.Spells.HeroOrders!;

        var from = own.Packets.Count;
        orders.StartAttack(hero, foe);
        match.Runtime.Delivery.Flush();
        TestAssert.Equal(0, Routes(own, from, hero), "an attack order on a target out of reach sends no route during the forced move");
        TestAssert.Equal(foe.NetId, hero.AttackTarget ?? 0u, "and the attack order stands");

        from = own.Packets.Count;
        orders.StartMove(hero, new Position2(hero.Position.X - 300.0, hero.Position.Z));
        match.Runtime.Delivery.Flush();
        TestAssert.Equal(0, Routes(own, from, hero), "a move order sends no route during the forced move");
    }
}
