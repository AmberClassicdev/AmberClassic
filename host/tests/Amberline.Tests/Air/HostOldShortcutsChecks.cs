using System.IO.Compression;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amberline.Host;
using Amberline.Platform;
using static Amberline.Tests.Air.HostModeChecks;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 856b (owner, 17 November 2026: "Approve, build it"; design Part 2): the 1.0.0.132 host's Move my 1.0.0.126 players here and the
/// stand-in on the old 1.0.0.126 port. Everything is made up and throwaway: a fake 1.0.0.126 install (its pointer in a fake local application
/// data, the folder with its marker from <c>126/patch.json</c> "home", a key made as that server makes it, an archive with or without the
/// capability marker, a <c>platform.json</c> naming a free port), a temporary 132 home on free loopback ports, a fake router and firewall.
/// Never this PC's profile, a real install, port 8391 or 8491; no key or password is ever printed.
/// </summary>
public static class HostOldShortcutsChecks
{
    private const string OldPassword = "Synthetic-old-Pw-856";
    private const string Guided = "Old 1.0.0.126 shortcuts are guided here", NotFound = "Your Amberline for 1.0.0.126 was not found",
        Update = "Update your Amberline for 1.0.0.126 to 0.1.26 or later", Allow = "Allow it through Windows Firewall",
        NotLocked = "Update your Amberline for 1.0.0.126 to 0.1.26 or later and start it once";

    /// <summary>
    /// The stand-in's trust and gates, driven directly (no Platform): off while guiding is off; runs on the files of the install this user's pointer
    /// names; the port from its <c>platform.json</c>, else the line's default; the archive must say <c>movedTo</c> 1; a missing key stays missing; a
    /// junction, a hard link or a swap between check and read is refused; at the drive's root only with every folder and file this user's (the
    /// seam, then the real ACLs); a busy port or a running 1.0.0.126 gives its reason. The fake install is the same file for file at the end.
    /// </summary>
    [Check("server.host-old-shortcuts-trust", TimeoutSeconds = 240)]
    public static void TheStandInUsesOnlyThe126FilesItMayTrust() => TrustAsync().GetAwaiter().GetResult();

    private static async Task TrustAsync()
    {
        // The 1.0.0.132 line guides the 1.0.0.126 line's friends' port (126/patch.json "ports" "remote"); the 1.0.0.126 line has no such value.
        var line = typeof(PlatformLine).GetField("CopyLoginsFrom")?.GetValue(null) as EarlierHome;
        using (var facts = JsonDocument.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(WorkspaceFiles.PatchFolder)!, "126", "patch.json"))))
            TestAssert.Equal(WorkspaceFiles.Patch == "132" ? facts.RootElement.GetProperty("ports").GetProperty("remote").GetInt32() : (int?)null, line?.RemotePort,
                "the old port the 1.0.0.132 line guides");
        if (!OperatingSystem.IsWindows()) return;
        var root = AirBootstrapChecks.TemporaryDirectory("league-old-shortcuts");
        try
        {
            var (name, marker) = HostLoginCopyChecks.Home126();
            var free = FreePorts(3);
            var (lineDefault, overridden, here) = (free[0], free[1], free[2]);
            var pc = Path.Combine(root, "pc");
            var install = Fake126(pc, name, marker, overridden);
            var before = HostLoginCopyChecks.Tree(pc);
            var key = Path.Combine(install, "data", "remote", "host.pfx");
            var tool = Path.Combine(install, "app", "bootstrap", "companion.zip");
            var pin126 = Pin(key);
            var paths = HostPaths.Resolve(Path.Combine(root, "data132"), Path.Combine(root, "Local132"), Path.Combine(root, "app", "platform"));
            Directory.CreateDirectory(paths.Data);
            var loop = new HostLoop(paths, _ => { }, TextWriter.Null) { CopyFrom = new CopyPlaces(Path.Combine(pc, "Local"), Path.Combine(pc, "C"), _ => true) };
            using var accounts = new PlatformAccounts(paths.Data);
            var from = new EarlierHome(name, marker, "1.0.0.126", RemotePort: lineDefault, LocksFrom: "0.1.26");
            var copy = new HostLoginCopy(loop, accounts, from);
            const string Pin132 = "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF";
            async Task<(bool Running, string Text)?> TryAsync(Action<HostOldShortcuts>? arrange = null, Func<HostOldShortcuts, Task>? whileOn = null)
            {
                await using var standIn = new HostOldShortcuts(loop, from, copy, "127.0.0.1", here, Pin132);
                arrange?.Invoke(standIn);
                await standIn.RefreshAsync(CancellationToken.None);
                if (whileOn is not null) await whileOn(standIn);
                return standIn.Line();
            }

            // Guiding off: nothing runs and no line, although the install is there.
            TestAssert.True(await TryAsync() is null, "guiding off: no line");
            TestAssert.False(Listening(overridden), "guiding off: nothing on the old port");
            Guide(paths.Data, true);

            // This user's pointer: trusted as found. The stand-in answers on the port its platform.json names, with that install's own key.
            TestAssert.Equal((true, Guided), await TryAsync(whileOn: async standIn =>
            {
                TestAssert.True(Listening(overridden) && !Listening(lineDefault),
                    $"on the port the 1.0.0.126 platform.json names ({standIn.Line()}; {overridden}: {Listening(overridden)}, {lineDefault}: {Listening(lineDefault)})");
                var (manifest, seen) = await GetAsync(overridden, "/bootstrap/client.json");
                TestAssert.Equal(HttpStatusCode.OK, manifest.Status, manifest.Text);
                TestAssert.Equal(pin126, seen, "the stand-in proves itself with the 1.0.0.126 key");
                using var json = JsonDocument.Parse(manifest.Text);
                var top = json.RootElement;
                TestAssert.Equal((1, "127.0.0.1", overridden, pin126, Sha(File.ReadAllBytes(tool))),
                    (top.GetProperty("protocol").GetInt32(), top.GetProperty("host").GetString(), top.GetProperty("port").GetInt32(),
                        top.GetProperty("certificateSha256").GetString(), top.GetProperty("companionSha256").GetString()), "the 1.0.0.126 manifest, the request's host");
                var moved = top.GetProperty("movedTo");
                TestAssert.Equal((here, Pin132, "Amberline", PlatformBootstrap.PlayerFolderName),
                    (moved.GetProperty("port").GetInt32(), moved.GetProperty("certificateSha256").GetString(), moved.GetProperty("name").GetString(),
                        moved.GetProperty("playerFolder").GetString()), "movedTo names this server");

                // Review, 17 November 2026: the files are read only at the start and on the press. A tool changed on disk is not picked up by a
                // retry (a re-read every few minutes made a retired install a live update source for anyone who can write to it): the retry
                // opens no file and the snapshot is still served. The press reads afresh: then the new tool is named and served, with the same key.
                var first = File.ReadAllBytes(tool);
                var newer = Zip(("capabilities.json", "{\"movedTo\":1}"), ("Amberline.AirCompanion.exe", "a newer tool"));
                File.WriteAllBytes(tool, newer);
                var opens = 0;
                standIn.BeforeOpen = _ => Interlocked.Increment(ref opens);
                TestAssert.False(await standIn.RefreshAsync(CancellationToken.None), "a retry changes nothing about running");
                TestAssert.Equal(0, opens, "a retry opens no file");
                async Task Serves(byte[] expected, string what)
                {
                    var (again, seenAgain) = await GetAsync(overridden, "/bootstrap/client.json");
                    var (served, _) = await GetAsync(overridden, "/bootstrap/companion.zip");
                    using var reread = JsonDocument.Parse(again.Text);
                    TestAssert.True(reread.RootElement.GetProperty("companionSha256").GetString() == Sha(expected) && served.Bytes.SequenceEqual(expected) && seenAgain == pin126, what);
                }
                await Serves(first, "after a retry, the snapshot's tool is named and served");
                TestAssert.False(await standIn.RefreshAsync(CancellationToken.None, read: true), "a press keeps it running on the same port");
                TestAssert.True(opens > 0, "a press reads the files");
                await Serves(newer, "after a press, the new tool is named and served, with the same key");
                standIn.BeforeOpen = null;
                File.WriteAllBytes(tool, first);
            }), "the pointer's install: guided");

            // No platform.json port: the line's default (a free port here, never 8391).
            var settings = Path.Combine(install, "data", "platform.json");
            File.Move(settings, settings + ".away");
            TestAssert.Equal((true, Guided), await TryAsync(whileOn: _ => { TestAssert.True(Listening(lineDefault), "the line's default port"); return Task.CompletedTask; }),
                "no platform.json: guided on the default port");
            File.Move(settings + ".away", settings);

            // The served tool must say it understands movedTo (interface 2): none, or another value, is the update reason, and nothing runs.
            var capable = File.ReadAllBytes(tool);
            foreach (var archive in new[] { Zip(("Amberline.AirCompanion.exe", "tool")), Zip(("capabilities.json", "{\"movedTo\":2}")), Zip(("sub/capabilities.json", "{\"movedTo\":1}")) })
            {
                File.WriteAllBytes(tool, archive);
                TestAssert.Equal((false, Update), await TryAsync(whileOn: _ => { TestAssert.False(Listening(overridden), "an old tool: nothing runs"); return Task.CompletedTask; }),
                    "a tool without the marker");
            }
            File.WriteAllBytes(tool, capable);

            // A missing key: not found, and never made.
            File.Move(key, key + ".away");
            TestAssert.Equal((false, NotFound), await TryAsync(), "no key: not found");
            TestAssert.False(File.Exists(key), "the stand-in never makes a 1.0.0.126 key");
            File.Move(key + ".away", key);

            // No install at all: neither the pointer nor the default folder.
            var pointer = Path.Combine(pc, "Local", name, "install.json");
            File.Move(pointer, pointer + ".away");
            File.Move(Path.Combine(install, marker), Path.Combine(install, marker + ".away"));
            TestAssert.Equal((false, NotFound), await TryAsync(), "no install: not found");
            File.Move(Path.Combine(install, marker + ".away"), Path.Combine(install, marker));
            File.Move(pointer + ".away", pointer);

            // Links, per handle: the key's folder a junction, the tool a hard link.
            var elsewhere = Path.Combine(root, "elsewhere");
            Directory.CreateDirectory(Path.Combine(elsewhere, "remote"));
            using (HostFiles.Certificate(Path.Combine(elsewhere, "remote"))) { }
            var remote = Path.Combine(install, "data", "remote");
            Directory.Move(remote, remote + ".away");
            HostLoginCopyChecks.Junction(remote, Path.Combine(elsewhere, "remote"));
            TestAssert.Equal((false, NotFound), await TryAsync(), "a junction on the key's way: refused");
            Directory.Delete(remote);
            Directory.Move(remote + ".away", remote);
            File.Copy(tool, Path.Combine(elsewhere, "tool.zip"));
            File.Move(tool, tool + ".away");
            HardLink(tool, Path.Combine(elsewhere, "tool.zip"));
            TestAssert.Equal((false, NotFound), await TryAsync(), "a hard-linked tool: refused");
            File.Delete(tool);
            File.Move(tool + ".away", tool);

            // Batch 857 (design step 5, route (b)): the install this user's pointer names is trusted only once it is locked. Open to the drive's users,
            // the reason says what to do; locked as that line's own start locks it (the same InstallLock), it is guided, and this line changes none of
            // its permissions.
            loop.CopyFrom = new CopyPlaces(Path.Combine(pc, "Local"), Path.Combine(pc, "C"), _ => false);
            TestAssert.Equal((false, NotLocked), await TryAsync(), "the pointer's install, not locked");
            InstallLockChecks.Hazard(Path.Combine(pc, "C"));
            loop.CopyFrom = new CopyPlaces(Path.Combine(pc, "Local"), Path.Combine(pc, "C"), CopyPlaces.Private);
            TestAssert.Equal((false, NotLocked), await TryAsync(), "the pointer's install, open to the drive's users: not locked");
            TestAssert.Equal(null, Amberline.AirCompanion.InstallLock.Lock(install, top: pc), "the 1.0.0.126 start's own lock");
            var lockedSddl = InstallLockChecks.Sddl(install);
            TestAssert.Equal((true, Guided), await TryAsync(), "the pointer's install, locked: guided");
            TestAssert.Equal(lockedSddl, InstallLockChecks.Sddl(install), "this line changed no permission of the 1.0.0.126 install");
            loop.CopyFrom = new CopyPlaces(Path.Combine(pc, "Local"), Path.Combine(pc, "C"), _ => true);

            // The default folder at the drive's root (no pointer of this user's): only while each folder and, on its handle, each file is this user's.
            File.Move(pointer, pointer + ".away");
            var asked = 0;
            var trusted = false;
            loop.CopyFrom = new CopyPlaces(Path.Combine(pc, "Local"), Path.Combine(pc, "C"), _ => { Interlocked.Increment(ref asked); return trusted; });
            TestAssert.Equal((false, NotLocked), await TryAsync(), "the drive's root, changeable by others: not locked");
            trusted = true;
            Interlocked.Exchange(ref asked, 0);
            TestAssert.Equal((true, Guided), await TryAsync(), "the drive's root, this user's alone: guided");
            TestAssert.Equal(8, asked, "asked: five folders (the install, data, data\\remote, app, app\\bootstrap), then three open handles");

            // A swap between check and read: once the five folders passed, the key's folder becomes a junction just before the key is opened (the
            // seam); the handle's own name shows it, so the key is refused.
            Interlocked.Exchange(ref asked, 0);
            var swappedAfter = -1;
            TestAssert.Equal((false, NotFound), await TryAsync(standIn => standIn.BeforeOpen = path =>
            {
                if (swappedAfter >= 0 || path != key) return;
                swappedAfter = Volatile.Read(ref asked);
                Directory.Move(remote, remote + ".away");
                HostLoginCopyChecks.Junction(remote, Path.Combine(elsewhere, "remote"));
            }), "a key swapped between the folder checks and the read: refused");
            TestAssert.Equal(5, swappedAfter, "the swap came after the five folder checks");
            Directory.Delete(remote);
            Directory.Move(remote + ".away", remote);
            loop.CopyFrom = new CopyPlaces(Path.Combine(pc, "Local"), Path.Combine(pc, "C"), CopyPlaces.Private);
            using (var identity = WindowsIdentity.GetCurrent())
            {
                foreach (var folder in Directory.GetDirectories(install, "*", SearchOption.AllDirectories).Prepend(install))
                    new DirectoryInfo(folder).SetAccessControl(HostLoginCopyChecks.PrivateTo<DirectorySecurity>(identity.User!));
                foreach (var file in Directory.GetFiles(install, "*", SearchOption.AllDirectories))
                    new FileInfo(file).SetAccessControl(HostLoginCopyChecks.PrivateTo<FileSecurity>(identity.User!));
                TestAssert.Equal((true, Guided), await TryAsync(), "the drive's root, private by its real ACLs: guided");
                // A new security object each time: one writes only what changed since its own last write.
                FileSecurity Writable()
                {
                    var writable = HostLoginCopyChecks.PrivateTo<FileSecurity>(identity.User!);
                    writable.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Write, AccessControlType.Allow));
                    return writable;
                }
                new FileInfo(key).SetAccessControl(Writable());
                TestAssert.Equal((false, NotLocked), await TryAsync(), "a key Users may write: not locked");
                new FileInfo(key).SetAccessControl(HostLoginCopyChecks.PrivateTo<FileSecurity>(identity.User!));
                new FileInfo(tool).SetAccessControl(Writable());
                TestAssert.Equal((false, NotLocked), await TryAsync(), "a tool Users may write: not locked");
                new FileInfo(tool).SetAccessControl(HostLoginCopyChecks.PrivateTo<FileSecurity>(identity.User!));
            }
            File.Move(pointer + ".away", pointer);
            loop.CopyFrom = new CopyPlaces(Path.Combine(pc, "Local"), Path.Combine(pc, "C"), _ => true);

            // A busy port: its reason; once it is free, a retry binds it with the snapshot it has and reads no file.
            var busy = new TcpListener(IPAddress.Loopback, overridden);
            busy.Start();
            try
            {
                TestAssert.Equal((true, Guided), await TryAsync(whileOn: async standIn =>
                {
                    TestAssert.Equal((false, $"Port {overridden} is in use by another program"), standIn.Line(), "another program holds the port");
                    busy.Stop();
                    var opens = 0;
                    standIn.BeforeOpen = _ => Interlocked.Increment(ref opens);
                    TestAssert.True(await standIn.RefreshAsync(CancellationToken.None), "the retry binds the port once it is free");
                    TestAssert.Equal(0, opens, "a retry reads no file: it binds with the snapshot");
                }), "the port free again: guided");
            }
            finally { busy.Stop(); }
            // While the 1.0.0.126 Amberline runs (its store's lease held) and holds the port: close it.
            var held = new TcpListener(IPAddress.Loopback, overridden);
            held.Start();
            try
            {
                using (new FileStream(Path.Combine(install, "data", "accounts.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    TestAssert.Equal((false, "Close your Amberline for 1.0.0.126"), await TryAsync(), "the 1.0.0.126 Amberline holds it");
            }
            finally { held.Stop(); }
            File.Delete(Path.Combine(install, "data", "accounts.lock"));
            TestAssert.Equal((true, Guided), await TryAsync(), "the port free again: guided");

            // The fake 1.0.0.126 install is as it was, file for file; the 132 data holds nothing of its key.
            var after = HostLoginCopyChecks.Tree(pc);
            TestAssert.Equal(string.Join(", ", before.Keys.Order(StringComparer.Ordinal)), string.Join(", ", after.Keys.Order(StringComparer.Ordinal)), "the 1.0.0.126 install's files");
            foreach (var (file, hash) in before) TestAssert.True(after[file].SequenceEqual(hash), file + " of the 1.0.0.126 install changed");
            NoKeyIn(paths.Data, key);
        }
        finally { Delete(root); }
    }

    /// <summary>
    /// The button and the stand-in in the running Platform. On 1.0.0.132: Users offers Move my 1.0.0.126 players here; its POST needs the form's
    /// token, copies the logins, then turns guiding on; a busy port, then a running 1.0.0.126, are the Status line's reasons; the stand-in answers
    /// the two paths with the 1.0.0.126 key and 404 otherwise; the router forwards its port while it runs; the Allow button's rule set has the
    /// old sign-in rule only while guiding is on, and its absence never fails the firewall check; an old tool turns it off; Stop guiding ends
    /// it. This server's own manifest stays byte for byte. On 1.0.0.126: none of it, with the same fake install and the guiding mark planted.
    /// </summary>
    [Check("server.host-move-126-players", TimeoutSeconds = 300)]
    public static void TheButtonCopiesThenGuidesOldShortcuts() => MoveAsync().GetAwaiter().GetResult();

    private static async Task MoveAsync()
    {
        var saved = new[] { "GAME_DATA_ROOT", "AIR_CLIENT_ROOT" }.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        // A new login takes its first champion from the champion list: read it the suite's way first (as server.host-copy-126-logins).
        TestAssert.True(Amberline.Platform.AirGameDtos.ChampionNames.Count > 0, "the champion list loads");
        await using var router = await HostUpnpChecks.FakeRouter.StartAsync();
        var firewall = new HostDashboardChecks.FakeFirewall();
        var home = Home.Create("league-host-move", network: new HostNetwork(
            cancel => UpnpGateway.DiscoverAsync(router.SsdpEndPoint, TimeSpan.FromSeconds(3), cancel), firewall.ReadAsync, firewall.RunAsync, () => (null, null)));
        var (name, marker) = HostLoginCopyChecks.Home126();
        // A port none of the home's own is (they were chosen and let go before this one).
        var oldPort = FreePorts(5).First(port => port != home.Port && port != home.RtmpPort && port != home.XmppPort && port != home.RemotePort);
        var pc = Path.Combine(home.Root, "pc");
        var install = Fake126(pc, name, marker, oldPort);
        using (var store = new PlatformAccounts(Path.Combine(install, "data")))
        {
            store.RegisterPublic("ann", OldPassword, "Ann There");
            store.RegisterPublic("bob", OldPassword, "Bob There", maxed: false);
        }
        var key = Path.Combine(install, "data", "remote", "host.pfx");
        var tool = Path.Combine(install, "app", "bootstrap", "companion.zip");
        var pin126 = Pin(key);
        File.WriteAllText(home.Loop.Paths.Settings, JsonSerializer.Serialize(new
        {
            httpPort = home.Port, rtmpPort = home.RtmpPort, xmppPort = home.XmppPort, remotePort = home.RemotePort, bindAddress = "127.0.0.1",
            publicHost = "127.0.0.1", addressSource = HostAddress.Typed, dataRoot = home.Game, airRoot = home.Air, serverName = "Test Server",
        }));
        home.Loop.CopyFrom = new CopyPlaces(Path.Combine(pc, "Local"), Path.Combine(pc, "C"), _ => true);
        var line132 = WorkspaceFiles.Patch == "132";
        // On 1.0.0.126 the guiding mark is planted, so only the missing wiring keeps it off.
        if (!line132) Guide(home.Loop.Paths.Data, true);
        var mainRules = HostFirewall.Rules(Environment.ProcessPath!, home.Loop.Paths.ServerExecutable, home.RemotePort, PlatformLine.FirstGamePort, 4);
        var run = Task.Run(() => HostMode.RunAsync(home.Loop));
        try
        {
            using var browser = HostDashboardChecks.Browser(home);
            await WaitForHealthAsync(browser, "ready", run);
            await home.SignInAsync(browser);
            var status = await HostDashboardChecks.WaitForStatusAsync(browser, Tested);
            TestAssert.False(HasLine(status), "no guiding line before the button");
            TestAssert.False(Listening(oldPort), "nothing on the old port before the button");
            if (!line132)
            {
                TestAssert.False((await browser.GetStringAsync(HostInvite.UsersPath)).Contains("Move my", StringComparison.Ordinal), "1.0.0.126: no button");
                // The dashboard's token (Users has no form here: no user and no offer).
                foreach (var path in new[] { HostOldShortcuts.MovePath, HostOldShortcuts.StopPath })
                    using (var refused = await PostAsync(browser, HostDashboard.PagePath, path))
                        TestAssert.Equal(HttpStatusCode.NotFound, refused.StatusCode, path + " is no route on 1.0.0.126");
                await AllowAsync(browser, firewall, mainRules);
                TestAssert.False(HasLine(await HostDashboardChecks.StatusAsync(browser)) || Listening(oldPort), "1.0.0.126: no line and no stand-in");
                return;
            }

            var users = await browser.GetStringAsync(HostInvite.UsersPath);
            TestAssert.True(users.Contains("<button type=submit class=secondary>Move my 1.0.0.126 players here</button>", StringComparison.Ordinal) &&
                users.Contains($"<form method=post action={HostOldShortcuts.MovePath}", StringComparison.Ordinal), "Users offers the button: " + users);
            using (var forged = await browser.PostAsync(HostOldShortcuts.MovePath, new FormUrlEncodedContent(new Dictionary<string, string>())))
                TestAssert.Equal(HttpStatusCode.BadRequest, forged.StatusCode, "the button needs the form's antiforgery token");
            TestAssert.False(File.Exists(HostFriendLight.StateFile(home.Loop.Paths.Data)) &&
                File.ReadAllText(HostFriendLight.StateFile(home.Loop.Paths.Data)).Contains(HostOldShortcuts.StateKey, StringComparison.Ordinal), "a forged press turns nothing on");

            // The press while another program holds the old port: the logins are copied and guiding is on; the line says why it does not run.
            var busy = new TcpListener(IPAddress.Loopback, oldPort);
            busy.Start();
            try
            {
                using (var moved = await PostAsync(browser, HostInvite.UsersPath, HostOldShortcuts.MovePath))
                {
                    var text = await moved.Content.ReadAsStringAsync();
                    TestAssert.True(moved.StatusCode == HttpStatusCode.OK && text.Contains("2 logins copied from your Amberline for 1.0.0.126.", StringComparison.Ordinal) &&
                        text.Contains("Old 1.0.0.126 shortcuts are guided to this server from now on.", StringComparison.Ordinal), $"the copy, then guiding ({moved.StatusCode}): {text}");
                }
                TestAssert.True(File.ReadAllText(HostFriendLight.StateFile(home.Loop.Paths.Data)).Contains($"\"{HostOldShortcuts.StateKey}\": true", StringComparison.Ordinal),
                    "guiding on is saved in this server's data");
                TestAssert.Equal(("waiting", $"Port {oldPort} is in use by another program"), Line(await HostDashboardChecks.StatusAsync(browser)), "a busy port");
                using (new FileStream(Path.Combine(install, "data", "accounts.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    await TestAgainAsync(browser);
                    TestAssert.Equal(("waiting", "Close your Amberline for 1.0.0.126"), Line(await HostDashboardChecks.StatusAsync(browser)), "the 1.0.0.126 Amberline runs");
                }
            }
            finally { busy.Stop(); }

            // Free: the stand-in runs. The firewall has no rule yet, so the line asks for the Allow button, which Users then offers.
            await TestAgainAsync(browser);
            status = await HostDashboardChecks.StatusAsync(browser);
            TestAssert.Equal(("waiting", Allow), Line(status), "running, its rule missing");
            TestAssert.True(Listening(oldPort), "the stand-in listens on the old port");
            users = await browser.GetStringAsync(HostInvite.UsersPath);
            TestAssert.True(users.Contains($"<p class=warn role=status>{Allow}</p>", StringComparison.Ordinal) &&
                users.Contains($"action={HostDashboard.PagePath}/firewall", StringComparison.Ordinal) &&
                users.Contains("Stop guiding old shortcuts</button>", StringComparison.Ordinal), "Users shows the line, Allow and Stop: " + users);
            await WaitForAsync(() => router.Table().ContainsKey(("TCP", oldPort)), "the router forwards the old port while the stand-in runs");

            // The two paths, with the 1.0.0.126 key; the request's own host name; 404 for everything else, and for a Host that is no name.
            var (manifest, seen) = await GetAsync(oldPort, "/bootstrap/client.json");
            TestAssert.Equal((HttpStatusCode.OK, pin126), (manifest.Status, seen), "the manifest, proved with the 1.0.0.126 key");
            var pin132 = Pin(Path.Combine(home.Loop.Paths.Data, "remote", "host.pfx"));
            TestAssert.False(pin126 == pin132, "two keys");
            TestAssert.Equal($"{{\"protocol\":1,\"host\":\"127.0.0.1\",\"port\":{oldPort},\"certificateSha256\":\"{pin126}\",\"companionZip\":\"/bootstrap/companion.zip\"," +
                $"\"companionSha256\":\"{Sha(File.ReadAllBytes(tool))}\",\"companionVersion\":\"{Sha(File.ReadAllBytes(tool))}\"," +
                $"\"movedTo\":{{\"port\":{home.RemotePort},\"certificateSha256\":\"{pin132}\",\"name\":\"Test Server\",\"playerFolder\":\"Amberline 132 Player\"}}}}",
                manifest.Text, "the stand-in's manifest (interface 1)");
            var (archive, _) = await GetAsync(oldPort, "/bootstrap/companion.zip");
            TestAssert.True(archive.Status == HttpStatusCode.OK && archive.Bytes.SequenceEqual(File.ReadAllBytes(tool)), "the 1.0.0.126 tool, as it is");
            TestAssert.Equal(HttpStatusCode.OK, (await GetAsync(oldPort, "/bootstrap/client.json", "localhost")).Answer.Status, "a name as Host");
            TestAssert.True((await GetAsync(oldPort, "/bootstrap/client.json", "localhost")).Answer.Text.Contains("\"host\":\"localhost\"", StringComparison.Ordinal), "the name it was reached by");
            TestAssert.Equal(HttpStatusCode.NotFound, (await GetAsync(oldPort, "/bootstrap/client.json", "[::1]")).Answer.Status, "a Host that is no DNS name or IPv4: no manifest");
            foreach (var path in new[] { "/", "/bootstrap/Play.cmd", "/air/rtmp", "/air/xmpp", "/register", "/host", "/health", "/bootstrap/client.json/x", "/BOOTSTRAP/client.json" })
                TestAssert.Equal(HttpStatusCode.NotFound, (await GetAsync(oldPort, path)).Answer.Status, path + " on the stand-in");
            TestAssert.Equal(HttpStatusCode.NotFound, (await GetAsync(oldPort, "/bootstrap/client.json", method: HttpMethod.Post)).Answer.Status, "POST on the stand-in");

            // This server's own manifest is as before, byte for byte (no movedTo).
            using (var own = await browser.GetAsync($"https://127.0.0.1:{home.RemotePort}/bootstrap/client.json"))
            {
                var ownZip = Sha(File.ReadAllBytes(Path.Combine(home.Loop.Paths.Bootstrap, "companion.zip")));
                TestAssert.Equal($"{{\"protocol\":1,\"host\":\"127.0.0.1\",\"port\":{home.RemotePort},\"certificateSha256\":\"{pin132}\",\"companionZip\":\"/bootstrap/companion.zip\"," +
                    $"\"companionSha256\":\"{ownZip}\",\"companionVersion\":\"{ownZip}\",\"serverName\":\"Test Server\"}}", await own.Content.ReadAsStringAsync(), "this server's manifest");
            }

            // Allow: one prompt for the three rules; then the line says the shortcuts are guided.
            var oldRule = HostFirewall.OldSignIn(Environment.ProcessPath!, oldPort);
            await AllowAsync(browser, firewall, [.. mainRules, oldRule]);
            status = await HostDashboardChecks.StatusAsync(browser);
            if (HostFirewall.Refusal(mainRules) is null)
            {
                TestAssert.Equal(("ok", "Windows Firewall lets players in"), HostDashboardChecks.Check(status, "firewall"));
                TestAssert.Equal(("ok", Guided), Line(status), "guided");
                // The old sign-in rule gone: the firewall check stays green, only the line asks again.
                firewall.Allowed = mainRules;
                await TestAgainAsync(browser);
                status = await HostDashboardChecks.StatusAsync(browser);
                TestAssert.Equal(("ok", "Windows Firewall lets players in"), HostDashboardChecks.Check(status, "firewall"));
                TestAssert.Equal(("waiting", Allow), Line(status), "the third rule missing changes only the line");
                TestAssert.Equal("Network: all checks passed", status.GetProperty("network").GetString());
                TestAssert.Equal(0, status.GetProperty("attention").GetArrayLength(), "no card for the third rule");
                firewall.Allowed = [.. mainRules, oldRule];
            }

            // Review, 17 November 2026: the files are read at the start and on the press only. An old tool (no capability marker) put on disk
            // changes nothing at Test again; the next press reads it: the update line, nothing on the port, and the router lets the port go.
            // The new tool back on disk counts from the next press too.
            var capable = File.ReadAllBytes(tool);
            File.WriteAllBytes(tool, Zip(("Amberline.AirCompanion.exe", "tool")));
            await TestAgainAsync(browser);
            TestAssert.True(Listening(oldPort) && Line(await HostDashboardChecks.StatusAsync(browser)).Text != Update, "Test again reads no file");
            using (var pressed = await PostAsync(browser, HostInvite.UsersPath, HostOldShortcuts.MovePath))
                TestAssert.Equal(HttpStatusCode.OK, pressed.StatusCode, "the press again");
            TestAssert.Equal(("waiting", Update), Line(await HostDashboardChecks.StatusAsync(browser)), "an old 1.0.0.126 tool, read on the press");
            TestAssert.False(Listening(oldPort), "nothing on the old port for an old tool");
            await WaitForAsync(() => !router.Table().ContainsKey(("TCP", oldPort)), "the router lets the old port go");
            File.WriteAllBytes(tool, capable);
            await TestAgainAsync(browser);
            TestAssert.Equal(("waiting", Update), Line(await HostDashboardChecks.StatusAsync(browser)), "the new tool on disk waits for the press");
            using (var pressed = await PostAsync(browser, HostInvite.UsersPath, HostOldShortcuts.MovePath))
                TestAssert.Equal(HttpStatusCode.OK, pressed.StatusCode, "the press once more");
            TestAssert.True(Listening(oldPort), "back once the tool is new and the host pressed");

            // Stop guiding: the stand-in ends, the line goes, the port is let go, and the rule set is the two again.
            using (var stop = await PostAsync(browser, HostInvite.UsersPath, HostOldShortcuts.StopPath))
                TestAssert.True(stop.StatusCode == HttpStatusCode.Redirect && stop.Headers.Location?.OriginalString == HostInvite.UsersPath, $"Stop goes back to Users ({stop.StatusCode})");
            TestAssert.False(Listening(oldPort), "Stop ends the stand-in");
            await WaitForAsync(() => !router.Table().ContainsKey(("TCP", oldPort)), "the router lets the old port go after Stop");
            TestAssert.False(HasLine(await HostDashboardChecks.StatusAsync(browser)), "no line after Stop");
            TestAssert.False(File.ReadAllText(HostFriendLight.StateFile(home.Loop.Paths.Data)).Contains(HostOldShortcuts.StateKey, StringComparison.Ordinal), "guiding off is saved");
            firewall.Allowed = [];
            await AllowAsync(browser, firewall, mainRules);
        }
        finally
        {
            await home.StopAsync(run);
            foreach (var pair in saved) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
        TestAssert.False(Listening(oldPort), "nothing on the old port after the Platform stopped");
        if (line132)
        {
            // A start with guiding saved on (every start after the press): the stand-in runs by itself and the router forwards its port.
            Guide(home.Loop.Paths.Data, true);
            var next = home.Again();
            next.Loop.CopyFrom = new CopyPlaces(Path.Combine(pc, "Local"), Path.Combine(pc, "C"), _ => true);
            var again = Task.Run(() => HostMode.RunAsync(next.Loop));
            try
            {
                using var browser = HostDashboardChecks.Browser(next);
                await WaitForHealthAsync(browser, "ready", again);
                await WaitForAsync(() => Listening(oldPort), "a start with guiding on runs the stand-in");
                await WaitForAsync(() => router.Table().ContainsKey(("TCP", oldPort)), "and the router forwards its port");
            }
            finally { await next.StopAsync(again); }
            TestAssert.False(Listening(oldPort) || router.Table().ContainsKey(("TCP", oldPort)), "the stop ends the stand-in and lets the port go");
            using var store = new PlatformAccounts(home.Loop.Paths.Data);
            TestAssert.Equal("ann, bob", string.Join(", ", store.ListAccounts().Select(account => account.Username).Order(StringComparer.Ordinal)), "the copied logins");
        }
        NoKeyIn(home.Loop.Paths.Data, key);
        Delete(home.Root);
    }

    /// <summary>Both the firewall line and the router line finished their test.</summary>
    private static bool Tested(JsonElement status) =>
        HostDashboardChecks.Check(status, "firewall").State != "checking" && HostDashboardChecks.Check(status, "router").State != "checking";

    private static bool HasLine(JsonElement status) => status.GetProperty("checks").EnumerateArray().Any(check => check.GetProperty("id").GetString() == "guiding");

    private static (string State, string Text) Line(JsonElement status) => HostDashboardChecks.Check(status, "guiding");

    /// <summary>Test again (the dashboard's form): the stand-in tries first, then the firewall and the router are asked.</summary>
    private static async Task TestAgainAsync(HttpClient browser)
    {
        using var again = await PostAsync(browser, HostDashboard.PagePath, HostDashboard.PagePath + "/network");
        TestAssert.Equal(HttpStatusCode.Redirect, again.StatusCode, "Test again");
    }

    /// <summary>Allow through Windows Firewall from a fresh state: one elevated command, for exactly <paramref name="rules"/>.</summary>
    private static async Task AllowAsync(HttpClient browser, HostDashboardChecks.FakeFirewall firewall, IReadOnlyList<FirewallRule> rules)
    {
        lock (firewall.Commands) firewall.Commands.Clear();
        firewall.Next = rules;
        using (var allow = await PostAsync(browser, HostDashboard.PagePath, HostDashboard.PagePath + "/firewall"))
            TestAssert.Equal(HttpStatusCode.Redirect, allow.StatusCode, "Allow");
        if (HostFirewall.Refusal(rules) is not null) return;
        TestAssert.Equal(1, firewall.Commands.Count, "one Windows prompt");
        TestAssert.Equal(HostFirewall.Command(rules, [], Environment.SystemDirectory), firewall.Commands[0], "the command for exactly these rules");
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string from, string path)
    {
        var token = Token(await client.GetStringAsync(from));
        return await client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string> { [token.Key] = token.Value }));
    }

    private static async Task WaitForAsync(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(100);
        TestAssert.True(done(), what);
    }

    private sealed record Answer(HttpStatusCode Status, string Text, byte[] Bytes);

    /// <summary>One request to the stand-in on loopback over HTTPS, as a friend's tool would make it; the pin of the certificate it showed.</summary>
    private static async Task<(Answer Answer, string? Pin)> GetAsync(int port, string path, string? host = null, HttpMethod? method = null)
    {
        string? pin = null;
        using var client = new HttpClient(new HttpClientHandler
        {
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                pin = certificate is null ? null : Convert.ToHexString(SHA256.HashData(certificate.RawData));
                return true;
            },
        }) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, $"https://127.0.0.1:{port}{path}");
        if (host is not null) request.Headers.Host = host;
        using var response = await client.SendAsync(request);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return (new(response.StatusCode, System.Text.Encoding.UTF8.GetString(bytes), bytes), pin);
    }

    /// <summary>
    /// The fake 1.0.0.126 install under <paramref name="pc"/>: <c>C\&lt;name&gt;</c> with its marker, which <c>Local\&lt;name&gt;\install.json</c>
    /// names; its key made as that server makes it; an archive with the capability marker; a <c>platform.json</c> naming <paramref name="port"/>.
    /// </summary>
    private static string Fake126(string pc, string name, string marker, int port)
    {
        var install = Path.Combine(pc, "C", name);
        Directory.CreateDirectory(Path.Combine(install, "app", "bootstrap"));
        File.WriteAllText(Path.Combine(install, marker), "{}");
        using (HostFiles.Certificate(Path.Combine(install, "data", "remote"))) { }
        File.WriteAllBytes(Path.Combine(install, "app", "bootstrap", "companion.zip"),
            Zip(("capabilities.json", "{\"movedTo\":1}"), ("Amberline.AirCompanion.exe", "tool")));
        File.WriteAllText(Path.Combine(install, "data", "platform.json"), JsonSerializer.Serialize(new { httpPort = 1, remotePort = port, serverName = "Old Server" }));
        Directory.CreateDirectory(Path.Combine(pc, "Local", name));
        File.WriteAllText(Path.Combine(pc, "Local", name, "install.json"), JsonSerializer.Serialize(new { folder = install }));
        return install;
    }

    /// <summary>Guiding on or off in a 1.0.0.132 data folder's <c>host\state.json</c>, as the button saves it.</summary>
    private static void Guide(string data, bool on)
    {
        var file = HostFriendLight.StateFile(data);
        var state = (File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null) ?? new JsonObject();
        if (on) state[HostOldShortcuts.StateKey] = true;
        else state.Remove(HostOldShortcuts.StateKey);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, state.ToJsonString());
    }

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in entries)
                using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write(text);
        return memory.ToArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string Pin(string pfx)
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(pfx, null);
        return certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }

    /// <summary>No file of the 1.0.0.132 data folder holds the 1.0.0.126 key file's bytes or its certificate (compared, never printed).</summary>
    private static void NoKeyIn(string data, string key)
    {
        var pfx = File.ReadAllBytes(key);
        using var certificate = X509CertificateLoader.LoadPkcs12(pfx, null);
        // The store's lease (accounts.lock, held with no sharing while its store is open) holds nothing and cannot be read.
        foreach (var file in Directory.GetFiles(data, "*", SearchOption.AllDirectories).Where(file => Path.GetFileName(file) != "accounts.lock"))
        {
            var bytes = File.ReadAllBytes(file);
            TestAssert.True(bytes.AsSpan().IndexOf(pfx) < 0 && bytes.AsSpan().IndexOf(certificate.RawData) < 0, "the 1.0.0.126 key is in " + Path.GetFileName(file));
        }
    }

    /// <summary>Whether something accepts a connection on loopback at <paramref name="port"/>.</summary>
    private static bool Listening(int port)
    {
        using var client = new TcpClient();
        try { return client.ConnectAsync(IPAddress.Loopback, port).Wait(TimeSpan.FromSeconds(3)) && client.Connected; }
        catch (AggregateException ex) when (ex.InnerException is SocketException) { return false; }
    }

    /// <summary>Free loopback ports, distinct (each held until all are chosen); never the real 8391 or 8491.</summary>
    private static int[] FreePorts(int count)
    {
        var listeners = Enumerable.Range(0, count).Select(_ => new TcpListener(IPAddress.Loopback, 0)).ToArray();
        foreach (var listener in listeners) listener.Start();
        var ports = listeners.Select(listener => ((IPEndPoint)listener.LocalEndpoint).Port).ToArray();
        foreach (var listener in listeners) listener.Stop();
        TestAssert.False(ports.Any(port => port is 8391 or 8491), "never the real ports");
        return ports;
    }

    /// <summary>A hard link (no privilege needed) at <paramref name="link"/> to <paramref name="target"/>.</summary>
    private static void HardLink(string link, string target)
    {
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/H", link, target }, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        mklink.StandardOutput.ReadToEnd();
        mklink.WaitForExit();
        TestAssert.True(mklink.ExitCode == 0 && File.Exists(link), "the hard link was made: " + mklink.StandardError.ReadToEnd());
    }
}
