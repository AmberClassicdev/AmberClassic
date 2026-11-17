using System.Globalization;

namespace Amberline.Network.Spectator;

/// <summary>One indexed chunk: its id (from 1), whether it is a start-up chunk, its game-time length and when its file was complete.</summary>
public sealed record SpectatorIndexEntry(int Id, bool Startup, long DurationMs, DateTimeOffset ClosedAt);

/// <summary>A stream's index as read: the chunks in id order and, once the game has ended, the id of its last chunk (0 before).</summary>
public sealed record SpectatorIndex(IReadOnlyList<SpectatorIndexEntry> Chunks, int EndChunkId);

/// <summary>
/// Batch 846 (spectating S1): one game's spectator stream on disk, written by the game server's recorder (S2) and served by the
/// Platform's grid (<c>SpectatorGrid</c>), two processes on one PC. The folder is <see cref="FolderName"/> in the game's own folder and
/// holds <c>chunk-N.bin</c> (sealed chunks, <see cref="SpectatorChunk.Seal"/>; ids from 1, every start-up chunk before the first game chunk)
/// and <c>index.txt</c>, one line per chunk appended once its file is complete, <c>"N startup|game durationMs closedAtUnixMs"</c>, and at the
/// end of the game <c>"end N closedAtUnixMs"</c> with the last chunk's id. A reader takes only lines that end in a line feed.
/// The Platform starts the game server with <see cref="StreamSwitch"/> <c>&lt;folder&gt;</c> and the chunk key in <see cref="KeyVariable"/>
/// (beside <c>AMBERLINE_MATCH_KEY</c>); the key is never written to disk. The grid publishes a start-up chunk at once and a game chunk the
/// spectator delay after its <c>closedAt</c> (wall clock, so a pause or a fast headless match keeps the schedule honest).
/// </summary>
public sealed class SpectatorRecording
{
    public const string FolderName = "spectator";
    public const string StreamSwitch = "--spectator-stream";
    public const string KeyVariable = "AMBERLINE_SPECTATOR_KEY";
    private const string IndexName = "index.txt";

    /// <summary>
    /// Reconstruction: the game time a chunk covers (the grid's <c>chunkTimeInterval</c>). Retail's 2011 value is not recovered. The client
    /// ends the replay when no new chunk is available for this plus <c>[Replay] DownloadFailureTimeout</c> (30 s),
    /// so S2 keeps closing chunks on this cadence in wall time, through pauses too.
    /// </summary>
    public static readonly TimeSpan ChunkInterval = TimeSpan.FromSeconds(30);

    private readonly string _folder;
    private readonly string _chunkKey;
    private readonly TimeProvider _clock;
    private int _lastId;
    private bool _gameStarted;
    private bool _ended;

    /// <summary>Starts a new stream in <paramref name="folder"/> (created if missing); a folder that already holds a stream is refused.</summary>
    public SpectatorRecording(string folder, string chunkKey, TimeProvider? clock = null)
    {
        SpectatorChunk.ChunkKeyBytes(chunkKey);
        Directory.CreateDirectory(folder);
        if (File.Exists(IndexPath(folder))) throw new IOException("A spectator stream is already recorded in " + folder);
        _folder = folder;
        _chunkKey = chunkKey;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>A start-up chunk (game time 0: the roster, the spawn, StartGame); all of them come before the first game chunk.</summary>
    public int AppendStartup(IReadOnlyList<SpectatorPacket> packets)
    {
        if (_gameStarted) throw new InvalidOperationException("Start-up chunks come before the first game chunk.");
        return Append("startup", packets, 0);
    }

    /// <summary>A game chunk covering <paramref name="duration"/> of game time; the grid publishes it the delay after this call.</summary>
    public int AppendGame(IReadOnlyList<SpectatorPacket> packets, TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        var id = Append("game", packets, (long)duration.TotalMilliseconds);
        _gameStarted = true;
        return id;
    }

    /// <summary>The game is over: the last chunk appended (it holds the end of game) becomes <c>endGameChunkId</c>.</summary>
    public void End()
    {
        if (_ended) return;
        if (_lastId == 0) throw new InvalidOperationException("A stream ends after at least one chunk.");
        File.AppendAllText(IndexPath(_folder), $"end {_lastId} {_clock.GetUtcNow().ToUnixTimeMilliseconds()}\n");
        _ended = true;
    }

    private int Append(string kind, IReadOnlyList<SpectatorPacket> packets, long durationMs)
    {
        if (_ended) throw new InvalidOperationException("The stream has ended.");
        var id = _lastId + 1;
        var path = ChunkPath(_folder, id);
        File.WriteAllBytes(path + ".tmp", SpectatorChunk.Seal(SpectatorChunk.Pack(packets), _chunkKey));
        File.Move(path + ".tmp", path, overwrite: true);
        File.AppendAllText(IndexPath(_folder), $"{id} {kind} {durationMs} {_clock.GetUtcNow().ToUnixTimeMilliseconds()}\n");
        _lastId = id;
        return id;
    }

    public static string ChunkPath(string folder, int id) => Path.Combine(folder, $"chunk-{id.ToString(CultureInfo.InvariantCulture)}.bin");

    private static string IndexPath(string folder) => Path.Combine(folder, IndexName);

    /// <summary>The stream's index as it stands (empty before the first chunk); a line still being written is left for the next read.</summary>
    public static SpectatorIndex Read(string folder)
    {
        string text;
        try
        {
            using var file = new FileStream(IndexPath(folder), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            text = new StreamReader(file).ReadToEnd();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return new([], 0); }

        var chunks = new List<SpectatorIndexEntry>();
        var end = 0;
        var lines = text.Split('\n');
        foreach (var line in lines.AsSpan(0, lines.Length - 1))
        {
            var fields = line.Split(' ');
            if (end != 0) throw new InvalidDataException("The spectator index goes on after its end line.");
            if (fields is ["end", var last, _] && Number(last) == chunks.Count && chunks.Count > 0)
                end = chunks.Count;
            else if (fields is [var id, "startup" or "game", var duration, var closed] && Number(id) == chunks.Count + 1 &&
                     (fields[1] == "game" || chunks.LastOrDefault()?.Startup != false))
                chunks.Add(new(chunks.Count + 1, fields[1] == "startup", Number(duration), DateTimeOffset.FromUnixTimeMilliseconds(Number(closed))));
            else throw new InvalidDataException("A spectator index line is malformed.");
        }
        return new(chunks, end);
    }

    private static long Number(string text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : throw new InvalidDataException("A spectator index number is malformed.");
}
