using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Amberline.Network.Spectator;
using Amberline.Platform;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 846 (spectating S1): the spectator grid on a free loopback port (never 8088), asked the way the stock client asks (one
/// <c>GET ... HTTP/1.0</c> per connection, read until the server closes), on a fake clock. The exact JSON of each line is pinned by its own
/// <c>server.spectate-grid-json-shapes</c>.
/// </summary>
public static class SpectatorGridChecks
{
    public static readonly DateTimeOffset Start = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    [Check("server.spectate-grid-serves-the-delayed-stream")]
    public static void ServesTheDelayedStream() => ServesTheDelayedStreamAsync().GetAwaiter().GetResult();

    private static async Task ServesTheDelayedStreamAsync()
    {
        var root = Directory.CreateTempSubdirectory("league-spectate-grid-").FullName;
        var clock = new Clock(Start);
        try
        {
            var folder = Path.Combine(root, SpectatorRecording.FolderName);
            await using var grid = new SpectatorGrid(new IPEndPoint(IPAddress.Loopback, 0), clock, TextWriter.Null);
            TestAssert.Equal(0, grid.ListeningPort, "no port before the first game");
            TestAssert.True(grid.Add(42, folder), "the first game binds");
            var port = grid.ListeningPort;
            TestAssert.True(port > 0 && port != SpectatorGrid.Port, "a free port, never 8088: " + port);
            // Batch 853: a registered game goes to nobody until the Platform sets the access rule (server.spectate-tunnel-serves-only-spectators).
            TestAssert.True((await GetAsync(port, Meta(42))).Head.StartsWith("HTTP/1.0 403 Forbidden\r\n", StringComparison.Ordinal), "no rule: refused");
            grid.MayWatch = (_, _) => true;

            // Registered before the recorder wrote anything: 200 with no chunk, polled again in a second.
            var meta = await JsonAsync(port, Meta(42));
            TestAssert.Equal(42, meta.GetProperty("gameKey").GetProperty("gameId").GetInt32(), "gameKey.gameId");
            TestAssert.False(meta.GetProperty("gameEnded").GetBoolean(), "gameEnded");
            TestAssert.Equal(30000, meta.GetProperty("chunkTimeInterval").GetInt32(), "chunkTimeInterval in ms");
            var info = await JsonAsync(port, ChunkInfo(42));
            TestAssert.Equal((0, 1000L), (info.GetProperty("chunkId").GetInt32(), info.GetProperty("nextAvailableChunk").GetInt64()), "nothing yet");

            var recording = new SpectatorRecording(folder, SpectatorChunk.NewChunkKey(), clock);
            recording.AppendStartup([Packet()]);
            info = await JsonAsync(port, ChunkInfo(42));
            TestAssert.Equal((1, 0L, 0L), (info.GetProperty("chunkId").GetInt32(), info.GetProperty("duration").GetInt64(),
                info.GetProperty("availableSince").GetInt64()), "the start-up chunk at once");
            var keyFrame = await JsonAsync(port, KeyFrameInfo(42));
            TestAssert.Equal((0, 2, 1), (keyFrame.GetProperty("chunkId").GetInt32(), keyFrame.GetProperty("nextChunkId").GetInt32(),
                keyFrame.GetProperty("endStartupChunkId").GetInt32()), "keyframe 0 (play every chunk), start-up chunk 1");
            var (head, body) = await GetAsync(port, Chunk(42, 1));
            TestAssert.True(head.StartsWith("HTTP/1.0 200 OK\r\n", StringComparison.Ordinal), head);
            TestAssert.True(head.Contains("\r\nContent-Type: application/octet-stream", StringComparison.Ordinal), head);
            TestAssert.Equal(Convert.ToHexString(File.ReadAllBytes(SpectatorRecording.ChunkPath(folder, 1))), Convert.ToHexString(body), "the chunk file as is");

            // A game chunk closed at +30 s is published at +210 s.
            clock.Now = Start.AddSeconds(30);
            recording.AppendGame([Packet()], TimeSpan.FromSeconds(30));
            info = await JsonAsync(port, ChunkInfo(42));
            TestAssert.Equal((1, 180_000L), (info.GetProperty("chunkId").GetInt32(), info.GetProperty("nextAvailableChunk").GetInt64()), "held back the delay");
            TestAssert.True((await GetAsync(port, Chunk(42, 2))).Head.StartsWith("HTTP/1.0 404 ", StringComparison.Ordinal), "chunk 2 is not served early");
            clock.Now = Start.AddSeconds(210) - TimeSpan.FromMilliseconds(1);
            TestAssert.Equal(1, (await JsonAsync(port, ChunkInfo(42))).GetProperty("chunkId").GetInt32(), "a millisecond early");
            clock.Now = Start.AddSeconds(210);
            info = await JsonAsync(port, ChunkInfo(42));
            TestAssert.Equal((2, 30_000L, 0L, 30_000L), (info.GetProperty("chunkId").GetInt32(), info.GetProperty("duration").GetInt64(),
                info.GetProperty("availableSince").GetInt64(), info.GetProperty("nextAvailableChunk").GetInt64()), "published; the next expected one interval on");
            TestAssert.Equal(Convert.ToHexString(File.ReadAllBytes(SpectatorRecording.ChunkPath(folder, 2))),
                Convert.ToHexString((await GetAsync(port, Chunk(42, 2))).Body), "chunk 2 now");

            // The end: chunk 3 closed at +40 s ends the game; nothing says so before it is published at +220 s.
            clock.Now = Start.AddSeconds(40);
            recording.AppendGame([Packet()], TimeSpan.FromSeconds(10));
            recording.End();
            clock.Now = Start.AddSeconds(215);
            info = await JsonAsync(port, ChunkInfo(42));
            TestAssert.Equal((2, 5000L, 0), (info.GetProperty("chunkId").GetInt32(), info.GetProperty("nextAvailableChunk").GetInt64(),
                info.GetProperty("endGameChunkId").GetInt32()), "the last chunk is 5 s away");
            TestAssert.False((await JsonAsync(port, Meta(42))).GetProperty("gameEnded").GetBoolean(), "not ended for the spectator yet");
            clock.Now = Start.AddSeconds(220);
            info = await JsonAsync(port, ChunkInfo(42));
            TestAssert.Equal((3, 0L, 3), (info.GetProperty("chunkId").GetInt32(), info.GetProperty("nextAvailableChunk").GetInt64(),
                info.GetProperty("endGameChunkId").GetInt32()), "the end chunk");
            TestAssert.True((await JsonAsync(port, Meta(42))).GetProperty("gameEnded").GetBoolean(), "gameEnded");

            // Anything else is 404 (400 for what is not a GET); the platform id, <n> and token segments are not read.
            foreach (var path in new[] { Meta(43), Chunk(42, 0), Chunk(42, 4), "/observer-mode/rest/consumer/getKeyFrame/NA1/42/1/token",
                         "/observer-mode/rest/consumer/getGameMetaData/NA1/42", "/observer-mode/rest/consumer/getGameDataChunk/NA1/42/x/token",
                         "/observer-mode/rest/consumer/getGameMetaData/NA1/-42/token", "/observer-mode/rest/consumer/nothing/NA1/42/1/token", "/" })
                TestAssert.True((await GetAsync(port, path)).Head.StartsWith("HTTP/1.0 404 Not Found\r\n", StringComparison.Ordinal), path);
            TestAssert.Equal(200, (await JsonStatusAsync(port, "/observer-mode/rest/consumer/getLastChunkInfo/EUW1/42/-5/anything")), "other segments");
            TestAssert.True((await RawAsync(port, "POST / HTTP/1.0\r\n\r\n")).StartsWith("HTTP/1.0 400 ", StringComparison.Ordinal), "only GET");

            // Retention: five minutes after the last chunk was published, the folder goes and the game is no longer served.
            clock.Now = Start.AddSeconds(220) + SpectatorGrid.Retention - TimeSpan.FromMilliseconds(1);
            grid.Sweep();
            TestAssert.True(Directory.Exists(folder), "kept until then");
            clock.Now = Start.AddSeconds(220) + SpectatorGrid.Retention;
            grid.Sweep();
            TestAssert.False(Directory.Exists(folder), "deleted");
            TestAssert.True((await GetAsync(port, Meta(42))).Head.StartsWith("HTTP/1.0 404 ", StringComparison.Ordinal), "and forgotten");

            // The fixed port: while one grid holds it, another one's Add says no (one log line) and binds nothing.
            var log = new StringWriter();
            await using var second = new SpectatorGrid(new IPEndPoint(IPAddress.Loopback, port), clock, log);
            TestAssert.False(second.Add(7, folder), "the port is taken");
            TestAssert.Equal(0, second.ListeningPort);
            TestAssert.True(log.ToString().StartsWith("Spectator grid: 127.0.0.1:" + port + " is not free", StringComparison.Ordinal), log.ToString());
            TestAssert.Throws<ArgumentException>(() => new SpectatorGrid(new IPEndPoint(IPAddress.Any, 0)), "loopback only");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Check("server.spectate-grid-deletes-leftovers")]
    public static void DeletesLeftovers()
    {
        var games = Directory.CreateTempSubdirectory("league-spectate-leftovers-").FullName;
        try
        {
            foreach (var game in new[] { "a", "b" })
            {
                Directory.CreateDirectory(Path.Combine(games, game, SpectatorRecording.FolderName));
                File.WriteAllText(Path.Combine(games, game, SpectatorRecording.FolderName, "chunk-1.bin"), "x");
                File.WriteAllText(Path.Combine(games, game, "game.json"), "{}");
                Directory.CreateDirectory(Path.Combine(games, game, "logs"));
            }
            Directory.CreateDirectory(Path.Combine(games, "c"));
            SpectatorGrid.DeleteLeftovers(games);
            foreach (var game in new[] { "a", "b" })
            {
                TestAssert.False(Directory.Exists(Path.Combine(games, game, SpectatorRecording.FolderName)), game + "'s stream");
                TestAssert.True(File.Exists(Path.Combine(games, game, "game.json")) && Directory.Exists(Path.Combine(games, game, "logs")),
                    game + "'s record and logs stay (result recovery reads them)");
            }
            TestAssert.True(Directory.Exists(Path.Combine(games, "c")), "a game without a stream");
            SpectatorGrid.DeleteLeftovers(Path.Combine(games, "missing"));
        }
        finally { Directory.Delete(games, recursive: true); }
    }

    public static string Meta(long gameId) => $"/observer-mode/rest/consumer/getGameMetaData/NA1/{gameId}/token";

    public static string ChunkInfo(long gameId) => $"/observer-mode/rest/consumer/getLastChunkInfo/NA1/{gameId}/30000/token";

    public static string KeyFrameInfo(long gameId) => $"/observer-mode/rest/consumer/getLastKeyFrameInfo/NA1/{gameId}/30000/token";

    public static string Chunk(long gameId, int id) => $"/observer-mode/rest/consumer/getGameDataChunk/NA1/{gameId}/{id}/token";

    public static SpectatorPacket Packet() => new(0, 1, [0x57, 0, 0, 0, 0, 1, 2]);

    /// <summary>The client's request, read until the server closes; the head and body split at the empty line.</summary>
    public static async Task<(string Head, byte[] Body)> GetAsync(int port, string path)
    {
        var text = await RawAsync(port, $"GET {path} HTTP/1.0\r\nHost: 127.0.0.1:{port}\r\n\r\n");
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        TestAssert.True(split > 0, "an empty line ends the head: " + text);
        var head = text[..split];
        var body = Encoding.Latin1.GetBytes(text[(split + 4)..]);
        TestAssert.True(head.Contains($"\r\nContent-Length: {body.Length}\r\n", StringComparison.Ordinal) && head.Contains("\r\nConnection: close", StringComparison.Ordinal),
            "the length and the close: " + head);
        return (head, body);
    }

    /// <summary>The body of a 200 answer as text (the JSON exactly as sent).</summary>
    public static async Task<string> TextAsync(int port, string path)
    {
        var (head, body) = await GetAsync(port, path);
        TestAssert.True(head.StartsWith("HTTP/1.0 200 OK\r\n", StringComparison.Ordinal) && head.Contains("\r\nContent-Type: application/json\r\n", StringComparison.Ordinal),
            path + ": " + head);
        return Encoding.UTF8.GetString(body);
    }

    private static async Task<JsonElement> JsonAsync(int port, string path) => JsonDocument.Parse(await TextAsync(port, path)).RootElement;

    private static async Task<int> JsonStatusAsync(int port, string path) => int.Parse((await GetAsync(port, path)).Head.Split(' ')[1]);

    /// <summary>One request on a new connection; the whole answer (bytes as Latin-1 characters), which ends with the server closing within 10 s.</summary>
    private static async Task<string> RawAsync(int port, string request)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var answer = new MemoryStream();
        await stream.CopyToAsync(answer, timeout.Token);
        return Encoding.Latin1.GetString(answer.ToArray());
    }

    public sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
