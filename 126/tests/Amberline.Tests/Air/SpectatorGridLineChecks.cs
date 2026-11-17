using System.Net;
using Amberline.Network.Spectator;
using Amberline.Platform;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 846: the spectator grid's JSON exactly as the 1.0.0.126 client gets it (126 the client,
/// The client); the 1.0.0.132 line's copy of this check pins its two extra fields.
/// </summary>
public static class SpectatorGridLineChecks
{
    [Check("server.spectate-grid-json-shapes")]
    public static void JsonShapes() => JsonShapesAsync().GetAwaiter().GetResult();

    private static async Task JsonShapesAsync()
    {
        var root = Directory.CreateTempSubdirectory("league-spectate-json-").FullName;
        var clock = new SpectatorGridChecks.Clock(SpectatorGridChecks.Start);
        try
        {
            var folder = Path.Combine(root, SpectatorRecording.FolderName);
            await using var grid = new SpectatorGrid(new IPEndPoint(IPAddress.Loopback, 0), clock, TextWriter.Null) { MayWatch = (_, _) => true };
            TestAssert.True(grid.Add(42, folder));
            var port = grid.ListeningPort;
            var recording = new SpectatorRecording(folder, SpectatorChunk.NewChunkKey(), clock);
            recording.AppendStartup([SpectatorGridChecks.Packet()]);
            clock.Now = SpectatorGridChecks.Start.AddSeconds(1);
            TestAssert.Equal("""{"gameKey":{"gameId":42},"gameEnded":false,"chunkTimeInterval":30000}""",
                await SpectatorGridChecks.TextAsync(port, SpectatorGridChecks.Meta(42)), "getGameMetaData");
            TestAssert.Equal("""{"chunkId":1,"duration":0,"availableSince":1000,"nextAvailableChunk":209000,"endGameChunkId":0}""",
                await SpectatorGridChecks.TextAsync(port, SpectatorGridChecks.ChunkInfo(42)), "getLastChunkInfo");
            TestAssert.Equal("""{"chunkId":0,"nextChunkId":2,"availableSince":0,"endStartupChunkId":1}""",
                await SpectatorGridChecks.TextAsync(port, SpectatorGridChecks.KeyFrameInfo(42)), "getLastKeyFrameInfo");

            clock.Now = SpectatorGridChecks.Start.AddSeconds(30);
            recording.AppendGame([SpectatorGridChecks.Packet()], TimeSpan.FromSeconds(30));
            recording.End();
            clock.Now = SpectatorGridChecks.Start.AddSeconds(210);
            TestAssert.Equal("""{"gameKey":{"gameId":42},"gameEnded":true,"chunkTimeInterval":30000}""",
                await SpectatorGridChecks.TextAsync(port, SpectatorGridChecks.Meta(42)), "getGameMetaData at the end");
            TestAssert.Equal("""{"chunkId":2,"duration":30000,"availableSince":0,"nextAvailableChunk":0,"endGameChunkId":2}""",
                await SpectatorGridChecks.TextAsync(port, SpectatorGridChecks.ChunkInfo(42)), "getLastChunkInfo at the end");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
