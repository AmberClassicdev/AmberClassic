using Amberline.Core;
using Amberline.Data;
using Amberline.Server;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;

namespace Amberline.Platform;

/// <summary>
/// Small source-derived AMF DTO factory for the stock AIR client.  These are deliberately wire objects: the AIR class aliases are
/// part of the protocol and must survive the remoting serializer. The served client line's own shapes are in <c>AirDtos.Line.cs</c>
/// (<see cref="AirLine"/>).
/// </summary>
public static partial class AirDtos
{
    private const string SessionAlias = AirProtocolNames.Session;
    private const string AccountSummaryAlias = AirProtocolNames.AccountSummary;
    private const string LoginDataAlias = AirProtocolNames.LoginDataPacket;
    private const string AllSummonerDataAlias = AirProtocolNames.AllSummonerData;
    private const string SummonerAlias = AirProtocolNames.Summoner;
    private const string PublicSummonerAlias = AirProtocolNames.PublicSummoner;
    private const string SummonerCatalogAlias = AirProtocolNames.SummonerCatalog;
    private const string SummonerLevelAlias = AirProtocolNames.SummonerLevel;
    private const string SummonerLevelAndPointsAlias = AirProtocolNames.SummonerLevelAndPoints;
    private const string SummonerDefaultSpellsAlias = AirProtocolNames.SummonerDefaultSpells;
    private const string SummonerTalentsAndPointsAlias = AirProtocolNames.SummonerTalentsAndPoints;
    private const string SpellBookAlias = AirProtocolNames.SpellBook;
    private const string SpellBookPageAlias = AirProtocolNames.SpellBookPage;
    private const string SlotEntryAlias = AirProtocolNames.SlotEntry;
    private const string RuneSlotAlias = AirProtocolNames.RuneSlot;
    private const string RuneInventoryAlias = AirProtocolNames.SummonerRuneInventory;
    private const string SummonerRuneAlias = AirProtocolNames.SummonerRune;
    private const string RuneTypeAlias = AirProtocolNames.RuneType;
    private const string ChampionAlias = AirProtocolNames.ChampionDTO;
    private const string ChampionSkinAlias = AirProtocolNames.ChampionSkinDTO;
    private const string GameTypeConfigAlias = AirProtocolNames.GameTypeConfigDTO;
    private const string GameMapAlias = AirProtocolNames.GameMap;
    private const string ClientStatesAlias = AirProtocolNames.ClientSystemStatesNotification;
    private const string ActiveBoostsAlias = AirProtocolNames.SummonerActiveBoostsDTO;
    private const string FulfillmentAlias = AirProtocolNames.StoreFulfillmentNotification;
    private const string TalentGroupAlias = AirProtocolNames.TalentGroup;
    private const string TalentRowAlias = AirProtocolNames.TalentRow;
    private const string TalentAlias = AirProtocolNames.Talent;
    private const string AssociatedTalentAlias = AirProtocolNames.SummonerAssociatedTalent;
    private const string AssociatedTalentPkAlias = AirProtocolNames.SummonerAssociatedTalentPK;

    private static readonly Lazy<RuneCatalog> Runes = new(() =>
        new RuneCatalog(GameData.Default.Paths), LazyThreadSafetyMode.PublicationOnly);
    private static readonly Lazy<MasteryCatalog> Masteries = new(() =>
        new MasteryCatalog(GameData.Default.Paths), LazyThreadSafetyMode.PublicationOnly);

    public static AsObject Login(PlatformAccount account, PlatformSessions.Session session)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(session);
        return AirWire.Object(SessionAlias,
            ("token", session.Token),
            ("accountSummary", AccountSummary(account)),
            // AIR prefixes this short-lived chat credential with AIR_. The
            // account password is never retained or returned by the platform.
            ("password", session.ChatToken));
    }

    /// <param name="rpOff">Batch 751: the host's "Offer skins and RP" switch is off (PlatformControls.PaidContentOff), so the RP balance is sent as 0.</param>
    /// <param name="disabled">Batch 751: the champions the host turned off (<see cref="PlatformControls.DisabledChampions"/>).</param>
    public static AsObject LoginData(PlatformAccount account, object? reconnect = null, PlatformEconomyRules? rules = null, bool rpOff = false,
        IReadOnlySet<int>? disabled = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        return WithLineFields(AirWire.Object(LoginDataAlias,
            ("rpBalance", (double)ShownRp(PlatformEconomy.For(account), rpOff)),
            ("clientHeartBeatEnabled", true),
            ("playerStatSummaries", StatsSummary(account)),
            ("maxPracticeGameSize", MatchRoster.MaxPlayers),
            ("reconnectInfo", reconnect),
            ("platformId", "NA1"),
            ("gameTypeConfigs", GameTypes()),
            ("ipBalance", (double)PlatformEconomy.For(account).Ip),
            ("clientSystemStates", ClientSystemStates(rules, disabled)),
            ("summonerCatalog", SummonerCatalog()),
            // The typo is present in the AIR class and is therefore wire ABI.
            ("langauges", AirWire.List(new object[] { "en_US" })),
            ("allSummonerData", AllSummonerData(account, rules)),
            // The getLoginDataPacketForUser route sets the account's LeaverBuster level (PlatformAccounts.LeaverStatus, batch 587).
            ("leaverPenaltyLevel", 0),
            ("broadcastNotification", null),
            ("matchMakingEnabled", true),
            // Not a penalty: the minutes LeaverBusterController.shouldPreventAlternateLogin lets a game this PC started under another
            // summoner block a second account's login (it then asks gameService.getGame and shows the reconnect view). Reconstruction:
            // retail's value is not in the client; 0 keeps the guard off, and PlatformGames.Get refuses a non-member's running game anyway.
            ("leaverBusterPenaltyTime", 0)));
    }

    public static AsObject AllSummonerData(PlatformAccount account, PlatformEconomyRules? rules = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        return AirWire.Object(AllSummonerDataAlias,
            ("summoner", Summoner(account)),
            ("spellBook", SpellBook(account)),
            ("summonerDefaultSpells", DefaultSpells(account)),
            ("summonerTalentsAndPoints", Talents(account)),
            ("summonerLevelAndPoints", LevelAndPoints(account)),
            ("summonerLevel", Level(account, rules)));
    }

    /// <param name="skinsOff">Batch 751: the host's skins switch is off (<see cref="AirGameDtos.SkinIds"/>), so no champion lists a skin.</param>
    /// <param name="disabled">Batch 751: the champions the host turned off (<see cref="PlatformControls.DisabledChampions"/>).</param>
    public static ArrayCollection Champions(PlatformAccount? account = null, PlatformEconomyRules? rules = null, bool skinsOff = false,
        IReadOnlySet<int>? disabled = null) => AirWire.List(
        AirGameDtos.AllChampionNames.OrderBy(pair => pair.Key)
            .Select(pair => Champion(pair.Value, pair.Key, account, rules, skinsOff, !Greyed(disabled, pair.Key))));

    /// <summary>
    /// Batch 751: a champion the host turned off is greyed out in the client, except Battle Training's three, which stay active so its fixed
    /// grid keeps them (<see cref="PlatformGames.KeptByBattleTraining"/>); the server still refuses those outside Battle Training.
    /// </summary>
    private static bool Greyed(IReadOnlySet<int>? disabled, int champion) =>
        disabled?.Contains(champion) == true && !PlatformGames.KeptByBattleTraining(champion);

    public static AsObject PublicSummonerData(PlatformAccount account, PlatformEconomyRules? rules = null)
    {
        var data = AllSummonerData(account, rules);
        data.TypeName = AirProtocolNames.AllPublicSummonerDataDTO;
        data["summoner"] = AirWire.Object(AirProtocolNames.BasePublicSummonerDTO,
            ("sumId", (double)account.SummonerId), ("acctId", (double)account.Id), ("name", account.Summoner),
            ("internalName", InternalName(account)), ("profileIconId", account.ProfileIconId), ("seasonOneTier", "UNRANKED"));
        return data;
    }

    // Reconstruction (owner decision, 18 October 2026): the two timer durations carry three seconds of latency slack
    // (93 and 13 for a displayed 90 and 10); the reason, and what retail did (not recovered), are on PlatformGames.MainPickServerSeconds.
    public static ArrayCollection GameTypes() => AirWire.List(new object[]
    {
        AirWire.Object(GameTypeConfigAlias,
            ("id", 1d),
            ("name", "GAME_CFG_PICK_BLIND"),
            ("pickMode", "SimulPickStrategy"),
            ("mainPickTimerDuration", (double)PlatformGames.MainPickServerSeconds),
            ("banTimerDuration", 0d),
            ("postPickTimerDuration", (double)PlatformGames.PostPickServerSeconds),
            ("maxAllowableBans", 0),
            ("allowTrades", false),
            ("exclusivePick", false)),
        // Draft Mode (batch 543). From the stock client: the id is CreatePracticeGameController.GAME_TYPE_DRAFT_MODE (2), the
        // bundle renders practiceGame_gameMode_GAME_CFG_DRAFT_STD as "Draft Mode", the pick mode is
        // GameTypeConfig.PICK_MODE_DRAFT_MODE_SINGLE_PICK (the client's draft music, tips and ban views key on it), and the
        // bundle's tips state four bans, two per captain. Reconstruction (the 2011 draft rules; no shipped file holds this DTO):
        // a champion is picked once per game (exclusivePick) and teammates may trade at the end (allowTrades; the stock trade
        // controls appear only then). The durations are on PlatformGames.DraftBanServerSeconds.
        AirWire.Object(GameTypeConfigAlias,
            ("id", (double)AirGameDtos.DraftConfigId),
            ("name", "GAME_CFG_DRAFT_STD"),
            ("pickMode", "DraftModeSinglePickStrategy"),
            ("mainPickTimerDuration", (double)PlatformGames.DraftPickServerSeconds),
            ("banTimerDuration", (double)PlatformGames.DraftBanServerSeconds),
            ("postPickTimerDuration", (double)PlatformGames.DraftPostPickServerSeconds),
            ("maxAllowableBans", PlatformGames.DraftMaxBans),
            ("allowTrades", true),
            ("exclusivePick", true)),
        // Batch 669: Battle Training (AirGameDtos.BattleTrainingConfigId). From the stock client: its tour tells the player "in this training
        // mode you may take as much time as you need" (champion_tutorial_time_limit) and warns 60 seconds before the end
        // (tutorialChampionSelection_timeoutMessage). Reconstruction (the DTO is the server's): the name, a blind pick, and the length,
        // PlatformGames.BattleTrainingPickServerSeconds.
        AirWire.Object(GameTypeConfigAlias,
            ("id", (double)AirGameDtos.BattleTrainingConfigId),
            ("name", "GAME_CFG_ADVANCED_TUTORIAL"),
            ("pickMode", "SimulPickStrategy"),
            ("mainPickTimerDuration", (double)PlatformGames.BattleTrainingPickServerSeconds),
            ("banTimerDuration", 0d),
            ("postPickTimerDuration", (double)PlatformGames.PostPickServerSeconds),
            ("maxAllowableBans", 0),
            ("allowTrades", false),
            ("exclusivePick", false)),
    });

    // gameMapService.getGameMapList: Summoner's Rift only, as batch 534 left it (the stock client reads the custom-game maps from
    // gameMapEnabledDTOList, and asks for this list only when that one is missing).
    public static ArrayCollection Maps() => AirWire.List(new object[] { MapFor(PlatformStoreCalendar.NormalRiftMapId) });

    // The stock client's map table (MapGeneratedData.as:30-33: name, displayName, totalPlayers) for every map a custom game may be
    // created on. A lobby search names the lobby's own map through this (H14, PracticeGameSearchResult.gameMap).
    public static AsObject MapFor(int mapId) => mapId switch
    {
        1 => Map(1, "SummonersRift", "Summoner's Rift", "Summoner's Rift", PlatformStoreCalendar.MaxSlotsOf(1), 1),
        2 => Map(2, "SummonersRiftWinter", "Summoner's Rift (Autumn)", "Summoner's Rift (Autumn)", PlatformStoreCalendar.MaxSlotsOf(2), 1),
        4 => Map(4, "TwistedTreeline", "The Twisted Treeline", "The Twisted Treeline", PlatformStoreCalendar.MaxSlotsOf(4), 1),
        // Batch 822: the winter rift, 1.52's own row (MapGeneratedData.as:33).
        6 => Map(6, "SummonersRiftXmas", "Summoner's Rift (Winter)", "Summoner's Rift (Winter)", PlatformStoreCalendar.MaxSlotsOf(6), 1),
        8 => Map(8, "CrystalScar", "The Crystal Scar", "The Crystal Scar", PlatformStoreCalendar.MaxSlotsOf(8), 1),
        _ => throw new ArgumentOutOfRangeException(nameof(mapId), mapId, "Not a map a custom game is created on."),
    };

    // The solo queues (PlatformGames.Queues) feed the same blind-pick and game handoff as customs. The stock play menu enables a leaf
    // whose queue id this list holds (ChooseGameFlowViewController.refreshItem), and each queue plays its one map.
    public static ArrayCollection QueueCatalog() => AirWire.List(PlatformGames.Queues.Select(queue => (object)
        AirWire.Object(AirProtocolNames.GameQueueConfig,
            ("id", (double)queue.Id), ("name", queue.Name), ("type", queue.Type), ("gameMode", Amberline.Core.GameMaps.ModeOf(queue.MapId)),
            // Reconstruction (batch 579): a party fills at most one side. The stock client reads both sizes only for ranked queues, whose
            // sizes and level are the client's own (batch 586, PlatformGames.RankedQueues).
            ("gameTypeConfigId", queue.ConfigId), ("numPlayersPerTeam", (double)queue.TeamSize),
            ("minimumParticipantListSize", queue.MinParty), ("maximumParticipantListSize", queue.PartyLimit),
            ("minLevel", queue.Ranked ? PlatformGames.RankedMinLevel : 1), ("maxLevel", 30), ("ranked", queue.Ranked),
            // Batch 844: 1.52's GameQueueConfig.teamOnly, true for the Ranked Teams queues 42 and 41 (AirLine.RankedTeamQueues): the play menu
            // offers their party leaf only to a team member (ChooseGameFlowViewController.as:245-262) and opens the team invite bay (:614).
            ("teamOnly", queue.TeamOnly),
            ("supportedMapIds", AirWire.List(new object[] { (double)queue.MapId })))));

    public static AsObject SummonerCatalog()
    {
        var groups = Masteries.Value.Definitions
            .GroupBy(definition => definition.GroupId)
            .OrderBy(group => group.Key)
            .Select(group => AirWire.Object(TalentGroupAlias,
                ("tltGroupId", group.Key),
                ("name", group.First().GroupName),
                ("index", group.Key),
                ("talentRows", AirWire.List(group
                    .GroupBy(definition => definition.Row)
                    .OrderBy(row => row.Key)
                    .Select((row, index) => AirWire.Object(TalentRowAlias,
                        ("tltGroupId", group.Key),
                        ("tltRowId", row.Key),
                        ("index", index),
                        ("pointsToActivate", row.First().PointsToActivate),
                        ("talents", AirWire.List(row.OrderBy(definition => definition.Column)
                            .Select(Talent)))))))));

        var runeTypes = new[]
        {
            (RuneType: 1, Count: RunePage.RedSlots),
            (RuneType: 3, Count: RunePage.YellowSlots),
            (RuneType: 5, Count: RunePage.BlueSlots),
            (RuneType: 7, Count: RunePage.BlackSlots),
        };
        var slotId = 1;
        var slots = runeTypes.SelectMany(type => Enumerable.Range(0, type.Count)
            .Select(index => AirWire.Object(RuneSlotAlias,
                ("id", slotId++),
                ("runeType", AirWire.Object(RuneTypeAlias, ("runeTypeId", type.RuneType))),
                ("minLevel", Enumerable.Range(1, 30).First(level => RunePage.UnlockedSlots(level,
                    type.RuneType switch { 1 => RuneColor.Red, 3 => RuneColor.Yellow, 5 => RuneColor.Blue, _ => RuneColor.Black }) > index)))));

        return AirWire.Object(SummonerCatalogAlias,
            ("talentTree", AirWire.List(groups)),
            ("spellBookConfig", AirWire.List(slots)));
    }

    public static AsObject RuneInventory(PlatformAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var economy = PlatformEconomy.For(account);
        // SummonerRune.recentPurchase() glows a rune bought in the last 48 hours (RuneRenderer), so the date is the real
        // purchase of that rune, and none for a rune the account was never sold (all-unlocked accounts, combiner awards).
        var purchased = (economy.Purchases ?? [])
            .Where(purchase => purchase.ItemId.StartsWith("rune:", StringComparison.OrdinalIgnoreCase))
            .GroupBy(purchase => purchase.ItemId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Max(purchase => purchase.PurchasedAt.UtcDateTime),
                StringComparer.OrdinalIgnoreCase);
        // An all-unlocked account owns the 374 regular runes. A limited-edition window rune (Harrowing 2011) is never part of that
        // grant: it is listed only when the account bought it, however many copies, and stays listed after its window closed.
        int Quantity(RuneData rune) => economy.AllUnlocked && Runes.Value.IsRegular(rune.Id) ? (rune.Color == RuneColor.Black ? 3 : 9)
            : economy.OwnedRunes?.GetValueOrDefault(rune.Id) ?? 0;
        var ids = Runes.Value.All
            .Concat(PlatformStoreCalendar.Default.WindowRuneIds.Order()
                .Select(id => Runes.Value.Get(id)).OfType<RuneData>().Where(rune => !Runes.Value.IsRegular(rune.Id)))
            .Where(rune => Quantity(rune) > 0)
            .Select(rune => AirWire.Object(SummonerRuneAlias,
                ("summonerId", (double)account.SummonerId),
                ("runeId", rune.Id),
                ("rune", TypedRune(rune)),
                ("quantity", Quantity(rune)),
                ("currentAvailableQuantity", Quantity(rune)),
                ("usesRem", 0),
                ("purchaseDate", purchased.TryGetValue("rune:" + rune.Id, out var when) ? when : null)));
        return AirWire.Object(RuneInventoryAlias,
            ("summonerId", (double)account.SummonerId),
            ("dateString", DateTime.UtcNow.ToString("O")),
            ("summonerRunes", AirWire.List(ids)));
    }

    // The rune book's "Runes Type" filter (RuneBookController.filterByRuneCategory) lists a rune when its first ItemEffect's
    // effectCategory equals the choice (RuneCategories: 1 Health, 2 Mana, 3 PhysicalAttack, 4 Magic, 5 Defense, 6 Utility; 0 All).
    // Batch 799: the category and type of each stat are the 1.52 catalogue's own (AirGeneratedContent RuneGeneratedData: categoryId and
    // runeType of each effect, 41 names with one pair each), which Inventory.hydrateRuneData reads and falls back to whenever
    // SummonerRune.rune is null or has no imagePath, so a null rune put every rune under Health. Batch 798's table came from the 1.45
    // catalogue, whose effects all say categoryId 1, and was a Reconstruction: energy was filed under Utility (the catalogue says Mana), the
    // mana pool under Mana (the catalogue says Health), and three percent stats were missing. effectType is the catalogue's runeType, the
    // primary type the tooltip ("Primary"/"Secondary") and the rune book glow read. RuneCategories.effectToCategoryMap is not used.
    private static readonly Dictionary<string, (int Category, int Type)> RuneEffectCategories = new(StringComparer.Ordinal)
    {
        ["FlatHPPoolMod"] = (1, 3), ["rFlatHPModPerLevel"] = (1, 3), ["FlatHPRegenMod"] = (1, 3), ["rFlatHPRegenModPerLevel"] = (1, 3),
        ["FlatMPPoolMod"] = (1, 5), ["rFlatMPModPerLevel"] = (2, 5), ["FlatMPRegenMod"] = (2, 3), ["rFlatMPRegenModPerLevel"] = (2, 3),
        ["FlatPhysicalDamageMod"] = (3, 1), ["rFlatPhysicalDamageModPerLevel"] = (3, 1), ["PercentAttackSpeedMod"] = (3, 1),
        ["rPercentAttackSpeedModPerLevel"] = (3, 1), ["FlatCritChanceMod"] = (3, 1), ["rFlatCritChanceModPerLevel"] = (3, 1),
        ["FlatCritDamageMod"] = (3, 1), ["rFlatCritDamageModPerLevel"] = (3, 1), ["rFlatArmorPenetrationMod"] = (3, 1),
        ["rFlatArmorPenetrationModPerLevel"] = (3, 1),
        ["FlatMagicDamageMod"] = (4, 5), ["rFlatMagicDamageModPerLevel"] = (4, 5), ["rFlatMagicPenetrationMod"] = (4, 1),
        ["rFlatMagicPenetrationModPerLevel"] = (4, 1),
        ["FlatArmorMod"] = (5, 3), ["rFlatArmorModPerLevel"] = (5, 3), ["FlatSpellBlockMod"] = (5, 5), ["rFlatSpellBlockModPerLevel"] = (5, 5),
        ["rFlatDodgeMod"] = (5, 3), ["rFlatDodgeModPerLevel"] = (5, 3),
        ["PercentMovementSpeedMod"] = (6, 7), ["rPercentCooldownMod"] = (6, 5), ["rPercentCooldownModPerLevel"] = (6, 5),
        ["rFlatGoldPer10Mod"] = (6, 7), ["PercentEXPBonus"] = (6, 7), ["rPercentTimeDeadMod"] = (6, 7),
        ["FlatEnergyPoolMod"] = (2, 5), ["rFlatEnergyModPerLevel"] = (2, 5), ["FlatEnergyRegenMod"] = (2, 3), ["rFlatEnergyRegenModPerLevel"] = (2, 3),
        ["PercentHPPoolMod"] = (2, 3), ["PercentLifeStealMod"] = (3, 1), ["PercentSpellVampMod"] = (4, 1),
    };

    // The fields Inventory.makeHydratedRune sets, from the shipped rune record. The values keep the shipped sign: the stock
    // renderers print "+ " before the formatted value, so a cooldown rune reads "+ -0.29%" exactly as with AIR's own catalog.
    // imagePath only has to be non-null (every renderer loads the art by itemId from AIR's catalog); the twelve Elusion
    // records have no icon in the shipped data or in that catalog, so they send "".
    private static AsObject TypedRune(RuneData rune) => AirWire.Object(AirProtocolNames.Rune,
        ("itemId", rune.Id),
        ("runeType", AirWire.Object(RuneTypeAlias, ("runeTypeId",
            rune.Color switch { RuneColor.Red => 1, RuneColor.Yellow => 3, RuneColor.Blue => 5, _ => 7 }))),
        ("tier", (int)rune.Tier),
        ("imagePath", rune.Icon.Length == 0 ? string.Empty : Path.ChangeExtension(Path.GetFileName(rune.Icon), ".png")),
        ("itemEffects", AirWire.List(rune.Modifiers.Select(modifier =>
        {
            var (category, type) = RuneEffectCategories.GetValueOrDefault(modifier.Name);
            return (object)AirWire.Object(AirProtocolNames.ItemEffect,
                ("name", modifier.Name), ("displayName", modifier.Name), ("value", modifier.Value),
                ("effectCategory", category), ("effectType", type));
        }))));

    public static AsObject StatsSummary(PlatformAccount account) => AirProfileDtos.PlayerStatSummaries(account, []);

    /// <summary>The self Summoner DTO; <paramref name="isMe"/> is false when another session looks this summoner up.</summary>
    public static AsObject Summoner(PlatformAccount account, bool isMe = true)
    {
        ArgumentNullException.ThrowIfNull(account);
        return AirWire.Object(SummonerAlias,
            ("summonerId", (double)account.SummonerId),
            ("sumId", (double)account.SummonerId),
            ("acctId", (double)account.Id),
            ("internalName", InternalName(account)),
            ("name", account.Summoner),
            ("profileIconId", account.ProfileIconId),
            ("isMe", isMe),
            ("seasonOneTier", "UNRANKED"),
            ("revisionId", 0d),
            ("revisionDate", DateTime.UtcNow),
            ("lastGameDate", null),
            ("nameChangeFlag", false),
            ("advancedTutorialFlag", false),
            ("tutorialFlag", account.TutorialFlag),
            ("helpFlag", account.HelpFlag),
            ("displayEloQuestionaire", false),
            ("socialNetworkUserIds", AirWire.List(Array.Empty<object>())));
    }

    // Search and invite responses hydrate the sealed PublicSummoner class in
    // AIR. Keep this projection separate from the self-only Summoner DTO:
    // flags, revision data and social IDs are not public summoner fields.
    public static AsObject PublicSummoner(PlatformAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var talents = Talents(account);
        return AirWire.Object(PublicSummonerAlias,
            ("summonerId", (double)account.SummonerId),
            ("acctId", (double)account.Id),
            ("internalName", InternalName(account)),
            ("name", account.Summoner),
            ("profileIconId", account.ProfileIconId),
            ("summonerLevel", (double)account.Loadout.SummonerLevel),
            ("summonerAssociatedTalents", talents["summonerAssociatedTalents"]));
    }

    public static AsObject SummonerSummary(PlatformAccount account) => AirWire.Object(AirProtocolNames.SummonerSummary,
        ("accountId", (double)account.Id), ("internalName", InternalName(account)), ("name", account.Summoner),
        ("level", (double)account.Loadout.SummonerLevel), ("wins", 0d), ("losses", 0d), ("leaves", 0d),
        ("playerStatSummaries", StatsSummary(account)), ("socialNetworkUserIds", AirWire.List(Array.Empty<object>())));

    public static AsObject ActiveBoosts(PlatformAccount account, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(account);
        var economy = PlatformEconomy.For(account);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var ipEndDate = FormatBoostEndDate(economy.IpBoostEndDate, now);
        var xpEndDate = FormatBoostEndDate(economy.XpBoostEndDate, now);
        var ipPerWin = economy.IpBoostPerWinCount > 0 ? $"{economy.IpBoostPerWinCount} wins" : string.Empty;
        var xpPerWin = economy.XpBoostPerWinCount > 0 ? $"{economy.XpBoostPerWinCount} wins" : string.Empty;
        return AirWire.Object(ActiveBoostsAlias,
            ("_summonerId", (double)account.SummonerId),
            ("summonerId", (double)account.SummonerId),
            ("_ipBoostEndDate", (double)economy.IpBoostEndDate),
            ("_xpBoostEndDate", (double)economy.XpBoostEndDate),
            ("_ipBoostPerWinCount", (double)economy.IpBoostPerWinCount),
            ("_xpBoostPerWinCount", (double)economy.XpBoostPerWinCount),
            // ActiveBoosts.as starts its native refresh timer from the public
            // setters. Keep these equal to the backing vars above; sending only
            // underscore fields bypasses that client behavior.
            ("ipBoostEndDate", (double)economy.IpBoostEndDate),
            ("xpBoostEndDate", (double)economy.XpBoostEndDate),
            ("ipBoostPerWinCount", (double)economy.IpBoostPerWinCount),
            ("xpBoostPerWinCount", (double)economy.XpBoostPerWinCount),
            ("displayIpEndDate", ipEndDate),
            ("displayXpEndDate", xpEndDate),
            ("displayIpBoostPerWinCount", ipPerWin),
            ("displayXpBoostPerWinCount", xpPerWin),
            ("displayIpBoostTooltip", string.Join("\n", new[] { ipPerWin, ipEndDate }.Where(value => value.Length > 0))));
    }

    public static AsObject ActiveBoostsNotification(PlatformAccount account, bool rpOff = false)
    {
        ArgumentNullException.ThrowIfNull(account);
        var economy = PlatformEconomy.For(account);
        return AirWire.Object(FulfillmentAlias,
            ("inventoryType", "BOOST"),
            ("ip", (double)economy.Ip),
            ("rp", (double)ShownRp(economy, rpOff)),
            ("data", ActiveBoosts(account)));
    }

    public static AsObject AccountBalanceNotification(PlatformAccount account, bool rpOff = false)
    {
        var economy = PlatformEconomy.For(account);
        return AirWire.Object(AirProtocolNames.StoreAccountBalanceNotification,
            ("ip", (double)economy.Ip), ("rp", (double)ShownRp(economy, rpOff)));
    }

    /// <summary>
    /// NOT RETAIL (batch 751): the RP balance the client is sent. While the host's "Offer skins and RP" switch is off
    /// (<see cref="PlatformControls.PaidContentOff"/>) it is 0; the stored balance is untouched and shows again when the switch is on.
    /// Retail always sent the balance.
    /// </summary>
    public static long ShownRp(PlatformEconomyState economy, bool rpOff) => rpOff ? 0 : economy.Rp;

    private static string FormatBoostEndDate(long endDate, DateTimeOffset now)
    {
        if (endDate <= now.ToUnixTimeMilliseconds()) return string.Empty;
        var remaining = TimeSpan.FromMilliseconds(endDate - now.ToUnixTimeMilliseconds());
        var days = (int)remaining.TotalDays;
        return days > 0 ? $"Expires in {days}d {remaining.Hours}h" : $"Expires in {remaining.Hours}h";
    }

    private static AsObject AccountSummary(PlatformAccount account) => AirWire.Object(AccountSummaryAlias,
        ("summonerName", account.Summoner),
        ("items", AirWire.List(Array.Empty<object>())),
        ("partnerMode", false),
        ("admin", false),
        ("hasBetaAccess", true),
        ("accountId", (double)account.Id),
        ("username", InternalName(account)),
        ("needsPasswordReset", false),
        ("summonerInternalName", InternalName(account)),
        ("groupCount", 0d));

    private static AsObject Level(PlatformAccount account, PlatformEconomyRules? rules = null) => AirWire.Object(SummonerLevelAlias,
        ("summonerLevel", (double)account.Loadout.SummonerLevel),
        ("summonerTier", (double)account.Loadout.SummonerLevel),
        ("expToNextLevel", (double)(rules ?? new PlatformEconomyRules()).ExperienceToNextLevel(account.Loadout.SummonerLevel)),
        ("expTierMod", 0d),
        ("infTierMod", 0d));

    private static AsObject LevelAndPoints(PlatformAccount account) => AirWire.Object(SummonerLevelAndPointsAlias,
        ("sumId", (double)account.SummonerId),
        ("summonerLevel", (double)account.Loadout.SummonerLevel),
        ("expPoints", (double)PlatformEconomy.For(account).Experience),
        ("infPoints", (double)PlatformEconomy.For(account).Ip));

    public static AsObject SpellBook(PlatformAccount account)
    {
        var pages = PlatformAccounts.RunePagesFor(account).OrderBy(page => page.PageId)
            .Select(page => (object)SpellBookPage(account, page)).ToArray();
        return AirWire.Object(SpellBookAlias,
            ("summonerId", (double)account.SummonerId),
            ("dateString", DateTime.UtcNow.ToString("O")),
            ("spellBookPages", AirWire.List(pages)),
            ("_spellBookPages", AirWire.List(pages)));
    }

    public static AsObject SpellBookPage(PlatformAccount account, PlatformRunePage page) =>
        AirWire.Object(SpellBookPageAlias,
            ("summonerId", (double)account.SummonerId), ("pageId", (double)page.PageId),
            ("name", page.Name), ("isCurrent", page.IsCurrent), ("createDate", page.CreatedAt),
            ("slotEntries", AirWire.List(page.Slots.OrderBy(slot => slot.Key).Select(slot =>
                AirWire.Object(SlotEntryAlias, ("runeSlotId", slot.Key), ("runeId", slot.Value))))));

    public static AsObject Talents(PlatformAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        // NOT RETAIL (MasteryPage.KnownOnly): a stored page from the 126 tree lists none of its ids, so the profile still loads.
        var known = Amberline.Core.MasteryPage.KnownOnly(Masteries.Value, account.Loadout.Masteries, out _)
                    ?? new Dictionary<int, int>();
        var entries = known
            .Where(pair => pair.Value > 0)
            .Select(pair => AirWire.Object(AssociatedTalentAlias,
                ("comp_id", AirWire.Object(AssociatedTalentPkAlias,
                    ("sumId", (double)account.SummonerId), ("tltId", pair.Key))),
                ("rank", pair.Value),
                ("talent", Talent(Masteries.Value.Get(pair.Key)))));
        var spent = known.Values.Sum();
        return AirWire.Object(SummonerTalentsAndPointsAlias,
            ("summonerId", (double)account.SummonerId),
            ("talentPoints", (double)account.Loadout.SummonerLevel),
            ("unusedTalentPoints", (double)Math.Max(0, account.Loadout.SummonerLevel - spent)),
            ("summonerAssociatedTalents", AirWire.List(entries)));
    }

    private static AsObject Champion(string name, int id, PlatformAccount? account, PlatformEconomyRules? rules, bool skinsOff, bool enabled)
    {
        var economy = account is null ? null : PlatformEconomy.For(account);
        // A champion the server cannot play is listed but locked, as an unowned champion was in retail: owned and free
        // are false whatever the account holds, so the client's grid shows it and ChampionSelectionController will not
        // offer it (isAvailable()); PlatformGames.Pick refuses it too.
        var playable = AirGameDtos.ChampionNames.ContainsKey(id);
        return AirWire.Object(ChampionAlias,
            ("championId", id),
            ("displayName", name),
            ("description", string.Empty),
            ("skinName", name),
            // Batch 751: retail's own switch for a champion pulled from play. The client greys an inactive champion out and will not
            // let it be chosen or randomed (ChampionSelectionController.as:1085, 1736, 1828) and its tooltip reads "Disabled"
            // (ChampionTooltip.as:274). The client resets this flag from ClientSystemStates.inactiveChampionIdList, which says the same.
            ("active", enabled),
            ("owned", playable && (economy is null || economy.AllUnlocked || (economy.OwnedChampions?.Contains(id) ?? false))),
            ("freeToPlay", playable && (rules ?? new PlatformEconomyRules()).FreeChampionIds.Contains(id)),
            ("freeToPlayReward", false),
            ("ownedByYourTeam", true),
            ("ownedByEnemyTeam", true),
            // NOT RETAIL (batch 751): a champion the host turned off is not botEnabled, so the lobby's add-bot list, which offers only
            // botEnabled champions (TeamSelectionController.as:861, 875), leaves it out; retail had no host switch.
            ("botEnabled", enabled && PlatformBots.IsBotEnabled(id)),
            ("banned", false),
            // AIR constructs its default skin itself; sending skinId=0 here
            // duplicates the base skin and indexes absent generated metadata.
            ("championSkins", AirWire.List(AirGameDtos.SkinIds(id, skinsOff).Select(skin => AirWire.Object(ChampionSkinAlias,
                ("championId", (double)id), ("skinId", (double)skin),
                ("owned", playable && (account is null || PlatformEconomy.OwnsSkin(account, skin))), ("lastSelected", false),
                // Batch 799: the flag the skin lists carry (Inventory.updateChampionSkins); a skin the price list does not sell is not obtainable.
                ("stillObtainable", PlatformStorePrices.Default.SkinRp(skin) is not null))))));
    }

    private static AsObject Map(int id, string name, string displayName, string description,
        int totalPlayers, int minCustomPlayers) => AirWire.Object(GameMapAlias,
        ("mapId", id), ("name", name), ("displayName", displayName),
        ("description", description), ("totalPlayers", totalPlayers),
        ("minCustomPlayers", minCustomPlayers));

    // Shipped SpellGeneratedData.as (minLevel), the AIR 1.52 client (batch 798; SpellGeneratedData.as:32-47): Surge (SummonerBattleCry) 1 and
    // Promote (SummonerPromoteSR) 8 are new, Garrison is 1 (was 4; wiki 129.46), 1.45's SummonerOdinPromote is gone.
    public static int SpellMinimumLevel(string canonicalName) => canonicalName.ToLowerInvariant() switch
    {
        "summonerboost" => 2, "summonerclairvoyance" => 10, "summonerdot" => 7,
        "summonerflash" => 12, "summonerfortify" => 4, "summonermana" => 6,
        "summonerrally" => 8, "summonerpromotesr" => 8,
        "summonerexhaust" or "summonerhaste" or "summonerheal" or "summonerrevive" or
            "summonersmite" or "summonerteleport" or "summonerodingarrison" or "summonerbattlecry" => 1,
        _ => throw new ArgumentException("Unsupported summoner spell.")
    };

    private static AsObject Talent(MasteryDefinition definition) => AirWire.Object(TalentAlias,
        ("gameCode", definition.Id), ("tltId", definition.Id),
        ("talentGroupId", definition.GroupId), ("talentRowId", definition.Row),
        ("index", definition.Column), ("name", definition.DisplayName),
        ("maxRank", definition.MaxRank), ("minLevel", definition.MinLevel),
        ("minTier", definition.MinTier), ("prereqTalentGameCode", definition.PrerequisiteId ?? 0),
        ("level1Desc", 0), ("level2Desc", 0), ("level3Desc", 0),
        ("level4Desc", 0), ("level5Desc", 0));

    private static string InternalName(PlatformAccount account) => $"sum{account.SummonerId}";

}
