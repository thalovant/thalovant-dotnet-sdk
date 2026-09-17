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
}
