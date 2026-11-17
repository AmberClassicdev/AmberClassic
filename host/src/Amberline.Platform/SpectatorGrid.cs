using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Amberline.Network.Spectator;

namespace Amberline.Platform;

/// <summary>
/// Batch 846 (spectating S1): the spectator grid, the HTTP service a stock game client started as a spectator downloads the game from
/// (ReplayServerConnection, 126 the client / 132 the client: port 8088 fixed in the client, base path <c>[Replay]
/// BaseRestServicePath</c> = <c>/observer-mode/rest</c>). The client sends one <c>GET &lt;path&gt; HTTP/1.0</c> per connection (126
/// The client), takes the second word of the reply as its status, the body after the first empty line
/// and reads until the server closes, so every answer is one write, then a close.
/// <para>
/// Five endpoints, <c>consumer/&lt;name&gt;/&lt;platformId&gt;/&lt;gameId&gt;[/&lt;n or id&gt;]/token</c> (batch 854: and AIR 1.52's
/// <c>endOfGameStats</c>, <see cref="EndOfGameStats"/>), for the games S3 registers
/// with <see cref="Add"/>; the platform id, the <c>&lt;n&gt;</c> (a per-session value) and the token are not read.
/// Start-up chunks are published at once, game chunks <see cref="Delay"/> after they were closed; a finished stream's folder is deleted
/// <see cref="Retention"/> after its last chunk is published, and <see cref="DeleteLeftovers"/> clears what an earlier run left.
/// </para>
/// NOT RETAIL: retail's grid was the publisher's public spectator service; this one listens on loopback only (friends reach it through the tunnel,
/// S4b) and binds when the first spectator takes a slot (<see cref="Listen"/>, batch 848) or a game registers, not at start, so a Platform
/// without spectators (and every check that runs one) never holds 8088. Both lines use the client's fixed port: while one line's Platform holds it, the other's <see cref="Add"/> says no.
/// </summary>
public sealed class SpectatorGrid : IAsyncDisposable
{
    public const int Port = 8088;

    /// <summary>
    /// Reconstruction (owner, 17 November 2026: three minutes, retail's default and the October 2011 FAQ): the spectator delay, the same
    /// value AIR waits (<c>GameDTO.spectatorDelay</c>, S3). Retail's 2011 custom-game value is not recovered.
    /// </summary>
    public static readonly TimeSpan Delay = TimeSpan.FromMinutes(3);

    /// <summary>Reconstruction (design C5): how long a finished stream stays after its last chunk is published.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Reconstruction (design C6): the 1.0.0.132 metadata's keyFrameTimeInterval, a multiple of the chunk interval as that client asserts.
    /// No keyframe exists before S6: getLastKeyFrameInfo answers keyframe 0, so both clients play every chunk in order.
    /// </summary>
    public static readonly TimeSpan KeyFrameInterval = 2 * SpectatorRecording.ChunkInterval;

    private const string BasePath = "/observer-mode/rest/consumer/";
    private const int MaxConnections = 16;
    private const int MaxRequest = 4096;
    private static readonly (int, string, byte[]) NotFound = (404, "text/plain", []);

    private readonly IPEndPoint _endPoint;
    private readonly TimeProvider _clock;
    private readonly TextWriter _log;
    private readonly object _gate = new();
    private readonly Dictionary<long, string> _games = [];
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private Task _accepting = Task.CompletedTask;
    private Task _sweeping = Task.CompletedTask;
    private int _connections;

    /// <summary>The grid on <paramref name="endPoint"/> (127.0.0.1:8088 by default; checks pass port 0), loopback only.</summary>
    public SpectatorGrid(IPEndPoint? endPoint = null, TimeProvider? clock = null, TextWriter? log = null)
    {
        _endPoint = endPoint ?? new IPEndPoint(IPAddress.Loopback, Port);
        if (!IPAddress.IsLoopback(_endPoint.Address)) throw new ArgumentException("The spectator grid listens on loopback only.", nameof(endPoint));
        _clock = clock ?? TimeProvider.System;
        _log = log ?? Console.Out;
    }

    /// <summary>The port the grid listens on, 0 while it does not.</summary>
    public int ListeningPort { get { lock (_gate) return (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0; } }

    /// <summary>
    /// Batch 853 (spectating S4b): whether a registered game's stream goes to this requester: (AIR's game id, the signed-in account of a
    /// request the friends' tunnel carried, <see cref="AirRemoteTunnel.MapSpectate"/>, or null for a request made on this PC). The Platform
    /// sets <c>PlatformGames.MaySpectate</c>; until something is set every such request is refused (403), never served.
    /// NOT RETAIL: retail's spectator service asked for no account (the URL's last segment is the literal <c>token</c>); this grid's chunks hold
    /// both teams' sight and all chat, so only that game's own spectators get them. Asked outside the grid's lock (the lobby, which calls
    /// <see cref="Add"/> under its own lock, answers it).
    /// </summary>
    public Func<long, long?, bool> MayWatch { get; set; } = (_, _) => false;

    /// <summary>
    /// Batch 854 (spectating S5): the answer to AIR 1.52's <c>endOfGameStats/&lt;platformId&gt;/&lt;gameId&gt;/&lt;handshakeToken&gt;</c>
    /// (SpectatorController.retrieveEndOfGameStats) for (AIR's game id, the account as for <see cref="MayWatch"/>): the base64 AMF3 text
    /// AIR decodes, or null (404, and AIR goes back to the lobby). The Platform sets <c>PlatformGames.SpectatorEndOfGame</c>. Asked only
    /// after <see cref="MayWatch"/> and once the stream's end is published, outside the grid's lock.
    /// </summary>
    public Func<long, long?, string?> EndOfGameStats { get; set; } = (_, _) => null;

    /// <summary>
    /// Batch 854: whether game <paramref name="gameId"/>'s stream still plays for its spectators: registered here and its end not yet
    /// published (the delay holds the last chunk back, past the result). A spectator's reconnect is offered while it does.
    /// </summary>
    public bool Streaming(long gameId)
    {
        string? folder;
        lock (_gate) _games.TryGetValue(gameId, out folder);
        if (folder is null) return false;
        try
        {
            var index = SpectatorRecording.Read(folder);
            return index.EndChunkId == 0 || _clock.GetUtcNow() < PublishedAt(index.Chunks[index.EndChunkId - 1]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }

    /// <summary>
    /// S3: serve game <paramref name="gameId"/> (AIR's id, the one in the spectator's launch) from its stream folder (the game folder's
    /// <see cref="SpectatorRecording.FolderName"/>; it may not exist yet). Binds the port at the first call; false, with one log line, while
    /// the port is taken, and spectating that game is off.
    /// </summary>
    public bool Add(long gameId, string folder)
    {
        lock (_gate)
        {
            if (!Listen()) return false;
            _games[gameId] = folder;
            return true;
        }
    }

    /// <summary>
    /// Batch 848: binds the port if it is not bound yet; false, with one log line, while it is taken. The lobby calls it when a spectator
    /// takes a slot, so the refusal (PG-0036) comes at the slot, before any game records; once bound, the grid keeps the port.
    /// </summary>
    public bool Listen()
    {
        lock (_gate)
        {
            if (_listener is not null) return true;
            var listener = new TcpListener(_endPoint) { ExclusiveAddressUse = true };
            try { listener.Start(); }
            catch (SocketException ex)
            {
                _log.WriteLine($"Spectator grid: {_endPoint} is not free ({ex.SocketErrorCode}); spectating is off until it is.");
                return false;
            }
            _listener = listener;
            _accepting = AcceptAsync(listener, _stop.Token);
            _sweeping = SweepEveryAsync(_stop.Token);
            return true;
        }
    }

    /// <summary>Platform start: removes every stream folder under the games folder (no game of this Platform runs yet).</summary>
    public static void DeleteLeftovers(string gamesDirectory)
    {
        if (!Directory.Exists(gamesDirectory)) return;
        foreach (var game in Directory.EnumerateDirectories(gamesDirectory))
        {
            var folder = Path.Combine(game, SpectatorRecording.FolderName);
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Deletes each finished stream <see cref="Retention"/> after its last chunk was published; runs every 30 s while listening.</summary>
    public void Sweep()
    {
        KeyValuePair<long, string>[] games;
        lock (_gate) games = [.. _games];
        var now = _clock.GetUtcNow();
        foreach (var (gameId, folder) in games)
        {
            try
            {
                var index = SpectatorRecording.Read(folder);
                if (index.EndChunkId == 0 || now < PublishedAt(index.Chunks[index.EndChunkId - 1]) + Retention) continue;
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                lock (_gate) _games.Remove(gameId);
            }
            // A request still reading a chunk, or a damaged index: the next sweep tries again.
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        lock (_gate) _listener?.Stop();
        await _accepting;
        await _sweeping;
        _stop.Dispose();
    }

    private static DateTimeOffset PublishedAt(SpectatorIndexEntry chunk) => chunk.Startup ? chunk.ClosedAt : chunk.ClosedAt + Delay;

    private static long Milliseconds(TimeSpan span) => (long)span.TotalMilliseconds;

    private async Task SweepEveryAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try { while (await timer.WaitForNextTickAsync(stop)) Sweep(); }
        catch (OperationCanceledException) { }
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            Socket socket;
            try { socket = await listener.AcceptSocketAsync(stop); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            if (Interlocked.Increment(ref _connections) > MaxConnections)
            {
                Interlocked.Decrement(ref _connections);
                socket.Dispose();
                continue;
            }
            _ = ServeAsync(socket, stop);
        }
    }

    private async Task ServeAsync(Socket socket, CancellationToken stop)
    {
        try
        {
            using var stream = new NetworkStream(socket, ownsSocket: true);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            if (await ReadTargetAsync(stream, timeout.Token) is not { } target) return;
            // Every connection here is from this PC (loopback only); the friends' requests come in-process through Answer.
            var (status, type, body) = Answer(target, account: null);
            var reason = status switch { 200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", _ => "Internal Server Error" };
            var head = Encoding.ASCII.GetBytes(
                $"HTTP/1.0 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            byte[] response = [.. head, .. body];
            await stream.WriteAsync(response, timeout.Token);
            socket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { Interlocked.Decrement(ref _connections); }
    }

    /// <summary>The request line's target; "" for a request that is not <c>GET &lt;target&gt; HTTP/1.x</c>, null when the peer left first.</summary>
    private static async Task<string?> ReadTargetAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[MaxRequest];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), token);
            if (read == 0) return null;
            count += read;
            var text = Encoding.ASCII.GetString(buffer, 0, count);
            if (!text.Contains("\n\r\n", StringComparison.Ordinal) && !text.Contains("\n\n", StringComparison.Ordinal)) continue;
            return text[..text.IndexOf('\n')].TrimEnd('\r').Split(' ') is ["GET", var target, var version] &&
                   version.StartsWith("HTTP/1.", StringComparison.Ordinal) ? target : "";
        }
        return "";
    }

    /// <summary>
    /// The answer to one request for <paramref name="target"/> (<c>/observer-mode/rest/consumer/...</c>): a request on this PC's port, or
    /// (batch 853) one the friends' tunnel carried for <paramref name="account"/>. 404 for an unknown game or path, 403 when
    /// <see cref="MayWatch"/> says no.
    /// </summary>
    public (int Status, string Type, byte[] Body) Answer(string target, long? account)
    {
        if (target.Length == 0) return (400, "text/plain", []);
        if (!target.StartsWith(BasePath, StringComparison.Ordinal)) return NotFound;
        var parts = target[BasePath.Length..].Split('/');
        if (parts.Length is not (4 or 5) || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var gameId)) return NotFound;
        string? folder;
        lock (_gate) _games.TryGetValue(gameId, out folder);
        if (folder is null) return NotFound;
        if (!MayWatch(gameId, account)) return (403, "text/plain", []);
        try
        {
            var index = SpectatorRecording.Read(folder);
            var now = _clock.GetUtcNow();
            var chunks = index.Chunks;
            var published = chunks.TakeWhile(chunk => PublishedAt(chunk) <= now).Count();
            var last = published == 0 ? null : chunks[published - 1];
            var startup = chunks.Count(chunk => chunk.Startup);
            var startGame = startup == 0 ? 0 : startup + 1;
            var ended = index.EndChunkId != 0 && published >= index.EndChunkId;
            switch (parts[0], parts.Length)
            {
                case ("getGameMetaData", 4):
                    return Json(SpectatorLine.GameMetaData(gameId, ended, Milliseconds(SpectatorRecording.ChunkInterval), Milliseconds(KeyFrameInterval)));
                case ("getLastChunkInfo", 5):
                    // Until the next chunk is closed, it is expected one chunk interval after the newest, plus the delay; the client polls
                    // again after this many milliseconds (at most its own wait), so the guess is never below a second.
                    var next = published < chunks.Count ? Milliseconds(PublishedAt(chunks[published]) - now)
                        : ended ? 0
                        : chunks.Count == 0 ? 1000
                        : Math.Max(1000, Milliseconds(chunks[^1].ClosedAt + SpectatorRecording.ChunkInterval + Delay - now));
                    return Json(SpectatorLine.LastChunkInfo(last?.Id ?? 0, last?.DurationMs ?? 0,
                        last is null ? 0 : Milliseconds(now - PublishedAt(last)), next, ended ? index.EndChunkId : 0, startGame));
                case ("getLastKeyFrameInfo", 5):
                    // The same fields on both lines; keyframe 0 turns the clients' jump off.
                    return Json(new { chunkId = 0, nextChunkId = startGame, availableSince = 0, endStartupChunkId = startup });
                case ("getGameDataChunk", 5) when int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
                                                  id >= 1 && id <= published:
                    return (200, "application/octet-stream", File.ReadAllBytes(SpectatorRecording.ChunkPath(folder, id)));
                case ("endOfGameStats", 4):
                    // Batch 854: AIR 1.52 asks once the drop-in spectator's client has ended; the result comes no earlier than the stream's
                    // end, which the delay holds back. Reconstruction: retail's status before then (404 sends AIR back to the lobby).
                    return ended && EndOfGameStats(gameId, account) is { } stats ? (200, "text/plain", Encoding.ASCII.GetBytes(stats)) : NotFound;
                default:
                    // getKeyFrame: no keyframes before S6.
                    return NotFound;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return NotFound; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return (500, "text/plain", []); }
    }

    private static (int, string, byte[]) Json(object value) => (200, "application/json", JsonSerializer.SerializeToUtf8Bytes(value));
}
