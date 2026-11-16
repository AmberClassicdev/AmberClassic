using System.Text.Json;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;

namespace Amberline.Platform;

/// <summary>
/// The wire shapes of the served AIR line, LolClient 1.52 of patch 1.0.0.132 (<see cref="AirLine"/>; batch 794). Evidence: the JPEXS
/// export of the 1.52 modules (batch 791, <c>scratch/patch132/u-air132</c>); file:line references are into it.
/// </summary>
public static partial class AirDtos
{
    /// <summary>
    /// The stock ticker message (batch 549). The client subscribes the <c>bc</c> channel (MessageRouterServiceImpl: subtopic "bc",
    /// client id "bc-&lt;accountId&gt;") and reads the same object from <c>LoginDataPacket.broadcastNotification</c>
    /// (ApplicationController.handleLoginDataPacket); either way it replaces its platform messages with <c>broadcastMessages</c>
    /// (LolClient.onBroadcastMessageReceived) and the ticker prints the bundle's <c>alert_ticker_&lt;messageKey&gt;</c> with
    /// <c>content</c> as {0} (AnimatedAlertTicker.setCurrentTickerMessage); <c>alert_ticker_generic</c> is "{0}", so the text
    /// is shown as written, on the INFO, WARN or ERROR background. An empty list clears it. The id is never 11: id 11 with key
    /// <c>hard_shutdown</c> starts the client's own five-minute shutdown countdown (PlatformShutdownDisplay). Reconstruction:
    /// retail's message ids are not recovered. Batch 794: in 1.52 the notification is IExternalizable, a JSON string whose messages
    /// are copied key by key into the sealed BroadcastMessage (BroadcastNotification.as:11, 26-61), so a message carries only that class's
    /// fields (active, messageKey, content, id, severity; never the getter-only isOnLoginDataPacket).
    /// </summary>
    public static AsObject BroadcastNotification(string? content, string severity = "WARN") =>
        Json(AirProtocolNames.BroadcastNotification, ("broadcastMessages", AirWire.List(content is null ? []
            : [AirWire.Object(AirProtocolNames.BroadcastMessage, ("id", 1d), ("active", true), ("content", content),
                ("messageKey", "generic"), ("severity", severity))])));

    // The platform-wide switches the retail server flipped per deployment. The values are this host's, chosen for what it
    // implements; the client's reaction to each is in ApplicationController.handleLoginDataPacket.
    // Reconstruction: observerModeEnabled / spectatorSlotLimit (no spectating),
    // socialIntegrationEnabled (no Facebook), tribunalEnabled (on: the report dialog opens and reportPlayer is stored for
    // the host's /manage review), archivedStatsEnabled (Season One statistics are never kept) and queueThrottleDTO (no throttle; a capacity refusal exists separately in PlatformGames). Retail's per-deployment values for these switches are not recovered.
    // Reconstruction: minNumPlayersForPracticeGame = 1 (retail's 'custom games temporarily require a minimum of {0}
    // players' knob; the owner allows a solo start), clientHeartBeatRateSeconds = 60 (the client's own default),
    // mucServiceEnabled = false (the client then builds local rooms and joins them over the XMPP connection).
    // Batch 794 (AIR 1.52): the notification is IExternalizable, a JSON string (ClientSystemStatesNotification.as:13, 77-86), and assignProps
    // copies every key the class declares (as:88-123): an absent key is undefined coerced (false, 0, null, an empty list), not the class
    // default, so every key is sent. 1.45's freeToPlayRewardChampionIdList is no 1.52 field (assignProps never read it) and is gone.
    private static AsObject ClientSystemStates(PlatformEconomyRules? rules = null, IReadOnlySet<int>? disabled = null) => Json(ClientStatesAlias,
        ("practiceGameEnabled", true),
        ("observerModeEnabled", false),
        // Batch 669: Battle Training runs (PlatformGames.CreateBattleTraining), so the client's own default, true (ClientSystemStatesNotification,
        // RiotApplication): after the basic tutorial the end-of-game screen offers it (EndOfGameStatsController.as:646-665).
        ("advancedTutorialEnabled", true),
        ("minNumPlayersForPracticeGame", 1),
        ("runeUniquePerSpellBook", false),
        ("socialIntegrationEnabled", false),
        ("tribunalEnabled", true),
        ("mucServiceEnabled", false),
        ("spectatorSlotLimit", 0),
        ("archivedStatsEnabled", false),
        ("storeCustomerEnabled", true),
        ("clientHeartBeatRateSeconds", 60),
        ("gameMapEnabledDTOList", AirWire.List(PlatformStoreCalendar.OpenMapIds
            .Select(id => AirWire.Object(AirProtocolNames.GameMapEnabledDTO, ("gameMapId", id), ("minPlayers", 1))))),
        // The custom-game form offers these, in the order of gameTypeConfigs, blind pick first (CreatePracticeGameView.initializeView).
        ("practiceGameTypeConfigIdList", AirWire.List(new object[] { (double)AirGameDtos.BlindPickConfigId, (double)AirGameDtos.DraftConfigId })),
        ("enabledQueueIdsList", AirWire.List(PlatformGames.Queues.Select(queue => (object)(double)queue.Id))),
        ("freeToPlayChampionIdList", AirWire.List((rules ?? new PlatformEconomyRules()).FreeChampionIds
            .Where(AirGameDtos.ChampionNames.ContainsKey).Select(id => (object)id))),
        // Batch 751: retail's list of champions pulled from play, here the ones the host turned off, less Battle Training's three (Greyed).
        // Once the champion list has loaded, the client sets every champion active and then this list's inactive
        // (Inventory.updateChampionsRoster_internal, Inventory.as:335-347, from ApplicationController.as:1040), so the greyed grid follows
        // this list rather than ChampionDTO.active alone.
        ("inactiveChampionIdList", AirWire.List((disabled ?? new HashSet<int>()).Where(id => Greyed(disabled, id)).Order().Select(id => (object)id))),
        // Batch 798: Fortify and Rally (AirGameDtos.InactiveSpellIds). The client resets every spell's active flag from this list whenever it
        // arrives (Inventory.as:294-305), so it carries the two the generated catalogue already marks inactive (SpellGeneratedData.as:38, 44).
        ("inactiveSpellIdList", AirWire.List(AirGameDtos.InactiveSpellIds.Select(id => (object)id))),
        // 1.52 always builds the throttle DTO (copyProps into a new QueueThrottleDTO), so an absent or empty one has level 0 and
        // updateClientSystemStates reads mode.toLowerCase() on null at login (ApplicationController.as:1136-1148). Level -1 with no mode
        // is no throttle: no message. Reconstruction: retail's no-throttle DTO is not recovered.
        ("queueThrottleDTO", AirWire.Object(AirProtocolNames.QueueThrottleDTO, ("level", -1))),
        // The keys 1.52 adds. Off: Ranked Teams (teamServiceEnabled, ApplicationController.as:1124, 1359-1366) and the game-zone start
        // (modularGameModeEnabled, as:1725-1727; createPracticeGame stays the start). Observer mode is off, so no game mode is observable
        // (SpectatorController.as:306 knows ALL and DROPINONLY; Reconstruction: NONE for neither). The skins on sale (obtainable/unobtainable,
        // Inventory.updateChampionSkins) are the price list's (SkinsOnSale, batch 799). Batch 798, Reconstruction: the per-mode
        // inactive summoner spells (Inventory.isSpellDisabledForMode, as:900-923, which the spell picker greys with) are empty: the generated
        // catalogue's gameModes already keep each spell to its modes (AirGameDtos.SpellNamesFor), and retail's per-mode switches are not recovered.
        ("teamServiceEnabled", false),
        ("modularGameModeEnabled", false),
        ("observableGameModes", AirWire.List(Array.Empty<object>())),
        ("observableCustomGameModes", "NONE"),
        ("inactiveTutorialSpellIdList", AirWire.List(Array.Empty<object>())),
        ("inactiveClassicSpellIdList", AirWire.List(Array.Empty<object>())),
        ("inactiveOdinSpellIdList", AirWire.List(Array.Empty<object>())),
        ("obtainableChampionSkinIDList", AirWire.List(SkinsOnSale(sold: true))),
        ("unobtainableChampionSkinIDList", AirWire.List(SkinsOnSale(sold: false))));

    // Batch 799: the global skin ids of the catalogue that the store sells (sold) or does not. 1.52 shows an unowned skin only while it is
    // stillObtainable (ChampionSkinBrowser.as:157), and Inventory.updateChampionSkins sets the flag from these two lists (as:425-470). The
    // store sells a skin only where its price list prices it (PlatformStoreCatalog.SkinProducts), so the lists are the price list's.
    // Reconstruction: which skins 1.0.0.132's store sold in January 2012 is not read in batch 799 (assets/air-store-2012 says what is priced).
    private static object[] SkinsOnSale(bool sold) => AirGameDtos.AllChampionNames.Keys.Order()
        .SelectMany(champion => AirGameDtos.SkinIds(champion, skinsOff: false))
        .Where(skin => (PlatformStorePrices.Default.SkinRp(skin) is not null) == sold)
        .Select(skin => (object)skin).ToArray();

    // Batch 794: AIR 1.52 no longer registers the catalog Spell alias (1.45's catalog.spell.Spell) while spellDefault1/2 stay typed
    // Spell (SummonerDefaultSpells.as:13-17), so a Spell object would reach the setter untyped and fail its coercion. 1.52 reads them
    // nowhere beyond the Session's copies (ApplicationController.as:1620, EndOfGameStatsController.as:395, 726,
    // SummonerNameController.as:208): both are null. Champion select takes the account's spells from the game's
    // PlayerChampionSelectionDTO (ChampionSelectionController.as:1700-1714).
    private static AsObject DefaultSpells(PlatformAccount account) => AirWire.Object(SummonerDefaultSpellsAlias,
        ("summonerId", (double)account.SummonerId),
        ("spellDefault1", null),
        ("spellDefault2", null));

    // Batch 794: the four fields 1.52's LoginDataPacket adds (LoginDataPacket.as:17, 21, 31, 55). handleLoginDataPacket keeps
    // inGhostGame (ApplicationController.as:1610; isInGhostGame has no caller) and enforces the shutdown law only for a minor with
    // minorShutdownEnforced and a positive minutesUntilMidnight (as:1729). Reconstruction: this host has no minors and no ghost games;
    // minutesUntilMidnight is NaN, the value the class leaves an unset Number.
    private static AsObject WithLineFields(AsObject loginData)
    {
        loginData["inGhostGame"] = false;
        loginData["minor"] = false;
        loginData["minorShutdownEnforced"] = false;
        loginData["minutesUntilMidnight"] = double.NaN;
        return loginData;
    }

    private static AirJsonObject Json(string alias, params (string Name, object? Value)[] fields)
    {
        var value = new AirJsonObject(alias);
        foreach (var (name, field) in fields)
            value[name] = field;
        return value;
    }
}

/// <summary>
/// Batch 794: an AIR 1.52 domain class that is IExternalizable, its body one <c>writeUTF</c> string of JSON. The client reads it with
/// <c>readUTF</c> and <c>com.adobe.serialization.json.JSON.decode</c>, then copies the keys it declares
/// (ClientSystemStatesNotification.as:77-123, BroadcastNotification.as:26-61). The fields are kept as an <see cref="AsObject"/> under the
/// class alias, so the server builds and inspects them as it does every other DTO; only the wire body differs (nested objects lose their
/// aliases, which JSON does not carry). A body is at most 65,535 UTF-8 bytes (<c>readUTF</c>'s length prefix).
/// </summary>
public sealed class AirJsonObject(string alias) : AsObject(alias), IExternalizable
{
    public void WriteExternal(IDataOutput output) => output.WriteUtf(JsonSerializer.Serialize<IDictionary<string, object>>(this));

    public void ReadExternal(IDataInput input) => throw new NotSupportedException("The client never sends this class.");
}
