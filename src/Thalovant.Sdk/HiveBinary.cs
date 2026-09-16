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
            var metadata = DecodeWireObject(metadataBytes, compressed);
            if (msgType == "bin") {
                var kind = reader.ReadUInt(4);
                return new HiveMessage(msgType, metadata: metadata,
                    binary: new ThalovantBinary(ThalovantBinary.KindName(kind), reader.ReadRemainingBytes(), metadata));
            }
            return new HiveMessage(msgType, payload: DecodeWireObject(reader.ReadRemainingBytes(), compressed), metadata: metadata);
        }

        /// <summary>
        /// Inflates a zlib stream when the frame says so. The encoder chooses
        /// per frame whichever of the two is shorter, so a hub really does send
        /// both, and a frame whose metadata cannot be read arrives with no
        /// language and no filename beside its audio. The clip itself is never
        /// compressed, whatever the flag says.
        /// </summary>
        private static JsonObject DecodeWireObject(byte[] bytes, bool compressed)
        {
            if (bytes.Length == 0) return new JsonObject();
            var raw = bytes;
            if (compressed) {
                try {
                    using var source = new MemoryStream(bytes);
                    using var inflate = new ZLibStream(source, CompressionMode.Decompress);
                    using var buffer = new MemoryStream();
                    inflate.CopyTo(buffer);
                    raw = buffer.ToArray();
                } catch (InvalidDataException) {
                    return new JsonObject();
                }
            }
            try {
                return JsonNode.Parse(new UTF8Encoding(false, true).GetString(raw)) as JsonObject ?? new JsonObject();
            } catch (Exception) {
                return new JsonObject();
            }
        }
    }
}
