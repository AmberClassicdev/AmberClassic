using Amberline.AirCompanion;
using Amberline.Network.Spectator;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 849 (spectating S4a): AIR's spectator launch through the companion's Maestro, on both lines (host/). Only the parse and the start
/// info are built here; the stock client is never started.
/// </summary>
public static class MaestroSpectateChecks
{
    // 32 standard base64 characters (24 bytes) with '+' and '/' in them, the shape a real credential key takes.
    private const string PinnedKey = "+/ABCDEFGHIJKLMNOPQRSTUVWXYZ0123";

    [Check("server.air-maestro-spectate-launch")]
    public static void ParsesSpectatorLaunchIntoTheExactStockCommandLine()
    {
        var payload = $"spectator 127.0.0.1 {PinnedKey} 1234567890 NA1";
        var request = MaestroCreateRequest.Parse(payload);
        TestAssert.True(request.IsSpectator, "the launch is a spectator launch");
        TestAssert.Equal(PinnedKey, request.SecurityKey);
        TestAssert.Equal(1234567890L, request.PlayerId);
        TestAssert.Equal(payload, request.ToGameArgument());
        // The platform's own key wrap is 32 characters too, so it parses as the launch's key.
        var wrapped = SpectatorChunk.WrapKey(1234567890, SpectatorChunk.NewChunkKey());
        TestAssert.Equal(wrapped, MaestroCreateRequest.Parse($"spectator 127.0.0.1 {wrapped} 1234567890 NA1").SecurityKey);

        var root = Path.Combine(Path.GetTempPath(), "amberline-spectate-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "League of Legends.exe");
        File.WriteAllBytes(executable, []);
        try
        {
            var start = StockClientLauncher.CreateStartInfo(executable, request, 8393);
            TestAssert.Equal(Path.GetFullPath(executable), start.FileName);
            TestAssert.Equal(Path.GetDirectoryName(Path.GetFullPath(executable)), start.WorkingDirectory);
            TestAssert.False(start.UseShellExecute);
            TestAssert.Equal(4, start.ArgumentList.Count);
            TestAssert.Equal("8393", start.ArgumentList[0]);
            TestAssert.Equal(string.Empty, start.ArgumentList[1]);
            TestAssert.Equal(string.Empty, start.ArgumentList[2]);
            TestAssert.Equal(payload, start.ArgumentList[3]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("server.air-maestro-spectate-refusal")]
    public static void RefusesMalformedSpectatorLaunches()
    {
        var refused = new[]
        {
            $"spectator 127.0.0.1 {PinnedKey} 42",                  // no platform ID
            $"spectator 127.0.0.1 {PinnedKey} 42 NA1 extra",        // a field too many
            $"spectator 127.0.0.1 {PinnedKey} 42 NA1 & whoami",     // a command tail
            $"Spectator 127.0.0.1 {PinnedKey} 42 NA1",              // the word in another case
            $"spectator 203.0.113.7 {PinnedKey} 42 NA1",            // another host
            $"spectator localhost {PinnedKey} 42 NA1",              // a name, not the grid's address
            $"spectator 127.0.0.1 {PinnedKey[..31]} 42 NA1",        // 31 characters
            $"spectator 127.0.0.1 {PinnedKey}A 42 NA1",             // 33 characters
            $"spectator 127.0.0.1 {PinnedKey[..30]}== 42 NA1",      // padding is not part of the key
            $"spectator 127.0.0.1 {PinnedKey[..31]}! 42 NA1",       // not base64
            $"spectator 127.0.0.1 {PinnedKey} 0 NA1",               // no game
            $"spectator 127.0.0.1 {PinnedKey} -1 NA1",
            $"spectator 127.0.0.1 {PinnedKey} 2147483648 NA1",      // above the 32-bit bound
            $"spectator 127.0.0.1 {PinnedKey} abc NA1",
            $"spectator 127.0.0.1 {PinnedKey} 42 na1",              // lower-case platform
            $"spectator 127.0.0.1 {PinnedKey} 42 NA123456789",      // nine characters
        };
        foreach (var payload in refused)
            TestAssert.Throws<FormatException>(() => MaestroCreateRequest.Parse(payload), "a malformed spectator launch is refused");
    }

    [Check("server.air-maestro-spectate-remote-host")]
    public static void SpectatorArgumentIgnoresTheRemoteHostAndPlayersKeepTheirOwn()
    {
        var spectator = MaestroCreateRequest.Parse($"spectator 127.0.0.1 {PinnedKey} 42 NA1");
        TestAssert.Equal(spectator.ToGameArgument(), (spectator with { ServerHost = "203.0.113.7" }).ToGameArgument(),
            "a remote game host never reaches a spectator launch");

        var player = MaestroCreateRequest.Parse("127.0.0.1 5119 1234567890123456 42");
        TestAssert.False(player.IsSpectator, "a player's payload is not a spectator launch");
        TestAssert.Equal("203.0.113.7 5119 1234567890123456 42", (player with { ServerHost = "203.0.113.7" }).ToGameArgument(),
            "a player still takes the remote game host");
    }
}
