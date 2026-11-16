using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Amberline.AirCompanion;

/// <param name="Root">The RADS root the launcher reads (<see cref="RadsRegistry.Root"/>).</param>
/// <param name="NewsPort">The loopback HTTP port that serves <c>/launcher/news</c> (AIR's endpoints use the same one).</param>
internal sealed record ClassicLayoutRequest(string Root, string LauncherFolder, string AirFolder, string GameFolder, int NewsPort);

/// <summary>The launcher's release folder (its required working folder) and the EXE copy in it.</summary>
internal sealed record ClassicLayout(string Deploy, string LauncherExecutable);

/// <summary>The part of the classic layout both release lines share (the stamp, <see cref="Ensure"/> and <see cref="Remove"/>); each line's
/// <c>ClassicLauncherLayout.Line.cs</c> holds its layout (<see cref="Plan"/>, its release numbers, <see cref="OwnProjects"/> and its stamp's
/// name, <see cref="StampName"/>, beside the other line's, <see cref="OtherStampName"/>).</summary>
internal static partial class ClassicLauncherLayout
{
    private const string JunctionMark = "J ";

    /// <param name="Bytes">A file's content; null with <paramref name="LinkTo"/> null is a folder.</param>
    /// <param name="Once">Created when missing, never compared or rewritten (the system cfgs).</param>
    private sealed record Item(string Path, byte[]? Bytes = null, string? LinkTo = null, bool Once = false);

    /// <summary>
    /// Builds or refreshes the layout. A refusal (one sentence naming the path or the problem) means the start falls back: a space in the root
    /// or a path that is not ours refuses before anything is written; a failure halfway leaves only stamped paths behind. Never throws: Task 3
    /// turns a refusal into the usual start, and a throw would leave the player without a way in.
    /// NOT RETAIL (batch 814; owner, 16 November 2026: "just wipe that folder when changing 126 &lt;&gt; 132"): both lines' launchers read
    /// the same root, so the other line's layout (its stamp, <see cref="OtherStampName"/>) is removed first, by its stamp, junctions as links
    /// only (<see cref="TakeOver"/>). Nothing unstamped goes: the game's managed layout and the launcher's root files stay.
    /// </summary>
    /// <param name="running">DIAGNOSTICS: test seam only; <see cref="ClassicStart.RunningFrom"/> otherwise.</param>
    internal static (ClassicLayout? Layout, string? Refusal) Ensure(ClassicLayoutRequest request, Action<string> log,
        Func<IReadOnlyList<string>, string?>? running = null)
    {
        (ClassicLayout?, string?) Refuse(string reason)
        {
            log("classic-layout: refused: " + reason);
            return (null, reason);
        }

        try
        {
            var root = Path.GetFullPath(request.Root);
            // The launcher starts AIR and the game with unquoted command lines (design 2026-11-12 §3).
            if (root.Contains(' ')) return Refuse($"The folder {root} has a space in its path, so the classic launcher cannot start from it.");
            var items = Plan(request);
            if (TakeOver(root, log, running ?? ClassicStart.RunningFrom) is { } taken) return Refuse(taken);
            var stampPath = Path.Combine(root, LauncherProject, StampName);
            var lines = new HashSet<string>(File.Exists(stampPath) ? File.ReadAllLines(stampPath).Where(line => line.Length > 0) : [], StringComparer.OrdinalIgnoreCase);
            var owned = new HashSet<string>(lines.Select(StampedPath), StringComparer.OrdinalIgnoreCase);
            const string NotOurs = "already exists and was not created by Amberline.";
            foreach (var item in items)
            {
                var full = Path.Combine(root, item.Path);
                if (Taken(full) && (!owned.Contains(item.Path) || (item.LinkTo is not null) != IsLink(full))) return Refuse($"{full} {NotOurs}");
            }
            foreach (var releases in OwnProjects.Select(project => Path.Combine(root, project, "releases")).Where(Directory.Exists))
                if (Directory.EnumerateDirectories(releases).FirstOrDefault(folder => !owned.Contains(Path.GetRelativePath(root, folder))) is { } old)
                    return Refuse($"{old} {NotOurs}");

            var (created, changed) = (0, 0);
            void Record(string line)
            {
                if (!lines.Add(line)) return;   // already stamped by a start that stopped before creating it
                File.AppendAllText(stampPath, line + "\r\n", Encoding.ASCII);
                created++;
            }
            List<string> Missing(string relative)
            {
                var missing = new List<string>();
                for (var path = relative; path.Length > 0 && !Taken(Path.Combine(root, path)); path = Path.GetDirectoryName(path)!) missing.Insert(0, path);
                return missing;
            }
            // Stamped before created: a kill or an error in between leaves a stamped path that is missing (Remove and Ensure skip or make it),
            // never an unstamped path of ours that would refuse every later start.
            void Folder(string relative)
            {
                var missing = Missing(relative);
                missing.ForEach(Record);
                if (missing.Count > 0) Directory.CreateDirectory(Path.Combine(root, relative));
            }

            // The stamp's own folder is the one exception: the stamp cannot be written before its folder exists.
            var stampFolder = Missing(LauncherProject);
            Directory.CreateDirectory(Path.Combine(root, LauncherProject));
            stampFolder.ForEach(Record);
            foreach (var item in items)
            {
                var full = Path.Combine(root, item.Path);
                if (item.Bytes is null && item.LinkTo is null) { Folder(item.Path); continue; }
                Folder(Path.GetDirectoryName(item.Path)!);
                if (!Taken(full))
                {
                    Record(item.LinkTo is null ? item.Path : JunctionMark + item.Path);
                    if (item.LinkTo is { } target) Junction(full, target);
                    else Write(full, item.Bytes!);
                }
                else if (item.LinkTo is { } target)
                {
                    if (SamePath(new FileInfo(full).LinkTarget!, target)) continue;
                    Directory.Delete(full);   // not recursive: removes the link only, never what it points at
                    Junction(full, target);
                    log($"classic-layout: re-pointed {item.Path} to {target}");
                    changed++;
                }
                else if (!item.Once && !File.ReadAllBytes(full).AsSpan().SequenceEqual(item.Bytes!))
                {
                    File.WriteAllBytes(full, item.Bytes!);   // ours (checked above): refreshed in place
                    changed++;
                }
            }
            log($"classic-layout: {created} paths created, {changed} refreshed under {root}");
            var deploy = Path.Combine(root, LauncherProject, "releases", LauncherVersion, "deploy");
            return (new ClassicLayout(deploy, Path.Combine(deploy, "LoLLauncher.exe")), null);
        }
        // Every exception, not a list: besides I/O, a corrupt player manifest throws IndexOutOfRange, Overflow, InvalidData or OutOfMemory
        // (RadsLayout.ReadManifest trusts its counts) and Process.Start a Win32Exception.
        catch (Exception exception)
        {
            return Refuse($"The classic launcher's folders under {request.Root} could not be prepared ({exception.GetType().Name}: {exception.Message}).");
        }
    }

    /// <summary>
    /// The other line's layout under <paramref name="root"/>, removed before this line's is built (<see cref="Ensure"/>). Null when there is none
    /// or it is gone; else the refusal: a launcher, AIR or game runs from the root or from a folder the other layout's junctions lead to (a program
    /// started through a junction may report either path; nothing is removed then), or its stamp is still there after <see cref="Remove"/> (a
    /// file in use: the stamp lists what is left, so the next start of either line finishes or keeps it).
    /// </summary>
    private static string? TakeOver(string root, Action<string> log, Func<IReadOnlyList<string>, string?> running)
    {
        var stamp = Path.Combine(root, LauncherProject, OtherStampName);
        if (!File.Exists(stamp)) return null;
        List<string> folders = [root];
        foreach (var line in File.ReadAllLines(stamp).Where(line => line.StartsWith(JunctionMark, StringComparison.Ordinal)))
            if (new FileInfo(Path.Combine(root, StampedPath(line))).LinkTarget is { } target) folders.Add(target);
        if (running(folders) is { } busy) return busy;
        log($"classic-layout: removing the other patch line's 2011 launcher layout ({OtherStampName}) under {root}");
        Remove(root, log, OtherStampName);
        return File.Exists(stamp)
            ? $"The other patch line's 2011 launcher files under {root} could not all be removed (a program still uses them)."
            : null;
    }

    /// <summary>
    /// Removes the stamped paths: junctions first and as links only; then stamped folders inside <see cref="OwnProjects"/> whole, with what the
    /// launcher wrote inside them (a junction left inside is removed as a link, never followed); then stamped files; then the stamp itself; then
    /// the other stamped folders (projects, solutions, system, and the stamps' own folder <c>projects\lol_launcher</c>), deepest first and only
    /// when empty, so the game's managed layout, the other line's stamp and anything else shared stays. The stamp goes after everything it
    /// lists but those folders, so a kill halfway leaves it listing what is left (batch 814). Unstamped paths (the launcher's root log) stay, and
    /// so does a stamped path that has become a link someone else made; a stamp line with a ".." part or outside the root is skipped and logged.
    /// After a failure the stamp is rewritten with what is still there, so a later Remove retries and Ensure still knows its own paths. Returns
    /// the paths it removed. This line's stamp only: the other line's goes in <see cref="TakeOver"/>.
    /// </summary>
    internal static IReadOnlyList<string> Remove(string root, Action<string> log) => Remove(root, log, StampName);

    private static IReadOnlyList<string> Remove(string root, Action<string> log, string stampName)
    {
        root = Path.GetFullPath(root);
        var stampPath = Path.Combine(root, LauncherProject, stampName);
        if (!File.Exists(stampPath)) return [];
        var inside = root.TrimEnd('\\', '/') + @"\";
        var entries = new List<(string Line, bool Junction, bool Whole, int Depth, string Path)>();
        foreach (var line in File.ReadAllLines(stampPath).Where(line => line.Length > 0))
        {
            var stamped = StampedPath(line);
            var path = Path.GetFullPath(Path.Combine(root, stamped));
            // Ensure never writes a ".." part: such a line, or one that resolves to the root or outside it, is hand-edited and skipped. The rest is
            // classified on the resolved path, so no spelling of another folder passes for one of OwnProjects (removed whole).
            if (stamped.Split('\\', '/').Contains("..") || !path.StartsWith(inside, StringComparison.OrdinalIgnoreCase))
            {
                log($"classic-layout: skipped the stamp line {line}");
                continue;
            }
            var relative = Path.GetRelativePath(root, path);
            // The stamps' folder itself is never removed whole: the stamp goes last, and the other line's may lie beside it.
            var whole = !relative.Equals(LauncherProject, StringComparison.OrdinalIgnoreCase) &&
                OwnProjects.Any(project => relative.Equals(project, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(project + @"\", StringComparison.OrdinalIgnoreCase));
            entries.Add((line, line.StartsWith(JunctionMark, StringComparison.Ordinal), whole, relative.Count(c => c == '\\'), path));
        }
        var removed = new List<string>();
        var failed = false;
        void Delete(string path, Action delete)
        {
            try { delete(); removed.Add(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                log($"classic-layout: could not remove {path} ({exception.Message})");
                failed = true;
            }
        }

        var paths = entries.Where(entry => !entry.Junction).OrderBy(entry => entry.Depth).ToList();
        foreach (var entry in entries.Where(entry => entry.Junction))
            if (IsLink(entry.Path)) Delete(entry.Path, () => Directory.Delete(entry.Path));   // not recursive: the link only
        foreach (var entry in paths.Where(entry => entry.Whole))   // outermost first; what lay inside is then gone
            if (!IsLink(entry.Path) && Directory.Exists(entry.Path)) Delete(entry.Path, () => Directory.Delete(entry.Path, recursive: true));
        foreach (var entry in paths)
            if (!IsLink(entry.Path) && File.Exists(entry.Path)) Delete(entry.Path, () => File.Delete(entry.Path));
        if (!failed && File.Exists(stampPath)) Delete(stampPath, () => File.Delete(stampPath));
        foreach (var entry in Enumerable.Reverse(paths).Where(entry => !entry.Whole))   // shared folders: deepest first, only when empty
            if (!IsLink(entry.Path) && Directory.Exists(entry.Path) && !Directory.EnumerateFileSystemEntries(entry.Path).Any())
                Delete(entry.Path, () => Directory.Delete(entry.Path));
        if (failed)
        {
            // Written again, with every stamped path that is still there (its folder too, recreated if a shared-folder step took it).
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(stampPath)!);
                File.WriteAllLines(stampPath, entries.Where(entry => Taken(entry.Path)).Select(entry => entry.Line), Encoding.ASCII);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                log($"classic-layout: could not keep the stamp {stampPath} ({exception.Message})");
            }
        }
        log($"classic-layout: removed {removed.Count} paths under {root}");
        return removed;
    }

    /// <summary>The launcher's project folder while a stamp still lists paths under <paramref name="root"/> (a Remove that failed keeps its stamp); else null.</summary>
    internal static string? Remaining(string root) =>
        File.Exists(Path.Combine(root, LauncherProject, StampName)) ? Path.Combine(root, LauncherProject) : null;

    private static string VersionText(uint version) =>
        string.Create(CultureInfo.InvariantCulture, $"{version >> 24}.{(version >> 16) & 255}.{(version >> 8) & 255}.{version & 255}");

    /// <summary>A stamp line's path, relative to the root (a junction's line carries the "J " mark).</summary>
    private static string StampedPath(string line) => line.StartsWith(JunctionMark, StringComparison.Ordinal) ? line[JunctionMark.Length..] : line;

    private static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    /// <summary>True for a file, a folder or a link (also a dangling one) at the path.</summary>
    private static bool Taken(string path) => Path.Exists(path) || IsLink(path);

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static void Write(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);   // CreateNew: never overwrites
        file.Write(bytes);
    }

    /// <summary>A directory junction (no administrator rights needed, unlike a symbolic link). Both paths are quoted, so cmd reads characters like &amp; literally.</summary>
    private static void Junction(string link, string target)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        using var process = Process.Start(start) ?? throw new IOException("cmd.exe did not start");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException($"mklink /J {link} failed ({process.ExitCode}): {output.Trim()}");
    }
}
