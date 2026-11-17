using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Amberline.Network.Protocol;

namespace Amberline.Network.Spectator;

/// <summary>
/// One recorded server-to-client game packet: <see cref="Data"/> is the whole packet as the game stream carries it before encryption
/// (<c>[type u8][param i32 little-endian, the net id][payload]</c>), <see cref="Channel"/> its ENet channel and <see cref="Time"/> the
/// game time in seconds the spectator client plays it at.
/// </summary>
public readonly record struct SpectatorPacket(float Time, byte Channel, byte[] Data);

/// <summary>
/// Batch 846 (spectating S1): a spectator chunk exactly as the stock game clients read it. Both lines' clients share the code (126
/// addresses here; the 132 ones are in <c>132/docs/maintenance/2026-11-17-batch-845-spectate-design.md</c> B3). A chunk is the packed block
/// of packets (<see cref="Pack"/>), gzipped and Blowfish-encrypted with the game's 16-character chunk key (<see cref="Seal"/>); the key reaches
/// the client inside the launch's credential key (<see cref="WrapKey"/>).
/// </summary>
public static class SpectatorChunk
{
    /// <summary>GetChunk inflates into a fixed 1 MiB buffer.</summary>
    public const int MaxUnpacked = 0x100000;

    /// <summary>the client asserts <c>packetDataSize &lt; 100000</c> for the bytes after the 5-byte header.</summary>
    public const int MaxPayload = 99_999;

    /// <summary>
    /// The most a packet grows by in a block: marker, f32 time and u32 length (its type and param are the packet's own first 5 bytes), so a
    /// block is never longer than the sum of its packets' lengths plus this per packet. S2 cuts chunks on that sum.
    /// </summary>
    public const int MaxOverhead = 9;

    private const string KeyCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>
    /// Packs packets the way the client unpacks them with <c>[Replay] EnableDataPacking</c> on (its default; 126 the client =
    /// 132 the client). Per packet a marker byte: low nibble the channel; 0x80 time = previous + u8 ms, else f32 seconds; 0x10 length
    /// u8, else u32; 0x40 type = previous, else u8; 0x20 param = previous + s8, else i32; then the payload. The first packet of every chunk
    /// carries its time, type and param in full, so a chunk reads the same whether or not the client keeps the previous chunk's values.
    /// A delta time is chosen against the time the client rebuilds (<c>u8 * 0.001f + previous</c>, in floats), so the error never adds up.
    /// </summary>
    public static byte[] Pack(IReadOnlyList<SpectatorPacket> packets)
    {
        ArgumentNullException.ThrowIfNull(packets);
        var output = new MemoryStream();
        Span<byte> head = stackalloc byte[14];
        float time = 0;
        byte type = 0;
        var param = 0;
        for (var index = 0; index < packets.Count; index++)
        {
            var (packetTime, channel, data) = packets[index];
            if (data is null || data.Length < 5) throw new ArgumentException($"Packet {index} is shorter than its 5-byte header.", nameof(packets));
            if (data.Length - 5 > MaxPayload) throw new ArgumentException($"Packet {index} carries more than {MaxPayload} bytes after its header.", nameof(packets));
            if (channel > 0x0F) throw new ArgumentException($"Packet {index} names channel {channel}; the marker holds 0 to 15.", nameof(packets));
            if (!float.IsFinite(packetTime)) throw new ArgumentException($"Packet {index} has no finite time.", nameof(packets));
            var first = index == 0;
            var marker = channel;
            var length = 1;

            var ms = MathF.Round((packetTime - time) * 1000f);
            if (!first && ms is >= 0 and <= 255 && MathF.Abs(ms * 0.001f + time - packetTime) <= 0.0005f)
            {
                marker |= 0x80;
                head[length++] = (byte)ms;
                time = ms * 0.001f + time;
            }
            else
            {
                BinaryPrimitives.WriteSingleLittleEndian(head[length..], packetTime);
                length += 4;
                time = packetTime;
            }

            var payload = data.Length - 5;
            if (payload <= byte.MaxValue)
            {
                marker |= 0x10;
                head[length++] = (byte)payload;
            }
            else
            {
                BinaryPrimitives.WriteInt32LittleEndian(head[length..], payload);
                length += 4;
            }

            if (!first && data[0] == type) marker |= 0x40;
            else head[length++] = data[0];
            type = data[0];

            var packetParam = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(1));
            var step = (long)packetParam - param;
            if (!first && step is >= sbyte.MinValue and <= sbyte.MaxValue)
            {
                marker |= 0x20;
                head[length++] = (byte)(sbyte)step;
            }
            else
            {
                BinaryPrimitives.WriteInt32LittleEndian(head[length..], packetParam);
                length += 4;
            }
            param = packetParam;

            head[0] = marker;
            output.Write(head[..length]);
            output.Write(data, 5, payload);
        }
        return output.ToArray();
    }

    /// <summary>
    /// Gzip, then Blowfish-ECB with the chunk key over the whole 8-byte blocks; a shorter tail stays as it is, as the client's decrypt
    /// leaves it (the function the ENet receive path uses too). The client inflates gzip only (window bits 31 at
    /// 126) into <see cref="MaxUnpacked"/> bytes; an empty chunk is refused because GetChunk's checked buffers fail on one.
    /// </summary>
    public static byte[] Seal(ReadOnlySpan<byte> block, string chunkKey)
    {
        if (block.IsEmpty) throw new ArgumentException("A chunk holds at least one packet.", nameof(block));
        if (block.Length > MaxUnpacked) throw new ArgumentException($"A chunk unpacks to at most {MaxUnpacked} bytes; this one is {block.Length}.", nameof(block));
        var cipher = new LegacyBlowfishCipher(ChunkKeyBytes(chunkKey));
        var gzip = new MemoryStream();
        using (var zip = new GZipStream(gzip, CompressionLevel.Optimal, leaveOpen: true)) zip.Write(block);
        var packed = gzip.GetBuffer().AsSpan(0, (int)gzip.Length);
        var whole = packed.Length - packed.Length % LegacyBlowfishCipher.BlockSize;
        return [.. cipher.Encrypt(packed[..whole]), .. packed[whole..]];
    }

    /// <summary>A new game's chunk key: 16 random letters and digits, as the game server's match key (no NUL can cut it short).</summary>
    public static string NewChunkKey() => RandomNumberGenerator.GetString(KeyCharacters, 16);

    /// <summary>
    /// The credential key the spectator launch carries (<c>"spectator &lt;host&gt; &lt;key&gt; &lt;gameId&gt; &lt;platformId&gt;"</c>): the client
    /// base64-decodes it (126 the client, after <c>strnlen(key, 17) &gt; 16</c>), keys Blowfish with the game id's decimal
    /// text, decrypts three blocks into its key buffer and
    /// zeroes byte 16, so it keeps the first 16 bytes. Wrapped here as the chunk key plus eight 0x08 pad bytes: 32 characters.
    /// </summary>
    public static string WrapKey(long gameId, string chunkKey)
    {
        // _ltoa takes the 32-bit long: an id above int.MaxValue would key the client with other digits.
        if (gameId is < 1 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(gameId), "A spectated game id is 1 to 2147483647.");
        var plain = new byte[24];
        ChunkKeyBytes(chunkKey).CopyTo(plain, 0);
        plain.AsSpan(16).Fill(8);
        return Convert.ToBase64String(new LegacyBlowfishCipher(GameIdKey(gameId)).Encrypt(plain));
    }

    /// <summary>
    /// The client keys Blowfish with the id's digits at their own length (length = strlen), and its key schedule
    /// cycles the key bytes over the 18 P words. The digits repeated whole until there are at least 4 bytes (the Blowfish code's minimum)
    /// cycle to the same bytes: "7" as "7777", "12" as "1212", "123" as "123123".
    /// </summary>
    private static byte[] GameIdKey(long gameId)
    {
        var digits = gameId.ToString(CultureInfo.InvariantCulture);
        return Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(digits, (digits.Length + 3) / digits.Length)));
    }

    internal static byte[] ChunkKeyBytes(string chunkKey)
    {
        if (chunkKey is not { Length: 16 } || chunkKey.Any(c => c is < '!' or > '~'))
            throw new ArgumentException("A chunk key is 16 printable ASCII characters.", nameof(chunkKey));
        return Encoding.ASCII.GetBytes(chunkKey);
    }
}
