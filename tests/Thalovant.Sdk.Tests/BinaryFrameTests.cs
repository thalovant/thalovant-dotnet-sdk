using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;
namespace Thalovant.Sdk.Tests;

/// <summary>
/// Binary frames, against the vectors and frames every SDK shares.
/// </summary>
/// <remarks>
/// A hub answers speak:synth by rendering the utterance and sending the audio
/// back, so a client with no synthesiser of its own can still speak; a file
/// arrives the same way. The expectations are binary-vectors.json and the
/// frames themselves are binary-frames.json -- hivemind-bus-client's own
/// encoder output, so this is tested against the wire a hub actually puts out
/// rather than against a reading of the specification.
/// </remarks>
public sealed class BinaryFrameTests {
    private static JsonNode Vectors(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)))!;

    private static byte[] Frame(string name) {
        var row = Vectors("binary-frames.json")["cases"]!.AsArray()
            .First(item => (string?)item!["name"] == name)!;
        return Convert.FromBase64String((string)row["frame"]!);
    }

    [Fact] public void ThePayloadKindsAreTheOnesTheVectorsName() {
        var named = Vectors("binary-vectors.json")["payload_kinds"]!.AsObject();
        Assert.Equal(named.Count, ThalovantBinary.PayloadKinds.Count);
        foreach (var pair in named) {
            Assert.Equal((string)pair.Value!, ThalovantBinary.KindName(int.Parse(pair.Key)));
        }
    }

    [Fact] public void APayloadTypeNobodyNamedArrivesUnderItsNumber() {
        // Only 0-15 can travel: the wire field is four bits. The naming has to
        // hold for every number all the same -- it is the last thing between a
        // payload type nobody has named yet and a frame that disappears.
        foreach (var pair in Vectors("binary-vectors.json")["unnamed_kind_names"]!.AsObject()) {
            Assert.Equal((string)pair.Value!, ThalovantBinary.KindName(int.Parse(pair.Key)));
        }
    }

    [Fact] public void TheReferenceEncodersFramesDecodeHere() {
        foreach (var item in Vectors("binary-frames.json")["cases"]!.AsArray()) {
            var row = item!;
            var name = (string)row["name"]!;
            var message = HiveWire.DecodeBinaryFrame(Convert.FromBase64String((string)row["frame"]!));
            Assert.Equal("bin", message.MsgType);
            Assert.NotNull(message.Binary);
            Assert.Equal((string)row["expected_kind"]!, message.Binary!.Kind);
            Assert.Equal(Convert.FromBase64String((string)row["expected_payload"]!), message.Binary.Data);
            foreach (var pair in row["expected_metadata"]!.AsObject()) {
                Assert.Equal((string?)pair.Value, JsonUtil.GetString(message.Binary.Metadata[pair.Key]));
            }
            Assert.False(string.IsNullOrEmpty(name));
        }
    }

    [Fact] public void ABinarizedBusFrameIsStillText() {
        // Only BINARY carries bytes; every other type binarized on the wire is
        // JSON and has to keep decoding as it always did.
        var raw = Convert.FromBase64String((string)Vectors("binary-frames.json")["bus_frame"]!);
        var message = HiveWire.DecodeBinaryFrame(raw);
        Assert.Equal("bus", message.MsgType);
        Assert.Null(message.Binary);
        Assert.Equal("speak", JsonUtil.GetString(message.Payload["type"]));
    }

    [Fact] public void EveryCaseTheVectorsDescribeDecodesAsItSays() {
        foreach (var item in Vectors("binary-vectors.json")["cases"]!.AsArray()) {
            var row = item!;
            var name = (string)row["name"]!;
            var binary = HiveWire.DecodeBinaryFrame(Frame(name)).Binary;
            Assert.NotNull(binary);
            // Recorded before the assert, for the same reason as the carry.
            // Absent is already null here, so nothing needs the translation the
            // Go recorder gives its empty strings.
            ConformanceRecord.Record("binary-vectors.json", name, new JsonObject {
                ["kind"] = binary!.Kind,
                ["utterance"] = binary.Utterance,
                ["lang"] = binary.Lang,
                ["file_name"] = binary.FileName,
            });
            var expected = row["expected"]!;
            Assert.Equal((string)expected["kind"]!, binary!.Kind);
            // An empty name is no name: rendering "" would put a blank filename
            // in front of somebody as though the hub had chosen it.
            Assert.Equal((string?)expected["utterance"], binary.Utterance);
            Assert.Equal((string?)expected["lang"], binary.Lang);
            Assert.Equal((string?)expected["file_name"], binary.FileName);
        }
    }

    [Fact] public void EveryKindTheMeshVectorsDeclareHasACase() {
        // A declared kind with no case is a rule written down and never checked.
        var spec = Vectors("mesh-vectors.json");
        var covered = spec["cases"]!.AsArray().Select(row => (string)row!["kind"]!).ToHashSet();
        foreach (var key in new[] { "kinds", "refused_kinds" }) {
            foreach (var kind in spec[key]!.AsArray()) {
                Assert.Contains((string)kind!, covered);
            }
        }
    }

    [Fact] public void OnlyTheMeshKindsAreSubscribable() {
        foreach (var item in Vectors("mesh-vectors.json")["cases"]!.AsArray()) {
            var row = item!;
            var accepted = (bool)row["expected"]!["accepted"]!;
            Assert.Equal(accepted, ThalovantContext.HiveKinds.Contains((string)row["kind"]!));
        }
    }

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    /// <summary>A compressed bus frame, laid out as hivemind-bus-client lays one out.</summary>
    private static byte[] CompressedBusFrame(byte[] payload)
    {
        var metadata = Zlib(System.Text.Encoding.UTF8.GetBytes("{}"));
        // The marker bit, no version, type 1 (bus), compressed; then the metadata's length.
        return new byte[] { 0x80 | (1 << 1) | 1, (byte)metadata.Length }.Concat(metadata).Concat(payload).ToArray();
    }

    private static byte[] BusPayload(int padding) =>
        System.Text.Encoding.UTF8.GetBytes("{\"type\":\"big\",\"data\":{\"pad\":\"" + new string('a', padding) + "\"},\"context\":{}}");

    [Fact]
    public void ACompressedPayloadMayInflateTo32MiB()
    {
        // Past the 1 MiB this SDK allowed before 0.9.1, within the reference's cap.
        var message = HiveWire.DecodeBinaryFrame(CompressedBusFrame(Zlib(BusPayload(2 * 1024 * 1024))));
        Assert.Equal("big", (string?)message.Payload["type"]);
        Assert.Equal(2 * 1024 * 1024, ((string)message.Payload["data"]!["pad"]!).Length);
    }

    [Fact]
    public void APayloadThatInflatesPast32MiBIsRefused()
    {
        var bomb = Zlib(BusPayload(HiveWire.MaxInflated));
        Assert.True(bomb.Length < 256 * 1024); // small on the wire, which is the point
        var error = Assert.Throws<ThalovantConnectionException>(() => HiveWire.DecodeBinaryFrame(CompressedBusFrame(bomb)));
        Assert.Contains("size limit", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)] // the checksum cut short
    [InlineData(4)] // the checksum gone
    [InlineData(40)] // the stream itself cut short
    public void ATruncatedPayloadIsRefused(int cut)
    {
        var payload = Zlib(BusPayload(4096));
        var frame = CompressedBusFrame(payload.Take(payload.Length - cut).ToArray());
        Assert.Throws<ThalovantConnectionException>(() => HiveWire.DecodeBinaryFrame(frame));
    }

    [Theory]
    [InlineData(4)] // the checksum gone
    [InlineData(-1)] // the checksum corrupt
    public void CompressedMetadataThatWillNotInflateRefusesTheFrame(int damage)
    {
        // As the reference does: it read as absent here before 0.9.1, which
        // delivered a clip with no language and no name instead of refusing it.
        var metadata = Zlib(System.Text.Encoding.UTF8.GetBytes("{\"lang\":\"en-US\"}"));
        metadata = damage > 0 ? metadata.Take(metadata.Length - damage).ToArray() : metadata;
        if (damage < 0) metadata[^1] ^= 1;
        // The marker bit, no version, type 12 (bin), compressed; the metadata; then kind 0 and a clip.
        var frame = new byte[] { 0x80 | (12 << 1) | 1, (byte)metadata.Length }.Concat(metadata).Concat(new byte[] { 0x0F, 0xF0 }).ToArray();
        var error = Assert.Throws<ThalovantConnectionException>(() => HiveWire.DecodeBinaryFrame(frame));
        Assert.Contains("metadata could not be decompressed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompressedMetadataThatInflatesIsRead()
    {
        var metadata = Zlib(System.Text.Encoding.UTF8.GetBytes("{\"lang\":\"en-US\"}"));
        var frame = new byte[] { 0x80 | (12 << 1) | 1, (byte)metadata.Length }.Concat(metadata).Concat(new byte[] { 0x0F, 0xF0 }).ToArray();
        var message = HiveWire.DecodeBinaryFrame(frame);
        Assert.Equal("en-US", (string?)message.Metadata["lang"]);
    }

    [Fact]
    public void ACorruptChecksumIsRefused()
    {
        var payload = Zlib(BusPayload(4096));
        payload[^1] ^= 1;
        Assert.Throws<ThalovantConnectionException>(() => HiveWire.DecodeBinaryFrame(CompressedBusFrame(payload)));
    }

    [Fact]
    public void AWholeStreamInflatesExactly()
    {
        foreach (var size in new[] { 1, 5552, 5553, 70_000 })
        {
            var data = Enumerable.Range(0, size).Select(index => (byte)(index * 7)).ToArray();
            Assert.Equal(data, HiveWire.Inflate(Zlib(data), HiveWire.MaxInflated));
        }
        // zlib.compress(b""): the shortest stream there is. (.NET 9 writes nothing at all for no input.)
        Assert.Empty(HiveWire.Inflate(Convert.FromHexString("789c030000000001"), HiveWire.MaxInflated));
    }
}
