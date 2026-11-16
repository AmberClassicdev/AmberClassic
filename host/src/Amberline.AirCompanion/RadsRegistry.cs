using Amberline.AirCompanion.Rads;
using Microsoft.Win32;

namespace Amberline.AirCompanion;

/// <summary>
/// The RADS root the stock programs read: rads.dll and both 2011 launchers (0.0.0.49 and 0.0.0.76) take
/// <c>HKLM\Software\Riot Games\RADS\LocalRootFolder</c> in the 32-bit registry view (they are 32-bit programs), else <c>c:\rads</c>, and
/// have no other override (batch 811 report, section 1). Both lines' classic starts build their launcher layout under it (owner,
/// 16 November 2026: one root, and the line that starts its classic launcher takes it over), and so does the 1.0.0.126 line's managed game
/// layout. A stage's <c>radsRoot</c> never changes it: the launcher would not follow.
/// </summary>
internal static class RadsRegistry
{
    /// <summary>The root (<paramref name="registryValue"/> is the test seam). Null when the registry names a path that is not absolute: the
    /// programs would resolve it from their own folder, which the classic start cannot build for.</summary>
    internal static string? Root(Func<string?>? registryValue = null)
    {
        var value = registryValue is null ? Read() : registryValue();
        if (string.IsNullOrWhiteSpace(value)) return RadsLayout.DefaultRoot;
        return Path.IsPathRooted(value) ? value : null;
    }

    private static string? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"Software\Riot Games\RADS");
            return key?.GetValue("LocalRootFolder") as string;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
