using System.IO.Compression;
using Amberline.AirCompanion;
using Amberline.Platform;

namespace Amberline.Tests.Air;

/// <summary>
/// Batch 815 (1.0.0.132 only): the setup accepts the 1.0.0.132 files the reference folders hold (GAME_DATA_ROOT, AIR_CLIENT_ROOT) and reads the
/// client bundle's layout. Read only: two executables are hashed, and the bundle's central directory is read; nothing is unpacked or written.
/// Each check prints why it is skipped when its reference is not on this PC.
/// </summary>
public static class RetailClientsLineChecks
{
    // The checks' usual environment: the line's game and AIR reference folders, and the client bundle zip when it is on this PC.
    private static readonly string GameFolder = Environment.GetEnvironmentVariable("GAME_DATA_ROOT") ?? "";
    private static readonly string AirFolder = Environment.GetEnvironmentVariable("AIR_CLIENT_ROOT") ?? "";
    private static readonly string Bundle = Environment.GetEnvironmentVariable("CLIENT_BUNDLE_ZIP") ?? "";

    [Check("server.retail-clients-132-reference-folders")]
    public static void TheReferenceFoldersPassTheSetupsChecks()
    {
        if (!File.Exists(Path.Combine(GameFolder, "League of Legends.exe")) || !File.Exists(Path.Combine(AirFolder, "LolClient.exe")))
        {
            Console.WriteLine($"skipped: the 1.0.0.132 reference folders ({GameFolder}, {AirFolder}) are not on this PC");
            return;
        }
        TestAssert.True(RetailClients.GameProblem(GameFolder) is null, $"the 1.0.0.132 game folder fails its check: {RetailClients.GameProblem(GameFolder)}");
        TestAssert.True(RetailClients.AirProblem(AirFolder) is null, $"the 1.0.0.132 AIR folder fails its check: {RetailClients.AirProblem(AirFolder)}");
        TestAssert.True(HostFolders.Check(FolderKind.Game, GameFolder, new FolderSetup()).Ok, "the setup page refuses the 1.0.0.132 game folder");
        TestAssert.True(HostFolders.Check(FolderKind.Air, AirFolder, new FolderSetup()).Ok, "the setup page refuses the 1.0.0.132 AIR folder");

        // The line's wording: a game folder that is not this patch names 1.0.0.132, never 1.0.0.126.
        var refused = RetailClients.GameProblem(GameFolder, new string('0', 64));
        TestAssert.True(refused is not null && refused.Contains("1.0.0.132", StringComparison.Ordinal) && !refused.Contains("1.0.0.126", StringComparison.Ordinal),
            $"the wording of the game check: {refused}");
    }

    [Check("server.retail-clients-132-client-bundle")]
    public static void TheClientBundleIsFoundByWhatItHolds()
    {
        if (!File.Exists(Bundle))
        {
            Console.WriteLine($"skipped: the 1.0.0.132 client bundle ({Bundle}) is not on this PC");
            return;
        }
        // The central directory only: the bundle is read, never unpacked (ClientZip.Unpack is not called).
        using var archive = ZipFile.OpenRead(Bundle);
        const string top = "patch-1.0.0.132-client-bundle/";
        TestAssert.Equal(new ClientZip.Layout(top + "game/", top + "air/", top + "launcher/"), ClientZip.Locate(archive), "the bundle's game, AIR client and launcher");
        TestAssert.Equal("patch-1.0.0.132-client-bundle.zip", ClientZip.BundleName, "the bundle's name");
    }
}
