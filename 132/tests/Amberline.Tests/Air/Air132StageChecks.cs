using System.Reflection;
using System.Text;
using System.Text.Json;
using Amberline.AirCompanion;
using Amberline.Core;
using Amberline.Data;
using Amberline.Host;
using Amberline.Platform;
using Amberline.Server;
using CompanionProgram = Amberline.AirCompanion.Program;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 795 (AIR 132 batch C): the companion's side of the AIR 1.52 client (patch 1.0.0.132) on master, which serves AIR 1.52 only.
/// Temp folders only; nothing is started. The 1.52 evidence is the batch 791 report (u-air132 REPORT.txt in the patch 132 scratch folder) and the
/// static reads of the 132 game executable and launcher.maestro.dll in docs/maintenance/2026-11-14-batch-795-air132-stage.md.
/// </summary>
public static class Air132StageChecks
{


    /// <summary>
    /// The AIR 1.52 client on this PC (read only): the one master's champion rows come from (<see cref="AirChampionData.ResolveSwf"/>: the
    /// 1.0.0.132 install's configured copy or the reference <c>132_AIR</c>). Batch 795: the AIR checks that used the 1.45 reference
    /// <c>lol_air_client</c> beside the 126 game data use it.
    /// </summary>
    internal static string ReferenceAir() => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(AirChampionData.ResolveSwf())!, "..", ".."));

    /// <summary>
    /// The overlay names the stage's own Platform: AIR132-PREVIEW's copy reaches web 8490, RTMP 2199 and chat 5222 (stage.json's port block)
    /// with the shared Maestro 8393, through the companion's --prepare-only as tools/install-132.ps1 -Air runs it. Before batch 795 the
    /// overlay wrote retail's 2099 and 5222 whatever the stage, which the 126 line's Platform holds. Without the options the copy takes this
    /// line's block (CompanionLine: RTMP 2199, chat 5222, which the client fixes; before batch 808 the 126 block).
    /// </summary>
    [Check("server.air132-overlay-line-ports")]
    public static void TheOverlayNamesTheStagesPorts()
    {
        var root = AirBootstrapChecks.TemporaryDirectory("league-air132-ports");
        try
        {
            var source = Path.Combine(root, "air152");
            AirStockLayoutFixture.Write(source);
            var destination = Path.Combine(root, "AIR132-PREVIEW", "air");
            var exit = CompanionProgram.Main(["--prepare-only", "--source", source, "--destination", destination, "--platform-url", "http://127.0.0.1:8490",
                "--maestro-port", "8393", "--rtmp-port", "2199", "--chat-port", "5222"]);
            TestAssert.Equal(0, exit, "--prepare-only with the stage's ports");
            foreach (var name in new[] { "lol.properties", "locale.properties" })
            {
                var text = File.ReadAllText(Path.Combine(destination, name));
                foreach (var line in new[] { "remoting_port=2199", "xmpp_muc_server_port=5222", "maestro_port=8393",
                             "lq_uri=http://127.0.0.1:8490/login-queue/rest/queue", "host=127.0.0.1", "xmpp_server_url=127.0.0.1" })
                    TestAssert.True(text.Contains(line + "\r\n", StringComparison.Ordinal), $"{name}: {line}\n{text}");
                foreach (var port in new[] { "2099", "8390" })
                    TestAssert.False(text.Contains(port, StringComparison.Ordinal), $"{name} names the 126 line's port {port}\n{text}");
            }

            // Configure on the same copy (the host's Play, the player's install) keeps one line per key and moves both ports.
            AirOverlay.Configure(destination, AirOverlay.ParsePlatformUrl("http://127.0.0.1:8490"), 8393, 2299, 5422);
            var again = File.ReadAllText(Path.Combine(destination, "lol.properties"));
            TestAssert.True(again.Contains("remoting_port=2299\r\n", StringComparison.Ordinal) && again.Contains("xmpp_muc_server_port=5422\r\n", StringComparison.Ordinal), again);
            TestAssert.Equal(1, again.Split("remoting_port=").Length - 1, "one remoting_port line");

            var retail = Path.Combine(root, "retail");
            AirOverlay.Prepare(new AirOverlayOptions(source, retail));
            var defaults = File.ReadAllText(Path.Combine(retail, "lol.properties"));
            TestAssert.True(defaults.Contains("remoting_port=2199\r\n", StringComparison.Ordinal) && defaults.Contains("xmpp_muc_server_port=5222\r\n", StringComparison.Ordinal),
                "without the options the copy takes this line's block (2199, 5222)\n" + defaults);

            var error = new StringWriter();
            var previous = Console.Error;
            Console.SetError(error);
            try
            {
                TestAssert.Equal(2, CompanionProgram.Main(["--prepare-only", "--source", source, "--destination", Path.Combine(root, "bad"), "--rtmp-port", "0"]), "port 0");
            }
            finally { Console.SetError(previous); }
            TestAssert.True(error.ToString().Contains("--rtmp-port", StringComparison.Ordinal), error.ToString());
            TestAssert.False(Directory.Exists(Path.Combine(root, "bad")), "a refused port leaves no copy");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    /// <summary>
    /// The game start of the 1.0.0.132 AIR entry. AIR 1.52 hands Maestro "ip port key summonerId" (MasterGameController.as:1456, unchanged
    /// from 1.45); the companion starts the loose 132 game from AIR132-PREVIEW\game with "&lt;maestroPort&gt;" "" "" "&lt;ip port key
    /// summonerId&gt;". The 132 executable reads that line as 126's did (the client: at least four tokens, atoi of the first is the Maestro
    /// port, the second and third go to MaestroGameController_Init, the fourth is sscanf'd "%s %d %s %lld"), and its launcher.maestro.dll
    /// frames are code-identical to 126's (batch 795 record). The summoner id reaches the server as the player id. No managed layout is built
    /// for the loose game (before batch 795 the start built one under this line's RADS root, which rads.dll never reads).
    /// </summary>
    [Check("server.air-maestro-132-launch-plan")]
    public static void TheLooseGameStartsWithTheMaestroCommandLine()
    {
        var root = AirBootstrapChecks.TemporaryDirectory("league-air132-launch");
        try
        {
            var stageRoot = Path.Combine(root, "AIR132-PREVIEW");
            var game = Path.Combine(stageRoot, "game");
            var executable = Path.Combine(game, "League of Legends.exe");
            Directory.CreateDirectory(game);
            File.WriteAllBytes(executable, []);
            var rads = Path.Combine(root, "rads132");
            // As tools/install-132.ps1 writes them: no maestroPort (the shared 8393), the Platform's port in platform.json.
            File.WriteAllText(Path.Combine(stageRoot, "stage.json"), JsonSerializer.Serialize(new { stockExecutable = executable, radsRoot = rads }));
            Directory.CreateDirectory(Path.Combine(stageRoot, "platform-data"));
            File.WriteAllText(Path.Combine(stageRoot, "platform-data", "platform.json"), JsonSerializer.Serialize(new { HttpPort = 8490, RtmpPort = 2199, XmppPort = 5222 }));
            var stage = CompanionProgram.LoadDesktopStage(stageRoot);
            TestAssert.Equal(8393, stage.MaestroPort, "the shared Maestro port");
            TestAssert.Equal(8490, stage.HttpPort, "the 132 Platform");
            TestAssert.Equal(executable, stage.StockExecutable);

            const long summonerId = 1_000_001;
            var request = MaestroCreateRequest.Parse($"127.0.0.1 5319 1234567890123456 {summonerId}");
            var start = StockClientLauncher.CreateStartInfo(stage.StockExecutable, request, stage.MaestroPort);
            TestAssert.Equal(executable, start.FileName);
            TestAssert.Equal(game, start.WorkingDirectory, "the loose game starts from its own folder");
            TestAssert.Equal(new[] { "8393", "", "", $"127.0.0.1 5319 1234567890123456 {summonerId}" }, start.ArgumentList.ToArray());

            // The 132 client's read of that line, step by step.
            var tokens = start.ArgumentList;
            TestAssert.True(tokens.Count >= 4, "Command line does not have correct number of parameters");
            TestAssert.Equal(stage.MaestroPort, int.Parse(tokens[0], System.Globalization.CultureInfo.InvariantCulture), "atoi(token 1) = MaestroGameController_Start's port");
            var fields = tokens[3].Split(' ');
            TestAssert.Equal(4, fields.Length, "sscanf(token 4, \"%s %d %s %lld\") == 4");
            TestAssert.Equal(summonerId, long.Parse(fields[3], System.Globalization.CultureInfo.InvariantCulture), "the %lld player id is the summoner id");
            // The server seats that summoner id as the player id of the session key check (the real server round trip:
            // server.air-reserved-native-player-identity).
            TestAssert.Equal((ulong)summonerId, MatchRoster.ToParticipants([new MatchPlayer("Summoner", "Annie", Team.Order, PlayerId: (int)summonerId)])[0].PlayerId);

            var lines = new List<string>();
            CompanionProgram.PrepareGameFolder(stage.StockExecutable, lines.Add);
            TestAssert.True(lines.Contains(CompanionLine.ManagedLayoutOff), string.Join("\n", lines));
            TestAssert.False(lines.Any(line => line.StartsWith("managed-layout: ", StringComparison.Ordinal) && line != CompanionLine.ManagedLayoutOff), string.Join("\n", lines));
            TestAssert.False(Directory.Exists(rads), "nothing is built under the stage's RADS root");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }


    /// <summary>
    /// Batch 808: the host's own Play (PrepareHostAir) keeps the home Platform's ports: RTMP and chat of its settings' defaults (batch 810: this
    /// line's PlatformLine block, 2199 and 5222). The stage names only the HTTP port (HostPlay.WriteStage).
    /// </summary>
    [Check("server.air132-host-play-keeps-the-home-platform-ports")]
    public static void TheHostPlayKeepsTheHomePlatformPorts()
    {
        var root = AirBootstrapChecks.TemporaryDirectory("league-air132-host-play");
        try
        {
            var source = Path.Combine(root, "host-air");
            AirStockLayoutFixture.Write(source);
            var stageRoot = Path.Combine(root, "stage");
            Directory.CreateDirectory(stageRoot);
            var airRoot = Path.Combine(stageRoot, "air");
            var stage = new CompanionProgram.DesktopStage(stageRoot, airRoot, "", "", "", 8490, 8393, AirSource: source);
            TestAssert.True(CompanionProgram.PrepareHostAir(stage), "the first start makes the AIR copy");
            var properties = File.ReadAllText(Path.Combine(airRoot, "lol.properties"));
            TestAssert.True(properties.Contains("lq_uri=http://127.0.0.1:8490/login-queue/rest/queue\r\n", StringComparison.Ordinal)
                && properties.Contains($"remoting_port={PlatformLine.RtmpPort}\r\n", StringComparison.Ordinal)
                && properties.Contains($"xmpp_muc_server_port={PlatformLine.XmppPort}\r\n", StringComparison.Ordinal), "the host's own copy: " + properties);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }
}
