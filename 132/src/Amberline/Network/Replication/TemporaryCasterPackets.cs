using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Simulation.Spells;

namespace Amberline.Network.Replication;

/// <summary>Stock TestCube template for a script-spawned, worldless missile caster.</summary>
public static class TemporaryCasterPackets
{
    public static IReadOnlyList<PlaintextPacket> Create(
        MissileCasterProjection caster, SpellCastInfo castInfo, Func<uint, uint> nextSyncId)
    {
        var origin = new Position2(caster.Origin.X, caster.Origin.Z);
        return
        [
            SpawnPackets.SpawnMinion(new SpawnMinionSnapshot(caster.NetId, 0x40,
                origin, TeamMapping.ToNative(caster.Team), "TestCube", "TestCubeRender",
                IgnoreCollision: true, Targetable: false, TargetableToTeam: CombatPackets.SFlagNontargetableAll), caster.Origin.Y),
            CombatPackets.MinionStats(caster.NetId, new MinionStatsSnapshot(
                Hp: 1, AttackDamage: 0, Speed: 0, Armor: 0, SpellBlock: 0,
                ActionState: Amberline.Simulation.Buffs.CharacterState.NoRenderBit, MagicImmune: 0, Invulnerable: 1), nextSyncId(caster.NetId)),
            CombatPackets.MinionVisionPercentModifier(caster.NetId, 0,
                nextSyncId(caster.NetId)),
            VisionPackets.EnterVisibilityClient(caster.NetId, "TestCubeRender",
                0, 1, 1, origin, caster.Facing),
            VisionPackets.EnterLocalVisibility(caster.NetId, 1, 1),
            // HowlingGale.luaobj assigns ExtraSlots[0] after creating TestCube;
            // its character data has no wave spell to seed locally.
            SpellPackets.SetSpellData(caster.NetId, castInfo.SpellHash, SpellSystem.ExtraSpellSlotStart),
        ];
    }
}
