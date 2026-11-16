// The 1.0.0.132 line's values for RetailClients (the shared part is host/src/Amberline.AirCompanion/RetailClients.cs). The 1.0.0.126 line
// has its own copy at this path. Linked as source into the Platform as well (132/src/Amberline.Platform/Amberline.Platform.csproj).
#if AMBERLINE_HOST_FOLDERS
namespace Amberline.Platform.Folders;
#else
namespace Amberline.AirCompanion;
#endif

/// <summary>
/// The pinned identity of this line's stock game: the patch's wording, the game executable's SHA-256, the client bundle's name and its game
/// folder, and the words the setup page uses for the download. The values were read from the reference game folder (D:\132_Game): 8,937,984
/// bytes, the same digest native/exehost/exehost.c checks.
/// </summary>
internal static partial class RetailClients
{
    /// <summary>The patch these files are, in the words the setup page and the companion's pages use.</summary>
    internal const string Version = "1.0.0.132";

    /// <summary>The year the patch is from, in the words the setup page and the friend's invitation use ("the 2012 game").</summary>
    internal const string Year = "2012";

    /// <summary>SHA-256 of the stock <c>League of Legends.exe</c> 1.0.0.132 (8,937,984 bytes); the value native/exehost/exehost.c checks.</summary>
    internal const string GameExecutableSha256 = "B2A1BF13FF633DAFDAA1A7BF51011B5D6B002342F0F31F13B40A5E70A83A5E18";

    /// <summary>The client bundle's file name (the setup accepts the bundle as one zip).</summary>
    internal const string ClientBundleName = "patch-1.0.0.132-client-bundle.zip";

    /// <summary>
    /// Where the game's release folder lies in the client bundle, below the bundle's game folder (the one that holds <c>projects</c>): the
    /// 1.0.0.126 bundle holds <c>projects\lol_game_client\releases\0.0.0.51</c>. Empty where the bundle's game folder holds the executable itself,
    /// as the 1.0.0.132 bundle does (<c>game\League of Legends.exe</c> beside <c>DATA</c> and <c>LEVELS</c>).
    /// </summary>
    internal const string ClientBundleGame = "";

    /// <summary>The parenthesis of the setup page's game row that says what the download holds.</summary>
    internal const string GameFolderHint = "it holds League of Legends.exe, DATA and LEVELS";
}
