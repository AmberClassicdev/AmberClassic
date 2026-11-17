using System.Text.Json;
using Amberline.Platform;
using Amberline.Server;
using RtmpSharp.IO;
using RtmpSharp.IO.AMF3;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 794 (AIR 132 A, the 1.52 login): master's platform serves the 1.0.0.132 AIR client, LolClient 1.52.12_01_17_13_40
/// (<c>D:\132_AIR</c>), and only it (owner, 14 November 2026); 1.45 stays on the 126 line. Evidence is the 1.52 export
/// (<c>scratch/patch132/u-air132</c>); file:line references below are into it.
/// </summary>
public static class Air132LoginChecks
{
    internal const string Client152 = "1.52.12_01_17_13_40";
    private const string Client145 = "1.45.11_10_04_15_28";

    /// <summary>
    /// The 1.52 client sends <c>Version.CURRENT_VERSION</c> "1.52.12_01_17_13_40" (Version.as:6) as <c>loginService.login</c>'s client version
    /// (LoginController.as:347); the platform admits it, refuses 1.45 with LOGIN-0001, and the companion's health probe, the platform's
    /// <c>/health</c> and the landing page name the same client.
    /// </summary>
    [Check("server.air132-login-accepts-1-52-refuses-1-45")]
    public static void LoginAccepts152AndRefuses145()
    {
        using var accounts = new PlatformAccounts(Path.Combine(Path.GetTempPath(), "league-air132-login-" + Guid.NewGuid().ToString("N")));
        var account = accounts.RegisterPublic("airlogin", "testing-password", "Air Login");
        var sessions = new PlatformSessions(accounts);
        AirFault? refused = null;
        try { sessions.Login("old", account.Username, sessions.IssueTicket(account), Client145); }
        catch (AirFault fault) { refused = fault; }
        TestAssert.True(refused is not null, "the 1.45 client is the 126 line's");
        TestAssert.Equal(AirProtocolNames.ClientVersionMismatchException, refused!.Alias);
        TestAssert.Equal("LOGIN-0001", refused.ErrorCode, "LoginView shows 'Client version {0} is rejected by server.'");
        TestAssert.Equal<object?>(Client145, refused.SubstitutionArguments.Single());
        var session = sessions.Login("new", account.Username, sessions.IssueTicket(account), Client152);
        TestAssert.True(sessions.AuthenticateChannel("new", account.Username, session.Token));

        static string Health(string client) =>
            "{\"status\":\"ready\",\"service\":\"Amberline.AirPlatform\",\"protocol\":1,\"client\":\"" + client + "\"}";
        TestAssert.True(Amberline.AirCompanion.Program.IsReadyHealth(Health(Client152)), "the companion waits for a 1.52 platform");
        TestAssert.False(Amberline.AirCompanion.Program.IsReadyHealth(Health(Client145)), "a 1.45 platform is the 126 line's");
        foreach (var home in new[] { false, true })
            TestAssert.True(Amberline.AirCompanion.Program.IsReadyHealth(JsonSerializer.Serialize(Amberline.Platform.Program.Health(home))),
                "the platform's /health names the client the companion expects");
        TestAssert.Equal(Client152, AirLine.ClientVersion);
        TestAssert.Equal(Client152, PlatformHomeModel.Testing().ClientIdentity);
    }

    /// <summary>
    /// 1.52's ClientSystemStatesNotification is IExternalizable: its body is one readUTF string of JSON (ClientSystemStatesNotification.as:13,
    /// 77-86), and assignProps copies every key it declares, so an absent key is not the class default but undefined coerced (false, 0,
    /// null, an empty list; as:88-123). Its queueThrottleDTO is always a new DTO, so an absent one has level 0, and
    /// updateClientSystemStates then calls mode.toLowerCase() on null (ApplicationController.as:1136-1148): level -1 is sent.
    /// </summary>
    [Check("server.air132-system-states-externalizable-json")]
    public static void SystemStatesAreExternalizedJson()
    {
        var login = Decode(AirDtos.LoginData(Account()));
        using var json = JsonDocument.Parse(JsonText(((AsObject)login)["clientSystemStates"]));
        var states = json.RootElement;
        // ClientSystemStatesNotification.as:16-70; 126's freeToPlayRewardChampionIdList is no 1.52 field and assignProps never reads it.
        string[] declared = ["advancedTutorialEnabled", "queueThrottleDTO", "enabledQueueIdsList", "practiceGameTypeConfigIdList",
            "teamServiceEnabled", "minNumPlayersForPracticeGame", "socialIntegrationEnabled", "inactiveOdinSpellIdList", "archivedStatsEnabled",
            "tribunalEnabled", "observableCustomGameModes", "practiceGameEnabled", "spectatorSlotLimit", "inactiveSpellIdList",
            "clientHeartBeatRateSeconds", "gameMapEnabledDTOList", "obtainableChampionSkinIDList", "unobtainableChampionSkinIDList",
            "observableGameModes", "freeToPlayChampionIdList", "runeUniquePerSpellBook", "storeCustomerEnabled", "mucServiceEnabled",
            "modularGameModeEnabled", "inactiveChampionIdList", "observerModeEnabled", "inactiveTutorialSpellIdList", "inactiveClassicSpellIdList"];
        TestAssert.Equal(string.Join(",", declared.Order(StringComparer.Ordinal)),
            string.Join(",", states.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)), "every key 1.52 declares, and only those");
        // 126's values, unchanged: an absent one would read false/0 (advancedTutorialEnabled, practiceGameEnabled, storeCustomerEnabled,
        // tribunalEnabled and the heartbeat interval among them).
        TestAssert.True(states.GetProperty("practiceGameEnabled").GetBoolean());
        TestAssert.True(states.GetProperty("advancedTutorialEnabled").GetBoolean());
        TestAssert.True(states.GetProperty("tribunalEnabled").GetBoolean());
        TestAssert.True(states.GetProperty("storeCustomerEnabled").GetBoolean());
        // Batch 848: spectating custom games, four slots. Batch 854 (restated): the friends list's drop-in, custom games only (owner,
        // 17 November 2026): canDropInSpectate reads these two, ALL for custom games, no queue.
        TestAssert.True(states.GetProperty("observerModeEnabled").GetBoolean());
        TestAssert.Equal(4, states.GetProperty("spectatorSlotLimit").GetInt32());
        TestAssert.Equal("ALL", states.GetProperty("observableCustomGameModes").GetString());
        TestAssert.Equal(0, states.GetProperty("observableGameModes").GetArrayLength());
        TestAssert.False(states.GetProperty("mucServiceEnabled").GetBoolean());
        TestAssert.Equal(1, states.GetProperty("minNumPlayersForPracticeGame").GetInt32());
        TestAssert.Equal(60, states.GetProperty("clientHeartBeatRateSeconds").GetInt32());
        TestAssert.Equal("1,2", string.Join(",", states.GetProperty("practiceGameTypeConfigIdList").EnumerateArray().Select(x => x.GetInt32())));
        TestAssert.Equal(string.Join(",", PlatformGames.Queues.Select(queue => queue.Id)),
            string.Join(",", states.GetProperty("enabledQueueIdsList").EnumerateArray().Select(x => x.GetInt32())));
        // Batch 844: Ranked Teams on (ApplicationController.as:1124 loads the RT module); the game-zone start stays off (as:1725-1727).
        TestAssert.True(states.GetProperty("teamServiceEnabled").GetBoolean());
        TestAssert.False(states.GetProperty("modularGameModeEnabled").GetBoolean());
        // copyProps writes each key into the sealed QueueThrottleDTO / GameMapEnabledDTO, so only their declared fields may appear.
        var throttle = states.GetProperty("queueThrottleDTO");
        TestAssert.Equal("level", string.Join(",", throttle.EnumerateObject().Select(p => p.Name)));
        TestAssert.Equal(-1, throttle.GetProperty("level").GetInt32(), "level 0 would read the null mode at login");
        var maps = states.GetProperty("gameMapEnabledDTOList").EnumerateArray().ToArray();
        TestAssert.Equal(string.Join(",", PlatformStoreCalendar.OpenMapIds), string.Join(",", maps.Select(map => map.GetProperty("gameMapId").GetInt32())));
        TestAssert.True(maps.All(map => map.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(["gameMapId", "minPlayers"])));
    }

    /// <summary>
    /// 1.52's BroadcastNotification is IExternalizable too (BroadcastNotification.as:11, 26-61): a readUTF string of JSON whose
    /// broadcastMessages are copied key by key into the sealed BroadcastMessage (active, messageKey, content, id, severity; the
    /// getter-only isOnLoginDataPacket must never be sent). The same object rides the bc push and LoginDataPacket.broadcastNotification.
    /// </summary>
    [Check("server.air132-broadcast-externalizable-json")]
    public static void BroadcastIsExternalizedJson()
    {
        using (var json = JsonDocument.Parse(JsonText(Decode(AirDtos.BroadcastNotification("Restart in 2 minutes", "ERROR")))))
        {
            TestAssert.Equal("broadcastMessages", string.Join(",", json.RootElement.EnumerateObject().Select(p => p.Name)));
            var message = json.RootElement.GetProperty("broadcastMessages").EnumerateArray().Single();
            TestAssert.Equal("active,content,id,messageKey,severity",
                string.Join(",", message.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
            TestAssert.Equal("Restart in 2 minutes", message.GetProperty("content").GetString());
            TestAssert.Equal("generic", message.GetProperty("messageKey").GetString());
            TestAssert.Equal("ERROR", message.GetProperty("severity").GetString());
            TestAssert.True(message.GetProperty("active").GetBoolean());
            TestAssert.False(message.GetProperty("id").GetDouble() == 11, "id 11 starts the client's own shutdown countdown");
        }
        using (var cleared = JsonDocument.Parse(JsonText(Decode(AirDtos.BroadcastNotification(null)))))
            TestAssert.Equal(0, cleared.RootElement.GetProperty("broadcastMessages").GetArrayLength());
        var login = AirDtos.LoginData(Account());
        login["broadcastNotification"] = AirDtos.BroadcastNotification("Inside the login packet");
        using var inside = JsonDocument.Parse(JsonText(((AsObject)Decode(login))["broadcastNotification"]));
        TestAssert.Equal("Inside the login packet",
            inside.RootElement.GetProperty("broadcastMessages").EnumerateArray().Single().GetProperty("content").GetString());
    }

    /// <summary>
    /// 1.52 no longer registers the Spell alias (com.riotgames.platform.catalog.spell.Spell) while SummonerDefaultSpells.spellDefault1/2 stay
    /// typed Spell (SummonerDefaultSpells.as:13-17): an untyped object would fail the setter's coercion. Nothing in 1.52 reads them beyond
    /// the Session copies (ApplicationController.as:1620, EndOfGameStatsController.as:395, 726, SummonerNameController.as:208), so both are
    /// null. The login packet also carries 1.52's four new LoginDataPacket fields (LoginDataPacket.as:17, 21, 31, 55) with the values that
    /// keep the shutdown law (ApplicationController.as:1729) and the ghost-game state (as:1610, 1829) off.
    /// </summary>
    [Check("server.air132-default-spells-null")]
    public static void DefaultSpellsAreNullAndTheLoginPacketCarries152Fields()
    {
        var account = Account();
        var login = AirDtos.LoginData(account);
        foreach (var data in new[] { (AsObject)login["allSummonerData"], AirDtos.PublicSummonerData(account) })
        {
            var spells = (AsObject)data["summonerDefaultSpells"];
            TestAssert.Equal(AirProtocolNames.SummonerDefaultSpells, spells.TypeName);
            TestAssert.Equal<object?>((double)account.SummonerId, spells["summonerId"]);
            TestAssert.Equal<object?>(null, spells["spellDefault1"]);
            TestAssert.Equal<object?>(null, spells["spellDefault2"]);
        }
        TestAssert.False(Aliases(login).Contains("com.riotgames.platform.catalog.spell.Spell"), "the alias 1.52 dropped is sent nowhere");
        TestAssert.Equal<object?>(false, login["inGhostGame"]);
        TestAssert.Equal<object?>(false, login["minor"]);
        TestAssert.Equal<object?>(false, login["minorShutdownEnforced"]);
        TestAssert.True(login["minutesUntilMidnight"] is double minutes && double.IsNaN(minutes), "a Number the class leaves unset (NaN)");
        // The packet still crosses the AMF writer whole.
        TestAssert.Equal(AirProtocolNames.LoginDataPacket, ((AsObject)Decode(login)).TypeName);
    }

    /// <summary>
    /// 1.52's GameException.init registers the UnexpectedServiceException alias to GameException (GameException.as:30; it runs as a shell
    /// mixin after _LolClient_FlexInit), so that root cause decodes as a GameException: PlatformException's errorCode,
    /// substitutionArguments and rootCauseClassname (PlatformException.as), nothing else. LcdsErrorHandler (unchanged from 1.45) then never
    /// sees an UnexpectedServiceException: the session-expired branch (its rootCauseClassname test) is dead, and getClassByAlias of that
    /// alias answers GameException, so useRuneCombiner's handler, registered for the UnexpectedServiceException class
    /// (InventoryServiceImpl.as:33-40), is unreachable. Each such fault reaches handleUnhandledRemoteError (BaseShell.as:1078-1097,
    /// ViewMediatorImpl.as:171-190): an InvalidSessionException with an errorCode logs out as expired; any other PlatformException
    /// with an errorCode shows that text.
    /// </summary>
    [Check("server.air132-unexpected-service-read-as-game-exception")]
    public static void RootCausesCarryOnlyPlatformExceptionFields()
    {
        foreach (var fault in new[] { AirFault.InvalidSession(), AirFault.Unexpected("refused"), AirFault.LoginFailed(), AirFault.Stale(),
                     AirFault.VersionMismatch(Client145), AirFault.AlreadyInGame(true, "lobby", 7) })
        {
            var root = fault.RootCause();
            TestAssert.Equal("errorCode,rootCauseClassname,substitutionArguments", string.Join(",", root.Keys.Order(StringComparer.Ordinal)),
                $"{root.TypeName} decodes into a PlatformException class");
            TestAssert.True(root["errorCode"] is string { Length: > 0 }, "handleUnhandledRemoteError acts only on an errorCode");
        }
        var expired = AirFault.InvalidSession().RootCause();
        TestAssert.Equal(AirProtocolNames.InvalidSessionException, expired.TypeName, "the session-expired logout needs an InvalidSessionException");
        var combine = AirFault.Unexpected("refused").RootCause();
        TestAssert.Equal(AirProtocolNames.UnexpectedServiceException, combine.TypeName);
        TestAssert.Equal<object?>("runeCombiner_combineGenericError", combine["errorCode"], "the combiner's own text, in the general alert");
    }

    internal static PlatformAccount Account() => new(1, 1_000_001, "air132", "Air One", "", "",
        new MatchPlayer("Air One", "Annie", Amberline.Core.Team.Order));

    /// <summary>The client's side of an externalizable 1.52 body: one readUTF string (the JSON), as readExternal reads it.</summary>
    internal sealed class JsonBody : IExternalizable
    {
        public string Text { get; private set; } = "";
        public void ReadExternal(IDataInput input) => Text = input.ReadUtf();
        public void WriteExternal(IDataOutput output) => throw new NotSupportedException();
    }

    /// <summary>A reader context that decodes the 1.52 JSON classes as <see cref="JsonBody"/> (an AMF reader that does not know them fails).</summary>
    internal static SerializationContext Context()
    {
        var context = new SerializationContext();
        context.Register(typeof(JsonBody));
        context.RegisterAlias(typeof(JsonBody), AirProtocolNames.ClientSystemStatesNotification, false);
        context.RegisterAlias(typeof(JsonBody), AirProtocolNames.BroadcastNotification, false);
        return context;
    }

    internal static object Decode(object value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new AmfWriter(buffer, new SerializationContext())) writer.WriteAmf3Item(value);
        using var reader = new AmfReader(new MemoryStream(buffer.ToArray()), Context());
        return reader.ReadAmf3Item();
    }

    internal static string JsonText(object? decoded) => decoded is JsonBody body
        ? body.Text
        : throw new InvalidOperationException($"not an externalizable JSON body: {decoded?.GetType().Name ?? "null"}");

    private static HashSet<string> Aliases(object? value, HashSet<string>? seen = null)
    {
        seen ??= new HashSet<string>(StringComparer.Ordinal);
        switch (value)
        {
            case AsObject typed:
                if (typed.IsTyped) seen.Add(typed.TypeName);
                foreach (var field in typed.Values) Aliases(field, seen);
                break;
            case System.Collections.IEnumerable items and not string:
                foreach (var item in items) Aliases(item, seen);
                break;
        }
        return seen;
    }
}
