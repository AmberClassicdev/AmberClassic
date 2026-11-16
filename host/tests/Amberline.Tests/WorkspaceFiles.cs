namespace Amberline.Tests;

/// <summary>
/// Batch 797: the repository's files for the checks that read or run them. A patch line's own files are in its folder (126/ or
/// 132/), the files both lines share in host/ beside it. A path is looked for in every folder above the working folder, then above
/// the test build, first as given, then under host/; the checks run from the patch folder (check.ps1 starts them there), so the
/// patch's own file is found before host's.
/// </summary>
internal static class WorkspaceFiles
{
    internal static string Find(string relativePath)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var current = new DirectoryInfo(start); current is not null; current = current.Parent)
                foreach (var candidate in new[] { Path.Combine(current.FullName, relativePath), Path.Combine(current.FullName, "host", relativePath) })
                    if (File.Exists(candidate)) return candidate;
        throw new FileNotFoundException(relativePath);
    }

    /// <summary>The patch folder the checks run for (the folder with patch.json); its name, 126 or 132, is the tools' -Patch.</summary>
    internal static string PatchFolder => Path.GetDirectoryName(Find("patch.json"))!;

    internal static string Patch => Path.GetFileName(PatchFolder);
}
