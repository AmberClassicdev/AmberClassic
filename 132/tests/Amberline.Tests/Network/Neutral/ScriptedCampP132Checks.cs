using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Network.Sessions;

namespace Amberline.Tests.Network.Neutral;

// Batch 835 (1.0.0.132 jungle items of fix batch C): the camp pin that needs a live production camp match. The helpers (StartCampMatch,
// Members, OrderHeroes) are the ones in ScriptedCampExperienceChecks.cs; this file is the same class.
public static partial class ScriptedCampExperienceChecks
{
    /// <summary>
    /// 1.0.0.132 (Dragon and Baron are immune to armour and MR shred, 130.41). The 132 ResistantSkinDragon.luaobj's BuffOnAllowAdd refuses an
    /// incoming buff of type BUFF_Shred (a BBElseIf with CO_EQUAL, SrcValue False), as it refuses BUFF_Blind and BUFF_Suppression. The Baron
    /// (Worm) carries ResistantSkin, whose Spells/ResistantSkin.luaobj lists BUFF_Shred (126's list did not). That skin also refuses buffs whose
    /// caster is more than 950 units away (its timer block, BBDistanceBetweenObjects CO_GREATER_THAN 950), so the caster stands 200 units from
    /// each unit it probes: the shred probe is then refused for its type, and an Internal probe from the same caster is taken. A wolf, with no
    /// skin, takes the shred probe.
    /// </summary>
    [Check("neutral.p132-dragon-and-baron-refuse-a-shred-buff", TimeoutSeconds = 300)]
    public static void DragonAndBaronRefuseAShredBuff()
    {
        var runtime = StartCampMatch();
        runtime.Tick(40.0);
        var dragon = runtime.World.Entities.Values.Single(entity => entity.Kind == EntityKind.Neutral && entity.Name == "Dragon");
        var baron = TickUntilBaronStands(runtime);
        var wolf = Members(runtime, 2).First(member => member.Name == "GiantWolf");
        var hero = OrderHeroes(runtime)[0];
        TestAssert.True(baron.Buffs.Any(buff => buff.Name == "ResistantSkin"), "the Baron carries ResistantSkin");
        foreach (var unit in new[] { dragon, baron })
        {
            hero.Position = new Position2(unit.Position.X + 200.0, unit.Position.Z);
            runtime.Buffs.AddBuff(unit, "ShredProbe", BuffType.Shred, 5.0, hero.NetId, canMitigateDuration: false);
            runtime.Buffs.AddBuff(unit, "InternalProbe", BuffType.Internal, 5.0, hero.NetId, canMitigateDuration: false);
        }
        runtime.Buffs.AddBuff(wolf, "ShredProbe", BuffType.Shred, 5.0, hero.NetId, canMitigateDuration: false);
        TestAssert.False(dragon.Buffs.Any(buff => buff.Name == "ShredProbe"), "the Dragon refuses a shred buff (ResistantSkinDragon)");
        TestAssert.False(baron.Buffs.Any(buff => buff.Name == "ShredProbe"), "the Baron refuses a shred buff (ResistantSkin)");
        TestAssert.True(wolf.Buffs.Any(buff => buff.Name == "ShredProbe"), "a wolf, with no skin, takes it");
        TestAssert.True(dragon.Buffs.Any(buff => buff.Name == "InternalProbe"), "the Dragon takes a non-shred buff from the same caster");
        TestAssert.True(baron.Buffs.Any(buff => buff.Name == "InternalProbe"), "the Baron takes a non-shred buff from the same caster");
    }

    /// <summary>
    /// 1.0.0.132 (130.42, Baron pushes wards away), both shipped paths, with real Sight Ward placements (batch 835's probe was a hand-built unit
    /// whose buff row carried no script, so no OnBuffActivate ran and it never moved):
    /// - a ward placed beside the standing Baron: Spells/SharedWardBuff.luaobj OnBuffActivate takes the closest unit within 300 that carries
    ///   ResistantSkin (AffectEnemies AffectNeutral AffectMinions, one unit) and BBMoveAway's the ward from it, Distance 300;
    /// - a ward already in the pit when the Baron spawns: Spells/ResistantSkin.luaobj OnBuffActivate moves every unit within 300 that carries
    ///   SharedWardBuff (AffectEnemies AffectMinions AffectWards) away from the Baron, Distance 300.
    /// Distance from a unit is a gap between the two bodies (BbNativeHandlers.MoveAway, Reconstruction W26d), so the ward ends 300 plus both
    /// radii from the Baron's centre. A ward 150 units from the Dragon stays: its skin is ResistantSkinDragon, which the exact filter does not
    /// name, and the change list names the Baron only.
    /// </summary>
    [Check("neutral.p132-baron-pushes-a-ward-away", TimeoutSeconds = 300)]
    public static void BaronPushesAWardAway()
    {
        var runtime = StartCampMatch();
        var hero = OrderHeroes(runtime)[0];
        // The hero stands 250 further out on the same line, outside the monster.
        Entity Place(Position2 point, double side)
        {
            hero.Position = new Position2(point.X + side * 250.0, point.Z);
            hero.Inventory = [new Amberline.Simulation.InventorySlot(2044), null, null, null, null, null];
            hero.ItemCooldowns[0] = 0.0;
            TestAssert.True(UseGroundItem(runtime, 1, hero, 0, point), $"a Sight Ward is placed at {point}");
            return runtime.World.Entities.Values.Single(unit => unit.IsWard && unit.Alive && Distance(unit.Position, point) < 1.0);
        }
        double Pushed(Entity ward, Entity from) => 300.0 + ward.CombatRadius + from.CombatRadius;

        // A ward in the pit before the spawn (the 180 s Sight Ward is placed 40 s before it).
        var pit = runtime.Waves!.LevelScript.NeutralCamps.Single(camp => camp.Number == 12).Positions[0];
        runtime.Tick(900.0 - 40.0 - runtime.World.Clock.Now());
        var early = Place(new Position2(pit.X + 150.0, pit.Z), 1.0);
        var baron = TickUntilBaronStands(runtime);
        runtime.Tick(1.0);
        TestAssert.True(early.Alive, "the early ward still stands");
        TestAssert.Near(Pushed(early, baron), Distance(early.Position, baron.Position), 5.0, "the Baron's spawn pushes the ward already in the pit");

        // A ward placed beside the standing Baron.
        var late = Place(new Position2(baron.Position.X - 150.0, baron.Position.Z), -1.0);
        runtime.Tick(1.0);
        TestAssert.Near(Pushed(late, baron), Distance(late.Position, baron.Position), 5.0, "a ward placed beside the Baron is pushed away");

        var dragon = runtime.World.Entities.Values.Single(entity => entity.Kind == EntityKind.Neutral && entity.Name == "Dragon" && entity.Alive);
        var stays = Place(new Position2(dragon.Position.X + 150.0, dragon.Position.Z), 1.0);
        runtime.Tick(1.0);
        TestAssert.Near(150.0, Distance(stays.Position, dragon.Position), 1.0, "a ward beside the Dragon stays (ResistantSkinDragon)");
    }

    private static double Distance(Position2 a, Position2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    // A ground cast of the hero's inventory slot through the client's own 0xA3 request (inventory slots are spell slots 4 to 9).
    private static bool UseGroundItem(MatchRuntime runtime, long connection, Entity hero, int inventorySlot, Position2 point)
    {
        var request = new byte[34];
        request[0] = 0xA3;
        request[5] = (byte)((inventorySlot + 4) << 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(1), hero.NetId);
        foreach (var offset in new[] { 6, 18 })
        {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset), (float)point.X);
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(request.AsSpan(offset + 8), (float)point.Z);
        }
        var accepted = false;
        runtime.Session.Receive(connection, 1, runtime.Cipher.Encrypt(request), context => accepted = runtime.Dependencies.Gameplay(context));
        return accepted;
    }

    // The Worm (Baron) camp's first spawn is 900 s (GroupDelaySpawnTime 800 after the first spawn at 100 s): tick until it stands.
    private static Entity TickUntilBaronStands(MatchRuntime runtime)
    {
        for (var step = 0; step < 120; step++)
        {
            var baron = Members(runtime, 12).FirstOrDefault(member => member.Name == "Worm");
            if (baron is not null) return baron;
            runtime.Tick(10.0);
        }
        throw new InvalidOperationException("the Baron (Worm, camp 12) never spawned");
    }
}
