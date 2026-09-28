using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace Thalovant
{
    /// <summary>
    /// A binary frame: the bytes a hub sent, and what it said about them.
    ///
    /// A hub answers <c>speak:synth</c> by rendering the utterance and sending
    /// one of these back, so a client with no synthesiser of its own can still
    /// speak; a file arrives the same way.
    /// </summary>
    public sealed class ThalovantBinary
    {
        /// <summary>Payload types a BINARY frame can carry, by wire number.</summary>
        public static readonly IReadOnlyDictionary<int, string> PayloadKinds = new Dictionary<int, string>
        {
            [1] = "raw_audio",
            [2] = "numpy_image",
            [3] = "file",
            [4] = "stt_transcribe",
            [5] = "stt_handle",
            [6] = "tts_audio",
        };

        /// <summary>
        /// Names a payload type. One nobody has named still arrives, under its
        /// number, rather than being dropped.
        /// </summary>
        public static string KindName(int wireNumber) =>
            PayloadKinds.TryGetValue(wireNumber, out var name) ? name : "binary:" + wireNumber;

        /// <summary><c>tts_audio</c>, <c>file</c>, ... or <c>binary:&lt;wire number&gt;</c>.</summary>
        public string Kind { get; }

        /// <summary>The payload itself. Never parsed, never decompressed.</summary>
        public byte[] Data { get; }

        /// <summary>What the hub sent beside it.</summary>
        public JsonObject Metadata { get; }

        /// <summary>What was said, when this is rendered speech.</summary>
        public string? Utterance { get; }

        /// <summary>The language it was said in.</summary>
        public string? Lang { get; }

        /// <summary>The name a file arrived under. An empty name is no name.</summary>
        public string? FileName { get; }

        /// <summary>
        /// Reads a hub's metadata into the shape above. A value the hub did not
        /// send and one it sent empty both read as null: rendering "" as a
        /// filename would put a blank name in front of somebody as though the
        /// hub had chosen it.
        /// </summary>
        public ThalovantBinary(string kind, byte[] data, JsonObject metadata)
        {
            Kind = kind;
            Data = data;
            Metadata = metadata;
            string? Text(string key)
            {
                var value = JsonUtil.GetString(metadata[key]);
                return string.IsNullOrEmpty(value) ? null : value;
            }
            Utterance = Text("utterance");
            Lang = Text("lang");
            FileName = Text("file_name");
        }
    }

    /// <summary>
    /// Reads WIRE-1 frames a bit at a time.
    ///
    /// The layout is leading zero padding, a single <c>1</c> bit, one bit saying
    /// whether a version follows, the version if so, five bits of message type,
    /// one bit of compression, eight bits of metadata length, that many metadata
    /// bytes, and then -- for BINARY alone -- four bits naming the payload type
    /// before the clip. The padding goes on the front, so the clip starts
    /// bit-misaligned and cannot be sliced out at a byte boundary.
    /// </summary>
    internal sealed class HiveBitReader
    {
        private readonly byte[] _bytes;
        private int _offset;

        internal HiveBitReader(byte[] bytes) { _bytes = bytes; }

        internal void SkipLeftPadding()
        {
            while (_offset < _bytes.Length * 8) {
                if (ReadBit() == 1) return;
            }
            throw new ThalovantConnectionException("HiveMind binary frame is all padding.");
        }

        internal int ReadBit()
        {
            if (_offset >= _bytes.Length * 8) throw new ThalovantConnectionException("Unexpected end of HiveMind binary frame.");
            var bit = (_bytes[_offset / 8] >> (7 - (_offset % 8))) & 1;
            _offset++;
            return bit;
        }

        internal int ReadUInt(int width)
        {
            var value = 0;
            for (var index = 0; index < width; index++) value = (value << 1) | ReadBit();
            return value;
        }

        internal byte[] ReadBytes(int count)
        {
            var out_ = new byte[count];
            for (var index = 0; index < count; index++) out_[index] = (byte)ReadUInt(8);
            return out_;
        }

        internal byte[] ReadRemainingBytes()
        {
            using var buffer = new MemoryStream();
            while (_bytes.Length * 8 - _offset >= 8) buffer.WriteByte((byte)ReadUInt(8));
            return buffer.ToArray();
        }
    }

    public static partial class HiveWire
    {
        private static readonly IReadOnlyDictionary<int, string> TypeCodes = new Dictionary<int, string>
        {
            [0] = "shake", [1] = "bus", [2] = "shared_bus", [3] = "broadcast", [4] = "propagate",
            [5] = "escalate", [6] = "hello", [7] = "query", [8] = "cascade", [9] = "ping",
            [10] = "rendezvous", [11] = "3rdparty", [12] = "bin",
        };

        /// <summary>
        /// Decodes a WIRE-1 binary frame.
        ///
        /// A BINARY frame does not carry JSON: four bits name the payload type
        /// and everything after them is the clip, raw and never inflated. Every
        /// other type binarized on the wire is still JSON and decodes as it
        /// always did.
        /// </summary>
        public static HiveMessage DecodeBinaryFrame(byte[] data)
        {
            var reader = new HiveBitReader(data);
            reader.SkipLeftPadding();
            if (reader.ReadBit() == 1) {
                var version = reader.ReadUInt(8);
                if (version > 1) throw new ThalovantConnectionException($"Unsupported HiveMind binary frame version: {version}.");
            }
            var typeCode = reader.ReadUInt(5);
            var compressed = reader.ReadBit() == 1;
            var metadataBytes = reader.ReadBytes(reader.ReadUInt(8));
            var msgType = TypeCodes.TryGetValue(typeCode, out var named) ? named : "3rdparty";
            // Metadata is optional, so an unreadable block reads as absent.
            var metadata = DecodeWireObject(metadataBytes, compressed, required: false);
            if (msgType == "bin") {
                var kind = reader.ReadUInt(4);
                return HiveMessage.WithBinary(msgType, metadata,
                    new ThalovantBinary(ThalovantBinary.KindName(kind), reader.ReadRemainingBytes(), metadata));
            }
            // The payload is not. A binarized bus frame whose body will not
            // decode is a malformed frame, and turning it into {} handed bus
            // handlers an empty event instead -- the text path rejects exactly
            // the same bytes.
            return new HiveMessage(msgType,
                payload: DecodeWireObject(reader.ReadRemainingBytes(), compressed, required: true),
                metadata: metadata);
        }

        /// <summary>
        /// The most a compressed payload may inflate to: 32 MiB, what a
        /// reassembled Noise message may hold (<c>NoiseSession.MaximumMessage</c>)
        /// and what the reference allows. Beyond it the frame is refused.
        /// </summary>
        internal const int MaxInflated = 32 * 1024 * 1024;

        /// <summary>
        /// The most a compressed metadata block may inflate to. It is at most 255
        /// bytes on the wire, which no zlib stream inflates past this, so reaching
        /// it means the block is not metadata.
        /// </summary>
        internal const int MaxInflatedMetadata = 1 << 20;

        /// <summary>
        /// Copy at most <paramref name="limit"/> bytes, then give up. A
        /// compressed block that inflates beyond its limit is a bomb, not a
        /// frame, and an unbounded copy would follow it until the process ran
        /// out of memory.
        /// </summary>
        private static void CopyBounded(Stream source, Stream destination, int limit)
        {
            var chunk = new byte[8192];
            var total = 0;
            int read;
            while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > limit) throw new InvalidDataException("the compressed block inflates past its size limit");
                destination.Write(chunk, 0, read);
            }
        }

        /// <summary>
        /// Inflates one zlib stream (RFC 1950) to at most <paramref name="limit"/>
        /// bytes, and refuses one that is truncated or corrupt.
        /// </summary>
        /// <remarks>
        /// .NET's inflaters return what they have when the input runs out, and
        /// read a stream with its checksum cut off as whole, so the header and
        /// the Adler-32 trailer are checked here: a stream that ends early does
        /// not end in the checksum of what it inflated to.
        /// </remarks>
        internal static byte[] Inflate(byte[] bytes, int limit)
        {
            // The shortest zlib stream -- a header, an empty final block, a
            // checksum -- is 8 bytes.
            if (bytes.Length < 8) throw new InvalidDataException("the compressed block is too short to be a zlib stream");
            if ((bytes[0] & 0x0F) != 8 || (bytes[0] >> 4) > 7 || ((bytes[0] << 8) | bytes[1]) % 31 != 0 || (bytes[1] & 0x20) != 0)
                throw new InvalidDataException("the compressed block has no zlib header");
            using var buffer = new MemoryStream();
            // The header is checked above, so what follows it is raw DEFLATE and
            // the four bytes after that its checksum; netstandard2.1 has no
            // ZLibStream, and one inflater on every target keeps them alike.
            using (var source = new MemoryStream(bytes, 2, bytes.Length - 6))
            using (var inflate = new DeflateStream(source, CompressionMode.Decompress))
            {
                CopyBounded(inflate, buffer, limit);
            }
            var data = buffer.ToArray();
            var expected = ((uint)bytes[bytes.Length - 4] << 24) | ((uint)bytes[bytes.Length - 3] << 16)
                | ((uint)bytes[bytes.Length - 2] << 8) | bytes[bytes.Length - 1];
            if (Adler32(data) != expected) throw new InvalidDataException("the compressed block is truncated or corrupt");
            return data;
        }

        private static uint Adler32(byte[] data)
        {
            const uint modulus = 65521;
            uint a = 1, b = 0;
            var index = 0;
            while (index < data.Length)
            {
                // 5552 bytes is the most that can be summed before b overflows.
                var end = Math.Min(index + 5552, data.Length);
                for (; index < end; index++)
                {
                    a += data[index];
                    b += a;
                }
                a %= modulus;
                b %= modulus;
            }
            return (b << 16) | a;
        }

        /// <summary>
        /// A frame's metadata or payload, inflated first when the frame says so.
        /// The encoder chooses per frame whichever of the two is shorter, so a hub
        /// really does send both. A payload that does not inflate -- past
        /// <see cref="MaxInflated"/>, truncated or corrupt -- refuses the frame;
        /// metadata that does not reads as absent, as unreadable metadata always
        /// has here. The clip of a BINARY frame is never compressed, whatever the
        /// flag says.
        /// </summary>
        private static JsonObject DecodeWireObject(byte[] bytes, bool compressed, bool required)
        {
            if (bytes.Length == 0)
            {
                if (required) throw new ThalovantConnectionException("HiveMind binary frame carries no payload.");
                return new JsonObject();
            }
            var raw = bytes;
            if (compressed)
            {
                try
                {
                    raw = Inflate(bytes, required ? MaxInflated : MaxInflatedMetadata);
                }
                catch (InvalidDataException error)
                {
                    if (required) throw new ThalovantConnectionException($"HiveMind binary payload could not be decompressed: {error.Message}.");
                    return new JsonObject();
                }
            }
            try {
                var parsed = JsonNode.Parse(new UTF8Encoding(false, true).GetString(raw)) as JsonObject;
                if (parsed is null && required) throw new ThalovantConnectionException("HiveMind binary payload is not a JSON object.");
                return parsed ?? new JsonObject();
            } catch (Exception error) when (error is not ThalovantConnectionException) {
                if (required) throw new ThalovantConnectionException($"HiveMind binary payload could not be read: {error.Message}");
                return new JsonObject();
            }
        }
    }
}
