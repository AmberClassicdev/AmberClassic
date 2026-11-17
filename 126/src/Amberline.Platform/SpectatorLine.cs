namespace Amberline.Platform;

/// <summary>
/// Batch 846: the spectator grid's JSON (<see cref="SpectatorGrid"/>) for the 1.0.0.126 client, the fields it reads: getGameMetaData
/// <c>gameKey.gameId</c>, <c>gameEnded</c>, <c>chunkTimeInterval</c>; getLastChunkInfo <c>chunkId</c>,
/// <c>duration</c>, <c>availableSince</c>, <c>nextAvailableChunk</c>, <c>endGameChunkId</c>. Whole numbers (the
/// client's property-tree reads), times in milliseconds. The 1.0.0.132 line's copy adds keyFrameTimeInterval and startGameChunkId.
/// </summary>
internal static class SpectatorLine
{
    public static object GameMetaData(long gameId, bool gameEnded, long chunkTimeInterval, long keyFrameTimeInterval) =>
        new { gameKey = new { gameId }, gameEnded, chunkTimeInterval };

    public static object LastChunkInfo(int chunkId, long duration, long availableSince, long nextAvailableChunk, int endGameChunkId, int startGameChunkId) =>
        new { chunkId, duration, availableSince, nextAvailableChunk, endGameChunkId };
}
