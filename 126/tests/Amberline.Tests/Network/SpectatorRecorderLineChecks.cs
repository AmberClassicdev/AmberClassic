using Amberline.Network.Protocol;

namespace Amberline.Tests.Network;

/// <summary>
/// Batch 847: the 1.0.0.126 packet numbers the spectator stream carries (design 845 C7; c-protocol opcode map). The recorder check
/// (<c>network.spectate-recorder-stream</c>) reads the stream with the line's own builders, so these pins say which numbers that is.
/// </summary>
public static class SpectatorRecorderLineChecks
{
    [Check("network.spectate-recorder-line-numbers")]
    public static void LineNumbers()
    {
        TestAssert.Equal((byte)0x57, LoadingPackets.SynchVersionS2C().Plaintext.Span[0], "SynchVersionS2C");
        TestAssert.Equal((byte)0x98, LoadingPackets.WorldSendGameNumber().Plaintext.Span[0], "WorldSendGameNumber");
        TestAssert.Equal((byte)0x5F, SpawnPackets.StartGame().Plaintext.Span[0], "StartGame");
        TestAssert.Equal((byte)0xC9, SpawnPackets.GameClock(0f).Plaintext.Span[0], "GameClock");
        TestAssert.Equal((byte)0xEE, VisionPackets.EnterTeamVisibility(1, 0).Plaintext.Span[0], "EnterTeamVisibility");
        TestAssert.Equal((byte)0xEF, VisionPackets.LeaveTeamVisibility(1, 1).Plaintext.Span[0], "LeaveTeamVisibility");
        TestAssert.Equal((byte)0xA9, PausePackets.Pause(0, 1).Plaintext.Span[0], "Pause");
        TestAssert.Equal((byte)0xCE, CombatPackets.EndGame(true).Plaintext.Span[0], "EndGame");
    }
}
