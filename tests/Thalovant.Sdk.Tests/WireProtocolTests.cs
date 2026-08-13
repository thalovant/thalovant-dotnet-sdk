using System;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace Thalovant.Sdk.Tests
{
    /// <summary>
    /// Unit tests for the pure encode/decode half of the WSS wire protocol.
    /// (Exercising the socket itself needs a live hub, so it is out of scope.)
    /// </summary>
    public class WireProtocolTests
    {
        [Fact]
        public void AuthorizationIsBase64UserAgentColonAccessKey()
        {
            var token = HiveWire.Authorization("ThalovantDotNetSDK/0.1.0", "access-1");
            Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("ThalovantDotNetSDK/0.1.0:access-1")), token);
        }

        [Fact]
        public void AuthorizedEndpointAppendsQueryParameter()
        {
            var url = HiveWire.AuthorizedEndpoint("wss://hub.example.com/ws", "abc+d=");
            Assert.Equal("wss", url.Scheme);
            Assert.Equal("hub.example.com", url.Host);
            Assert.Equal("/ws", url.AbsolutePath);
            Assert.Contains("authorization=", url.Query, StringComparison.Ordinal);
            // The decoded query value must be the exact credential.
            Assert.Contains("authorization=" + Uri.EscapeDataString("abc+d="), url.Query, StringComparison.Ordinal);
        }

        [Fact]
        public void AuthorizedEndpointReplacesExistingAuthorization()
        {
            var url = HiveWire.AuthorizedEndpoint("wss://hub.example.com/ws?authorization=old&keep=1", "new");
            var query = url.Query;
            Assert.Contains("authorization=new", query, StringComparison.Ordinal);
            Assert.DoesNotContain("authorization=old", query, StringComparison.Ordinal);
            Assert.Contains("keep=1", query, StringComparison.Ordinal);
        }

        [Fact]
        public void AuthorizedEndpointRejectsNonWebSocketSchemes()
        {
            Assert.Throws<ThalovantConnectionException>(() => HiveWire.AuthorizedEndpoint("https://hub.example.com", "x"));
        }

        [Fact]
        public void HelloMessageShape()
        {
            var hello = HiveWire.HelloMessage("site-1", publicKey: null, sessionId: "thalovant-dotnet-abc");
            var text = HiveWire.Encode(hello);
            var frame = (JsonObject)JsonNode.Parse(text)!;
            Assert.Equal("hello", (string?)frame["msg_type"]);
            Assert.Equal("", (string?)frame["payload"]!["pubkey"]);
            Assert.Equal("site-1", (string?)frame["payload"]!["site_id"]);
            Assert.Equal("thalovant-dotnet-abc", (string?)frame["payload"]!["session"]!["session_id"]);
            // Every frame carries the full HiveMind field set, with explicit nulls.
            Assert.Empty((JsonObject)frame["metadata"]!);
            Assert.Empty((JsonArray)frame["route"]!);
            foreach (var key in new[] { "node", "target_site_id", "target_pubkey", "source_peer" })
            {
                Assert.True(frame.ContainsKey(key), $"expected explicit null for {key}");
                Assert.Null(frame[key]);
            }
        }

        [Fact]
        public void BusMessageShape()
        {
            var message = HiveWire.BusMessage(
                ThalovantEvents.RecognizerLoopUtterance,
                ThalovantContext.UtterancePayload("hello hub", "en-us"),
                ThalovantContext.WithCorrelation(null, sessionId: "s-1", siteId: "site-1", lang: "en-us", requestId: "r-1"));
            var frame = (JsonObject)JsonNode.Parse(HiveWire.Encode(message))!;
            Assert.Equal("bus", (string?)frame["msg_type"]);
            var payload = (JsonObject)frame["payload"]!;
            Assert.Equal("recognizer_loop:utterance", (string?)payload["type"]);
            var data = (JsonObject)payload["data"]!;
            Assert.Equal("hello hub", (string?)((JsonArray)data["utterances"]!)[0]);
            Assert.Equal("en-us", (string?)data["lang"]);
            var context = (JsonObject)payload["context"]!;
            Assert.Equal("r-1", (string?)context["request_id"]);
            Assert.Equal("r-1", (string?)context["thalovant_request_id"]);
            var session = (JsonObject)context["session"]!;
            Assert.Equal("s-1", (string?)session["session_id"]);
            Assert.Equal("site-1", (string?)session["site_id"]);
            Assert.Equal("r-1", (string?)session["request_id"]);
            Assert.Equal("en-us", (string?)session["lang"]);
        }

        [Fact]
        public void DecodePlaintextFrame()
        {
            var text = """
            {"msg_type": "handshake", "payload": {"preshared_key": true}, "route": [], "node": null}
            """;
            var message = HiveWire.Decode(text);
            Assert.Equal("handshake", message.MsgType);
            Assert.True((bool?)message.Payload["preshared_key"]);
            Assert.Null(message.Node);
        }

        [Fact]
        public void EncryptedFrameRoundTrip()
        {
            var key = "0123456789abcdefextra";
            var original = HiveWire.BusMessage(
                "speak",
                new JsonObject { ["utterance"] = "hi" },
                new JsonObject { ["session"] = new JsonObject { ["session_id"] = "s-1" } });
            var wireText = HiveWire.Encode(original, cryptoKey: key, encrypt: true);
            // The frame on the wire is an encrypted envelope, not plaintext JSON.
            var envelope = (JsonObject)JsonNode.Parse(wireText)!;
            Assert.True(envelope.ContainsKey("ciphertext"));
            Assert.True(envelope.ContainsKey("tag"));
            Assert.True(envelope.ContainsKey("nonce"));
            Assert.False(envelope.ContainsKey("msg_type"));

            var decoded = HiveWire.Decode(wireText, key);
            Assert.Equal("bus", decoded.MsgType);
            Assert.True(JsonNode.DeepEquals(original.ToJsonObject(), decoded.ToJsonObject()));
        }

        [Fact]
        public void EncodeWithoutKeyStaysPlaintext()
        {
            var message = HiveWire.BusMessage("speak");
            var text = HiveWire.Encode(message, cryptoKey: null, encrypt: true);
            Assert.Equal("bus", (string?)JsonNode.Parse(text)!["msg_type"]);
        }

        [Fact]
        public void DecodeBinaryFrame()
        {
            var text = """{"msg_type": "bus", "payload": {"type": "speak", "data": {"utterance": "hi"}}}""";
            var message = HiveWire.Decode(Encoding.UTF8.GetBytes(text));
            Assert.Equal("bus", message.MsgType);
            Assert.Equal("speak", (string?)message.Payload["type"]);
        }

        [Fact]
        public void PresharedKeyHandshakeDetection()
        {
            Assert.True(HiveWire.IsPresharedKeyHandshake(new JsonObject { ["preshared_key"] = true }));
            Assert.True(HiveWire.IsPresharedKeyHandshake(new JsonObject { ["preshared_key"] = "salt" }));
            Assert.False(HiveWire.IsPresharedKeyHandshake(new JsonObject()));
            Assert.False(HiveWire.IsPresharedKeyHandshake(new JsonObject { ["preshared_key"] = false }));
            Assert.False(HiveWire.IsPresharedKeyHandshake(new JsonObject { ["preshared_key"] = null }));
            Assert.False(HiveWire.IsPresharedKeyHandshake(new JsonObject
            {
                ["preshared_key"] = true,
                ["handshake"] = new JsonObject(),
            }));
            Assert.False(HiveWire.IsPresharedKeyHandshake(new JsonObject
            {
                ["preshared_key"] = true,
                ["envelope"] = "x",
            }));
        }

        [Fact]
        public void HiveMessageDecodingToleratesMissingOptionalFields()
        {
            var message = HiveWire.Decode("""{"msg_type": "bus", "payload": {}}""");
            Assert.Empty(message.Metadata);
            Assert.Empty(message.Route);
            Assert.Null(message.TargetSiteId);
        }

        [Fact]
        public void TransportEndpointUriCarriesAuthorization()
        {
            var identity = new ThalovantIdentity(new JsonObject
            {
                ["access_key"] = "access-1",
                ["password"] = "p",
                ["site_id"] = "s",
                ["default_master"] = "https://hub.example.com",
                ["data_plane_endpoints"] = new JsonObject { ["wss"] = "wss://hub.example.com/ws" },
            });
            using var transport = new HiveMindWssTransport(identity, "ThalovantDotNetSDK/0.1.0");
            var url = transport.EndpointUri();
            Assert.Equal("hub.example.com", url.Host);
            Assert.Equal("/ws", url.AbsolutePath);
            var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("ThalovantDotNetSDK/0.1.0:access-1"));
            Assert.Contains("authorization=" + Uri.EscapeDataString(expected), url.Query, StringComparison.Ordinal);
        }
    }
}
