namespace Amberline.Platform;

/// <summary>
/// Batch 846: the spectator grid's JSON (<see cref="SpectatorGrid"/>) for the 1.0.0.132 client: the 1.0.0.126 fields plus
/// <c>keyFrameTimeInterval</c> in getGameMetaData (the client asserts it is a multiple of chunkTimeInterval) and
/// <c>startGameChunkId</c> in getLastChunkInfo (the first chunk after the start-up chunks, where keyframe 1 maps to).
/// Whole numbers (the client's property-tree reads), times in milliseconds.
/// </summary>
internal static class SpectatorLine
{
    public static object GameMetaData(long gameId, bool gameEnded, long chunkTimeInterval, long keyFrameTimeInterval) =>
        new { gameKey = new { gameId }, gameEnded, chunkTimeInterval, keyFrameTimeInterval };

    public static object LastChunkInfo(int chunkId, long duration, long availableSince, long nextAvailableChunk, int endGameChunkId, int startGameChunkId) =>
        new { chunkId, duration, availableSince, nextAvailableChunk, endGameChunkId, startGameChunkId };
}
