using Amberline.Core;
using Amberline.Data;
using Amberline.Network.Sessions;
using Amberline.Network.Spectator;
using System.Text.Json;
using System.Runtime.CompilerServices;
using RtmpSharp.IO;
using static Amberline.Platform.AirWire;

namespace Amberline.Platform;

/// <summary>
/// Wire names recovered from the four patch-126 SWFs; see the AIR contract ledger. Batch 798: what the two AIR lines differ in (the summoner
/// spell tables and the GameDTO's ban field) is in each line's <c>AirGameDtos.Line.cs</c>.
/// </summary>
public static partial class AirGameDtos
{
    public const int BlindPickConfigId = 1;
    /// <summary>Draft Mode: CreatePracticeGameController.GAME_TYPE_DRAFT_MODE is 2 in the stock client (GAME_TYPE_BLIND_PICK is 1).</summary>
    public const int DraftConfigId = 2;
    /// <summary>
    /// Batch 669: Battle Training's config, GameTypeConfig.ADVANCED_TUTORIAL_GAMETYPE_ID (GameTypeConfig.as:22); its guided champion select
    /// (AdvancedTutorialController) runs on it. Not a custom-game config: practiceGameTypeConfigIdList stays 1 and 2.
    /// </summary>
    public const int BattleTrainingConfigId = 11;
    public const int NormalQueueId = 2;
    /// <summary>The champions the server plays: the playable ones of <see cref="ChampionCatalog"/>, in roster order.</summary>
    public static IReadOnlyDictionary<int, string> ChampionNames => PlayableNames.Value;
    /// <summary>DIAGNOSTICS: whether <see cref="ChampionNames"/> is loaded already; the demo data reads them only then, so it never loads the AIR client.</summary>
    public static bool ChampionNamesLoaded => PlayableNames.IsValueCreated;
    // Batch 657: loaded on first use, never in the type's initializer, whose failure the runtime keeps for the process's life (every
    // login answered 500); a failed load is tried again.
    private static readonly Lazy<IReadOnlyDictionary<int, string>> PlayableNames = new(() =>
        ChampionCatalog.Playable.ToDictionary(champion => champion.Id, champion => champion.Name), LazyThreadSafetyMode.PublicationOnly);
    /// <summary>
    /// All 84 champions of the patch. Retail's <c>getAvailableChampions</c> listed every champion with its owned flag, and the
    /// champion-select grid showed the unowned ones locked; the ones this server cannot play are listed that way (never owned,
    /// never free) and <see cref="PlatformGames.Pick"/> refuses them as 'not owned'.
    /// </summary>
    public static IReadOnlyDictionary<int, string> AllChampionNames => AllNames.Value;
    private static readonly Lazy<IReadOnlyDictionary<int, string>> AllNames = new(() =>
        ChampionCatalog.All.ToDictionary(champion => champion.Id, champion => champion.Name), LazyThreadSafetyMode.PublicationOnly);
    /// <summary>
    /// Batch 727: a spell a seat may hold is on the game's map list (<see cref="SpellNamesFor"/>) and unlocked at the summoner's level
    /// (<see cref="AirDtos.SpellMinimumLevel"/>, the shipped SpellGeneratedData minLevel).
    /// </summary>
    public static bool SpellUsable(int spellId, int mapId, int summonerLevel) =>
        SpellNamesFor(mapId).TryGetValue(spellId, out var name) && AirDtos.SpellMinimumLevel(name) <= summonerLevel;
    // AirGeneratedContent.swf: ChampionGeneratedData (ChampionCatalog.SkinCount). The catalogs are
    // contiguous, with global skinId = championId * 1000 + native skinIndex.
    // NOT RETAIL (batch 751): <paramref name="skinsOff"/> is the host's "Offer skins and RP" switch turned off (PlatformControls.PaidContentOff);
    // retail always offered every skin. Off, no champion has a skin to offer, so every caller sees only the default look.
    public static IEnumerable<int> SkinIds(int champion, bool skinsOff) => skinsOff ? [] : !ChampionNames.TryGetValue(champion, out var playable)
        // A champion the server cannot play has no installed skin data to filter against: its catalog skins, all locked.
        ? Enumerable.Range(1, ChampionCatalog.ById(champion)!.SkinCount).Select(index => champion * 1000 + index)
        : Amberline.Data.GameData.Default.Skins(playable)
            .Where(s => s.Id > 0 && s.Id <= ChampionCatalog.ById(champion)!.SkinCount).Select(s => champion * 1000 + s.Id);
    public static int SpellId(string name) => SpellNames.Concat(DominionSpellNames)
        .First(p => p.Value.Equals(name, StringComparison.OrdinalIgnoreCase)).Key;
    public static string InternalName(PlatformAccount a) => "sum" + a.SummonerId;

    // Reconstruction: clientInSynch is always true. The client sends a 'CHAMP_SELECT_CLIENT'
    // acknowledgement once its champion list is ready (ChampionSelectionController.tryToAck) and has 'synched'/'out of sync'
    // tooltips for the flag, so retail probably tracked acknowledgements per player (and may have started the pick timer from
    // them); how is not recoverable. A simultaneous (blind) pick has one turn, so pickTurn is 1. In a draft it is the turn the
    // player picks in: updateChampionSelectionTurn announces the players whose pickTurn is the game's pickTurn - 1.
    public static AsObject Participant(PlatformGames.Lobby game, PlatformGames.Member member, string state) => Object(AirProtocolNames.PlayerParticipant,
        ("accountId", (double)member.Account.Id), ("summonerId", (double)member.Account.SummonerId),
        ("summonerName", member.Account.Summoner), ("summonerInternalName", InternalName(member.Account)),
        ("summonerLevel", (double)member.Account.Loadout.SummonerLevel), ("profileIconId", member.Account.ProfileIconId), ("clientInSynch", true),
        ("pickTurn", game.IsDraft ? member.PickTurn : 1),
        ("pickMode", PlatformGames.PickMode(game, member, state)), ("aggregatedLifetimeStatistics", List([])));

    // Field shape from the stock BotParticipant/GameParticipant classes (alias
    // AirProtocolNames.BotParticipant). `champion` stays null: the client fills it from its
    // registry using the champion name inside summonerInternalName (updateBotChampions), and
    // bots are picked, not picking, so their pick mode is DONE once selection starts. In a draft a bot's pickTurn is 0, so the
    // client announces its champion as the picks begin (pickTurn 1), before the first human pick.
    public static AsObject BotParticipant(PlatformGames.Lobby game, PlatformGames.BotSeat bot, string state) => Object(AirProtocolNames.BotParticipant,
        ("summonerName", PlatformBots.DisplayName(bot.Champion)), ("summonerInternalName", PlatformBots.InternalName(bot.Champion, bot.Team)),
        ("pickTurn", game.IsDraft ? 0 : 1), ("pickMode", state == "TEAM_SELECT" ? 0 : 2), ("teamId", PlatformBots.TeamId(bot.Team)),
        ("botSkillLevel", (double)bot.SkillLevel), ("botSkillLevelName", bot.SkillLevelName), ("champion", null));

    public static AsObject QueueInfo(int queueId, int queueLength, int waitTime)
    {
        var span = TimeSpan.FromSeconds(waitTime);
        // GameQueueManager.setQueueInfo adds waitTime to a Date, so the value is milliseconds, and it rebuilds waitTimeStr
        // itself; the seconds the platform measures are converted here.
        return Object(AirProtocolNames.QueueInfo,
            ("queueId", queueId), ("queueLength", queueLength), ("waitTime", (int)Math.Min(int.MaxValue, waitTime * 1000L)),
            ("waitTimeStr", $"{(int)span.TotalMinutes}:{span.Seconds:00}"));
    }

    public static AsObject SearchingForMatch(AsObject queueInfo) =>
        Object(AirProtocolNames.SearchingForMatchNotification,
            ("ghostGameSummoners", List([])), ("joinedQueues", List([queueInfo])),
            ("playerJoinFailures", null));

    /// <summary>
    /// The join answer for players who are already in a game: MasterGameViewController.handleFailedJoinPlayers shows its
    /// 'you are currently assigned to an existing game' dialog from the ALREADY_IN_GAME failures, one per summoner (a party's members too).
    /// </summary>
    public static AsObject QueueAlreadyInGame(params PlatformAccount[] players) =>
        QueueJoinFailures(players.Select(player => (player, "ALREADY_IN_GAME")).ToArray());

    /// <summary>
    /// FailedJoinPlayer rows, one per summoner and reason: ALREADY_IN_GAME, or a ranked requirement (RANKED_MIN_LEVEL, RANKED_NUM_CHAMPS),
    /// which handleFailedJoinPlayers hands to RankedQueueFailuresDialog; it groups the reasons by summoner.name (batch 586).
    /// </summary>
    public static AsObject QueueJoinFailures(params (PlatformAccount Player, string Reason)[] failures) =>
        Object(AirProtocolNames.SearchingForMatchNotification,
            ("ghostGameSummoners", List([])), ("joinedQueues", List([])),
            ("playerJoinFailures", List(failures.Select(failure => Object(AirProtocolNames.FailedJoinPlayer,
                ("reasonFailed", failure.Reason), ("summoner", AirDtos.Summoner(failure.Player)))))));

    /// <summary>
    /// The dodge-timer refusal (batch 589): one <see cref="AirProtocolNames.QueueDodger"/> per dodger. handleFailedJoinPlayers sorts
    /// the rows by class (<c>is QueueDodger</c>) into QueueDodgeDialog, which counts the longest dodgePenaltyRemainingTime down in
    /// milliseconds (onPenaltyTimer subtracts 1000 a second) and closes at zero; whole seconds, rounded up.
    /// </summary>
    public static AsObject QueueDodgers(params (PlatformAccount Player, TimeSpan Remaining)[] dodgers) =>
        Object(AirProtocolNames.SearchingForMatchNotification,
            ("ghostGameSummoners", List([])), ("joinedQueues", List([])),
            ("playerJoinFailures", List(dodgers.Select(dodger => Object(AirProtocolNames.QueueDodger,
                ("reasonFailed", "QUEUE_DODGER"), ("summoner", AirDtos.Summoner(dodger.Player)),
                ("dodgePenaltyRemainingTime", Math.Ceiling(dodger.Remaining.TotalSeconds) * 1000))))));

    public static AsObject QueueFailure(int queueId, string message) =>
        Object(AirProtocolNames.SearchingForMatchNotification,
            ("ghostGameSummoners", List([])), ("joinedQueues", List([])),
            ("playerJoinFailures", List([Object(AirProtocolNames.QueueDisabled,
                ("reasonFailed", "QUEUE_DISABLED"), ("queueId", queueId), ("message", message))])));

    public static AsObject QueueThrottled(int queueId, string message) =>
        Object(AirProtocolNames.SearchingForMatchNotification,
            ("ghostGameSummoners", List([])), ("joinedQueues", List([])),
            ("playerJoinFailures", List([Object(AirProtocolNames.QueueThrottled,
                ("reasonFailed", "QUEUE_THROTTLED"), ("queueId", queueId), ("message", message))])));

    /// <summary>
    /// Batch 580: the Basic Tutorial's games (map 3, <see cref="PlatformGames.CreateTutorial"/>) are <c>TUTORIAL_GAME</c> (GameType.as:6) in the
    /// GameDTO, the EndOfGameStats and the recent games: the client's tutorial flow, its end-of-game branch and its profile history read it
    /// (MasterGameController.as:1215-1224, EndOfGameStatsController.as:644-665, SummonerProfileController.filterOutTutorialGames).
    /// </summary>
    /// Batch 669: and Battle Training (map 1 in TUTORIAL): the type follows the mode.
    private static string GameTypeOf(string mode, string other) => mode == Amberline.Core.GameMaps.TutorialMode ? "TUTORIAL_GAME" : other;

    public static AsObject Game(PlatformGames.Lobby g, string? state = null)
    {
        state ??= g.State;
        var queue = g.Queue;
        return Object(AirProtocolNames.GameDTO, ("id", (double)g.Id), ("name", g.Name),
            ("gameState", state), ("gameStateString", state), ("gameType", GameTypeOf(g.GameMode, queue?.GameType ?? "PRACTICE_GAME")), ("gameMode", g.GameMode),
            ("queueTypeName", queue?.Type ?? "NONE"), ("mapId", g.MapId), ("gameTypeConfigId", g.ConfigId), ("maxNumPlayers", g.Capacity),
            ("optimisticLock", (double)g.Version), ("creationTime", g.Created.UtcDateTime),
            ("expiryTime", Math.Max(0, (g.Deadline - DateTimeOffset.UtcNow).TotalMilliseconds)), ("pickTurn", g.PickTurn), ("passwordSet", g.Password.Length > 0),
            // banOrder: no stock code reads it, so it stays empty; the bans the client shows are the line's ban field (Bans).
            // Batch 848: spectatorDelay is the seconds AIR waits before it starts a spectator (SpectatorController.spectateGame), the grid's delay.
            ("spectatorsAllowed", g.SpectatorsAllowed), ("spectatorDelay", (int)SpectatorGrid.Delay.TotalSeconds),
            ("observers", List(g.Observers.Select(Observer))), ("banOrder", List([])),
            Bans(g), ("accountSpellsMap", List(g.Members.SelectMany(m => new object[] { (double)m.Account.Id, (double)m.Spell1, (double)m.Spell2 }))), ("terminatedCondition", g.TerminatedCondition),
            ("queuePosition", g.QueuePosition),
            ("ownerSummary", g.Members.FirstOrDefault(m => m.Account.Id == g.Owner) is { } owner ? Participant(g, owner, state) : null),
            ("teamOne", List(g.Members.Where(m => m.Team == Team.Order).Select(m => Participant(g, m, state))
                .Concat(g.Bots.Where(b => b.Team == Team.Order).Select(b => BotParticipant(g, b, state))))),
            ("teamTwo", List(g.Members.Where(m => m.Team == Team.Chaos).Select(m => Participant(g, m, state))
                .Concat(g.Bots.Where(b => b.Team == Team.Chaos).Select(b => BotParticipant(g, b, state))))),
            // Bots keep their lobby champion: the stock champion list marks a champion taken
            // for a team when a participant's selection names it, so humans cannot re-pick it.
            ("playerChampionSelections", List(g.Members.Where(m => m.Champion != 0).Select(m => Object(AirProtocolNames.PlayerChampionSelectionDTO,
                ("summonerInternalName", InternalName(m.Account)), ("championId", m.Champion), ("selectedSkinIndex", m.Skin),
                ("spell1Id", (double)m.Spell1), ("spell2Id", (double)m.Spell2)))
                .Concat(g.Bots.Select(b => Object(AirProtocolNames.PlayerChampionSelectionDTO,
                    ("summonerInternalName", PlatformBots.InternalName(b.Champion, b.Team)), ("championId", b.Champion), ("selectedSkinIndex", 0),
                    ("spell1Id", (double)SpellId(b.Loadout.SummonerSpell1)), ("spell2Id", (double)SpellId(b.Loadout.SummonerSpell2))))))));
    }

    public static AsObject Search(PlatformGames.Lobby g) => Object(AirProtocolNames.PracticeGameSearchResult,
        ("id", g.Id), ("name", g.Name), ("gameMapId", g.MapId), ("gameMode", g.GameMode), ("maxNumPlayers", g.Capacity),
        ("gameMap", AirDtos.MapFor(g.MapId)),
        ("team1Count", g.Members.Count(m => m.Team == Team.Order) + g.Bots.Count(b => b.Team == Team.Order)),
        ("team2Count", g.Members.Count(m => m.Team == Team.Chaos) + g.Bots.Count(b => b.Team == Team.Chaos)),
        // Batch 848: the browser's Spectate button needs ALL (PracticeGameSearchResult.canSpectate) and a free slot (isSpectatorFull:
        // spectatorCount against the system states' spectatorSlotLimit); its column shows "count/limit".
        ("privateGame", g.Password.Length > 0), ("allowSpectators", g.SpectatorsAllowed ? "ALL" : "NONE"), ("spectatorCount", g.Observers.Count),
        ("owner", Participant(g, g.Members.First(m => m.Account.Id == g.Owner), g.State)));

    /// <summary>
    /// Batch 848: a spectator in the GameDTO's <c>observers</c>, a <c>GameObserver</c> (GameObserver.as: accountId, summonerId,
    /// profileIconId, and GameParticipant's names). The client finds itself there by summonerName (GameDTO.getTeamNameForSummoner, team
    /// "spectator"), so it then shows the spectator views.
    /// </summary>
    public static AsObject Observer(PlatformAccount account) => Object(AirProtocolNames.GameObserver,
        ("accountId", (double)account.Id), ("summonerId", (double)account.SummonerId), ("summonerName", account.Summoner),
        ("summonerInternalName", InternalName(account)), ("profileIconId", account.ProfileIconId));

    /// <summary>
    /// Batch 848: a spectator's start, the PlayerCredentialsDto AIR launches the spectator with (SpectatorController.startSpectating:
    /// Maestro's "spectator &lt;observerServerIp&gt; &lt;key&gt; &lt;gameId&gt; &lt;platformId&gt;"). The key is the chunk key wrapped for the game
    /// (<see cref="SpectatorChunk.WrapKey"/>), in the line's field (<see cref="ObserverKey"/>). No game server address: a spectator
    /// downloads from the grid. NOT RETAIL: observerServerIp is loopback (retail named its public spectator service); the host's own
    /// grid is there, and a friend's player tool carries it (S4b). observerServerPort is the client's fixed 8088 (1.52's end-of-game request
    /// reads it). Reconstruction: playerId 0. NOT RETAIL (batch 854): handshakeToken is the literal <c>token</c>, the game client's own last
    /// URL segment: AIR 1.52 puts it in the end-of-game URL (SpectatorController.retrieveEndOfGameStats), and no secret goes in a URL; the
    /// grid asks the account instead (<see cref="SpectatorGrid.MayWatch"/>). Retail's per-spectator value is not recovered.
    /// </summary>
    public static AsObject ObserverCredentials(int gameId, string chunkKey)
    {
        var credentials = Object(AirProtocolNames.PlayerCredentialsDto, ("gameId", (double)gameId), ("playerId", 0d), ("serverIp", ""),
            ("serverPort", 0), ("encryptionKey", ""), ("observer", true), ("observerServerIp", "127.0.0.1"),
            ("observerServerPort", SpectatorGrid.Port), ("handshakeToken", "token"));
        ObserverKey(credentials, SpectatorChunk.WrapKey(gameId, chunkKey));
        return credentials;
    }

    public static AsObject Results(PlatformGames.Lobby game, PlatformGames.Member viewer, PlatformReward? reward = null,
        PlatformEconomyRules? rules = null, Func<PlatformAccount, (int Wins, int Losses, int Leaves)>? record = null,
        DateTimeOffset? now = null, PlatformRating? rating = null, (PlatformTeamSide Mine, PlatformTeamSide Other, bool Ranked)? team = null)
    {
        var result = game.Result ?? throw new InvalidOperationException("Match has no authoritative result.");
        var queue = game.Queue;
        var me = result.Players.Single(p => p.PlayerId == (ulong)viewer.Account.SummonerId);
        var economy = PlatformEconomy.For(viewer.Account);
        var levelUp = reward is not null && reward.Level > reward.PreviousLevel;
        // EndOfGameStatsController refreshes AllSummonerData before displaying
        // these values. expPoints is current-level progress and infPoints is
        // the live balance; PlatformReward.ExperienceTotal is lifetime XP.
        var experienceTotal = economy.Experience;
        var ipTotal = economy.Ip;
        var expToNext = reward is null
            ? Math.Max(0, (rules ?? new PlatformEconomyRules()).ExperienceToNextLevel(economy.Level) - experienceTotal)
            : Math.Max(0, reward.ExpToNextLevel - experienceTotal);
        var talentPoints = levelUp ? reward!.Level - reward.PreviousLevel : 0;
        AsObject ProjectPlayer(PlayerMatchResult player)
        {
            var member = game.Members.FirstOrDefault(m => (ulong)m.Account.SummonerId == player.PlayerId);
            // EndOfGameStatsController prints the local player's wins, losses and leaves as the record counters and
            // EndOfGamePlayerTooltip prints each player's wins; they are the profile's record of the game's queue type (PlatformGames.Tick).
            var (wins, losses, leaves) = member is null || record is null ? (0, 0, 0) : record(member.Account);
            return PlayerResult(game.Id, result, player, member?.Account.ProfileIconId ?? 0,
                member?.Account.Loadout.SummonerLevel ?? player.Level, wins, losses, leaves);
        }
        var dto = Object(AirProtocolNames.EndOfGameStats, ("gameId", (double)game.Id),
            ("userId", (double)viewer.Account.SummonerId), ("gameLength", AirWhole(result.DurationSeconds)),
            ("gameType", GameTypeOf(result.Mode, queue?.GameType ?? "PRACTICE_GAME")), ("gameMode", result.Mode), ("queueType", queue?.Type ?? "NONE"), ("ranked", queue?.Ranked == true), ("invalid", false),
            ("skinName", me.Champion), ("skinIndex", (double)me.SkinId),
            ("leveledUp", levelUp), ("newSpells", List([])),
            // Batch 592: a tutorial's results carry the game-type penalty the client renames to TUTORIAL (PlatformAccounts.TryAward).
            ("pointsPenalties", List(result.Mode == Amberline.Core.GameMaps.TutorialMode
                ? [Object(AirProtocolNames.PointsPenalty, ("type", "GAME_TYPE"), ("penalty", 1.0))] : [])),
            ("experienceEarned", WholeReward(reward?.XpEarned ?? 0, "experienceEarned")),
            ("experienceTotal", WholeReward(experienceTotal, "experienceTotal")), ("talentPointsGained", WholeReward(talentPoints, "talentPointsGained")),
            ("expPointsToNextLevel", WholeReward(expToNext, "expPointsToNextLevel")),
            ("ipEarned", WholeReward(reward?.IpEarned ?? 0, "ipEarned")),
            ("ipTotal", WholeReward(ipTotal, "ipTotal")),
            // PlatformReward component fields were appended for boost receipts.
            // Older persisted rows deserialize them as zero, so preserve their
            // historical IP display when no component was recorded at all.
            ("basePoints", WholeReward(reward is not null && reward.BaseIpEarned == 0 &&
                reward.TimedIpEarned == 0 && reward.WinIpEarned == 0 && reward.FirstWinIpEarned == 0
                ? reward.IpEarned : reward?.BaseIpEarned ?? 0, "basePoints")),
            // First Win of the Day (PlatformEconomy.FirstWinBonusIp): the IP bar's "+{0} Bonus IP gained For your first win of
            // the day" segment (EndOfGameStatsView, firstWinBonus > 0). The stock 126 client also copies firstWinBonus into its
            // first-win countdown (EndOfGameStatsController: session.firstWinOfDayTimeRemaining = firstWinBonus, in milliseconds)
            // and never reads timeUntilNextFirstWinBonus; both are sent as retail defined them, the client's quirk is left alone.
            ("firstWinBonus", WholeReward(reward?.FirstWinIpEarned ?? 0, "firstWinBonus")),
            ("timeUntilNextFirstWinBonus", (double)Math.Max(0,
                PlatformEconomy.FirstWinAvailableAt(viewer.Account) - (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds())),
            ("boostIpEarned", WholeReward((reward?.TimedIpEarned ?? 0) + (reward?.WinIpEarned ?? 0), "boostIpEarned")),
            ("boostXpEarned", WholeReward((reward?.TimedXpEarned ?? 0) + (reward?.WinXpEarned ?? 0), "boostXpEarned")),
            ("teamPlayerParticipantStats", List(result.Players.Where(p => p.Team == me.Team).Select(ProjectPlayer))),
            ("otherTeamPlayerParticipantStats", List(result.Players.Where(p => p.Team != me.Team).Select(ProjectPlayer))));
        // AIR Number fields default to NaN when omitted, but the stock results
        // view still reads these optional progress fields.
        foreach (var field in new[] { "locationBoostIpEarned",
            "loyaltyBoostIpEarned", "odinBonusIp", "completionBonusPoints",
            "queueBonusEarned", "locationBoostXpEarned",
            "loyaltyBoostXpEarned" }) dto[field] = 0d;
        // EndOfGameStatsView shows the 'XP/IP reward minutes left today' panel for custom games whenever
        // practiceMinutesLeftToday is not negative, reads 0 as an exhausted quota and prints practiceMsecsUntilReset as
        // the time to the refill (queue type BOT, Co-op vs. AI, as well). The numbers are the account's real daily budget
        // (PlatformEconomy.Practice); normal queue games and a custom game on a host that pays no custom rewards send -1, which keeps
        // the panel hidden.
        var quota = PlatformGames.PaysFromPracticeBudget(game.QueueId) && (queue is not null || (rules ?? new PlatformEconomyRules()).CustomGameRewards)
            ? PlatformEconomy.Practice(viewer.Account, rules ?? new PlatformEconomyRules(), now ?? DateTimeOffset.UtcNow)
            : null;
        dto["practiceMinutesLeftToday"] = quota is null ? -1d : Math.Ceiling(quota.SecondsLeft / 60);
        dto["practiceMinutesPlayedToday"] = quota is null ? 0d : Math.Floor(quota.SecondsSpent / 60);
        dto["practiceMsecsUntilReset"] = quota is null ? 0d : (double)quota.MillisecondsUntilReset;
        // A ranked game (batch 586): EndOfGameStatsView prints "Ranked (elo +eloChange)" once the local player's wins + losses, this
        // queue's record above, reach 10 (EndOfGameStatsController.showRankedElo), and "Ranked (Unranked)" before.
        dto["elo"] = rating?.After ?? 0;
        dto["eloChange"] = rating is null ? 0 : rating.After - rating.Before;
        // Batch 844: a Ranked Teams game, 1.52's EndOfGameStats.myTeamInfo, otherTeamInfo and myTeamStatus (sent for no other game).
        // EndOfGameStatsView prints "Ranked" and, unless myTeamStatus is RANKED, "(Unranked)", else "(elo +change)" (as:3141-3160); the
        // scoreboard puts each side's "[tag] " before its names (EndOfGameStatsController.as:508-517, 849-944). Reconstruction: elo is the
        // team's rating, and the status RANKED once the team has played 5 games of this queue, from when the profile shows that rating
        // (ProfileQueueSummaryView, TeamStatDetail.GAMES_PLAYED_FOR_RANKED_STATUS).
        if (team is { } sides)
        {
            dto["myTeamInfo"] = AirTeamDtos.TeamInfo(sides.Mine);
            dto["otherTeamInfo"] = AirTeamDtos.TeamInfo(sides.Other);
            dto["myTeamStatus"] = sides.Ranked ? "RANKED" : "PROVISIONAL";
            dto["elo"] = sides.Mine.Rating?.After ?? 0;
            dto["eloChange"] = sides.Mine.Rating is { } change ? change.After - change.Before : 0;
        }
        dto["difficulty"] = "";
        return dto;
    }

    /// <summary>
    /// Batch 854 (spectating S5): AIR 1.52's spectator end of game, the SpectatorEndOfGameStats the grid serves
    /// (<see cref="PlatformGames.SpectatorEndOfGame"/>). SpectatorController.retrieveEndOfGameStats reads it with
    /// <c>readObject() as SpectatorEndOfGameStats</c>, so the object is the observer class
    /// (<see cref="AirProtocolNames.SpectatorEndOfGameStats"/>; the statistics alias would read as null), and its rows and stats the
    /// observer classes 1.52 registers beside it. No row is the viewer's: the client then shows the blue team first
    /// (EndOfGameStatsController.as:475-499); here Order is the first team. A spectator earns nothing, so every reward is 0; the totals, the
    /// first-win timer and the practice reset are the viewer's own, which the client shows and copies into its session (as:473-474).
    /// Reconstruction: retail's spectator values.
    /// </summary>
    public static AsObject SpectatorResults(PlatformGames.Lobby game, PlatformAccount viewer, DateTimeOffset now, PlatformEconomyRules? rules = null)
    {
        var result = game.Result ?? throw new InvalidOperationException("Match has no authoritative result.");
        var queue = game.Queue;
        var economy = PlatformEconomy.For(viewer);
        AsObject Row(PlayerMatchResult player)
        {
            var member = game.Members.FirstOrDefault(m => (ulong)m.Account.SummonerId == player.PlayerId);
            var row = PlayerResult(game.Id, result, player, member?.Account.ProfileIconId ?? 0, member?.Account.Loadout.SummonerLevel ?? player.Level);
            row.TypeName = AirProtocolNames.SpectatorPlayerParticipantStatsSummary;
            foreach (AsObject stat in (System.Collections.IEnumerable)row["statistics"]) stat.TypeName = AirProtocolNames.SpectatorRawStatDTO;
            return row;
        }
        var dto = Object(AirProtocolNames.SpectatorEndOfGameStats, ("gameId", (double)game.Id), ("userId", (double)viewer.SummonerId),
            ("gameLength", AirWhole(result.DurationSeconds)), ("gameType", GameTypeOf(result.Mode, queue?.GameType ?? "PRACTICE_GAME")),
            ("gameMode", result.Mode), ("queueType", queue?.Type ?? "NONE"), ("ranked", queue?.Ranked == true), ("invalid", false),
            ("skinName", ""), ("skinIndex", 0d), ("leveledUp", false), ("newSpells", List([])), ("pointsPenalties", List([])),
            ("difficulty", ""), ("elo", 0), ("eloChange", 0),
            ("experienceTotal", WholeReward(economy.Experience, "experienceTotal")), ("ipTotal", WholeReward(economy.Ip, "ipTotal")),
            ("expPointsToNextLevel", WholeReward(Math.Max(0, (rules ?? new PlatformEconomyRules()).ExperienceToNextLevel(economy.Level) - economy.Experience),
                "expPointsToNextLevel")),
            ("timeUntilNextFirstWinBonus", (double)Math.Max(0, PlatformEconomy.FirstWinAvailableAt(viewer) - now.ToUnixTimeMilliseconds())),
            // The reward panel stays hidden (as for a normal queue game's results: practiceMinutesLeftToday -1).
            ("practiceMinutesLeftToday", -1d),
            ("teamPlayerParticipantStats", List(result.Players.Where(p => p.Team == "Order").Select(Row))),
            ("otherTeamPlayerParticipantStats", List(result.Players.Where(p => p.Team != "Order").Select(Row))));
        foreach (var field in new[] { "basePoints", "boostIpEarned", "boostXpEarned", "completionBonusPoints", "experienceEarned", "firstWinBonus",
                     "ipEarned", "locationBoostIpEarned", "locationBoostXpEarned", "loyaltyBoostIpEarned", "loyaltyBoostXpEarned", "odinBonusIp",
                     "practiceMinutesPlayedToday", "practiceMsecsUntilReset", "queueBonusEarned", "talentPointsGained" })
            dto[field] = 0d;
        return dto;
    }

    /// <summary>Batch 854: one AMF3 value as base64 text, what AIR 1.52 decodes from the grid (Base64Decoder, then ByteArray.readObject).</summary>
    public static string Amf3Base64(object value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new AmfWriter(buffer, new SerializationContext())) writer.WriteAmf3Item(value);
        return Convert.ToBase64String(buffer.ToArray());
    }

    public static AsObject RecentGames(PlatformAccount account, IEnumerable<PlatformResult> receipts) =>
        Object(AirProtocolNames.RecentGames, ("userId", (double)account.Id),
            ("gameStatistics", List(receipts.OrderByDescending(r => r.CompletedAt).ThenByDescending(r => r.GameId)
                .Select(receipt => (receipt, result: receipt.Result.Deserialize<MatchResult>() ?? throw new InvalidDataException("Empty match result.")))
                // A receipt that does not name this summoner exactly once is skipped, so one odd receipt cannot break the history.
                .Where(row => row.result.Players.Count(p => p.PlayerId == (ulong)account.SummonerId) == 1)
                .Take(10).Select(row =>
            {
                var (receipt, result) = row;
                var me = result.Players.Single(p => p.PlayerId == (ulong)account.SummonerId);
                var queue = PlatformGames.QueueOf(receipt.QueueId);
                var reward = receipt.Rewards?.GetValueOrDefault(account.Id);
                // PlayerGameStats iterates RawStat directly; unlike the end-of-game
                // summary it does not convert RawStatDTO into a displayable stat.
                var endStats = (System.Collections.IEnumerable)PlayerResult(checked((int)receipt.GameId), result, me,
                    account.ProfileIconId, account.Loadout.SummonerLevel)["statistics"];
                var statistics = List(endStats.Cast<AsObject>().Select(stat =>
                    Object(AirProtocolNames.RawStat,
                        ("statType", stat["statTypeName"]), ("value", stat["value"]))));
                // Bots hold no summoner ID, so only human seats are fellow players.
                var fellows = result.Players.Where(player => player.PlayerId != me.PlayerId && !player.IsBot)
                    .Select(player => Object(AirProtocolNames.FellowPlayerInfo,
                        ("championId", (double)ChampionNames.Single(pair => pair.Value == player.Champion).Key),
                        ("summonerId", (double)player.PlayerId),
                        ("teamId", player.Team == "Order" ? 100d : 200d)));
                return Object(AirProtocolNames.PlayerGameStats, ("userId", (double)account.Id),
                    ("gameId", (double)receipt.GameId), ("championId", (double)ChampionNames.Single(p => p.Value == me.Champion).Key),
                    ("teamId", me.Team == "Order" ? 100d : 200d), ("gameMapId", (double)result.MapId),
                    // PlayerGameStats.gameTypeString keys summonerProfile_recentGame_<gameType>; the bundle has MATCHED_GAME
                    // ('(normal game)') and PRACTICE_GAME, not NORMAL_GAME. GameDTO and EndOfGameStats keep NORMAL_GAME. A queueType of BOT
                    // reads '(Co-op vs. AI game)' whatever the gameType (PlayerGameStats.gameTypeString, QueueType.isCoopVsAI).
                    ("gameMode", result.Mode), ("gameType", GameTypeOf(result.Mode, queue is null ? "PRACTICE_GAME" : "MATCHED_GAME")), ("queueType", queue?.Type ?? "NONE"),
                    // A ranked game reads "(ranked game)": PlayerGameStats.gameTypeString is RANKED_GAME when ranked (batch 586).
                    ("createDate", receipt.CompletedAt.UtcDateTime), ("ranked", queue?.Ranked == true), ("invalid", false), ("leaver", me.IsLeaver),
                    ("ipEarned", WholeReward(reward?.IpEarned ?? 0, "ipEarned")),
                    ("experienceEarned", WholeReward(reward?.XpEarned ?? 0, "experienceEarned")),
                    ("leveledUp", reward is not null && reward.Level > reward.PreviousLevel), ("fellowPlayers", List(fellows)),
                    // PlayerGameStats.statistics builds its private rawStats;
                    // assigning rawStats alone calls an empty stock setter.
                    ("statistics", statistics));
            }))));

    private static AsObject PlayerResult(int gameId, MatchResult result, PlayerMatchResult p,
        int profileIconId = 0, int summonerLevel = 0, int wins = 0, int losses = 0, int leaves = 0)
    {
        var s = p.Statistics;
        var stats = new Dictionary<string, double>
        {
            // Stock AIR tests WIN's presence, not its value, for the victory banner.
            [p.Team == result.Winner ? "WIN" : "LOSE"] = 1,
            ["LEVEL"] = p.Level, ["CHAMPIONS_KILLED"] = p.Kills, ["NUM_DEATHS"] = p.Deaths, ["ASSISTS"] = p.Assists,
            ["MINIONS_KILLED"] = p.LaneMinionsKilled + p.OtherMinionsKilled, ["NEUTRAL_MINIONS_KILLED"] = p.NeutralMinionsKilled,
            ["TURRETS_KILLED"] = p.TurretsKilled, ["BARRACKS_KILLED"] = p.InhibitorsKilled, ["GOLD_EARNED"] = AirWhole(s.GoldEarned),
            ["GOLD_SPENT"] = AirWhole(s.GoldSpent), ["TIME_PLAYED"] = AirWhole(result.DurationSeconds), ["TOTAL_DAMAGE_DEALT"] = AirWhole(s.TotalDamageDealt),
            ["TOTAL_DAMAGE_TAKEN"] = AirWhole(s.TotalDamageTaken), ["PHYSICAL_DAMAGE_DEALT_PLAYER"] = AirWhole(s.PhysicalDamageDealt),
            ["MAGIC_DAMAGE_DEALT_PLAYER"] = AirWhole(s.MagicDamageDealt), ["PHYSICAL_DAMAGE_TAKEN"] = AirWhole(s.PhysicalDamageTaken),
            ["MAGIC_DAMAGE_TAKEN"] = AirWhole(s.MagicDamageTaken), ["TOTAL_HEAL"] = AirWhole(s.TotalHeal), ["LARGEST_CRITICAL_STRIKE"] = AirWhole(s.LargestCriticalStrike),
            ["LARGEST_KILLING_SPREE"] = p.LargestKillingSpree, ["LARGEST_MULTI_KILL"] = s.LargestMultiKill,
            ["TOTAL_TIME_SPENT_DEAD"] = AirWhole(s.TotalTimeSpentDead), ["DOUBLE_KILLS"] = s.DoubleKills,
            ["TRIPLE_KILLS"] = s.TripleKills, ["QUADRA_KILLS"] = s.QuadraKills, ["PENTA_KILLS"] = s.PentaKills
        };
        for (var i = 0; i < 6; i++) stats["ITEM" + i] = i < p.Inventory.Count ? p.Inventory[i]?.ItemId ?? 0 : 0;
        // The stock Dominion screen (gameMode ODIN) shows these: EndOfGameStatsController.filterStats lists them, OdinParticipantSorting
        // orders each team by TOTAL_PLAYER_SCORE (the local player's row 1-3 gets the gold/silver/bronze icon), EndOfGameStatsView
        // prints the first row's VICTORY_POINT_TOTAL as the team's final nexus health, and RecentGameRenderer shows TOTAL_SCORE_RANK's
        // medal. Field contract: .artifacts/deep-dive/dominion-score-contract.md. A result without the block reads as zeros.
        if (result.MapId == Amberline.Core.GameMaps.CrystalScar)
        {
            var odin = p.Odin ?? new();
            stats["TOTAL_PLAYER_SCORE"] = odin.TotalScore;
            stats["COMBAT_PLAYER_SCORE"] = odin.CombatScore;
            stats["OBJECTIVE_PLAYER_SCORE"] = odin.ObjectiveScore;
            stats["NODE_CAPTURE"] = odin.NodeCaptures;
            stats["NODE_CAPTURE_ASSIST"] = odin.NodeCaptureAssists;
            stats["NODE_NEUTRALIZE"] = odin.NodeNeutralizes;
            stats["NODE_NEUTRALIZE_ASSIST"] = odin.NodeNeutralizeAssists;
            stats["NODE_KILL_OFFENSE"] = odin.NodeKillsOffense;
            stats["NODE_KILL_DEFENSE"] = odin.NodeKillsDefense;
            stats["TEAM_OBJECTIVE"] = odin.TeamObjectives;
            stats["VICTORY_POINT_TOTAL"] = odin.VictoryPoints;
            // Reconstruction: the place inside the player's own team (as the end-of-game icon ranks it); ties share a place.
            stats["TOTAL_SCORE_RANK"] = 1 + result.Players.Count(other =>
                other.Team == p.Team && (other.Odin?.TotalScore ?? 0) > odin.TotalScore);
        }
        // PlayerParticipantStatsSummary.summonerName resolves a bot's text through the champion
        // resource table (getChampionResourceString), so a bot row carries its champion skin name.
        return Object(AirProtocolNames.PlayerParticipantStatsSummary, ("userId", (double)p.PlayerId),
            ("gameId", (double)gameId), ("summonerName", p.IsBot ? p.Champion : p.Name), ("skinName", p.Champion),
            ("level", (double)(summonerLevel > 0 ? summonerLevel : p.Level)),
            ("profileIconId", profileIconId),
            ("teamId", p.Team == "Order" ? 100d : 200d), ("botPlayer", p.IsBot), ("leaver", p.IsLeaver), ("inChat", false),
            ("wins", (double)wins), ("losses", (double)losses), ("leaves", (double)leaves), ("elo", 0), ("eloChange", 0),
            ("spell1Id", (double)SpellId(p.SummonerSpells[0])), ("spell2Id", (double)SpellId(p.SummonerSpells[1])),
            ("statistics", List(stats.Select(v => Object(AirProtocolNames.RawStatDTO, ("statTypeName", v.Key), ("value", v.Value))))));
    }

    private static double AirWhole(double value, [CallerArgumentExpression(nameof(value))] string? field = null)
    {
        if (!double.IsFinite(value))
            throw new InvalidDataException($"AIR result statistic '{field ?? "value"}' must be finite.");
        return Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private static double WholeReward(double value, string field) => AirWhole(value, field);
}
