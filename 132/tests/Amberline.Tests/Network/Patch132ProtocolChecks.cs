using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Network.Transport;

namespace Amberline.Tests.Network;

/// <summary>
/// Batch 767: the 1.0.0.132 protocol (scratch/patch132/c-protocol REPORT.md and opcode-map.csv, a static analysis of both clients). The
/// 132 pkttype_e inserts 0x42 and appends 0xF4 and 0xF5, ten server packets change layout, IsTargetable and IsTargetableToTeam leave the
/// replication tables (0xF4 carries them), hero mExp moves to MAP bit 4, the client sends "Version 1.0.0.132 [PUBLIC]" in C2S 0xC6, and its
/// ENet receive buffer is 996 bytes.
/// </summary>
public static class Patch132ProtocolChecks
{
    private const uint A = 0x40000001, B = 0x40010001;

    [Check("protocol.132-s2c-opcode-remap")]
    public static void ServerOpcodesFollowThe132Enum()
    {
        var catalog = PacketCatalog.Default;
        // Below 0x42 nothing moved; from 0x42 up every enum opcode is the 126 one plus 1; channels 0 and 6 keep their numbers.
        (byte Opcode, byte Channel, string Name)[] rows =
        [
            (0x0D, 3, "BasicAttack"), (0x3C, 3, "SpawnPet"), (0x41, 3, "MissileReplication"), (0x3A, 3, "LeaveLocalVisibility"),
            (0x46, 3, "LevelUp"), (0x50, 3, "CreateHero"), (0x58, 3, "SynchVersionS2C"), (0x81, 3, "SpawnMinion"), (0xA6, 3, "CreateTurret"),
            (0xBE, 3, "CastSpellAns"), (0xC3, 3, "EnterVisibility"), (0xCA, 3, "GameClock"), (0xCD, 3, "OnReplication"), (0xCF, 3, "EndGame"),
            (0xC2, 4, "WaypointList"), (0x65, 4, "WaypointGroup"), (0x68, 4, "WaypointGroupWithSpeed"), (0x9E, 4, "PingLoadInfo"),
            (0x66, 3, "StartSpawn"), (0x67, 3, "CreateNeutral"), (0x78, 3, "SetNexusSkin"), (0xF4, 3, "UnitTargetableStatusChanged"),
            (0x00, 0, "RegistryAdd"), (0x78, 0, "SetLocalClientId"),
            (0x65, 6, "RequestReskin"), (0x66, 6, "RequestRename"), (0x67, 6, "TeamRosterUpdate"),
        ];
        foreach (var (opcode, channel, name) in rows)
            TestAssert.True(catalog.TryGet(opcode, channel, out var spec) && spec.Name == name,
                $"0x{opcode:X2} on channel {channel} is {name} (found {(catalog.TryGet(opcode, channel, out var found) ? found.Name : "nothing")})");
        // The new 0x42 (MovementDriverReplication) and 0xF5 (shut down) are not sent; 126's numbers of moved rows are gone.
        TestAssert.False(catalog.KnowsOpcode(0x42) || catalog.KnowsOpcode(0xF5), "0x42 and 0xF5 are not sent");
        TestAssert.False(catalog.TryGet(0xCC, 3, out var old) && old.Name == "OnReplication", "0xCC is NeutralCampEmpty in 132, not the replication");

        // The builders write the 132 numbers.
        TestAssert.Equal((byte)0x46, CombatPackets.NpcLevelUp(A, 2, 1).Plaintext.Span[0], "LevelUp, the first moved row above 0x42");
        TestAssert.Equal((byte)0x3C, SpawnPackets.SpawnPet(new SpawnPetSnapshot(A, B, 0x40, default, "Pet", "Pet"), 0.0).Plaintext.Span[0]);
        TestAssert.Equal((byte)0x41, SpellPackets.MissileReplication(A, default, default, default, default, default, default, default, 1f, 0f, 0,
            new SpellCastInfo(7, 8, 1, A, B, default, default, [], 1, 2, 0, default)).Plaintext.Span[0], "0x41 keeps its number");
        TestAssert.Equal((byte)0x58, LoadingPackets.SynchVersionS2C().Plaintext.Span[0]);
        TestAssert.Equal((byte)0xCD, CombatPackets.HeroGold(A, 1f).Plaintext.Span[0]);
        TestAssert.Equal((byte)0x67, LoadingPackets.TeamRosterUpdate([1ul], [2ul]).Plaintext.Span[0], "the loading channel keeps 0x67");
        TestAssert.Equal((byte)'x', SpawnPackets.SetLocalClientId(1).Plaintext.Span[0], "channel 0 keeps 0x78");
    }

    [Check("protocol.132-c2s-opcode-remap")]
    public static void ClientOpcodesFollowThe132Enum()
    {
        // C2S 0xC6 is SynchVersion in 132 (126: 0xC5) and 0xC7 CharSelected, the world-ready report (126: 0xC6).
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), solo: true, readySettleSeconds: 0.0);
        var session = runtime.Session;
        runtime.World.Clock.SetVirtual(0.0);
        session.Receive(1, 0, session.BuildKeyCheck(1));
        TestAssert.Equal(0, session.Receive(1, 1, [0xC5]).Count, "126's SynchVersion number is no request in 132");
        var version = session.Receive(1, 1, [0xC6]);
        TestAssert.True(version.Any(item => item.Packet.Plaintext.Span[0] == 0x58) && version.Any(item => item.Packet.Plaintext.Span[0] == 0x99),
            "C2S 0xC6 is answered with SynchVersionS2C 0x58 and WorldSendGameNumber 0x99");
        var seat = session.Participant(1)!;
        session.Receive(1, 1, [0xC6, 0, 0, 0, 0]);
        TestAssert.False(seat.Loaded, "a 5-byte 0xC6 is the version request, not the world-ready report");
        session.Receive(1, 1, [0xC7, 0, 0, 0, 0]);
        TestAssert.True(seat.Loaded, "C2S 0xC7 (CharSelected) marks the seat loaded");

        // The client builders and readers.
        TestAssert.Equal((byte)0x76, ClientGameplayPackets.MoveOrder(A, 2, default).Plaintext.Span[0], "IssueOrderReq");
        TestAssert.Equal((byte)0x5B, PingPackets.ClientOpcode, "MapPing");
        TestAssert.Equal((byte)0x4D, EmotePackets.ClientOpcode, "PlayEmote");
        TestAssert.Equal((byte)0xAD, SurrenderPackets.ClientOpcode, "TeamSurrenderVote");
        TestAssert.Equal((byte)0xAA, PausePackets.PauseOpcode, "Pause, both directions");
        TestAssert.Equal((byte)0x0A, PausePackets.ResumeOpcode, "Resume keeps 0x0A");
        TestAssert.Equal(9u, ItemsPackets.ParseBuyItem([0x88, 1, 0, 0, 0x40, 9, 0, 0, 0]).ItemId, "BuyItemReq 0x88");
        TestAssert.Throws<ArgumentException>(() => ItemsPackets.ParseBuyItem([0x87, 1, 0, 0, 0x40, 9, 0, 0, 0]), "126's 0x87 is no buy");
    }

    [Check("protocol.132-layout-sizes")]
    public static void TheTenChangedLayouts()
    {
        // SynchVersionS2C 0x58: 4417 bytes, 159-byte PlayerLiteInfo (name char[64] at byte 0x17, champion char[64] at byte 0x57), the version at byte 0x77E,
        // the mode at byte 0x87E, then the ranked team names/tags and the stats endpoint, all empty.
        var players = new[] { new LeagueLoadingPlayer(7, "Summoner", "Annie", 100), new LeagueLoadingPlayer(8, "Other", "Ashe", 200) };
        var synch = LoadingPackets.SynchVersionS2C(players: players).Plaintext.Span;
        TestAssert.Equal(4417, synch.Length, "SynchVersionS2C");
        TestAssert.Equal(7ul, BinaryPrimitives.ReadUInt64LittleEndian(synch[10..]), "first player id");
        TestAssert.Equal("Summoner", Ascii(synch.Slice(10 + 0x17, 64)), "PlayerLiteInfo name at 0x17");
        TestAssert.Equal("Annie", Ascii(synch.Slice(10 + 0x57, 64)), "PlayerLiteInfo champion at 0x57");
        TestAssert.Equal(8ul, BinaryPrimitives.ReadUInt64LittleEndian(synch[(10 + 159)..]), "the second entry 159 bytes later");
        TestAssert.Equal(ulong.MaxValue, BinaryPrimitives.ReadUInt64LittleEndian(synch[(10 + 2 * 159)..]), "an empty slot");
        TestAssert.Equal("CLASSIC", Ascii(synch.Slice(0x87E, 128)), "the mode at 0x87E");
        TestAssert.True(synch[0x8FE..].IndexOfAnyExcept((byte)0) < 0, "no team names, tags or stats endpoint");

        // CreateHero 0x50: 191 bytes, the name char[128] at byte 0x17, the skin at byte 0x97.
        var hero = SpawnPackets.CreateHero(A, 1, new string('n', 100), "Annie", true).Plaintext.Span;
        TestAssert.Equal(191, hero.Length, "CreateHero");
        TestAssert.Equal(new string('n', 100), Ascii(hero.Slice(0x17, 128)), "a name longer than 126's 40 bytes");
        TestAssert.Equal("Annie", Ascii(hero.Slice(0x97, 40)), "the skin at 0x97");

        // SpawnMinion 0x81: 171 bytes, flags at byte 0x22 (bit 3 targetable), IsTargetableToTeam at byte 0x23, visibility at byte 0x27, name at byte 0x2B, skin at byte 0x6B.
        var minion = SpawnPackets.SpawnMinion(new SpawnMinionSnapshot(A, 0x40, default, 200, "Minion_Name", "Minion_Skin", IsWard: true,
            IgnoreCollision: true, VisibilitySize: 12.5f), 0.0).Plaintext.Span;
        TestAssert.Equal(171, minion.Length, "SpawnMinion");
        TestAssert.Equal(200u, BinaryPrimitives.ReadUInt32LittleEndian(minion[0x1E..]), "team at 0x1E");
        TestAssert.Equal((byte)0x0B, minion[0x22], "ignore collision 1 | ward 2 | targetable 8");
        TestAssert.Equal(CombatPackets.SFlagTargetableToAll, BinaryPrimitives.ReadUInt32LittleEndian(minion[0x23..]), "IsTargetableToTeam at 0x23");
        TestAssert.Equal(12.5f, BinaryPrimitives.ReadSingleLittleEndian(minion[0x27..]), "visibility size at 0x27");
        TestAssert.Equal("Minion_Name", Ascii(minion.Slice(0x2B, 64)));
        TestAssert.Equal("Minion_Skin", Ascii(minion.Slice(0x6B, 64)), "the skin ProcessSingleEvent reads at 0x6B");
        var cube = SpawnPackets.SpawnMinion(new SpawnMinionSnapshot(A, 0x40, default, 100, "TestCube", "TestCube", Targetable: false,
            TargetableToTeam: CombatPackets.SFlagNontargetableAll), 0.0).Plaintext.Span;
        TestAssert.Equal((byte)0, cube[0x22], "an untargetable unit clears bit 3");

        // CreateTurret 0xA6: 79 bytes, bit 0 of byte 0x4A targetable, IsTargetableToTeam at byte 0x4B.
        var turret = SpawnPackets.CreateTurret(A, 0x40, "Turret_T1_C_01_A", targetable: false, CombatPackets.SFlagNontargetableAll).Plaintext.Span;
        TestAssert.Equal(79, turret.Length, "CreateTurret");
        TestAssert.Equal("Turret_T1_C_01_A", Ascii(turret.Slice(0x0A, 64)));
        TestAssert.Equal((byte)0, turret[0x4A], "not targetable");
        TestAssert.Equal(CombatPackets.SFlagNontargetableAll, BinaryPrimitives.ReadUInt32LittleEndian(turret[0x4B..]));

        // SpawnPet 0x3C: 304 bytes, the name char[128] at byte 0x26 and every later field 96 bytes on.
        var pet = SpawnPackets.SpawnPet(new SpawnPetSnapshot(A, B, 0x40, default, "Pet", "PetSkin", CloneNetId: A, SkinId: 4,
            BuffName: "Buff", AiScript: "Pet.lua", ShowMinimapIcon: true), 0.0).Plaintext.Span;
        TestAssert.Equal(304, pet.Length, "SpawnPet");
        TestAssert.Equal("PetSkin", Ascii(pet.Slice(0xA6, 32)), "skin at 0xA6");
        TestAssert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(pet[0xC6..]), "skin id at 0xC6");
        TestAssert.Equal("Buff", Ascii(pet.Slice(0xCA, 64)), "buff name at 0xCA");
        TestAssert.Equal(A, BinaryPrimitives.ReadUInt32LittleEndian(pet[0x10A..]), "clone id at 0x10A");
        TestAssert.Equal("Pet.lua", Ascii(pet.Slice(0x10F, 32)), "AI script at 0x10F");
        TestAssert.Equal((byte)1, pet[0x12F], "minimap flag at 0x12F");

        // HeroReincarnate 0x4E: 21 bytes, the float at byte 0x11. ForceDead 0x1E: 9 bytes. StopAnimation 0x2B: 6 bytes, u8 flags.
        var respawn = CombatPackets.HeroReincarnate(A, new Position3(1, 2, 3), 250f).Plaintext.Span;
        TestAssert.Equal(21, respawn.Length, "HeroReincarnate");
        TestAssert.Equal(250f, BinaryPrimitives.ReadSingleLittleEndian(respawn[0x11..]), "the PAR at 0x11");
        TestAssert.Equal(9, CombatPackets.ForceDead(A).Plaintext.Length, "ForceDead");
        var stop = SpellPackets.StopAnimation(A, blend: false).Plaintext.Span;
        TestAssert.Equal(6, stop.Length, "StopAnimation");
        TestAssert.Equal((byte)0x03, stop[5], "past the lock (bit 1), hard stop (bit 0)");
        TestAssert.Equal((byte)0x02, SpellPackets.StopAnimation(A).Plaintext.Span[5], "a blended stop");

        // WorldSendGameNumber 0x99: the u64 and a NUL-terminated string (empty), 14 bytes.
        var number = LoadingPackets.WorldSendGameNumber(42).Plaintext.Span;
        TestAssert.Equal(14, number.Length, "WorldSendGameNumber");
        TestAssert.Equal(42ul, BinaryPrimitives.ReadUInt64LittleEndian(number[5..]));
        TestAssert.Equal((byte)0, number[13], "the string's NUL");

        // OnReplication 0xCD: the frame is unchanged.
        var block = CombatPackets.HeroGold(A, 5f, 3).Plaintext.Span;
        TestAssert.True(CombatPackets.TryReadReplicationBlock(block, out var sync, out var unit, stackalloc uint[5]) && sync == 3 && unit == A,
            "the 0xCD frame reads as 126's 0xCC did");
    }

    [Check("protocol.132-replication-bit-shift")]
    public static void ReplicationTablesWithoutTheTargetableFields()
    {
        Span<uint> masks = stackalloc uint[5];
        // Hero: CLIENT_ONLY gold 0, cast bits 1-2, mana costs 3-6; LOCAL1 without IsTargetable/IsTargetableToTeam; MAP mExp at 4.
        var hero = CombatPackets.HeroStats(A, new HeroStatsSnapshot(Armor: 21, MaxHp: 500, MaxMp: 300, MoveSpeed: 325, AttackSpeedMod: 0.7f),
            2, currentHp: 400, currentMp: 200, level: 3, gold: 475, exp: 280).Plaintext.Span;
        TestAssert.Equal(163, hero.Length, "the 132 hero block (126: 171)");
        TestAssert.True(CombatPackets.TryReadReplicationBlock(hero, out _, out _, masks), "hero block parses");
        TestAssert.Equal(0x7Fu, masks[0], "CLIENT_ONLY: gold, two cast-bit words, four mana costs");
        TestAssert.Equal(0x07F4FFD1u, masks[1], "LOCAL1 (126: 0x1FD3FF59)");
        TestAssert.Equal(0x29Fu, masks[3], "MAP: HP, MP, max HP, max MP, mExp, move speed, level");
        TestAssert.Equal(475f, BinaryPrimitives.ReadSingleLittleEndian(hero[19..]), "mGold is CLIENT_ONLY bit 0");
        TestAssert.Equal(21f, BinaryPrimitives.ReadSingleLittleEndian(hero[67..]), "mArmor is LOCAL1 bit 8 (126: 10)");
        TestAssert.Equal(0.7f, BinaryPrimitives.ReadSingleLittleEndian(hero[99..]), "mAttackSpeedMod is LOCAL1 bit 18 (126: 20)");
        TestAssert.Equal(280f, BinaryPrimitives.ReadSingleLittleEndian(hero[151..]), "mExp is MAP bit 4");
        TestAssert.Equal(325f, BinaryPrimitives.ReadSingleLittleEndian(hero[155..]), "mMoveSpeed is MAP bit 7 (126: 6)");
        TestAssert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(hero[159..]), "mLevelRef is MAP bit 9 (126: 8)");

        // The narrow hero blocks.
        TestAssert.Equal(0x01u, Mask(CombatPackets.HeroGold(A, 1f), 15), "gold alone: CLIENT_ONLY bit 0 (126: bit 1)");
        var goldExp = CombatPackets.HeroGoldExp(A, 10f, 20f).Plaintext.Span;
        TestAssert.Equal(31, goldExp.Length, "gold and experience");
        TestAssert.Equal((byte)0x09, goldExp[10], "CLIENT_ONLY and MAP");
        TestAssert.True(CombatPackets.TryReadReplicationBlock(goldExp, out _, out _, masks) && masks[0] == 0x01 && masks[3] == 0x10,
            "gold at CLIENT_ONLY 0, mExp at MAP 4");
        TestAssert.Equal(0x400u, Mask(CombatPackets.HeroNeutralKills(A, 1), 15), "mNumNeutralMinionsKilled MAP bit 10 (126: 9)");
        TestAssert.Equal(0x100u, Mask(CombatPackets.UnitModelScale(A, 1.2f, isHero: true), 15), "hero model scale MAP bit 8 (126: 7)");
        TestAssert.Equal(0x08u, Mask(CombatPackets.UnitModelScale(A, 1.2f, isHero: false), 15), "others keep MAP bit 3");
        TestAssert.Equal(0x60u, Mask(CombatPackets.UnitBubbleRadiusMods(A, 1f, 1f, isHero: true), 15), "hero bubble mods MAP 5-6 (126: 4-5)");
        TestAssert.Equal(0x03u, Mask(CombatPackets.UnitBubbleRadiusMods(A, 1f, 1f, isHero: false), 15), "others keep MAP 0-1");

        // Minion: 19 LOCAL1 entries, every field after IsInvulnerable two bits lower.
        var minion = CombatPackets.MinionStats(A, 400f, 12f, 325f, armor: 7f, attackSpeedMod: 1.5f, hpRegen: 2.5f).Plaintext.Span;
        TestAssert.Equal(103, minion.Length, "the 132 minion block (126: 111)");
        TestAssert.Equal(0x7FFFFu, Mask(minion, 15), "LOCAL1 bits 0-18 (126: 0-20)");
        TestAssert.Equal(12f, BinaryPrimitives.ReadSingleLittleEndian(minion[(19 + 4 * 8)..]), "mBaseAttackDamage bit 8 (126: 10)");
        TestAssert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(minion[(19 + 4 * 11)..]), "mAttackSpeedMod bit 11 (126: 13)");
        TestAssert.Equal(2.5f, BinaryPrimitives.ReadSingleLittleEndian(minion[(19 + 4 * 15)..]), "mHPRegenRate bit 15 (126: 17)");
        TestAssert.Equal(0x10u, Mask(CombatPackets.MinionActionState(A, 7), 15), "ActionState stays bit 4");
        TestAssert.Equal(0x800u, Mask(CombatPackets.MinionAttackSpeed(A, 1f), 15), "attack speed alone bit 11 (126: 0x2000)");
        TestAssert.Equal(0x801u, Mask(CombatPackets.MinionCurrentStats(A, 1f, 1f), 15), "current HP and attack speed (126: 0x2001)");
        TestAssert.Equal(0x70u, Mask(CombatPackets.MinionCombatState(A, 7, 0, 1), 15), "action, magic immune, invulnerable (126: 0xF0)");

        // Turret 14 entries, building 2.
        var turret = CombatPackets.TurretStats(A, 1000f, 150f).Plaintext.Span;
        TestAssert.Equal(83, turret.Length, "the 132 turret block (126: 91)");
        TestAssert.Equal(0x3FFFu, Mask(turret, 15), "turret LOCAL1 bits 0-13 (126: 0-15)");
        var protection = CombatPackets.TurretProtectionFlags(A, CombatPackets.SFlagNontargetableEnemy, 4, invulnerable: 1, targetable: 1);
        TestAssert.Equal(2, protection.Length, "the protection is a block and an 0xF4");
        TestAssert.Equal(0x1000u, Mask(protection[0], 15), "IsInvulnerable alone (126: 0x7000 with the two targetable fields)");
        TestAssert.Equal((byte)0xF4, protection[1].Plaintext.Span[0]);
        var building = CombatPackets.BuildingStats(A, 4000f, CombatPackets.SFlagTargetableToAll, invulnerable: 1);
        TestAssert.Equal(0x03u, Mask(building[0], 15), "building LOCAL1: mHP, IsInvulnerable (126: 0x0F)");
        TestAssert.Equal(27, building[0].Plaintext.Length);
        TestAssert.Equal((byte)0xF4, building[1].Plaintext.Span[0]);
    }

    [Check("protocol.132-unit-targetable-status-0xf4")]
    public static void TargetabilityIsItsOwnPacket()
    {
        var packet = CombatPackets.UnitTargetableStatus(A, false, CombatPackets.SFlagNontargetableEnemy);
        var bytes = packet.Plaintext.Span;
        TestAssert.Equal(10, bytes.Length, "[0xF4][net id][IsTargetable u8][IsTargetableToTeam u32]");
        TestAssert.Equal((byte)0xF4, bytes[0]);
        TestAssert.Equal(A, BinaryPrimitives.ReadUInt32LittleEndian(bytes[1..]));
        TestAssert.Equal((byte)0, bytes[5]);
        TestAssert.Equal(CombatPackets.SFlagNontargetableEnemy, BinaryPrimitives.ReadUInt32LittleEndian(bytes[6..]));
        TestAssert.Equal(LeagueProtocolConstants.VariantChannel, packet.Channel);
        TestAssert.True(packet.Reliability == LeaguePacketReliability.Reliable && packet.Encrypt, "reliable and encrypted");

        // The unit's own state: the team flags a client is told (WireNotTargetable) and the buff-derived IsTargetable.
        var ward = new Entity(B, EntityKind.Minion, UnitType.Minion, Team.Order, default) { NotTargetableToEnemy = true };
        var status = CombatPackets.UnitTargetableStatus(ward).Plaintext.Span;
        TestAssert.Equal((byte)1, status[5], "no untargetable buff");
        TestAssert.Equal(0x01000000u, BinaryPrimitives.ReadUInt32LittleEndian(status[6..]), "not targetable to the enemy");
        TestAssert.Equal(CombatPackets.SFlagTargetableToAll,
            BinaryPrimitives.ReadUInt32LittleEndian(CombatPackets.UnitTargetableStatus(new Entity(B, EntityKind.Minion, UnitType.Minion,
                Team.Order, default), true).Plaintext.Span[6..]), "no flag is targetable to all (126 replicated a raw 1)");

        // Each client is sent a state once: a repeat is dropped, a creation of the unit (the client's SetTargetable runs again) or a change
        // lets the next one through.
        var store = new ClientReplicationStore(_ => false);
        TestAssert.True(store.Process(packet) is not null, "the first status goes out");
        TestAssert.True(store.Process(packet) is null, "the same status again is not sent");
        TestAssert.True(store.Process(CombatPackets.UnitTargetableStatus(A, true, CombatPackets.SFlagNontargetableEnemy)) is not null, "a change goes out");
        store.Process(SpawnPackets.SpawnMinion(new SpawnMinionSnapshot(A, 0x40, default, 100, "Minion_Basic", "Minion_Basic"), 0.0));
        TestAssert.True(store.Process(CombatPackets.UnitTargetableStatus(A, true, CombatPackets.SFlagNontargetableEnemy)) is not null,
            "after a creation the status goes out again");
        TestAssert.True(PacketCatalog.Default.TryGet(0xF4, LeagueProtocolConstants.VariantChannel, out var spec) &&
            spec.References.SequenceEqual([1]), "the catalog row names the unit at 1");
    }

    [Check("protocol.132-version-echo")]
    public static void TheServerEchoesThe132Version()
    {
        TestAssert.Equal("Version 1.0.0.132 [PUBLIC]", LeagueProtocolConstants.VersionString, "the string the 132 client sends in C2S 0xC6");
        var synch = LoadingPackets.SynchVersionS2C().Plaintext.Span;
        TestAssert.Equal((byte)1, synch[5], "isVersionOk, the one byte the client checks");
        TestAssert.Equal("Version 1.0.0.132 [PUBLIC]", Ascii(synch.Slice(0x77E, 256)), "the server string the client prints (+0x77E)");

        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), solo: true, readySettleSeconds: 0.0);
        var session = runtime.Session;
        session.Receive(1, 0, session.BuildKeyCheck(1));
        var request = new byte[269];
        request[0] = 0xC6;
        Encoding.ASCII.GetBytes("Version 1.0.0.132 [PUBLIC]").CopyTo(request, 5);
        // The client's 269-byte request goes Blowfish-encrypted in whole blocks, the 5-byte tail as it is.
        byte[] wire = [.. runtime.Cipher.Encrypt(request.AsSpan(0, 264)), .. request.AsSpan(264)];
        var answer = session.Receive(1, 1, wire).Single(item => item.Packet.Plaintext.Span[0] == 0x58).Packet.Plaintext.Span;
        TestAssert.Equal(4417, answer.Length);
        TestAssert.Equal((byte)1, answer[5], "the session's answer says the version is fine");
        TestAssert.Equal("Version 1.0.0.132 [PUBLIC]", Ascii(answer.Slice(0x77E, 256)));
    }

    [Check("transport.132-mtu-996")]
    public static void EveryPeerIsCappedAtTheClientsReceiveBuffer()
    {
        using var server = new LeagueEnetHost(port: 0, peerLimit: 1, channelLimit: 8, bindHost: "127.0.0.1");
        using var client = new LeagueEnetHost(port: 0, peerLimit: 1, channelLimit: 8, bindHost: "127.0.0.1");
        var clientPeer = client.Connect("127.0.0.1", server.Port, channelCount: 8);
        LeagueEnetPeer? serverPeer = null;
        var connected = false;
        Pump(server, client, () => connected && serverPeer is not null, @event =>
        {
            if (@event.Type != LeagueEnetEventType.Connect) return;
            if (ReferenceEquals(@event.Peer, clientPeer)) connected = true;
            else serverPeer = @event.Peer;
        });
        TestAssert.Equal(996, LeagueEnetHost.MaximumMtu, "the 132 client's ENET_PROTOCOL_MAXIMUM_MTU (0x3E4)");
        TestAssert.Equal((ushort)996, serverPeer!.Stats.Mtu, "the server's peer of a connecting client is capped at 996");
        TestAssert.Equal((ushort)996, clientPeer.Stats.Mtu, "and the verify-connect tells the client 996");

        // A SynchVersionS2C-sized packet goes out in fragments no larger than the cap and arrives whole.
        var payload = Enumerable.Range(0, 4417).Select(index => (byte)index).ToArray();
        serverPeer.Send(3, payload, LeagueEnetPacketFlags.Reliable);
        server.Flush();
        byte[]? received = null;
        Pump(server, client, () => received is not null, @event =>
        {
            if (@event.Type == LeagueEnetEventType.Receive && ReferenceEquals(@event.Peer, clientPeer)) received = @event.Payload.ToArray();
        });
        TestAssert.True(received!.SequenceEqual(payload), "the fragmented packet is reassembled");
    }

    /// <summary>
    /// The 1.0.0.132 client's EventDetailsList in id order: the event byte of 0x4B and 0xAC indexes it, and the announcer finds the event's
    /// Announcements_*.ini section by the name. Read from the shipped 132 executable's static initializer (, table, 20 bytes
    /// per id; scratch/patch132/n-crash/scripts/evscan.py). 132 inserted ids 3, 5, 6, 56 to 60 and 89 to 91 into 126's list of 99.
    /// </summary>
    private static readonly string[] EventDetailsList132 = (
        "OnDie OnKill OnChampionDie OnChampionKillPre OnChampionKill OnChampionKillPost OnChampionSingleKill " +
        "OnChampionDoubleKill OnChampionTripleKill OnChampionQuadraKill OnChampionPentaKill OnChampionUnrealKill OnFirstBlood " +
        "OnDamageTaken OnDamageGiven OnSpellCast1 OnSpellCast2 OnSpellCast3 OnSpellCast4 OnSpellAvatarCast1 OnSpellAvatarCast2 " +
        "OnGoldSpent OnGoldEarned OnItemConsumeablePurchased OnCriticalStrike OnAce OnReincarnate OnDampenerKill OnDampenerDie " +
        "OnDampenerRespawnSoon OnDampenerRespawn OnTurretKill OnTurretDie OnMinionKill OnMinionDenied OnNeutralMinionKill " +
        "OnSuperMonsterKill OnHQKill OnHQDie OnHeal OnCastHeal OnBuff OnKillingSpree OnKillingSpreeSet1 OnKillingSpreeSet2 " +
        "OnKillingSpreeSet3 OnKillingSpreeSet4 OnKillingSpreeSet5 OnKillingSpreeSet6 OnKilledUnitOnKillingSpree " +
        "OnKilledUnitOnKillingSpreeSet1 OnKilledUnitOnKillingSpreeSet2 OnKilledUnitOnKillingSpreeSet3 " +
        "OnKilledUnitOnKillingSpreeSet4 OnKilledUnitOnKillingSpreeSet5 OnKilledUnitOnKillingSpreeSet6 " +
        "OnKilledUnitOnKillingSpreeDoubleKill OnKilledUnitOnKillingSpreeTripleKill OnKilledUnitOnKillingSpreeQuadraKill " +
        "OnKilledUnitOnKillingSpreePentaKill OnKilledUnitOnKillingSpreeUnrealKill OnDeathAssist OnQuit OnLeave OnReconnect " +
        "OnGameStart OnPing OnPingPlayer OnPingBuilding OnPingOther OnEndGame OnSpellLevelup1 OnSpellLevelup2 OnSpellLevelup3 " +
        "OnSpellLevelup4 OnItemPurchased OnSurrenderVoteStart OnSurrenderVote OnSurrenderVoteAlready OnSurrenderCountDown " +
        "OnSurrenderFailedVotes OnSurrenderTooEarly OnSurrenderAgreed OnSurrenderSpam OnPause OnPauseResume OnMinionsSpawn " +
        "OnStartGameMessage1 OnStartGameMessage2 OnStartGameMessage3 OnStartGameMessage4 OnStartGameMessage5 OnAlert " +
        "OnScoreboardOpen OnAudioEventFinished OnNexusCrystalStart OnCapturePointNeutralized_A OnCapturePointNeutralized_B " +
        "OnCapturePointNeutralized_C OnCapturePointNeutralized_D OnCapturePointNeutralized_E OnCapturePointCaptured_A " +
        "OnCapturePointCaptured_B OnCapturePointCaptured_C OnCapturePointCaptured_D OnCapturePointCaptured_E " +
        "OnVictoryPointThreshold1 OnVictoryPointThreshold2 OnVictoryPointThreshold3 OnVictoryPointThreshold4").Split(' ');

    /// <summary>
    /// Batch 780 (the crash of the owner's first native 132 game): the server sent 126's OnStartGameMessage2 id 80 at HUD 60, which is
    /// OnSurrenderFailedVotes in 132; its chat line "Surrender vote failed for @Team@ ..." read a team past the 18-byte packet and
    /// the client's team-name lookup indexed its name table with it (an access violation). Every event id the server writes
    /// names the 132 event it means.
    /// </summary>
    [Check("protocol.132-world-event-ids")]
    public static void EveryServerEventIdNamesThe132Event()
    {
        TestAssert.Equal(110, EventDetailsList132.Length, "the 132 list has 110 ids");
        string Name(byte id) => id < EventDetailsList132.Length ? EventDetailsList132[id] : $"no event {id}";

        // The classic match's own announcements, as the session schedules and sends them: the crash's packet.
        using var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(), solo: true, readySettleSeconds: 0.0);
        var session = runtime.Session;
        runtime.World.Clock.SetVirtual(0.0);
        session.Authenticate(1, session.BuildKeyCheck(1));
        session.Spawn(1);
        session.MarkReady(1);
        session.RunSchedulers();
        runtime.Tick(1.25);
        session.RunSchedulers();
        TestAssert.Equal("OnStartGameMessage1 OnStartGameMessage2 OnMinionsSpawn",
            string.Join(' ', session.Announcements.Select(item => Name(item.EventId))), "the HUD 30, 60 and 90 s announcements");
        runtime.Tick(61.0 - session.HudTime!.Value);
        var sent = session.RunSchedulers().Select(item => item.Packet.Plaintext.ToArray()).Where(bytes => bytes[0] == 0x4B).ToArray();
        TestAssert.Equal("OnStartGameMessage1 OnStartGameMessage2", string.Join(' ', sent.Select(bytes => Name(bytes[5]))),
            "the 0x4B packets sent by HUD 61 s (126's 79 and 80 are OnSurrenderCountDown and OnSurrenderFailedVotes in 132)");
        TestAssert.Equal(18, sent[1].Length, "the 18-byte form: only events whose texts read no further than the map number fit it");

        // Every other id the builders write.
        TestAssert.Equal("OnChampionDie", Name(CombatPackets.ChampionDie(A, B, 300f, []).Plaintext.Span[5]));
        TestAssert.Equal("OnChampionDoubleKill OnChampionTripleKill OnChampionQuadraKill OnChampionPentaKill OnChampionUnrealKill OnChampionUnrealKill",
            string.Join(' ', Enumerable.Range(2, 6).Select(count => Name(CombatPackets.HeroMultiKill(A, B, count).Plaintext.Span[5]))));
        foreach (var point in Enumerable.Range(1, 5))
        {
            var letter = (char)('A' + point - 1);
            TestAssert.Equal($"OnCapturePointNeutralized_{letter}", Name(CombatPackets.CapturePointEventId(false, point)));
            TestAssert.Equal($"OnCapturePointCaptured_{letter}", Name(CombatPackets.CapturePointEventId(true, point)));
        }
        foreach (var stage in Enumerable.Range(1, 3))
            TestAssert.Equal($"OnVictoryPointThreshold{stage}", Name((byte)(CombatPackets.VictoryPointThreshold1 + stage - 1)));
        TestAssert.Equal("OnVictoryPointThreshold4", Name(CombatPackets.QuestReceived), "the Dominion quest announcement");

        // Batch 859: every death recap (0x24) record names OnDamageGiven. 126's 11 is OnChampionUnrealKill here, whose BaseParams reader
        // takes 9 bytes, not ParamsDamage's 0x29: the client walked a recap of several records past its buffer.
        var origin = Amberline.Simulation.Combat.DeathRecapOrigin.BasicAttack(0, Amberline.Simulation.Combat.DeathRecapSourceType.Minion);
        var records = new[]
        {
            new Amberline.Simulation.Combat.DeathRecapRecord(1.0, A, B, 20.0, 0.0, 0.0, origin),
            new Amberline.Simulation.Combat.DeathRecapRecord(2.0, A, B + 1, 20.0, 0.0, 0.0, origin),
            new Amberline.Simulation.Combat.DeathRecapRecord(3.0, A, B, 20.0, 0.0, 0.0, origin),
        };
        var recap = CombatPackets.DeathRecap(new Amberline.Simulation.Combat.DeathRecapSnapshot(1, A, B, 15.0,
            Amberline.Simulation.Combat.DeathRecapSourceType.Minion, records), records).Plaintext.ToArray();
        var offset = 0x19;
        foreach (var index in Enumerable.Range(0, records.Length))
        {
            TestAssert.Equal("OnDamageGiven", Name(recap[offset + 6]), $"death recap record {index}");
            offset += 6 + 0x29;
        }
        TestAssert.Equal(recap.Length, offset, "the client's walk (six-byte prefix plus ParamsDamage's 0x29 bytes a record) ends at the packet's end");
    }

    private static uint Mask(PlaintextPacket packet, int offset) => Mask(packet.Plaintext.Span, offset);

    private static uint Mask(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    private static string Ascii(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? field : field[..end]);
    }

    private static void Pump(LeagueEnetHost server, LeagueEnetHost client, Func<bool> complete, Action<LeagueEnetEvent> observe)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
        while (!complete())
        {
            observe(server.Service(20));
            observe(client.Service(20));
            if (Stopwatch.GetTimestamp() > deadline) throw new InvalidOperationException("ENet loopback event did not arrive within five seconds");
        }
    }
}
