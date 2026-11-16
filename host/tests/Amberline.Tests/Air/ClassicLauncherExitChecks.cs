using System.Net;
using System.Net.Sockets;
using Amberline.AirCompanion;

namespace Amberline.Tests.Air;

/// <summary>Batch 818: the classic session's end, with fake process states and a fake launcher Maestro on an ephemeral port (never 8393).</summary>
public static class ClassicLauncherExitChecks
{
    /// <summary>
    /// A launcher that outlives AIR gets AIR's MAESTROMESSAGETYPE_CLOSE (header 16, version 1, type 3, no payload) once, GracePolls polls
    /// after AIR and the game were last seen, and is stopped GracePolls polls later; a game running after AIR starts the count again.
    /// Nothing is sent or stopped before AIR was seen, nor when the launcher ends on its own; a Maestro that no longer listens is logged.
    /// </summary>
    [Check("server.classic-launcher-exit")]
    public static void LauncherOutlivingAirGetsAirsCloseThenStops()
    {
        var grace = ClassicStart.GracePolls;
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            // AIR polls 1-3, then the game alone polls 5-8 (AIR closed mid-game); the launcher runs until it is stopped.
            var polls = 0;
            var closeAt = -1;
            var stoppedAt = -1;
            var seen = 0;
            var received = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(cancel.Token);
                var stream = peer.GetStream();
                var message = await MaestroProtocol.ReadAsync(stream, cancel.Token);
                closeAt = Volatile.Read(ref polls);
                await MaestroProtocol.WriteAsync(stream, MaestroMessageType.Reply, cancellationToken: cancel.Token);
                return message;
            });
            var log = new List<string>();
            Wait(() => ++polls is >= 1 and <= 3, () => polls is >= 5 and <= 8, () => stoppedAt >= 0, () => seen++,
                () => stoppedAt = polls, log, port, cancel.Token);
            TestAssert.True(received.Wait(TimeSpan.FromSeconds(10)), "the launcher's Maestro got a frame");
            var close = received.Result;
            TestAssert.True(close is not null && close.Type == (int)MaestroMessageType.Close && close.Payload.Length == 0, "the frame is AIR's close, no payload");
            TestAssert.Equal(8 + grace, closeAt, "sent GracePolls polls after the game, the last of AIR and the game, was seen");
            TestAssert.Equal(8 + 2 * grace, stoppedAt, "stopped GracePolls polls after the close");
            TestAssert.Equal(1, seen, "AIR seen once");
            TestAssert.False(listener.Pending(), "one close only");
            TestAssert.True(log.Contains("classic: the launcher, AIR and the game have closed"), "the wait ended once the stopped launcher exited");

            // AIR never ran (the launcher's screen before Play), then the player closes the launcher: nothing sent, nothing stopped.
            polls = 0;
            var stopped = false;
            Wait(() => false, () => false, () => ++polls > 3 * grace, () => { }, () => stopped = true, [], port, cancel.Token);
            TestAssert.False(stopped || listener.Pending(), "no close and no stop before AIR was seen");

            // Retail's own end: the launcher exits within GracePolls polls after AIR (AIR's close arrived): nothing sent, nothing stopped.
            polls = 0;
            Wait(() => ++polls <= 3, () => false, () => polls > 3 + grace - 1, () => { }, () => stopped = true, [], port, cancel.Token);
            TestAssert.False(stopped || listener.Pending(), "a launcher that ends on its own gets nothing");

            // Nothing listens on the port any more (the launcher's Maestro is gone): logged, and the launcher is still stopped.
            listener.Stop();
            polls = 0;
            stoppedAt = -1;
            log.Clear();
            Wait(() => ++polls <= 1, () => false, () => stoppedAt >= 0, () => { }, () => stoppedAt = polls, log, port, cancel.Token);
            TestAssert.True(log.Exists(line => line.StartsWith($"classic: the launcher's Maestro on port {port}: ", StringComparison.Ordinal)), "the refused close is logged");
            TestAssert.Equal(1 + 2 * grace, stoppedAt, "stopped all the same");
        }
        finally
        {
            listener.Stop();
        }

        static void Wait(Func<bool> air, Func<bool> game, Func<bool> exited, Action airSeen, Action stop, List<string> log, int port, CancellationToken cancel) =>
            ClassicStart.WaitForEndAsync(air, game, exited, airSeen, stop, line => { lock (log) log.Add(line); }, port, TimeSpan.FromMilliseconds(1), cancel)
                .GetAwaiter().GetResult();
    }
}
