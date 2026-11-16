using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Amberline.AirCompanion.Rads;

namespace Amberline.AirCompanion;

/// <summary>
/// NOT RETAIL: retail's update service built this layout under the RADS root and kept it current; it no longer exists, so the classic start
/// builds the same layout from the player's own launcher, AIR and game files (design 2026-11-12 §3 and §6). The generated files (English base
/// translations for the other languages, region overrides pointing the news at
/// our page, the solution manifests, a release manifest when the player has none) exist only here.
/// Add-only: every path created is recorded in <see cref="StampName"/> (relative to the root, one per line, junctions marked "J ") before it is
/// created. Ours means the path itself is in the stamp: only those are refreshed or removed. A needed path that exists and is not ours, or a
/// release folder of one of <see cref="OwnProjects"/> that is not ours (a real old install: the stand-in would report its higher version), refuses
/// the start by name before anything is written. The system cfgs are created once and then left to the launcher, which rewrites them when the
/// player changes region.
/// </summary>
internal static partial class ClassicLauncherLayout
{
    /// <summary>This line's stamp, under <see cref="LauncherProject"/>. Every stamp written before batch 814 has this name, so it is this line's;
    /// the 1.0.0.132 line writes <see cref="OtherStampName"/> beside it, and a classic start removes the other line's layout first.</summary>
    internal const string StampName = "classic.stamp";
    private const string OtherStampName = "classic132.stamp";

    /// <summary>The release folders the stand-in answers (<see cref="RadsKernelStandIn"/>): null, every one under the root, as before batch 814.
    /// After the takeover nothing of the other line is left there, and <c>projects\lol_game_client</c> is this line's own managed game layout.</summary>
    internal static readonly IReadOnlyCollection<string>? AnsweredReleases = null;

    private const string LauncherProject = @"projects\lol_launcher", LauncherVersion = "0.0.0.49";
    private const string Solution = @"solutions\lol_game_client_sln\releases\0.0.0.81";

    /// <summary>The junctions to the AIR and game folders, relative to the root: the launcher starts AIR and the game through them.</summary>
    private const string AirDeploy = @"projects\lol_air_client\releases\0.0.0.126\deploy", GameDeploy = Solution + @"\deploy";

    /// <summary>The launcher, AIR and the game as a classic start runs them under <paramref name="root"/>: the launcher's copy in its release
    /// folder, and AIR and the game through the layout's junctions (design 2026-11-12 §3 defines them; the launcher starts both by those paths).</summary>
    internal static (string Launcher, string Air, string Game) Images(string root) => (
        Path.Combine(root, LauncherProject, "releases", LauncherVersion, "deploy", "LoLLauncher.exe"),
        Path.Combine(root, AirDeploy, "LolClient.exe"),
        Path.Combine(root, GameDeploy, "League of Legends.exe"));

    /// <summary>The projects and solution that hold nothing but this layout: a stamped folder at or inside one of them is removed whole. Every
    /// other stamped folder (projects, solutions, system; projects\lol_game_client, the game's managed layout, is never stamped) is shared and
    /// only ever removed when empty. The same list names the release folders an old install would clash with.</summary>
    private static readonly string[] OwnProjects =
    [
        LauncherProject, @"projects\lol_air_client", @"projects\lol_air_client_config_na", @"projects\lol_air_client_config_euw",
        @"projects\lol_air_client_config_eune", @"solutions\lol_game_client_sln",
    ];

    /// <summary>The seven region overrides the launcher loads after the base translation (design 2026-11-12 §6), each only the news lines.</summary>
    internal static IReadOnlyDictionary<string, string> RegionOverrides(int newsPort)
    {
        var news = string.Create(CultureInfo.InvariantCulture, $"tr \"url_host_news\" = \"127.0.0.1:{newsPort}\"\r\ntr \"url_path_news\" = \"/launcher/news\"\r\n");
        return new[] { "NA_en_US", "EUW_en_gb", "EUW_de_de", "EUW_es_es", "EUW_fr_fr", "EUNE_en_gb", "EUNE_pl_pl" }.ToDictionary(name => name + ".tra", _ => news);
    }

    /// <summary>Every path the layout needs, with its content, in creation order (relative to the root).</summary>
    private static List<Item> Plan(ClassicLayoutRequest request)
    {
        var launcher = Path.GetFullPath(request.LauncherFolder);
        var air = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.AirFolder));
        var game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.GameFolder));
        var release = $@"{LauncherProject}\releases\{LauncherVersion}";
        var deploy = $@"{release}\deploy";

        // No eula_* file anywhere: an absent EULA means no EULA screen (design 2026-11-12 §6).
        var files = ClassicLauncherFiles.Required.Where(file => !file.StartsWith("eula_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(file => file, file => File.ReadAllBytes(Path.Combine(launcher, file)), StringComparer.OrdinalIgnoreCase);
        var version = RadsLayout.ParseVersion(LauncherVersion);
        byte[] manifest;
        IReadOnlyList<RadsLayout.ManifestFile> entries;
        if (ClassicLauncherFiles.ManifestFor(launcher) is { } playerManifest)
        {
            manifest = File.ReadAllBytes(playerManifest);
            entries = RadsLayout.ReadManifest(playerManifest).Files;
        }
        else
        {
            // Mode 0, no managed flag: the launcher then opens each file from its working folder, which holds all of them.
            // Not proven: owner test (design 2026-11-12 §3).
            entries = [.. files.Select(file => new RadsLayout.ManifestFile(file.Key.Replace('\\', '/'), version, MD5.HashData(file.Value), 0, (uint)file.Value.Length, (uint)file.Value.Length))];
            var temp = Path.GetTempFileName();
            try { RadsLayout.WriteManifest(temp, "lol_launcher", version, entries); manifest = File.ReadAllBytes(temp); }
            finally { File.Delete(temp); }
        }

        static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
        // The RADS text reader strips one trailing CR or LF, so the two manifests use LF only (CRLF would leave "\r" in a binary-mode read).
        static byte[] Lines(IEnumerable<string> lines) => Ascii(string.Join("\n", lines) + "\n");
        var gameVersion = ManagedLayout.ParseGameDirectory(game) is { Project: "lol_game_client" } gameRelease ? gameRelease.Version : "0.0.0.51";
        string[] configurations = ["en_US", "en_GB", "de_DE", "es_ES", "fr_FR", "pl_PL"];
        var properties = File.ReadAllBytes(Path.Combine(air, "lol.properties"));

        List<Item> items =
        [
            new(@"system\launcher.cfg", Ascii("airConfigProject = lol_air_client_config_na\r\n"), Once: true),
            new(@"system\locale.cfg", Ascii("locale = en_us\r\n"), Once: true),
            new(@"system\system.cfg", Ascii("Region = NA\r\n"), Once: true),
            new(@"system\user.cfg", Ascii("disableP2P = true\r\n"), Once: true),   // the launcher crashes without it
            new(deploy),   // ours whole: the launcher writes its logs and temp\ in it
            new($@"{release}\releasemanifest", manifest),
            .. files.Select(file => new Item($@"{deploy}\{file.Key}", file.Value)),
            // English launcher text for the other languages (the player's launcher has only en_US and ru_RU).
            .. new[] { "en_gb", "de_de", "es_es", "fr_fr", "pl_pl" }.Select(locale => new Item($@"{deploy}\{locale}.tra", files["en_US.tra"])),
            .. RegionOverrides(request.NewsPort).Select(file => new Item($@"{deploy}\{file.Key}", Ascii(file.Value))),
            .. entries.Where(entry => files.ContainsKey(entry.Path.Replace('/', '\\'))).Select(entry => new Item(
                $@"{LauncherProject}\managedfiles\{VersionText(entry.Version)}\{entry.Path.Replace('/', '\\')}", files[entry.Path.Replace('/', '\\')])),
            new(AirDeploy, LinkTo: air),
            // Every region's config project: the launcher copies its lol.properties over AIR's at each update.
            .. new[] { "na", "euw", "eune" }.Select(region => new Item($@"projects\lol_air_client_config_{region}\releases\0.0.0.1\deploy\lol.properties", properties)),
            new(GameDeploy, LinkTo: game),
            new($@"{Solution}\solutionmanifest", Lines([
                "RADS Solution Manifest", "1.0.0.0", "lol_game_client_sln", "0.0.0.81",
                "1", "lol_game_client", gameVersion, "0", "0",                               // projects: name, version, priority, flags
                configurations.Length.ToString(CultureInfo.InvariantCulture),
                .. configurations.SelectMany(name => new[] { name, "0", "1", "lol_game_client" })])),   // configurations: name, flags, project count, names
            new($@"{Solution}\configurationmanifest", Lines(["RADS Configuration Manifest", "1.0.0.0", "en_US", "1", "lol_game_client"])),
        ];
        return items;
    }
}
