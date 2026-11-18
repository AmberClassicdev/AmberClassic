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

    /// <summary>The number the file gives <paramref name="key"/>, or null when the switch is off.</summary>
    public static double? Number(string key)
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
                    if (stamp != DateTime.MinValue)
                        foreach (var line in File.ReadAllLines(FilePath))
                        {
                            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
                            if (parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                                values[parts[0]] = value;
                        }
                    _values = values;
                }
                return _values.TryGetValue(key, out var found) ? found : null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    public static bool On(string key) => Number(key) is { } value && value != 0.0;
}
