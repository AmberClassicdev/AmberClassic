namespace Amberline.Tests.Air;

/// <summary>
/// Test oracle for a stock AIR 1.52 folder (patch 1.0.0.132), taken from the reference AIR 1.52 folder (file sizes match the
/// batch 795 survey of that folder, section 1; batch 795 restated it from the 1.45 <c>lol_air_client</c>: the .swf and
/// the three old modules changed size, css\fonts.swf grew 2 bytes, and mod_champ, mod_rep, mod_rt and the championDetail and
/// rankedTeams asset folders are new). The friend launcher and the companion must agree with it.
/// Files are sized with SetLength, so the fixture costs no real disk writes.
/// </summary>
public static class AirStockLayoutFixture
{
    public static readonly (string Name, long Size)[] Files =
    [
        ("LolClient.exe", 144384),
        ("LolClient.swf", 7009444),
        ("META-INF\\AIR\\application.xml", 5115),
        ("mimetype", 59),
        ("Adobe AIR\\Versions\\1.0\\Adobe AIR.dll", 10887040),
        ("css\\fonts.swf", 180288),
        ("mod_lgn.dat", 4323221),
        ("mod_man.dat", 14798372),
        ("mod_ser.dat", 564621),
        ("mod_champ.dat", 659799),
        ("mod_rep.dat", 600204),
        ("mod_rt.dat", 823935),
    ];

    public static readonly string[] Folders =
    [
        "assets\\championDetail",
        "assets\\data",
        "assets\\help",
        "assets\\htmlTemplates",
        "assets\\images",
        "assets\\locale",
        "assets\\rankedTeams",
        "assets\\sounds",
        "assets\\storeImages",
        "assets\\swfs",
    ];

    /// <summary>
    /// Writes the stock layout under <paramref name="root"/>. <paramref name="missing"/> drops one file or folder,
    /// <paramref name="truncated"/> shortens one file by a byte, and <paramref name="emptied"/> keeps a folder with no file.
    /// </summary>
    public static void Write(string root, string? missing = null, string? truncated = null, string? emptied = null)
    {
        foreach (var (name, size) in Files)
        {
            if (name == missing) continue;
            var file = Path.Combine(root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var stream = File.Create(file);
            stream.SetLength(name == truncated ? size - 1 : size);
        }
        foreach (var folder in Folders)
        {
            if (folder == missing) continue;
            var directory = Path.Combine(root, folder);
            Directory.CreateDirectory(directory);
            if (folder != emptied) File.WriteAllBytes(Path.Combine(directory, "stock.bin"), [0]);
        }
    }
}
