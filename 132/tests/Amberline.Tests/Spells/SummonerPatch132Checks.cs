using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Simulation;

namespace Amberline.Tests;

/// <summary>
/// Batch 772: the summoner spells of 1.0.0.132 on the game server (the AIR offer is a later batch). The numbers are the 132 scripts' own
/// (Spells/Summoner*.luaobj); the wiki ids name the change.
/// </summary>
public static partial class SummonerAdvancedChecks
{
    /// <summary>
    /// 129.44: SummonerFlash.luaobj's SelfExecute clamps the cast point to 400 along the facing (126: 475), and the engine's announced landing
    /// (SpellSystem.FlashRange) is that reach. The cast cooldown is the script's 265.
    /// </summary>
    [Check("spells-summoners-p132-flash-reaches-400")]
    public static void FlashReaches400()
    {
        var runtime = CreateRuntime();
        var start = new Position2(7000.0, 7000.0);
        var hero = Hero(runtime, Team.Order, start, ["SummonerFlash", "SummonerDot"], level: 5);
        runtime.HeroStats.Refresh(hero);
        TestAssert.Equal(400.0, Amberline.Simulation.Spells.SpellSystem.FlashRange, "the engine's Flash reach is the 132 script's");
        var target = new Position2(start.X + 700.0, start.Z + 700.0);
        TestAssert.True(Cast(runtime, hero, 0, target), "Flash is cast");
        var moved = Math.Sqrt((hero.Position.X - start.X) * (hero.Position.X - start.X) + (hero.Position.Z - start.Z) * (hero.Position.Z - start.Z));
        // The landing is the client grid point the placement correction picks (SpellSystem.CorrectTeleportDestination), a few units short.
        TestAssert.True(moved > 390.0 && moved < 401.0, $"Flash lands 400 away, not 126's 475 (moved {moved:0.##})");
        TestAssert.Near(265.0, hero.SummonerCooldowns[0], 1e-9, "Flash cools for 265");
    }

    /// <summary>129.45: Fortify and Rally left the summoner pool; the server refuses their cast and starts no cooldown, a classic spell still casts.</summary>
    [Check("spells-summoners-p132-fortify-and-rally-are-refused")]
    public static void FortifyAndRallyAreRefused()
    {
        TestAssert.Equal(new[] { "SummonerFortify", "SummonerRally" }, SummonerLoadout.RetiredNames.ToArray(), "the retired pair");
        var runtime = CreateRuntime();
        var hero = Hero(runtime, Team.Order, SpawnPoint(), ["SummonerFortify", "SummonerRally"], level: 5);
        TestAssert.False(Cast(runtime, hero, 0, hero.Position), "Fortify is refused");
        TestAssert.False(Cast(runtime, hero, 1, hero.Position), "Rally is refused");
        TestAssert.Equal(new[] { 0.0, 0.0 }, hero.SummonerCooldowns.Take(2).ToArray(), "no cooldown starts");
        hero.SummonerSpells = ["SummonerHaste", "SummonerRally"];
        TestAssert.True(Cast(runtime, hero, 0, hero.Position), "control: Ghost still casts");
    }

    /// <summary>
    /// 129.40 / 132.53: Surge is Spells/SummonerBattleCry.luaobj. Its TargetExecute adds the SummonerBattleCry buff to the caster for 12 s:
    /// IncPercentAttackSpeedMod 0.35 and IncFlatMagicDamageMod {10, 14, 18, 22, 26 ...}[level]; AdjustCooldown 180.
    /// </summary>
    [Check("spells-summoners-p132-surge-is-summoner-battle-cry")]
    public static void SurgeIsSummonerBattleCry()
    {
        TestAssert.Equal("SummonerBattleCry", SummonerLoadout.CanonicalFromDisplay("Surge"), "Surge is SummonerBattleCry");
        var runtime = CreateRuntime();
        var hero = Hero(runtime, Team.Order, SpawnPoint(), ["SummonerBattleCry", "SummonerFlash"], level: 5);
        runtime.HeroStats.Refresh(hero);
        var abilityPower = hero.AbilityPower;
        var attackSpeed = runtime.HeroStats.Calculate(hero).AttackSpeedMod;
        TestAssert.True(Cast(runtime, hero, 0, hero.Position), "Surge is cast");
        TestAssert.Near(180.0, hero.SummonerCooldowns[0], 1e-9, "Surge cools for 180");
        Tick(runtime, 0.25);
        TestAssert.True(hero.Buffs.Any(buff => buff.Name == "SummonerBattleCry"), "the Surge buff is on the caster");
        TestAssert.Near(abilityPower + 26.0, hero.AbilityPower, 1e-6, "level 5: +26 ability power");
        TestAssert.Near(attackSpeed + 0.35, runtime.HeroStats.Calculate(hero).AttackSpeedMod, 1e-6, "+35 % attack speed");
        Tick(runtime, 12.0);
        TestAssert.False(hero.Buffs.Any(buff => buff.Name == "SummonerBattleCry"), "the buff lasts 12 s");
    }

    /// <summary>
    /// 129.48 / 129.49: Promote reaches Summoner's Rift as Spells/SummonerPromoteSR.luaobj. Its CanCast wants an allied minion within 1000 that
    /// holds PromoteMeBuff (the siege minions' CharOnActivate adds it) and does not hold SummonerPromoteSR yet; SelfExecute promotes the
    /// nearest one (SummonerPromoteSR buff, 25000 s) and AdjustCooldown says 180.
    /// </summary>
    [Check("spells-summoners-p132-promote-on-summoners-rift")]
    public static void PromoteOnSummonersRift()
    {
        TestAssert.Equal("SummonerPromoteSR", SummonerLoadout.CanonicalFromDisplay("Promote"), "Promote is SummonerPromoteSR");
        var runtime = CreateRuntime();
        var hero = Hero(runtime, Team.Order, new Position2(7000.0, 7000.0), ["SummonerPromoteSR", "SummonerFlash"], level: 5);
        runtime.HeroStats.Refresh(hero);
        TestAssert.False(Cast(runtime, hero, 0, hero.Position), "no promotable minion: the script's CanCast refuses");
        var cannon = new Entity(runtime.World.Allocate(UnitType.Minion), EntityKind.Minion, UnitType.Minion, Team.Order,
            new Position2(hero.Position.X + 300.0, hero.Position.Z))
        {
            Name = "Blue_Minion_MechCannon", ChampionName = "Blue_Minion_MechCannon", Health = 700.0, MaxHealth = 700.0,
            Alive = true, Created = true, Visible = true, Radius = 65.0,
        };
        runtime.World.Register(cannon);
        TestAssert.True(runtime.Spells.UnitScripts!.AddShippedBuff(cannon, "PromoteMeBuff", BuffType.Internal, 25000.0) is not null,
            "the siege minion's own PromoteMeBuff");
        Tick(runtime, 0.3);
        TestAssert.True(Cast(runtime, hero, 0, hero.Position), "Promote is cast beside the siege minion");
        TestAssert.True(cannon.Buffs.Any(buff => buff.Name == "SummonerPromoteSR"), "the siege minion is promoted");
        TestAssert.Near(180.0, hero.SummonerCooldowns[0], 1e-9, "Promote cools for 180");
    }
}
