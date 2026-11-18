namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: the Platform's own block on the 1.0.0.126 line (batch 810): the ports of <c>126/patch.json</c> "ports" (HTTP 8390, public
/// HTTPS 8391, RTMP 2099, chat 5222, game UDP 5219 to 5222) and the patch name the host's pages print. The defaults of
/// <see cref="PlatformSettings"/> and the host code read these. The 1.0.0.132 line keeps its own copy at this path (8490, 8491, 2199,
/// 5322, 5319). The friends' companion has its own block (<c>126/src/Amberline.AirCompanion/CompanionLine.cs</c>), which the Platform
/// does not compile.
/// </summary>
public static class PlatformLine
{
    public const int HttpPort = 8390;
    public const int RemotePort = 8391;
    public const int RtmpPort = 2099;
    public const int XmppPort = 5222;
    public const int FirstGamePort = 5219;

    /// <summary>The name the host's pages give the friends' AIR client and game.</summary>
    public const string PatchName = "patch-126";

    /// <summary>
    /// Batch 816: what the host's update check (<see cref="HostUpdates"/>) asks GitHub, and the tag prefix of this line's releases. 126
    /// releases keep the v0.1.x tags and are the project's latest release (owner, 16 November 2026), so the check asks for the latest
    /// release only. The 1.0.0.132 line lists the releases and takes its own p132-v tags.
    /// </summary>
    public const string ReleaseQuery = "https://api.github.com/repos/Amberline-Dev/Project-Amberline/releases/latest";
    public const string ReleaseTagPrefix = "v";

    /// <summary>
    /// NOT RETAIL (batch 879): the start of the host page's cookie names (<see cref="HostSessions.CookieName"/>, <see cref="HostSessions.FormsCookie"/>).
    /// Cookies ignore ports, so with one name on both lines a browser using both lines' pages on 127.0.0.1 kept one line's session and form
    /// token only (batch 875). The 1.0.0.132 line has its own (Amberline132); the old shared Amberline.Host and Amberline.Forms are read by
    /// neither and never deleted, so an older Amberline still running keeps its own.
    /// </summary>
    public const string CookiePrefix = "Amberline126";
}
