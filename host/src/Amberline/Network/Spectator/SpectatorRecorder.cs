using System.Buffers.Binary;
using System.Diagnostics;
using Amberline.Core;
using Amberline.Network.Protocol;
using Amberline.Network.Replication;
using Amberline.Network.Sessions;

namespace Amberline.Network.Spectator;

/// <summary>What one recorder wrote (sizes in bytes, times in wall milliseconds of the match thread).</summary>
public sealed class SpectatorRecorderStatistics
{
    public int StartupChunks { get; internal set; }
    public int GameChunks { get; internal set; }
    public long Packets { get; internal set; }
    public long Skipped { get; internal set; }
    public long Heartbeats { get; internal set; }
    public long KeepAlives { get; internal set; }
    /// <summary>The packed blocks' size bound (each packet plus <see cref="SpectatorChunk.MaxOverhead"/>).</summary>
    public long BlockBytes { get; internal set; }
    public long FileBytes { get; internal set; }
    public long StartupFileBytes { get; internal set; }
    public long LargestFileBytes { get; internal set; }
    public long LargestBlockBytes { get; internal set; }
    public double RecordMilliseconds { get; internal set; }
    public double WriteMilliseconds { get; internal set; }
    public double LongestWriteMilliseconds { get; internal set; }

    public override string ToString() => FormattableString.Invariant(
        $"spectator startup-chunks={StartupChunks} game-chunks={GameChunks} packets={Packets} skipped={Skipped} heartbeats={Heartbeats} keep-alives={KeepAlives} ") +
        FormattableString.Invariant(
        $"block-bytes={BlockBytes} file-bytes={FileBytes} startup-file-bytes={StartupFileBytes} largest-file={LargestFileBytes} largest-block={LargestBlockBytes} ") +
        FormattableString.Invariant(
        $"record-ms={RecordMilliseconds:0.0} write-ms={WriteMilliseconds:0.0} longest-write-ms={LongestWriteMilliseconds:0.0}");
}

/// <summary>
/// Batch 847 (spectating S2): the game server's recorder. A spectator is not a game-server peer: the stock client started as a spectator
/// downloads the match as recorded server-to-client packets from the Platform's grid (design 845 A, B3). This class is that spectator's
/// replication view (<see cref="ReplicationObserver.ForSpectator"/>: no hero, the union of both teams' sight, the per-team masks) and its
/// transport, plus the session packets every seat gets (<see cref="DuelSession.Spectator"/>), written through S1's
/// <see cref="SpectatorRecording"/>: the start-up chunk at the start, then a game chunk every <see cref="SpectatorRecording.ChunkInterval"/>
/// of the server's monotonic time (wall time live, the virtual clock in a headless match; pauses included), and the end. It never throws
/// into the match: a failure stops the recording (one log line) and the game plays on.
/// </summary>
public sealed class SpectatorRecorder : IReplicationTransport
{
    /// <summary>Reconstruction (design 845 C4): a chunk is cut early once its packed block could pass half the client's 1 MiB inflate buffer.</summary>
    public const int ChunkBudget = 512 * 1024;

    /// <summary>
    /// The replay client adds Game.cfg <c>[TournamentPause] HeartbeatDelay</c> to its paused time for every pause packet it plays (126
    /// The client, "Pause Heartbeat Processed"; the ctor the client reads the key with the default 5.0 at
    ///, and no shipped Game.cfg sets it), so retail's stream repeated the pause every 5 s while the game stood. The recorder does the
    /// same; the players' clients get the pause once, as before.
    /// </summary>
    public const double PauseHeartbeatSeconds = 5.0;

    private static readonly PlaintextPacket EndGameShape = CombatPackets.EndGame(true);

    private readonly SpectatorRecording _recording;
    private readonly string _folder;
    private readonly DuelSession _session;
    private readonly ReplicationDelivery _delivery;
    private readonly Func<double> _gameTime;
    private readonly Func<double> _monotonic;
    private readonly ISessionLogSink? _log;
    private readonly List<SpectatorPacket> _chunk = [];
    private long _chunkBytes;
    private double _t0;
    private float _stamp;
    private float _chunkOpenStamp;
    private double _chunkOpenedAt;
    private double? _nextHeartbeat;
    private uint _pauseClientId = uint.MaxValue;
    private bool _started, _startup, _endSeen, _ended, _stopped, _attached;

    private SpectatorRecorder(string folder, string chunkKey, DuelSession session, ReplicationDelivery delivery, Func<double> gameTime,
        Func<double> monotonic, ISessionLogSink? log)
    {
        _recording = new SpectatorRecording(folder, chunkKey);
        _folder = folder;
        _session = session;
        _delivery = delivery;
        _gameTime = gameTime;
        _monotonic = monotonic;
        _log = log;
        Observer = ReplicationObserver.ForSpectator(this);
    }

    /// <summary>
    /// Records <paramref name="runtime"/>'s match into <paramref name="folder"/> with the chunk key the Platform handed over
    /// (<see cref="SpectatorRecording.KeyVariable"/>). A bad key or a folder that already holds a stream throws here, before the match runs.
    /// Packet times are the match's game time since the start (the HUD time 0xC9 carries): the replay client's clock is the pausable game timer
    /// (the client plays a packet once its time is reached, and goes past a packet's time only while the tournament pause holds it, the
    /// timer's scale at 0, the client), so a pause freezes both, and the heartbeats carry the paused wall time.
    /// </summary>
    public static SpectatorRecorder Attach(MatchRuntime runtime, string folder, string chunkKey, ISessionLogSink? log = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var recorder = new SpectatorRecorder(folder, chunkKey, runtime.Session, runtime.Delivery, () => runtime.Combat.CurrentTime,
            runtime.World.Clock.Monotonic, log);
        runtime.Session.Spectator = recorder;
        return recorder;
    }

    public ReplicationObserver Observer { get; }
    public SpectatorRecorderStatistics Statistics { get; } = new();
    public bool Started => _started;
    public bool Ended => _ended;
    public bool Stopped => _stopped;

    /// <summary>The match started: the start-up chunk(s) at time 0, then the view joins the delivery. The chunk is built here, inside the catch.</summary>
    internal void Start(Func<IReadOnlyList<PlaintextPacket>> startup)
    {
        if (_started) return;
        _started = true;
        try
        {
            _t0 = _gameTime();
            _startup = true;
            foreach (var packet in startup()) Add(packet);
            Cut();
            _startup = false;
            _chunkOpenedAt = _monotonic();
            _delivery.AddObserver(Observer);
            _attached = true;
            Write("INFO", "spectator.recording", null);
        }
        catch (Exception error) { Stop("start", error); }
    }

    /// <summary>A packet every seat gets that a spectator sees too (the session's own sends).</summary>
    public void Record(PlaintextPacket packet)
    {
        if (!_started || _ended || _stopped || _startup) return;
        try { Add(packet); }
        catch (Exception error) { Stop("record", error); }
    }

    void IReplicationTransport.Send(PlaintextPacket packet) => Record(packet);

    void IReplicationTransport.Flushed()
    {
        if (!_started) return;
        if (_ended || _stopped)
        {
            Detach();
            return;
        }
        try
        {
            if (_endSeen)
            {
                Cut();
                _recording.End();
                _ended = true;
                Detach();
                Write("INFO", "spectator.ended", null);
                return;
            }
            var now = _monotonic();
            if (!_session.Paused) _nextHeartbeat = null;
            else if (_nextHeartbeat is not double due) _nextHeartbeat = now + PauseHeartbeatSeconds;
            else if (now >= due)
            {
                Add(PausePackets.Pause(_pauseClientId, Math.Max(1, (int)Math.Ceiling(_session.PauseSecondsRemaining))));
                Statistics.Heartbeats++;
            }
            if (now - _chunkOpenedAt + 1e-9 < SpectatorRecording.ChunkInterval.TotalSeconds) return;
            if (_chunk.Count == 0)
            {
                // Reconstruction: a chunk is never empty (the client's GetChunk fails on one, S1); a stretch without a packet gets the clock
                // at the game time now (frozen in a pause), which is what a seat's 0xC9 would carry.
                Add(SpawnPackets.GameClock(Math.Max(_stamp, (float)Math.Max(0.0, _gameTime() - _t0))));
                Statistics.KeepAlives++;
            }
            Cut();
        }
        catch (Exception error) { Stop("chunk", error); }
    }

    private void Add(PlaintextPacket packet)
    {
        var started = Stopwatch.GetTimestamp();
        // The one place every packet to the spectator passes, as MatchRuntimeNetwork.Send is a seat's: a 0xCC block carries only what this
        // client's replication store lacks, and no unit's CLIENT_ONLY fields (the spectator owns no hero).
        if (Observer.View.Store.Process(packet, 0) is not { } processed) return;
        var data = processed.Plaintext.ToArray();
        // The block format needs the 5-byte header and a channel in the marker's low nibble; the client asserts no batched packet
        // (0xFF). The server builds neither, so a skip is only counted.
        if (data.Length < 5 || data.Length - 5 > SpectatorChunk.MaxPayload || processed.Channel > 0x0F || data[0] == 0xFF)
        {
            Statistics.Skipped++;
            return;
        }
        var stamp = _startup ? 0f : Math.Max(_stamp, (float)Math.Max(0.0, _gameTime() - _t0));
        _stamp = stamp;
        if (_chunk.Count > 0 && _chunkBytes + data.Length + SpectatorChunk.MaxOverhead > ChunkBudget) Cut();
        _chunk.Add(new SpectatorPacket(stamp, processed.Channel, data));
        _chunkBytes += data.Length + SpectatorChunk.MaxOverhead;
        Statistics.Packets++;
        Statistics.BlockBytes += data.Length + SpectatorChunk.MaxOverhead;
        if (data.Length == 14 && data[0] == PausePackets.PauseOpcode && processed.Channel == LeagueProtocolConstants.CombatChannel)
        {
            _pauseClientId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(5));
            _nextHeartbeat = _monotonic() + PauseHeartbeatSeconds;
        }
        if (data.Length == EndGameShape.Plaintext.Length && data[0] == EndGameShape.Plaintext.Span[0] && processed.Channel == EndGameShape.Channel)
            _endSeen = true;
        Statistics.RecordMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private void Cut()
    {
        if (_chunk.Count == 0) return;
        var started = Stopwatch.GetTimestamp();
        // A chunk covers the game time from the previous cut to this one, whenever its last packet came.
        var end = _startup ? 0f : Math.Max(_stamp, (float)Math.Max(0.0, _gameTime() - _t0));
        var id = _startup ? _recording.AppendStartup(_chunk)
            : _recording.AppendGame(_chunk, TimeSpan.FromSeconds(Math.Max(0f, end - _chunkOpenStamp)));
        var size = new FileInfo(SpectatorRecording.ChunkPath(_folder, id)).Length;
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (_startup)
        {
            Statistics.StartupChunks++;
            Statistics.StartupFileBytes += size;
        }
        else Statistics.GameChunks++;
        Statistics.FileBytes += size;
        Statistics.LargestFileBytes = Math.Max(Statistics.LargestFileBytes, size);
        Statistics.LargestBlockBytes = Math.Max(Statistics.LargestBlockBytes, _chunkBytes);
        Statistics.WriteMilliseconds += elapsed;
        Statistics.LongestWriteMilliseconds = Math.Max(Statistics.LongestWriteMilliseconds, elapsed);
        _chunk.Clear();
        _chunkBytes = 0;
        _chunkOpenStamp = end;
        _chunkOpenedAt = _monotonic();
    }

    // The view leaves the delivery once the stream is over; called from the end of a flush, whose observer list is a copy.
    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _delivery.RemoveObserver(Observer.ObserverId);
    }

    private void Stop(string where, Exception error)
    {
        if (_stopped) return;
        _stopped = true;
        _chunk.Clear();
        // A vanished folder (a Platform restart's leftover sweep beside an orphaned game) or a full disk ends the stream; the game plays on.
        Write("WARN", "spectator.stopped", new Dictionary<string, object?>
        {
            ["at"] = where,
            ["error"] = error.GetType().Name,
            ["message"] = error.Message,
        });
    }

    private void Write(string level, string message, Dictionary<string, object?>? properties)
    {
        properties ??= new Dictionary<string, object?>();
        properties["chunks"] = Statistics.StartupChunks + Statistics.GameChunks;
        properties["file_bytes"] = Statistics.FileBytes;
        try { _log?.Write("SESSION", level, message, properties); }
        catch { /* a log sink never ends the recording */ }
    }
}
