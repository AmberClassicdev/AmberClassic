using System.Text;
using Amberline.Network.Spectator;
using Amberline.Platform;
using Amberline.Server;
using Amberline.Tests.Network;
using RtmpSharp.IO;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 848: what the 1.0.0.126 line's AIR 1.45 reads for spectating. Its SpectatorController.startSpectating hands Maestro the
/// credentials' <c>encryptionKey</c> (SpectatorController.as:131-151; 1.45's PlayerCredentialsDTO has no observerEncryptionKey), and its
/// lobby shows spectating only while the system states' observerModeEnabled is set, with spectatorSlotLimit slots in the browser
/// (ApplicationController.as:990-1034, PracticeGameSearchResult.isSpectatorFull).
/// </summary>
public static class SpectatorLobbyLineChecks
{
    [Check("server.air-spectate-line-fields")]
    public static void LineFields()
    {
        const int gameId = 1234;
        var chunkKey = SpectatorChunk.NewChunkKey();
        var credentials = (AsObject)AirSpectatorLobbyChecks.Rig.RoundTrip(AirGameDtos.ObserverCredentials(gameId, chunkKey));
        TestAssert.Equal("com.riotgames.platform.game.PlayerCredentialsDto", credentials.TypeName);
        TestAssert.Equal(SpectatorChunk.WrapKey(gameId, chunkKey), credentials["encryptionKey"], "1.45 launches with encryptionKey");
        TestAssert.False(credentials.ContainsKey("observerEncryptionKey"), "a field 1.45 does not declare");
        TestAssert.Equal(Encoding.ASCII.GetBytes(chunkKey), SpectatorChunkChecks.Client.ChunkKey((string)credentials["encryptionKey"], gameId));

        var account = new PlatformAccount(1, 1_000_001, "air126", "Air One", "", "", new MatchPlayer("Air One", "Annie", Amberline.Core.Team.Order));
        var login = (AsObject)AirSpectatorLobbyChecks.Rig.RoundTrip(AirDtos.LoginData(account));
        var states = (AsObject)login["clientSystemStates"];
        TestAssert.Equal(true, states["observerModeEnabled"], "the create box, the Spectate button and the lobby's spectator slots");
        TestAssert.Equal(4, states["spectatorSlotLimit"], "four spectators (the October 2011 FAQ)");
        // The launch's fifth field is the login packet's platformId (ApplicationController.as:1584-1587 into riotApplication.platformId).
        TestAssert.Equal("NA1", login["platformId"]);
    }

    /// <summary>
    /// Batch 854: AIR 1.45's GameReconnectionInfo declares game and playerCredentials only (aliases-126.csv; 1.52 added reconnectDelay), so a
    /// spectator's reconnect answer carries no other field, and nothing this line sends names one of 1.52's spectator end-of-game classes,
    /// which 1.45 does not register (the grid's end of game is 404 here, <c>server.spectate-end-of-game-stats</c>).
    /// </summary>
    [Check("server.air-spectate-s5-line-fields")]
    public static void S5LineFields() => S5LineFieldsAsync().GetAwaiter().GetResult();

    private static async Task S5LineFieldsAsync()
    {
        var sent = await SpectatorS5Checks.ReconnectScenarioAsync();
        sent.AddRange(await SpectatorS5Checks.EndOfGameScenarioAsync());
        var answers = sent.Select(entry => entry.Value).OfType<AsObject>().Where(value => value.TypeName == "com.riotgames.platform.game.GameReconnectInfoDTO").ToArray();
        TestAssert.True(answers.Length >= 3, "the reconnect answers were seen: " + answers.Length);
        foreach (var answer in answers)
            TestAssert.Equal("game,playerCredentials", string.Join(",", answer.Keys.Order(StringComparer.Ordinal)), "1.45's GameReconnectionInfo fields only");
        TestAssert.False(sent.Any(entry => entry.Value is AsObject value && value.TypeName.StartsWith("com.riotgames.platform.observer.", StringComparison.Ordinal)),
            "no 1.52 spectator end-of-game class");
    }
}
