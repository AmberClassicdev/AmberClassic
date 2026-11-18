using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amberline.AirCompanion;
using Amberline.Host;

namespace Amberline.Tests.Air;

public static partial class AirPlayerInstallChecks
{
    /// <summary>
    /// Batch 856 (design "End to end, in process"): batch 856b's stand-in against batch 856a's player tool. The host's 1.0.0.126 install
    /// (closed, found by this user's pointer: a test key, its <c>platform.json</c> naming a free old port, its 0.1.25 tool with
    /// <c>capabilities.json</c>) and the host's 1.0.0.132 bootstrap routes on another free port with another test key. A friend's 1.0.0.126
    /// player with a 0.1.24 tool: the released reader (the frozen record) sees the old server and a newer tool, so the tool updates as every
    /// released tool stages and switches; the new tool's finishing run shows the moved dialog, never plays, and Set up reaches the 1.0.0.132
    /// tool's <c>--install</c> with the right connection file. An old Play file accepts the stand-in's manifest (its rule needs a valid host),
    /// and the tool it then runs shows the moved dialog in its <c>--install</c> window instead of a 1.0.0.126 install. Synthetic profile and
    /// installs, free loopback ports, no window and no started program (the seams).
    /// </summary>
    [Check("server.host-old-shortcuts-end-to-end", TimeoutSeconds = 300)]
    public static void AnOldShortcutUpdatesThenOffersTheMovedServer() => EndToEndAsync().GetAwaiter().GetResult();

    private static async Task EndToEndAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-old-shortcuts-e2e");
        try
        {
            // The host's 1.0.0.132 side: its bootstrap routes with its own key, serving its tool (v132).
            using var key132 = HostFiles.Certificate(Path.Combine(root, "remote132"));
            var pin132 = key132.GetCertHashString(HashAlgorithmName.SHA256);
            var port132 = AirBootstrapChecks.FreePort();
            var oldPort = AirBootstrapChecks.FreePort();
            while (oldPort == port132) oldPort = AirBootstrapChecks.FreePort();
            var served132 = ServedTool(root, "v132");
            await using var platform132 = await AirPlayFileChecks.StartPlatformAsync(served132.Folder, port132, key132);

            // The host's 1.0.0.126 install, closed: its pointer, its key, its port, and its 0.1.25 tool with the capability marker.
            var (name, marker) = HostLoginCopyChecks.Home126();
            var pc = Path.Combine(root, "pc");
            var install126 = Path.Combine(pc, "C", name);
            Directory.CreateDirectory(Path.Combine(install126, "app", "bootstrap"));
            File.WriteAllText(Path.Combine(install126, marker), "{}");
            string oldPin;
            using (var key126 = HostFiles.Certificate(Path.Combine(install126, "data", "remote"))) oldPin = key126.GetCertHashString(HashAlgorithmName.SHA256);
            File.WriteAllText(Path.Combine(install126, "data", "platform.json"), JsonSerializer.Serialize(new { remotePort = oldPort }));
            var tool125 = Directory.CreateDirectory(Path.Combine(root, "tool-v125")).FullName;
            WriteTool(tool125, "v125");
            File.WriteAllText(Path.Combine(tool125, "capabilities.json"), "{\"movedTo\":1}");
            var archive125 = Path.Combine(install126, "app", "bootstrap", "companion.zip");
            ZipFile.CreateFromDirectory(tool125, archive125);
            var sha125 = RetailClients.Sha256(archive125);
            Directory.CreateDirectory(Path.Combine(pc, "Local", name));
            File.WriteAllText(Path.Combine(pc, "Local", name, "install.json"), JsonSerializer.Serialize(new { folder = install126 }));

            // The stand-in in the 1.0.0.132 Platform's place, guiding on. Its player folder is the 1.0.0.132 line's, as the host's Platform sends
            // it, on the 1.0.0.126 line; the 1.0.0.132 line's tool refuses its own line's folder (and never meets a stand-in), so there MovedFolder.
            var folder132 = OwnPlayerFolder == "Amberline Player" ? "Amberline 132 Player" : MovedFolder;
            var paths = Amberline.Platform.HostPaths.Resolve(Path.Combine(root, "data132"), Path.Combine(root, "Local132"), Path.Combine(root, "app", "platform"));
            var state = Amberline.Platform.HostFriendLight.StateFile(paths.Data);
            Directory.CreateDirectory(Path.GetDirectoryName(state)!);
            File.WriteAllText(state, new JsonObject { [Amberline.Platform.HostOldShortcuts.StateKey] = true }.ToJsonString());
            var loop = new Amberline.Platform.HostLoop(paths, _ => { }, TextWriter.Null)
                { CopyFrom = new Amberline.Platform.CopyPlaces(Path.Combine(pc, "Local"), Path.Combine(pc, "C"), _ => true) };
            using var accounts = new Amberline.Platform.PlatformAccounts(paths.Data);
            var from = new Amberline.Platform.EarlierHome(name, marker, "1.0.0.126", oldPort, "0.1.26");
            await using var standIn = new Amberline.Platform.HostOldShortcuts(loop, from, new Amberline.Platform.HostLoginCopy(loop, accounts, from), "127.0.0.1",
                port132, pin132) { PlayerFolder = folder132 };
            await standIn.RefreshAsync(CancellationToken.None);
            TestAssert.Equal((true, "Old 1.0.0.126 shortcuts are guided here"), standIn.Line(), "the stand-in runs");

            // A friend's 1.0.0.126 player, installed from the old Play file (the server reached as localhost), with a 0.1.24 tool.
            var fixture = Fixture.Make(Path.Combine(root, "player"));
            var result = PlayerInstall.Install(fixture.Profile, fixture.Plan(), Connection("localhost", oldPin, oldPort), "Alpha", fixture.Tool);
            var install = fixture.Install;
            var tool = Path.Combine(install, PlayerInstall.ToolFolder);
            var local = fixture.Profile.LocalAppData;

            // 1. The released reader (frozen, every tool to 0.1.24) reads the stand-in's manifest as the old server's: protocol 1, the old port and
            // pin, the host it was reached by (no change), and a tool other than its own, so it updates: the stand-in's archive, checked against
            // the manifest's SHA-256, staged (the stage step reads no movedTo), then switched in as the staged tool switches it.
            string text;
            using (var client = AirRemoteProxy.CreatePinnedClient(Convert.FromHexString(oldPin)))
                text = await client.GetStringAsync(new Uri($"https://localhost:{oldPort}/bootstrap/client.json"));
            TestAssert.Equal(new ReleasedManifest(1, "localhost", oldPort, oldPin, sha125),
                JsonSerializer.Deserialize<ReleasedManifest>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!, "the released reader sees the old server");
            TestAssert.False(PlayerInstall.Mark(tool) == sha125, "the old tool is not the stand-in's");
            var staged = await PlayerInstall.StageUpdateAsync(install, result.Server, CancellationToken.None);
            TestAssert.Equal(tool + ".next", staged);
            PlayerInstall.InstallTool(staged!, install);
            TestAssert.Equal(("v125", sha125), (Version(tool), PlayerInstall.Mark(tool)), "the shortcut's tool is the 1.0.0.126 install's 0.1.25 tool");
            TestAssert.True(File.Exists(Path.Combine(tool, "capabilities.json")), "with its capability marker");
            TestAssert.Equal("localhost", Amberline.AirCompanion.Program.ReadRemoteInvitation(Path.Combine(result.Server, "server.json")).Host, "the saved address stays");

            // 2. The new tool's finishing run: the moved dialog, never play; Set up as the 1.0.0.132 Play file, to its tool's --install with the
            // connection the 1.0.0.132 server advises (its host), its port and its pin.
            var ui = new FakeStart { LocalAppData = local, SetUp = true };
            TestAssert.Equal(0, await PlayerMode.StartAsync(install, PlayerInstall.ServerId(oldPin), checkUpdate: false, ui.Seam));
            var question = ui.Moves.Single();
            TestAssert.Equal(("Alpha", "Alpha now runs patch 1.0.0.132 (January 2012).", false), (question.Title, question.WhatHappened, question.Installed));
            TestAssert.Equal((0, 0), (ui.Played, ui.Problems.Count));
            var companion132 = Path.Combine(local, folder132, PlayerInstall.ToolFolder);
            var connection = Path.Combine(local, folder132, "server.json");
            var (file, arguments) = ui.Started.Single();
            TestAssert.Equal(Path.Combine(companion132, "Amberline.AirCompanion.exe"), file);
            TestAssert.Equal(new[] { "--install", "--connection", connection, "--server-name", "Amberline" }, arguments);
            var written = Amberline.AirCompanion.Program.ReadRemoteInvitation(connection);
            TestAssert.Equal(("127.0.0.1", port132, pin132), (written.Host, written.Port, written.CertificateSha25664hex), "the 1.0.0.132 connection file");
            TestAssert.Equal(("v132", served132.Sha256), (Version(companion132), File.ReadAllText(companion132 + ".sha256")), "the 1.0.0.132 tool");

            // 3. An old Play file run again: its manifest rule (play-bootstrap.cmd: protocol 1, its port and pin, a valid host, a SHA-256) accepts the
            // stand-in's manifest, so it downloads the 0.1.25 tool, whose --install window shows the moved dialog instead of a 1.0.0.126 install.
            using (var manifest = JsonDocument.Parse(text))
            {
                var top = manifest.RootElement;
                var advised = top.GetProperty("host").GetString()!;
                TestAssert.True(top.GetProperty("protocol").GetInt32() == 1 && top.GetProperty("port").GetInt32() == oldPort &&
                    top.GetProperty("certificateSha256").GetString() == oldPin && Regex.IsMatch(advised, "^[A-Za-z0-9.-]{1,253}$") &&
                    Uri.CheckHostName(advised) is UriHostNameType.Dns or UriHostNameType.IPv4 &&
                    Regex.IsMatch(top.GetProperty("companionSha256").GetString()!.ToUpperInvariant(), "^[0-9A-F]{64}$"), "an old Play file's rule: " + text);
            }
            var window = new FakeStart { LocalAppData = local };
            TestAssert.Equal(1, await ServerMoved.BeforeInstallAsync(Connection("localhost", oldPin, oldPort), "Alpha", Path.Combine(root, "install.log"), window.Moved));
            TestAssert.Equal(("Alpha now runs patch 1.0.0.132 (January 2012).", 0), (window.Moves.Single().WhatHappened, window.Started.Count),
                "the --install window: the moved dialog (Not now), no 1.0.0.126 install");
        }
        finally { Cleanup(root); }
    }
}
