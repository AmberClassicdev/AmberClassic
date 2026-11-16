using Amberline.Core;
using Amberline.Core.Buffs;
using Amberline.Data;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;
using Amberline.Simulation.Combat;
using Amberline.Simulation.Movement;
using Amberline.Simulation.Neutral;

namespace Amberline.Tests.Network.Neutral;

// Batch 219 (coordinator follow-up) paid a camp's experience through the script's GiveExpToNearHeroesFromNeutral. Batch 771 (1.0.0.132): the
// Summoner's Rift NeutralMinionDeath no longer calls it, and the kill path pays the monster's own reward (its record's ExpGivenOnDeath and what
// its scripts added) over 400 units, the killer always included (V1.0.0.130). Each check runs a production match with three Order heroes
// (near at 250 units from the monster, mid at 500, far at 1500) and one Chaos hero at 1500, then kills every member of the camp type with
// one hit from the mid hero, outside the 400: the near hero and the killer share the reward (the map's two-recipient share), the far hero
// gets only the team's global experience (Worm), and the killer the monster's gold (its record's and what BBIncPermanentGoldReward added).
public static class ScriptedCampExperienceChecks
{
    [Check("neutral.camp-experience.wraith")]
    public static void WraithCampsPayTheirShare() => AssertCampShares("Wraith", 3, 9);

    [Check("neutral.camp-experience.golem")]
    public static void GolemCampsPayTheirShare() => AssertCampShares("Golem", 5, 11);

    [Check("neutral.camp-experience.wolves")]
    public static void WolvesCampsPayTheirShare() => AssertCampShares("Wolves", 2, 8);

    [Check("neutral.camp-experience.lizard")]
    public static void LizardCampsPayTheirShare() => AssertCampShares("Lizard", 4, 10);

    [Check("neutral.camp-experience.ancient-golem")]
    public static void AncientGolemCampsPayTheirShare() => AssertCampShares("Ancient Golem", 1, 7);

    [Check("neutral.camp-experience.dragon")]
    public static void DragonCampPaysItsShare() => AssertCampShares("Dragon", 6);

    [Check("neutral.camp-experience.baron")]
    public static void BaronCampPaysItsShare() => AssertCampShares("Baron", 12);

    /// <summary>
    /// Batch 771: the 132 Summoner's Rift script never calls GiveExpToNearHeroesFromNeutral, so a camp kill pays its experience with the
    /// script's native switched off, and pays it once (the alone killer gets the whole reward, solo factor 1.0). Replaces the 126 check
    /// <c>neutral.camp-experience-script-pays-only</c> (the script was the only payer there).
    /// </summary>
    [Check("neutral.camp-experience-kill-path-pays-on-132")]
    public static void KillPathPaysCampExperienceWithoutTheScriptNative()
    {
        var runtime = StartCampMatch();
        runtime.Waves!.LevelScript.AwardNeutralExperience = null;
        var near = OrderHeroes(runtime)[0];
        var monster = Members(runtime, 5)[0];
        Place(near, monster, 250.0);
        var before = near.Experience;
        TestAssert.True(runtime.Combat.ApplyHit(near.NetId, monster.NetId, monster.Health + 1.0,
            DamageType.True, DamageSourceKind.Spell), "one hit kills the first golem camp member");
        TestAssert.True(monster.ExperienceReward > 0.0, "a 132 camp monster carries its own reward");
        TestAssert.Near(monster.ExperienceReward * (1.0 + Bonus(near)), near.Experience - before, 1e-6,
            "the kill path pays the monster's own reward once, without the script's native");
    }

    /// <summary>
    /// Batch 771: the rewards a 132 camp monster carries at the first spawn, independent of the kill path: its record's ExpGivenOnDeath and
    /// GoldGivenOnDeath (the 132 DATA/Characters records) plus its CharOnActivate's BBIncPermanentExpReward / GoldReward deltas (Golem +24 / +8,
    /// SmallGolem -12 / -8, GiantWolf +14 / +6, wolf -5 / -3, Wraith +33, LesserWraith -11). The small camps spawn at 100 s, before
    /// GlobalMonsterBuff's spawnTime of 101, so it adds nothing yet.
    /// </summary>
    [Check("neutral.camp-experience.p132-monster-rewards")]
    public static void CampMonstersCarryTheir132Rewards()
    {
        var runtime = StartCampMatch();
        (string Name, int Camp, double Experience, double Gold)[] expected =
        [
            ("wolf", 2, 15 - 5, 8 - 3), ("GiantWolf", 2, 114 + 14, 38 + 6),
            ("Wraith", 3, 72 + 33, 33), ("LesserWraith", 3, 22 - 11, 4),
            ("SmallGolem", 5, 50 - 12, 23 - 8), ("Golem", 5, 93 + 24, 32 + 8),
        ];
        foreach (var (name, camp, experience, gold) in expected)
        {
            var members = Members(runtime, camp).Where(member => member.Name == name).ToArray();
            TestAssert.True(members.Length > 0, $"camp {camp} has a {name}");
            foreach (var monster in members)
            {
                TestAssert.Near(experience, monster.ExperienceReward, 1e-6, $"{name} experience reward");
                TestAssert.Near(gold, monster.GoldReward, 1e-6, $"{name} gold reward");
            }
        }
    }

    /// <summary>
    /// Batch 771: the experience range is 400 (V1.0.0.130): a champion of the killer's team at 450 units shares nothing, and the killer at
    /// 350 takes the whole reward (solo factor 1.0).
    /// </summary>
    [Check("neutral.camp-experience.p132-range-400")]
    public static void CampExperienceReachesFourHundredUnits()
    {
        var runtime = StartCampMatch();
        var heroes = OrderHeroes(runtime);
        var monster = Members(runtime, 2).First(member => member.Name == "GiantWolf");
        Place(heroes[0], monster, 350.0);
        Place(heroes[1], monster, 450.0);
        Place(heroes[2], monster, 1500.0);
        var before = heroes.Select(hero => hero.Experience).ToArray();
        TestAssert.True(runtime.Combat.ApplyHit(heroes[0].NetId, monster.NetId, monster.Health + 1.0,
            DamageType.True, DamageSourceKind.Spell), "one hit kills the GiantWolf");
        TestAssert.Near(monster.ExperienceReward * (1.0 + Bonus(heroes[0])), heroes[0].Experience - before[0], 1e-6,
            "the killer at 350 takes the whole reward");
        TestAssert.Near(0.0, heroes[1].Experience - before[1], 1e-9, "a teammate at 450 is outside the 400 range");
        TestAssert.Near(0.0, heroes[2].Experience - before[2], 1e-9, "a teammate at 1500 gets nothing");
    }

    /// <summary>
    /// 1.0.0.132: a wall push is the buff <c>GlobalWallPush</c>, and the monsters that must not be moved refuse it in BuffOnAllowAdd by
    /// its script name (<c>ScriptName == "globalwallpush"</c>; the Dragon's <c>ResistantSkinDragon</c>). A wolf, which carries no such buff, takes it.
    /// </summary>
    [Check("neutral.p132-dragon-refuses-the-global-wall-push")]
    public static void TheDragonRefusesTheGlobalWallPush()
    {
        var runtime = StartCampMatch();
        runtime.Tick(40.0);
        var dragon = runtime.World.Entities.Values.Single(entity => entity.Kind == EntityKind.Neutral && entity.Name == "Dragon");
        var wolf = Members(runtime, 2).First(member => member.Name == "GiantWolf");
        var hero = OrderHeroes(runtime)[0];
        runtime.Buffs.AddBuff(dragon, "GlobalWallPush", BuffType.Internal, 1.0, hero.NetId, canMitigateDuration: false);
        runtime.Buffs.AddBuff(wolf, "GlobalWallPush", BuffType.Internal, 1.0, hero.NetId, canMitigateDuration: false);
        TestAssert.False(dragon.Buffs.Any(buff => buff.Name == "GlobalWallPush"), "the Dragon refuses a wall push (ResistantSkinDragon)");
        TestAssert.True(wolf.Buffs.Any(buff => buff.Name == "GlobalWallPush"), "a wolf takes it");
    }

    /// <summary>
    /// Batch 787: 1.0.0.132 CharScriptGolem and CharScriptSmallGolem add GlobalMonsterBuff with TargetVar and AttackerVar "Attacker", where
    /// CharScriptWolf and CharScriptGiantWolf name "Owner". A character's CharOnActivate binds Attacker to the unit itself
    /// (BbHookRouter.RunCharOnActivate; champion CharOnActivate adds go to "Attacker" too: CharScriptCaitlyn, JarvanIV, MasterYi), so the golems
    /// carry the buff on themselves like the wolves. GlobalMonsterBuff's OnBuffActivate grows its holder by max(game time - spawnTime 101, 0)
    /// seconds of healthPerMinute and damagePerMinute over 60: nothing at the first spawn (100 s), and a respawned camp by its life so far.
    /// The two camps die together and come back together, so every member has grown by the same minutes, read here from its health
    /// (Golem 37, SmallGolem 25, GiantWolf 31, wolf 16.5 a minute) and checked against its attack damage (1.05, 0.84, 0.44, 0.286).
    /// </summary>
    [Check("neutral.p132-respawned-camps-grow-through-global-monster-buff-golems-included")]
    public static void RespawnedCampsGrowThroughGlobalMonsterBuff()
    {
        var runtime = StartCampMatch();
        var hero = OrderHeroes(runtime)[0];
        var perMinute = new Dictionary<string, (double Health, double Damage)>
        {
            ["Golem"] = (37.0, 1.05), ["SmallGolem"] = (25.0, 0.84), ["GiantWolf"] = (31.0, 0.44), ["wolf"] = (16.5, 0.286),
        };
        var first = Members(runtime, 5).Concat(Members(runtime, 2)).ToArray();
        TestAssert.True(perMinute.Keys.All(name => first.Any(member => member.Name == name)), "both camps are up");
        var baseline = new Dictionary<string, (double Health, double Damage)>();
        foreach (var monster in first)
        {
            TestAssert.True(monster.Buffs.Any(buff => buff.Name == "GlobalMonsterBuff"), $"{monster.Name} carries GlobalMonsterBuff from its first spawn");
            baseline[monster.Name] = (monster.MaxHealth, monster.AttackDamage);
            TestAssert.True(runtime.Combat.ApplyHit(hero.NetId, monster.NetId, monster.Health + 1.0, DamageType.True, DamageSourceKind.Spell),
                $"{monster.Name} dies to one hit");
        }
        runtime.Tick(90.0);
        var respawned = Members(runtime, 5).Concat(Members(runtime, 2)).Where(member => member.Alive).ToArray();
        TestAssert.Equal(first.Length, respawned.Length, "both camps respawned whole");
        TestAssert.False(respawned.Intersect(first).Any(), "as new units");
        double? minutes = null;
        foreach (var monster in respawned)
        {
            TestAssert.True(monster.Buffs.Any(buff => buff.Name == "GlobalMonsterBuff"), $"the respawned {monster.Name} carries GlobalMonsterBuff");
            var (health, damage) = perMinute[monster.Name];
            var lived = (monster.MaxHealth - baseline[monster.Name].Health) / health;
            TestAssert.True(lived > 0.0, $"the respawned {monster.Name} grew by its camp's life so far ({lived:0.###} min)");
            TestAssert.Near(lived * damage, monster.AttackDamage - baseline[monster.Name].Damage, 1e-6,
                $"the respawned {monster.Name}'s attack damage grew by the same minutes");
            minutes ??= lived;
            TestAssert.Near(minutes.Value, lived, 1e-6, $"{monster.Name} grew by the same minutes as the rest (Attacker is the unit)");
        }
    }

    private static void AssertCampShares(string label, params int[] camps)
    {
        var runtime = StartCampMatch();
        var heroes = OrderHeroes(runtime);
        var chaos = runtime.Session.Participants.Single(participant => participant.Team == Team.Chaos).Hero!;
        var rules = MapProgressionRules.Load(GameData.Default.Paths);
        foreach (var camp in camps)
        foreach (var monster in Members(runtime, camp))
        {
            var near = heroes[0];
            var mid = heroes[1];
            var far = heroes[2];
            Place(near, monster, 250.0);
            Place(mid, monster, 500.0);
            Place(far, monster, 1500.0);
            Place(chaos, monster, 1500.0);
            var all = new[] { near, mid, far, chaos };
            var experienceBefore = all.Select(hero => hero.Experience).ToArray();
            var goldBefore = all.Select(hero => hero.Gold).ToArray();
            TestAssert.True(runtime.Combat.ApplyHit(mid.NetId, monster.NetId, monster.Health + 1.0,
                DamageType.True, DamageSourceKind.Spell), $"{label} member {monster.NeutralMember} dies to one hit");
            // The near hero (inside 400) and the killer (outside it, paid anyway) share it: the map's two-recipient share each.
            var share = rules.SharedUnitExperience(monster.ExperienceReward, 2, 1.0);
            var global = monster.GlobalExperienceReward;
            double[] expectedExperience =
            [
                (share + global) * (1.0 + Bonus(near)),
                (share + global) * (1.0 + Bonus(mid)),
                global * (1.0 + Bonus(far)),
                0.0,
            ];
            double[] expectedGold =
            [
                monster.GlobalGoldReward,
                monster.GoldReward + monster.GlobalGoldReward,
                monster.GlobalGoldReward,
                0.0,
            ];
            for (var index = 0; index < all.Length; index++)
            {
                TestAssert.Near(expectedExperience[index], all[index].Experience - experienceBefore[index], 1e-6,
                    $"{label} member {monster.NeutralMember} experience of hero {index}");
                TestAssert.Near(expectedGold[index], all[index].Gold - goldBefore[index], 1e-6,
                    $"{label} member {monster.NeutralMember} gold of hero {index}");
            }
        }
    }

    private static double Bonus(Entity hero)
    {
        var values = GameData.Default.LevelConstants.Values;
        return Math.Clamp(hero.LoadoutExperiencePercent, values["gcd_PercentEXPBonusMinimum"], values["gcd_PercentEXPBonusMaximum"]);
    }

    private static MatchRuntime StartCampMatch()
    {
        var roster = new[]
        {
            new SessionParticipantDefinition(1, 0, "Order", "LeeSin", Team.Order),
            new SessionParticipantDefinition(2, 1, "Order", "Annie", Team.Order),
            new SessionParticipantDefinition(3, 2, "Order", "Shaco", Team.Order),
            new SessionParticipantDefinition(4, 3, "Chaos", "Orianna", Team.Chaos),
        };
        var runtime = MatchRuntime.CreateProduction(NavigationGrid.LoadDefault(),
            GameData.Default, readySettleSeconds: 0.0, roster: roster);
        runtime.World.Clock.SetVirtual(0.0);
        var session = runtime.Session;
        session.CreateWorld();
        foreach (var participant in session.Participants)
        {
            var connection = checked((long)participant.PlayerId);
            TestAssert.True(session.Authenticate(connection,
                session.BuildKeyCheck(participant.PlayerId)) is not null);
            session.Spawn(connection);
            TestAssert.True(session.MarkReady(connection));
            participant.Hero!.HeroAutoAcquire = false;
        }
        TestAssert.True(session.Start(), "production neutral match starts");
        runtime.Tick(120.0);
        return runtime;
    }

    private static Entity[] OrderHeroes(MatchRuntime runtime) =>
        runtime.Session.Participants.Where(participant => participant.Team == Team.Order)
            .Select(participant => participant.Hero!).ToArray();

    private static Entity[] Members(MatchRuntime runtime, int camp) =>
        runtime.World.Entities.Values
            .Where(entity => entity.Kind == EntityKind.Neutral && entity.CampId == camp)
            .OrderBy(entity => entity.NeutralMember).ToArray();

    private static void Place(Entity hero, Entity monster, double offset) =>
        hero.Position = new Position2(monster.Position.X + offset, monster.Position.Z);
}
