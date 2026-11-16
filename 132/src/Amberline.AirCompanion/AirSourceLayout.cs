// Linked as source into the Platform as well (see RetailClients.cs).
#if AMBERLINE_HOST_FOLDERS
namespace Amberline.Platform.Folders;
#else
namespace Amberline.AirCompanion;
#endif

/// <summary>
/// The stock AIR layout a source must hold before it is copied: this release line's files and folders (<see cref="CompanionLine.AirFiles"/>,
/// <see cref="CompanionLine.AirFolders"/>). Mirrors Get-AirSourceProblems in tools/air-client-bootstrap.cmd (same entries, same wording); keep
/// both in step (server.air-source-layout-refusal). Sizes are the stock files of the line's reference AIR client, so a truncated download is
/// refused before any copy exists.
/// </summary>
public static class AirSourceLayout
{
    private static readonly (string Name, long Size)[] Files = CompanionLine.AirFiles;

    private static readonly string[] Folders = CompanionLine.AirFolders;

    /// <summary>What is missing or damaged in an AIR folder; empty when the stock layout is complete.</summary>
    public static IReadOnlyList<string> Problems(string root)
    {
        var problems = new List<string>();
        if (!Directory.Exists(root))
        {
            problems.Add("the AIR folder");
            return problems;
        }
        foreach (var (name, size) in Files)
        {
            var file = Path.Combine(root, name);
            if (!File.Exists(file)) problems.Add(name);
            else if (new FileInfo(file).Length != size) problems.Add(name + " (damaged)");
        }
        foreach (var folder in Folders)
        {
            var directory = Path.Combine(root, folder);
            if (!Directory.Exists(directory) || !Directory.EnumerateFileSystemEntries(directory).Any()) problems.Add(folder + "\\");
        }
        return problems;
    }
}
