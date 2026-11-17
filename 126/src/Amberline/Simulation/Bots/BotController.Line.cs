using Amberline.Core;
using Amberline.Data;

namespace Amberline.Simulation.Bots;

/// <summary>
/// The 1.0.0.126 line's bot role lanes (batch 851; owner decision, 17 November 2026: "Yes, mirrored on 126"). This is the 1.0.0.132 rule of
/// batch 836 with the top and bottom lanes swapped; the 1.0.0.132 line has its own copy at <c>132/src/Amberline/Simulation/Bots/BotController.Line.cs</c>.
/// </summary>
public sealed partial class BotController
{
    /// <summary>
    /// The lane letter each playable champion's role sends a bot to on 1.0.0.126. Retail's note for 1.0.0.132 (the wiki's V1.0.0.132
    /// page, 17 January 2012, Undocumented changes, Co-op vs AI): "Bottom and top lane AI champions switched." and "The AD carry and
    /// support AI now go to the bottom lane while the tank and bruiser AI go to the top lane." Read backwards, that is the 1.0.0.126
    /// rule: the AD carry and support AI go top, the tank and bruiser AI bottom. Summoner's Rift's top lane is <c>L</c> (Turret_T1_L_03
    /// and Turret_T2_L_03 stand at the map's top edge), the bottom lane <c>R</c>, mid <c>C</c>.
    /// Reconstruction: the role is the server's own lane label (<see cref="ChampionCatalog.PlayableRoles"/>, no shipped file carries
    /// one): adc and support go top, top and tank go bottom, mid goes mid. A jungle label goes bottom too, as a bruiser (a judgement:
    /// the shipped bot scripts have no jungle task, and the jungle-labelled retail bots Master Yi, Nunu, Trundle and Warwick are melee
    /// fighters). The note names no mid rule; a mid label keeps mid.
    /// </summary>
    private static readonly Dictionary<string, string> RoleLanes = ChampionCatalog.PlayableRoles
        .Select(row => (row.Name, Lane: row.Role switch
        {
            "adc" or "support" => "L",
            "top" or "tank" or "jungle" => "R",
            "mid" => "C",
            _ => null,
        }))
        .Where(row => row.Lane is not null)
        .ToDictionary(row => row.Name, row => row.Lane!, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reconstruction: how retail's game server made the bots follow the role rule is not recoverable. The shipped Bot.luaobj and
    /// TaskPushLane.luaobj are byte-identical on 126 and 132 and score a lane only by distance, teammates on it and a random term
    /// (TaskPushLane.txt proto 0.1), and the natives they call live in the game server, not in the client. So the rule sits in the one
    /// native the lane choice reads, <c>GetMinions(GetTeam(), LaneID)</c>, which only TaskPushLane calls with a lane (TaskKillMinion
    /// asks for every lane, -1): the bot's own lanes other than its role lane answer no minions, the script scores them 0 (proto 0.1
    /// 0046-0049, 0076) and the bot pushes its role lane by the shipped formula. While the role lane has no live friendly minion
    /// (a wave lost, or not yet spawned) the other lanes stay visible, so the bot pushes by the script's own spread rather than stand
    /// with no lane task, and returns once its lane's next minion appears.
    /// Reconstruction (scope): only on the three-lane maps (Summoner's Rift and the Harrowing rift), which the note's top and bottom
    /// lanes describe; not on Twisted Treeline or the Basic Tutorial (fewer lanes) nor The Crystal Scar, whose layout keeps the rift's
    /// lane tables but whose bots take capture points (<see cref="Quests"/>).
    /// </summary>
    partial void HideOffRoleLane(Entity hero, Team side, int lane, ref bool hidden)
    {
        if (side != hero.Team || lane < 0 || MapDefinitions.Lanes.Count != 3 || Quests is not null ||
            !RoleLanes.TryGetValue(hero.ChampionName, out var role) || lane == LaneIndex(role))
            return;
        foreach (var other in _world.Entities.Values)
            if (other.Alive && other.Team == side && other.Kind == EntityKind.Minion && other.MinionLane == role)
            {
                hidden = true;
                return;
            }
    }
}
