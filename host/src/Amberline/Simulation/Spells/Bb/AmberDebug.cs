using System.Globalization;

namespace Amberline.Simulation.Spells.Bb;

/// <summary>
/// Amber Classic debug switches (NOT RETAIL): <c>C:\amber-debug.txt</c>, one <c>key=value</c> per line, read again whenever the file changes,
/// so an experiment needs no rebuild and no restart. A missing file, a missing key or an unreadable value means "off" (the stock behaviour).
/// </summary>
internal static class AmberDebug
{
    private const string FilePath = @"C:\amber-debug.txt";
    private static readonly object Gate = new();
    private static DateTime _stamp = DateTime.MinValue;
    private static Dictionary<string, double> _values = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> _texts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"fizz-e-run": the heroes whose run animation is overridden for the dash in progress (restored when it ends).</summary>
    public static readonly HashSet<uint> RunOverridden = [];

    /// <summary>"shark-anim-delay": plays sent again later (unit, clip, lock, blend, scale, due time).</summary>
    public static readonly List<(Amberline.Core.Entity Unit, string Name, bool Lock, bool Blend, float Scale, double Due)> Replays = [];

    /// <summary>The number the file gives <paramref name="key"/>, or null when the switch is off.</summary>
    public static double? Number(string key) => Read(key) && _values.TryGetValue(key, out var found) ? found : (double?)null;

    /// <summary>The text the file gives <paramref name="key"/>, or null when the switch is off.</summary>
    public static string? Text(string key) => Read(key) && _texts.TryGetValue(key, out var found) && found.Length > 0 ? found : null;

    private static bool Read(string key)
    {
        try
        {
            lock (Gate)
            {
                var stamp = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
                if (stamp != _stamp)
                {
                    _stamp = stamp;
                    var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                    var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    if (stamp != DateTime.MinValue)
                        foreach (var line in File.ReadAllLines(FilePath))
                        {
                            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
                            if (parts.Length == 2) texts[parts[0]] = parts[1];
                            if (parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                                values[parts[0]] = value;
                        }
                    _values = values;
                    _texts = texts;
                }
                return true;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    public static bool On(string key) => Number(key) is { } value && value != 0.0;
}
