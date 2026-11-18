using Amberline.AirCompanion.Rads;

namespace Amberline.AirCompanion;

/// <summary>
/// Gives the picked game folder the stock client's managed file layout (RADS manifest + RAF archives), built on this PC from this PC's own game
/// files. Nothing is shipped: the same promise as <see cref="GameLanguagePack"/>. It exists because the first buff tooltip of a game builds the
/// client's buff database from 780 loose files (about 5 s on a cold disk) and the managed layout serves them from one archive (about 0.2 s); see
/// docs/maintenance/2026-10-18-w15-rads-delivery.md.
/// The work runs on a background thread so it never delays AIR or the game; a game that starts before the layout is complete simply runs
/// on its own files, exactly as before (the layout becomes visible only when its manifest is moved into place last). A folder that does not
/// read like <c>projects/&lt;name&gt;/releases/&lt;version&gt;</c> stays unmanaged: the client decides managed or unmanaged from its working
/// directory, and staging a second copy of the game under a RADS-shaped path would be a launcher change, not a file step.
/// </summary>
internal static class GameManagedLayout
{
    internal const string NoShortNamesWarning = "managed-layout: WARNING this volume has no 8.3 short names and no managed layout is active: the stock client cannot build its spell index from loose files and stops at hero creation (\"Avatar spell not found!\"). Keep the game in a folder like projects/lol_game_client/releases/<version> (the layout is then built on any drive) or on the system drive.";

    /// <summary>Set to 0 to turn the whole step off (support switch: the game then runs exactly as it did before this existed).</summary>
    internal const string OffVariable = "AMBERLINE_MANAGED_LAYOUT";

    /// <summary>
    /// Where rads.dll looks for its manifest: <c>HKLM\Software\Riot Games\RADS\LocalRootFolder</c> in the 32-bit registry view (the client is a
    /// 32-bit process), else its built-in default. Null when the registry names a path that is not absolute (the client would resolve it from its
    /// own folder; this class cannot build for that).
    /// </summary>
    internal static string? ResolveRoot(Func<string?>? registryValue = null) => RadsRegistry.Root(registryValue);

    /// <summary>The options of a build that runs beside a starting game: a quarter of the cores and small batches, so memory and disk stay light.</summary>
    internal static ManagedLayout.EnsureOptions BackgroundOptions() =>
        new(Parallelism: Math.Max(1, Environment.ProcessorCount / 4), BatchMegabytes: 32);

    /// <summary>
    /// Starts the check (and, when needed, the build) for <paramref name="gameDirectory"/> and returns at once. The task result is the log line, which
    /// <paramref name="write"/> receives as well; it holds no path. The task never faults. <paramref name="notice"/> shows the player why a RADS root
    /// is not used (<see cref="LockedRoot"/>), before this returns.
    /// </summary>
    internal static Task<string> Start(string gameDirectory, Action<string> write, string? root = null, ManagedLayout.EnsureOptions? options = null, bool? gameRunning = null, Func<string>? shortNames = null,
        Action<string>? notice = null)
    {
        if (Environment.GetEnvironmentVariable(OffVariable) == "0")
        {
            const string off = $"managed-layout: off ({OffVariable}=0)";
            write(off);
            return Task.FromResult(off);
        }
        root ??= ResolveRoot();
        if (root is null)
        {
            const string relative = "managed-layout: unmanaged (the RADS root in the registry is not an absolute path)";
            write(relative);
            return Task.FromResult(relative);
        }
        // Batch 857 re-review 3: the root is settled here, before the start goes on, so the notice shows while the start window does (a later
        // one would open that window again). A folder the client never manages takes no root.
        var skipped = options?.DryRun != true && ManagedLayout.ParseGameDirectory(Path.GetFullPath(gameDirectory)) is not null
            ? LockedRoot(Path.GetFullPath(root), notice) : null;
        // A game from an earlier session can outlive its companion and have the archives open: then a stale layout is only taken out of use.
        var running = gameRunning ?? System.Diagnostics.Process.GetProcessesByName("League of Legends").Length > 0;
        return Task.Factory.StartNew(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            string line;
            ManagedLayout.Result? result = null;
            try { result = ManagedLayout.Ensure(gameDirectory, root, (options ?? BackgroundOptions()) with { GameRunning = running, PrepareRoot = _ => skipped }); line = result.ToString(); }
            // A background step on someone else's machine must never take the companion down; Ensure already reports I/O problems as results.
            catch (Exception exception) { line = $"managed-layout: failed ({exception.GetType().Name})"; }
            write(line);
            // Without an active layout the client runs on its loose files, which need 8.3 short names (see GameFolderReport.ShortNames): say so in the log,
            // where a friend whose game stops at hero creation will paste it.
            if (result?.Outcome is not (ManagedLayout.Outcome.Built or ManagedLayout.Outcome.Current) && (shortNames ?? (() => GameFolderReport.ShortNames(gameDirectory)))() == "off")
                write(NoShortNamesWarning);
            return line;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>
    /// NOT RETAIL (batch 857 re-review 2, MEDIUM 2): the layout's root is the classic start's (c:\rads), so it takes the same rules
    /// (<see cref="ClassicLauncherLayout.RootRefusal"/>): a root that is not there is made locked to this Windows user and marked; one that is there
    /// is used only while it passes the classic start's checks. Else nothing is built or written there. A manifest an earlier layout left in that
    /// root stays in use, though: the game reads the files it lists from that layout's archives, not from its own folder, until the folder is
    /// deleted. So <paramref name="notice"/> tells the player why, with the classic start's own reason (which says to delete the folder), as the
    /// classic start's fallback does. Null when the root may be used; else the log's reason, without a path.
    /// </summary>
    private static string? LockedRoot(string root, Action<string>? notice)
    {
        string? refused;
        try { refused = ClassicLauncherLayout.RootRefusal(root, null); }
        // It runs on the start's own path now, which it must never stop: any failure skips the layout.
        catch (Exception exception) { refused = exception.Message; }
        if (refused is null) return null;
        notice?.Invoke($"The game's file layout was not updated: {refused.TrimEnd()}");
        return "the RADS root is not locked to your Windows account; delete that folder, and the next start lays it out again, locked";
    }
}
