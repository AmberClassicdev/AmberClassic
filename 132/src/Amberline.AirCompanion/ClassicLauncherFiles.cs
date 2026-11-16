using System.Security.Cryptography;

// Linked as source into the Platform as well (the host's optional launcher folder); BCL types only, no other companion type.
#if AMBERLINE_HOST_FOLDERS
namespace Amberline.Platform.Folders;
#else
namespace Amberline.AirCompanion;
#endif

/// <summary>
/// NOT RETAIL: retail's update service installed and verified the 2011 launcher itself; the classic start instead accepts the player's own copy
/// of launcher 1.0.1.19 (release 0.0.0.76, the 1.0.0.132 line's classic launcher; the 1.0.0.126 line keeps 1.0.0.251 on its own branch), checked
/// by its EXE's fingerprint and the release's file list, in the flat folder shape of that release.
/// </summary>
internal static class ClassicLauncherFiles
{
    /// <summary>SHA-256 of the 1.0.1.19 <c>LoLLauncher.exe</c> (release 0.0.0.76).</summary>
    internal const string LauncherSha256 = "8d8ef1689faf225c7e1a0a8478dbca80778313fe92a48a852793683b16bd22da";

    /// <summary>
    /// The base translation of each locale the launcher offers. The layout writes each one as en_US.tra's text (NOT RETAIL, see ClassicLauncherLayout),
    /// so the launcher shows English whatever locale the player picks.
    /// </summary>
    internal static IReadOnlyList<string> BaseFiles { get; } =
        ["en_US.tra", "de_DE.tra", "el_GR.tra", "en_GB.tra", "es_ES.tra", "fr_FR.tra", "pl_PL.tra", "ro_RO.tra"];

    /// <summary>
    /// The nine region translations, one per region and locale pair of the launcher's regions.txt (NA en_us; EUW en_gb, de_de, es_es, fr_fr; EUNE en_gb,
    /// pl_pl, ro_ro, el_gr). The launcher reads a pair's file after the base translation; the layout points its news lines at the local news.
    /// </summary>
    internal static IReadOnlyList<string> RegionFiles { get; } =
    [
        "na_region.tra", "euw_region.tra", "euw_region_de_DE.tra", "euw_region_es_ES.tra", "euw_region_fr_FR.tra",
        "eune_region.tra", "eune_region_el_GR.tra", "eune_region_pl_PL.tra", "eune_region_ro_RO.tra",
    ];

    /// <summary>
    /// The release's 34 files the classic start needs, relative to the launcher folder (release 0.0.0.76): the launcher's own 15 (its EXE and version
    /// file, the Maestro DLL, the three SWFs, the safety check, the English base translation, the slideshow's xml and six images), the base translations
    /// of the other locales the launcher offers, the region translations and the three lists it reads at start. Not required, so not copied: the EULA and
    /// NDA files (an absent EULA means no EULA screen) and the unknown_region translations (only for a region code regions.txt does not list).
    /// </summary>
    internal static IReadOnlyList<string> Required { get; } =
    [
        "LoLLauncher.exe", "LoLLauncher.exe.version", "launcher.maestro.dll", "LoLLauncher.swf", "RADS.swf", "slideshow.swf", "Riot_SafetyCheck.exe",
        .. BaseFiles, .. RegionFiles, "regions.txt", "shards.txt", "languages.txt",
        @"xml\slideshow.xml", @"img\ad0.png", @"img\ad1.png", @"img\ad2.png", @"img\ad3.png", @"img\ad4.png", @"img\ad5.png",
    ];

    /// <summary>The release files whose names name the game or its publisher, by their role: the sentence below is player-facing text (owner, 3 Nov).</summary>
    private static readonly Dictionary<string, string> Neutral = new()
    {
        ["LoLLauncher.exe"] = "the launcher's program file",
        ["LoLLauncher.exe.version"] = "the launcher's version file",
        ["LoLLauncher.swf"] = "the launcher's main screen file",
        ["Riot_SafetyCheck.exe"] = "the launcher's safety-check program",
    };

    /// <summary>
    /// Why <paramref name="folder"/> is not the accepted launcher (a missing file, then the EXE's fingerprint), or null when it is. One short
    /// sentence that never names the game or its publisher; never throws, since the classic start falls back on any problem.
    /// </summary>
    /// <param name="sha256">DIAGNOSTICS: test seam: the accepted fingerprint; production always checks <see cref="LauncherSha256"/> (checks pass their fixtures' own).</param>
    internal static string? Problem(string folder, string sha256 = LauncherSha256)
    {
        if (Required.FirstOrDefault(file => !File.Exists(Path.Combine(folder, file))) is { } missing)
            return $"The classic launcher folder is missing {Neutral.GetValueOrDefault(missing, missing)}.";
        try
        {
            using var exe = File.OpenRead(Path.Combine(folder, "LoLLauncher.exe"));
            return Convert.ToHexString(SHA256.HashData(exe)).Equals(sha256, StringComparison.OrdinalIgnoreCase) ? null : "The classic launcher is not version 1.0.1.19.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "The classic launcher's program file could not be read.";   // locked or unreadable
        }
    }

    /// <summary>The release's <c>releasemanifest</c> in <paramref name="folder"/> or its parent (the download's shape), else null.</summary>
    internal static string? ManifestFor(string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return new[] { folder, Path.GetDirectoryName(folder) }.OfType<string>().Select(d => Path.Combine(d, "releasemanifest")).FirstOrDefault(File.Exists);
    }
}
