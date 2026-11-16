using System.Security.Cryptography;

// Linked as source into the Platform as well (its setup page checks the host's folders); keep this file free of other companion types.
// The values that differ per patch line (the wording, the game executable's SHA-256, the client bundle) are in RetailClients.Line.cs.
#if AMBERLINE_HOST_FOLDERS
namespace Amberline.Platform.Folders;
#else
namespace Amberline.AirCompanion;
#endif

/// <summary>
/// The pinned identity of the stock files the companion runs: the game executable (per patch line, in RetailClients.Line.cs beside each
/// patch folder) and the AIR client's LolClient.exe, which is the same file on both lines. Shared by the bring-your-own mode and the archive
/// installer. This file names no download, so it stays in a build without <c>ClientFilesInstaller.cs</c>.
/// </summary>
internal static partial class RetailClients
{
    /// <summary>SHA-256 of the stock AIR client's <c>LolClient.exe</c> (Adobe's launcher under the client's name), the same on both patch lines.</summary>
    internal const string LolClientSha256 = "193ED9D875838EC276A7AE6C667DFCC434027D8775E132DA361CBD0CDDFFED30";

    /// <summary>Why <paramref name="folder"/> is not the stock game folder (its League of Legends.exe and DATA), or null when it is.</summary>
    internal static string? GameProblem(string folder, string expectedSha256 = GameExecutableSha256)
    {
        var executable = Path.Combine(folder, "League of Legends.exe");
        if (!File.Exists(executable)) return "there is no game executable in it";
        if (!Directory.Exists(Path.Combine(folder, "DATA"))) return "there is no DATA folder beside the game executable";
        return Sha256(executable) == expectedSha256 ? null : $"The game executable is not the {Version} game (its SHA-256 differs)";
    }

    /// <summary>Why <paramref name="folder"/> is not the complete stock AIR folder (the layout, then LolClient.exe), or null when it is.</summary>
    internal static string? AirProblem(string folder, string expectedSha256 = LolClientSha256)
    {
        var problems = AirSourceLayout.Problems(folder);
        if (problems.Count > 0) return "it is not the complete AIR client (missing or damaged: " + string.Join(", ", problems).Replace("LolClient.swf", "the AIR client's .swf file").Replace("LolClient.exe", "the AIR client's executable") + ")";
        return Sha256(Path.Combine(folder, "LolClient.exe")) == expectedSha256 ? null : $"The AIR client's executable is not the {Version} AIR client (its SHA-256 differs)";
    }

    internal static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
