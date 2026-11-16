using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Amberline.AirCompanion.Rads;

namespace Amberline.AirCompanion;

/// <summary>
/// NOT RETAIL: retail's update service built this layout under the RADS root and kept it current; it no longer exists, so the classic start
/// builds the same layout from the player's own launcher, AIR and game files (design 2026-11-12 §3 and §6). The generated files (the English base
/// and region translations, the region news lines pointing at our page; a release manifest when the player has none) exist only here.
/// Add-only: every path created is recorded in <see cref="StampName"/> (relative to the root, one per line, junctions marked "J ") before it is
/// created. Ours means the path itself is in the stamp: only those are refreshed or removed. A needed path that exists and is not ours, or a
/// release folder of one of <see cref="OwnProjects"/> that is not ours (a real old install: the stand-in would report its higher version), refuses
/// the start by name before anything is written. The system cfgs are created once and then left to the launcher, which rewrites them when the
/// player changes region.
/// </summary>
internal static partial class ClassicLauncherLayout
{
    /// <summary>This line's stamp, under <see cref="LauncherProject"/> (batch 814). The 1.0.0.126 line's is <see cref="OtherStampName"/>, the name
    /// every stamp written before batch 814 has: a classic start removes the other line's layout first. A separate name keeps a 1.0.0.126 build
    /// from before batch 814 off this layout: it reads only its own stamp, finds this line's system cfgs unstamped and refuses before writing.</summary>
    internal const string StampName = "classic132.stamp";
    private const string OtherStampName = "classic.stamp";

    /// <summary>The release folders the stand-in answers (<see cref="RadsKernelStandIn"/>): this layout's own. In the shared root the launcher
    /// would otherwise also be told of <c>projects\lol_game_client</c> (the 1.0.0.126 line's managed game layout, which stays) and of any of that
    /// line's releases a takeover could not see (the stand-in answers the highest folder; batch 811 report, section 3).</summary>
    internal static readonly IReadOnlyCollection<string>? AnsweredReleases =
    [
        $@"{LauncherProject}\releases\{LauncherVersion}", AirRelease, Solution,
        .. new[] { "na", "euw", "eune" }.Select(region => $@"projects\lol_air_client_config_{region}\releases\{ConfigVersion}"),
    ];

    private const string LauncherProject = @"projects\lol_launcher", LauncherVersion = "0.0.0.76";
    // Reconstruction: no 132 solution release is in the shipped files. 0.0.0.70 is the third-party number of the 131-to-132 patch (unverified).
    private const string SolutionVersion = "0.0.0.70", Solution = @"solutions\lol_game_client_sln\releases\" + SolutionVersion;
    // Reconstruction (batch 814): retail's lol_air_client and AIR config releases of 1.0.0.132 are not in the shipped files. 0.0.0.132 names this
    // line, as 0.0.0.126 names the 1.0.0.126 line's AIR release, so the two lines' AIR copies and ports never share a release folder.
    private const string AirRelease = @"projects\lol_air_client\releases\0.0.0.132", ConfigVersion = "0.0.0.132";

    /// <summary>The junctions to the AIR and game folders, relative to the root: the launcher starts AIR and the game through them.</summary>
    private const string AirDeploy = AirRelease + @"\deploy", GameDeploy = Solution + @"\deploy";

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

    /// <summary>
    /// The region translation with its news host and path pointed at the local news page (<c>/launcher/news</c>), where the launcher reads its news.
    /// Every other line stays as shipped, the region's own links and status addresses included. The bytes map one to one through Latin-1, so a BOM
    /// and the CRLF endings come back unchanged.
    /// </summary>
    internal static byte[] WithNews(byte[] translation, int newsPort)
    {
        var text = Encoding.Latin1.GetString(translation);
        text = Regex.Replace(text, "tr \"url_host_news\" = \"[^\"\r\n]*\"", string.Create(CultureInfo.InvariantCulture, $"tr \"url_host_news\" = \"127.0.0.1:{newsPort}\""));
        text = Regex.Replace(text, "tr \"url_path_news\" = \"[^\"\r\n]*\"", "tr \"url_path_news\" = \"/launcher/news\"");
        return Encoding.Latin1.GetBytes(text);
    }

    /// <summary>The English translation of a region: the file its region code names (na_region.tra, euw_region.tra or eune_region.tra).</summary>
    private static string EnglishRegion(string region) => region.Split('_')[0] + "_region.tra";

    /// <summary>Every path the layout needs, with its content, in creation order (relative to the root).</summary>
    private static List<Item> Plan(ClassicLayoutRequest request)
    {
        var launcher = Path.GetFullPath(request.LauncherFolder);
        var air = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.AirFolder));
        var game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.GameFolder));
        var release = $@"{LauncherProject}\releases\{LauncherVersion}";
        var deploy = $@"{release}\deploy";

        // No EULA file is required or copied: an absent EULA means no EULA screen (design 2026-11-12 §6).
        var files = ClassicLauncherFiles.Required.ToDictionary(file => file, file => File.ReadAllBytes(Path.Combine(launcher, file)),
            StringComparer.OrdinalIgnoreCase);
        // NOT RETAIL: retail's 1.0.0.132 launcher shows the language the player picks, from its own translations. This layout keeps the launcher English
        // whatever is picked (owner, 14 November 2026: "Keep it English", as on the 126 line; batch 777): every base translation is en_US.tra's text, and
        // every region translation is its region's English file's text, with its news lines pointed at the local news page (design 2026-11-12 §6).
        // ponytail: the base copies keep en_US's own news lines; the region file, read after the base, overrides them. If the launcher ever reads the
        // region first, apply WithNews to the base copies too.
        foreach (var file in ClassicLauncherFiles.BaseFiles) files[file] = files["en_US.tra"];
        var regionText = ClassicLauncherFiles.RegionFiles.ToDictionary(region => region, region => files[EnglishRegion(region)]);
        foreach (var (region, text) in regionText) files[region] = WithNews(text, request.NewsPort);
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
            // Not proven: owner test (design 2026-11-12 §3). 0.0.0.76 ships no releasemanifest, so this path is untested against that launcher.
            entries = [.. files.Select(file => new RadsLayout.ManifestFile(file.Key.Replace('\\', '/'), version, MD5.HashData(file.Value), 0, (uint)file.Value.Length, (uint)file.Value.Length))];
            var temp = Path.GetTempFileName();
            try { RadsLayout.WriteManifest(temp, "lol_launcher", version, entries); manifest = File.ReadAllBytes(temp); }
            finally { File.Delete(temp); }
        }

        static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
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
            .. entries.Where(entry => files.ContainsKey(entry.Path.Replace('/', '\\'))).Select(entry => new Item(
                $@"{LauncherProject}\managedfiles\{VersionText(entry.Version)}\{entry.Path.Replace('/', '\\')}", files[entry.Path.Replace('/', '\\')])),
            new(AirDeploy, LinkTo: air),
            // Every region's config project: the launcher copies its lol.properties (this line's AIR copy's, with its ports) over AIR's at each update.
            .. new[] { "na", "euw", "eune" }.Select(region => new Item($@"projects\lol_air_client_config_{region}\releases\{ConfigVersion}\deploy\lol.properties", properties)),
            // No solution or configuration manifest (batch 814; batch 811 report, section 2): one naming lol_game_client 0.0.0.51 would make the 132
            // game's rads.dll load <root>\projects\lol_game_client\releases\0.0.0.51, the 1.0.0.126 line's archives in the shared root. Without one
            // it finds no solution manifest and runs loose from its own folder, as the usual start runs it.
            new(GameDeploy, LinkTo: game),
        ];
        return items;
    }
}
