using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Amberline.Network.Spectator;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

namespace Amberline.Tests.Network;

/// <summary>
/// Batch 846 (spectating S1): the spectator chunk against a decoder written from the stock client's code (126 addresses; the 132 client's
/// functions have the same logic, design B3). The decoder shares no code with the packer or the key wrap.
/// </summary>
public static class SpectatorChunkChecks
{
    [Check("network.spectate-chunk-round-trip")]
    public static void RoundTrip()
    {
        // A pinned block: the first packet in full (marker 0x11: channel 1, u8 length; f32 time, length, type, i32 param), the second all
        // deltas (marker 0xF1: + u8 ms, u8 length, same type, + s8 param).
        TestAssert.Equal("11" + "00000000" + "03" + "10" + "01000040" + "AAABAC" + "F1" + "00" + "00" + "01",
            Convert.ToHexString(SpectatorChunk.Pack([Packet(0f, 1, 0x10, 0x40000001, 3), Packet(0f, 1, 0x10, 0x40000002, 0)])), "the packed bytes");

        var payload255 = Packet(0.255f, 2, 0x20, 0x40000002 - 128, 255);
        SpectatorPacket[] packets =
        [
            Packet(0f, 1, 0x10, 0x40000001, 3),
            Packet(0f, 1, 0x10, 0x40000002, 0),
            payload255,                                              // + 255 ms, u8 length 255, new type, param - 128
            Packet(0.511f, 2, 0x20, 0x40000002 - 128 + 127, 256),    // + 256 ms: f32 time; u32 length; param + 127
            Packet(0.511f, 2, 0x20, 0x40000002 - 128 + 127 + 128, 1), // param + 128: i32
            Packet(0.544f, 3, 0x21, int.MinValue, 7),                // + 33 ms; a param jump across the sign
            Packet(12.3456f, 4, 0x21, int.MaxValue, 9),
            Packet(12.3789f, 4, 0x21, int.MaxValue, 9),              // + 33.3 ms: rebuilt within half a millisecond
            Packet(1000.0333f, 15, 0xFF, -1, SpectatorChunk.MaxPayload), // channel 15, the largest payload
            Packet(999f, 0, 0x00, 0, 0),                             // back in time: f32
        ];
        var block = SpectatorChunk.Pack(packets);
        var decoded = Client.Unpack(block);
        TestAssert.Equal(packets.Length, decoded.Count, "every packet comes back");
        for (var i = 0; i < packets.Length; i++)
        {
            TestAssert.Equal(Convert.ToHexString(packets[i].Data), Convert.ToHexString(decoded[i].Packet), $"packet {i}'s bytes");
            TestAssert.Equal((int)packets[i].Channel, decoded[i].Channel, $"packet {i}'s channel");
            TestAssert.Near(packets[i].Time, decoded[i].Time, 0.0005 + 1e-6, $"packet {i}'s time");
        }
        TestAssert.Equal(new[] { false, true, true, false, true, true, false, true, false, false }, decoded.Select(p => p.DeltaTime).ToArray(), "delta times where 0..255 ms");

        // BouncyCastle's Blowfish cycles the key bytes as the client's key schedule does: the premise of the id key.
        TestAssert.Equal(Encrypt("1234"), Encrypt("12341234"), "a repeated key is the same key");
        TestAssert.Equal(Encrypt("12345"), Encrypt("1234512345"), "a repeated key is the same key");

        foreach (var gameId in new long[] { 1, 7, 12, 123, 1234, 98765, int.MaxValue })
        {
            var key = SpectatorChunk.NewChunkKey();
            TestAssert.True(key.Length == 16 && key.All(char.IsAsciiLetterOrDigit), "16 letters and digits: " + key);
            var credential = SpectatorChunk.WrapKey(gameId, key);
            TestAssert.Equal(32, credential.Length, "24 bytes in base64");
            TestAssert.Equal(key, Encoding.ASCII.GetString(Client.ChunkKey(credential, gameId)), $"game {gameId}: the client unwraps the chunk key");
            var opened = Client.Open(SpectatorChunk.Seal(block, key), Client.ChunkKey(credential, gameId));
            TestAssert.Equal(Convert.ToHexString(block), Convert.ToHexString(opened), $"game {gameId}: the chunk opens to the block");
        }

        // The on-disk stream: the chunk files open with the key the launch carries.
        var folder = Directory.CreateTempSubdirectory("league-spectate-chunks-").FullName;
        try
        {
            var chunkKey = SpectatorChunk.NewChunkKey();
            var recording = new SpectatorRecording(Path.Combine(folder, SpectatorRecording.FolderName), chunkKey);
            TestAssert.Equal(1, recording.AppendStartup(packets[..2]));
            TestAssert.Equal(2, recording.AppendGame(packets[2..], TimeSpan.FromSeconds(30)));
            recording.End();
            var stream = Path.Combine(folder, SpectatorRecording.FolderName);
            var index = SpectatorRecording.Read(stream);
            TestAssert.Equal("1 True 0;2 False 30000", string.Join(";", index.Chunks.Select(c => $"{c.Id} {c.Startup} {c.DurationMs}")), "the index");
            TestAssert.Equal(2, index.EndChunkId, "the last chunk ends the game");
            var key = Client.ChunkKey(SpectatorChunk.WrapKey(5, chunkKey), 5);
            TestAssert.Equal(2, Client.Unpack(Client.Open(File.ReadAllBytes(SpectatorRecording.ChunkPath(stream, 1)), key)).Count, "chunk 1");
            TestAssert.Equal(packets.Length - 2, Client.Unpack(Client.Open(File.ReadAllBytes(SpectatorRecording.ChunkPath(stream, 2)), key)).Count, "chunk 2");
            TestAssert.Equal(new[] { "chunk-1.bin", "chunk-2.bin", "index.txt" },
                Directory.GetFiles(stream).Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal).ToArray(), "no key or temporary file on disk");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Check("network.spectate-chunk-limits")]
    public static void Limits()
    {
        var key = SpectatorChunk.NewChunkKey();
        var full = new byte[SpectatorChunk.MaxUnpacked];
        full[^1] = 1;
        TestAssert.Equal(SpectatorChunk.MaxUnpacked, Client.Open(SpectatorChunk.Seal(full, key), Encoding.ASCII.GetBytes(key)).Length, "1 MiB fits the client's buffer");
        TestAssert.Throws<ArgumentException>(() => SpectatorChunk.Seal(new byte[SpectatorChunk.MaxUnpacked + 1], key), "a byte more does not");
        TestAssert.Throws<ArgumentException>(() => SpectatorChunk.Seal([], key), "an empty chunk");
        TestAssert.Throws<ArgumentException>(() => SpectatorChunk.Seal([1], "short"), "a key of other than 16 characters");
        TestAssert.Throws<ArgumentException>(() => SpectatorChunk.Seal([1], "with a space 123"), "a key with a space");

        TestAssert.Throws<ArgumentException>(() => SpectatorChunk.Pack([new SpectatorPacket(0, 0, [1, 2, 3, 4])]), "under the 5-byte header");
        TestAssert.Throws<ArgumentException>(() => SpectatorChunk.Pack([Packet(0, 0, 1, 0, SpectatorChunk.MaxPayload + 1)]), "packetDataSize < 100000");
        TestAssert.Throws<ArgumentException>(() => SpectatorChunk.Pack([Packet(0, 16, 1, 0, 0)]), "the channel nibble");
        TestAssert.Throws<ArgumentException>(() => SpectatorChunk.Pack([Packet(float.NaN, 0, 1, 0, 0)]), "a time");
        TestAssert.True(SpectatorChunk.Pack([Packet(0, 0, 1, 0, 300), Packet(9, 0, 2, 9, 300)]).Length <= 2 * (305 + SpectatorChunk.MaxOverhead),
            "a block is never longer than its packets plus MaxOverhead each");

        TestAssert.Throws<ArgumentOutOfRangeException>(() => SpectatorChunk.WrapKey(0, key), "game id 0");
        TestAssert.Throws<ArgumentOutOfRangeException>(() => SpectatorChunk.WrapKey(int.MaxValue + 1L, key), "an id _ltoa cannot print");

        var folder = Directory.CreateTempSubdirectory("league-spectate-limits-").FullName;
        try
        {
            var recording = new SpectatorRecording(folder, key);
            TestAssert.Throws<InvalidOperationException>(recording.End, "no end before a chunk");
            recording.AppendGame([Packet(0, 0, 1, 0, 0)], TimeSpan.FromSeconds(1));
            TestAssert.Throws<InvalidOperationException>(() => recording.AppendStartup([Packet(0, 0, 1, 0, 0)]), "start-up after a game chunk");
            recording.End();
            TestAssert.Throws<InvalidOperationException>(() => recording.AppendGame([Packet(0, 0, 1, 0, 0)], TimeSpan.Zero), "a chunk after the end");
            TestAssert.Throws<IOException>(() => new SpectatorRecording(folder, key), "a folder that holds a stream");

            File.AppendAllText(Path.Combine(folder, "index.txt"), "2 game 1000 17\n");
            TestAssert.Throws<InvalidDataException>(() => SpectatorRecording.Read(folder), "a line after the end");
            File.WriteAllText(Path.Combine(folder, "index.txt"), "1 game 1000 17\n2 game 5 1");
            TestAssert.Equal(1, SpectatorRecording.Read(folder).Chunks.Count, "a line still being written waits for its line feed");
            File.WriteAllText(Path.Combine(folder, "index.txt"), "1 game 1000 17\n2 startup 0 18\n");
            TestAssert.Throws<InvalidDataException>(() => SpectatorRecording.Read(folder), "a start-up chunk after a game chunk");
            File.WriteAllText(Path.Combine(folder, "index.txt"), "2 game 1000 17\n");
            TestAssert.Throws<InvalidDataException>(() => SpectatorRecording.Read(folder), "ids from 1, in order");
            TestAssert.Equal(0, SpectatorRecording.Read(Path.Combine(folder, "missing")).Chunks.Count, "no folder yet: no chunk");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static SpectatorPacket Packet(float time, byte channel, byte type, int param, int payload)
    {
        var data = new byte[5 + payload];
        data[0] = type;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(1), param);
        for (var i = 0; i < payload; i++) data[5 + i] = (byte)(0xAA + i);
        return new SpectatorPacket(time, channel, data);
    }

    private static string Encrypt(string key)
    {
        var engine = new BlowfishEngine();
        engine.Init(true, new KeyParameter(Encoding.ASCII.GetBytes(key)));
        var block = new byte[8];
        engine.ProcessBlock(block, 0, block, 0);
        return Convert.ToHexString(block);
    }

    /// <summary>The spectator client's side, from its code (batch 847's recorder checks read the stream with it too).</summary>
    internal static class Client
    {
        /// <summary>
        /// Main: base64-decode the key, key Blowfish with <c>_ltoa(gameId)</c> (strlen bytes; the client cycles
        /// them, so the longest whole repetition up to Blowfish's 56 bytes gives the same schedule), decrypt three blocks, keep 16 bytes.
        /// </summary>
        public static byte[] ChunkKey(string credential, long gameId)
        {
            TestAssert.True(credential.Length > 16, "strnlen(key, 0x11) > 16");
            var decoded = Convert.FromBase64String(credential);
            var digits = Encoding.ASCII.GetBytes(((int)gameId).ToString(CultureInfo.InvariantCulture));
            var key = new byte[56 / digits.Length * digits.Length];
            for (var i = 0; i < key.Length; i++) key[i] = digits[i % digits.Length];
            var engine = new BlowfishEngine();
            engine.Init(false, new KeyParameter(key));
            var buffer = decoded[..24];
            for (var offset = 0; offset < 24; offset += 8) engine.ProcessBlock(buffer, offset, buffer, offset);
            return buffer[..16];
        }

        /// <summary>GetChunk: decrypt the whole 8-byte blocks, gunzip into 1 MiB.</summary>
        public static byte[] Open(byte[] body, byte[] chunkKey)
        {
            var engine = new BlowfishEngine();
            engine.Init(false, new KeyParameter(chunkKey));
            var plain = body.ToArray();
            for (var offset = 0; offset + 8 <= plain.Length; offset += 8) engine.ProcessBlock(plain, offset, plain, offset);
            using var gzip = new GZipStream(new MemoryStream(plain), CompressionMode.Decompress);
            var output = new byte[0x100000 + 1];
            var count = 0;
            int read;
            while (count < output.Length && (read = gzip.Read(output, count, output.Length - count)) > 0) count += read;
            TestAssert.True(count <= 0x100000, "the chunk fits the client's 1 MiB buffer");
            return output[..count];
        }

        /// <summary>the client (packing on), one packet per call until the block ends.</summary>
        public static List<(float Time, int Channel, byte[] Packet, bool DeltaTime)> Unpack(byte[] block)
        {
            var packets = new List<(float, int, byte[], bool)>();
            float time = 0;
            byte type = 0;
            var param = 0;
            var at = 0;
            while (at < block.Length)
            {
                var marker = block[at++];
                if ((marker & 0x80) != 0) time = block[at++] * 0.001f + time;
                else { time = BitConverter.ToSingle(block, at); at += 4; }
                int length;
                if ((marker & 0x10) != 0) length = block[at++];
                else { length = BitConverter.ToInt32(block, at); at += 4; }
                if ((marker & 0x40) == 0) type = block[at++];
                if ((marker & 0x20) != 0) param = unchecked((sbyte)block[at++] + param);
                else { param = BitConverter.ToInt32(block, at); at += 4; }
                TestAssert.True(length is >= 0 and < 100000, "packetDataSize >= 0 && packetDataSize < 100000");
                var packet = new byte[length + 5];
                packet[0] = type;
                BitConverter.TryWriteBytes(packet.AsSpan(1), param);
                block.AsSpan(at, length).CopyTo(packet.AsSpan(5));
                at += length;
                packets.Add((time, marker & 0x0F, packet, (marker & 0x80) != 0));
            }
            return packets;
        }
    }
}
