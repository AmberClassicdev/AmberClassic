using System.Diagnostics;

namespace Amberline.Tests;

/// <summary>
/// Batch 797, the layout's one rule: a file is in host/ (both patch lines use the same text) or in a patch folder (126/, 132/) at the
/// same relative path, never in both. Two copies would compile twice into one assembly, or one tool would shadow the other.
/// </summary>
public static class LayoutChecks
{
    [Check("layout.no-path-in-host-and-a-patch-folder")]
    public static void NoPathIsInHostAndAPatchFolder()
    {
        var checkout = Path.GetDirectoryName(WorkspaceFiles.PatchFolder)!;
        var start = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-C", checkout, "ls-files", "-z" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start");
        var errors = process.StandardError.ReadToEndAsync();
        var files = process.StandardOutput.ReadToEnd().Split('\0', StringSplitOptions.RemoveEmptyEntries);
        process.WaitForExit();
        TestAssert.Equal(0, process.ExitCode, "git ls-files: " + errors.Result);
        var tracked = files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var host = files.Where(file => file.StartsWith("host/", StringComparison.Ordinal)).Select(file => file["host/".Length..]).ToArray();
        TestAssert.True(host.Length > 0, "git lists no file in host/ of " + checkout);
        var twice = host.SelectMany(file => new[] { "126/" + file, "132/" + file }).Where(tracked.Contains).ToArray();
        TestAssert.True(twice.Length == 0, "in host/ and a patch folder: " + string.Join(", ", twice));
    }
}
