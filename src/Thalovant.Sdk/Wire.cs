using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Thalovant
{
    /// <summary>
    /// A HiveMind wire frame. Field names and null placeholders match the frames
    /// produced by the Node SDK's WSS transport byte-for-byte in structure:
    /// <c>msg_type</c>, <c>payload</c>, <c>metadata</c>, <c>route</c>, <c>node</c>,
    /// <c>target_site_id</c>, <c>target_pubkey</c>, <c>source_peer</c> are always present.
    /// </summary>
    public sealed class HiveMessage
    {
        public string MsgType { get; }
        public JsonObject Payload { get; }
        public JsonObject Metadata { get; }
        public JsonArray Route { get; }
        public string? Node { get; }
        public string? TargetSiteId { get; }
        public string? TargetPubkey { get; }
        public string? SourcePeer { get; }

        public HiveMessage(
            string msgType,
            JsonObject? payload = null,
            JsonObject? metadata = null,
            JsonArray? route = null,
            string? node = null,
            string? targetSiteId = null,
            string? targetPubkey = null,
            string? sourcePeer = null)
        {
            MsgType = msgType;
            Payload = payload ?? new JsonObject();
            Metadata = metadata ?? new JsonObject();
            Route = route ?? new JsonArray();
            Node = node;
            TargetSiteId = targetSiteId;
            TargetPubkey = targetPubkey;
            SourcePeer = sourcePeer;
        }

        /// <summary>Serializes the frame with the full field set and explicit nulls.</summary>
        public JsonObject ToJsonObject()
        {
            return new JsonObject
            {
                ["msg_type"] = MsgType,
                ["payload"] = JsonUtil.CloneObject(Payload),
                ["metadata"] = JsonUtil.CloneObject(Metadata),
                ["route"] = JsonUtil.CloneArray(Route),
                // Explicit nulls: the runtime expects these keys on every frame.
                ["node"] = Node is null ? null : JsonValue.Create(Node),
                ["target_site_id"] = TargetSiteId is null ? null : JsonValue.Create(TargetSiteId),
                ["target_pubkey"] = TargetPubkey is null ? null : JsonValue.Create(TargetPubkey),
                ["source_peer"] = SourcePeer is null ? null : JsonValue.Create(SourcePeer),
            };
        }

        /// <summary>Parses a frame, tolerating missing optional fields.</summary>
        public static HiveMessage FromJsonObject(JsonObject frame)
        {
            var msgType = JsonUtil.GetString(frame["msg_type"]);
            if (msgType is null)
            {
                throw new ThalovantConnectionException("HiveMind frame is missing msg_type.");
            }
            return new HiveMessage(
                msgType,
                JsonUtil.CloneObject(JsonUtil.AsObject(frame["payload"])),
                JsonUtil.CloneObject(JsonUtil.AsObject(frame["metadata"])),
                JsonUtil.CloneArray(frame["route"] as JsonArray),
                JsonUtil.GetString(frame["node"]),
                JsonUtil.GetString(frame["target_site_id"]),
                JsonUtil.GetString(frame["target_pubkey"]),
                JsonUtil.GetString(frame["source_peer"]));
        }
    }

    /// <summary>Pure encode/decode helpers for the HiveMind WSS wire protocol.</summary>
    public static class HiveWire
    {
        /// <summary>
        /// The <c>authorization</c> credential sent on connect:
        /// <c>base64("&lt;user agent&gt;:&lt;access key&gt;")</c>.
        /// </summary>
        public static string Authorization(string userAgent, string accessKey)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(userAgent + ":" + accessKey));
        }

        /// <summary>Appends the <c>authorization</c> query parameter to a <c>ws://</c>/<c>wss://</c> endpoint.</summary>
        public static Uri AuthorizedEndpoint(string endpoint, string authorization)
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                || (!string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ThalovantConnectionException("WSS endpoint must start with ws:// or wss://.");
            }
            var query = uri.Query.StartsWith("?", StringComparison.Ordinal) ? uri.Query.Substring(1) : uri.Query;
            var kept = new List<string>();
            foreach (var pair in query.Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var name = pair.Split(new[] { '=' }, 2)[0];
                if (!string.Equals(Uri.UnescapeDataString(name), "authorization", StringComparison.Ordinal))
                {
                    kept.Add(pair);
                }
            }
            kept.Add("authorization=" + Uri.EscapeDataString(authorization));
            var builder = new UriBuilder(uri) { Query = string.Join("&", kept) };
            return builder.Uri;
        }

        /// <summary>The <c>hello</c> frame answering a preshared-key handshake.</summary>
        public static HiveMessage HelloMessage(string siteId, string? publicKey, string sessionId)
        {
            return new HiveMessage(
                "hello",
                new JsonObject
                {
                    ["pubkey"] = publicKey ?? "",
                    ["session"] = new JsonObject { ["session_id"] = sessionId },
                    ["site_id"] = siteId,
                });
        }

        /// <summary>A <c>bus</c> frame carrying a <c>{type, data, context}</c> event payload.</summary>
        public static HiveMessage BusMessage(string type, JsonObject? data = null, JsonObject? context = null)
        {
            return new HiveMessage(
                "bus",
                new JsonObject
                {
                    ["type"] = type,
                    ["data"] = JsonUtil.CloneObject(data),
                    ["context"] = JsonUtil.CloneObject(context),
                });
        }

        /// <summary>
        /// Serializes a frame for the socket. When <paramref name="encrypt"/> is set and a
        /// crypto key is available the JSON is wrapped in the AES-128-GCM envelope.
        /// </summary>
        public static string Encode(HiveMessage message, string? cryptoKey = null, bool encrypt = false)
        {
            var serialized = message.ToJsonObject().ToJsonString();
            if (!encrypt || cryptoKey is null || ThalovantCrypto.RuntimeKey(cryptoKey) is null)
            {
                return serialized;
            }
            return ThalovantCrypto.EncryptJson(cryptoKey, serialized);
        }

        /// <summary>
        /// Parses an incoming text frame, transparently decrypting
        /// <c>{"ciphertext": ...}</c> envelopes when a crypto key is available.
        /// </summary>
        public static HiveMessage Decode(string text, string? cryptoKey = null)
        {
            JsonObject frame;
            try
            {
                frame = JsonUtil.ParseObject(text);
            }
            catch (JsonException)
            {
                throw new ThalovantConnectionException("HiveMind frame is not valid JSON.");
            }
            if (frame.ContainsKey("ciphertext") && cryptoKey is not null)
            {
                var plaintext = ThalovantCrypto.DecryptJson(cryptoKey, frame);
                try
                {
                    frame = JsonUtil.ParseObject(plaintext);
                }
                catch (JsonException)
                {
                    throw new ThalovantConnectionException("Decrypted HiveMind frame is not valid JSON.");
                }
            }
            return HiveMessage.FromJsonObject(frame);
        }

        /// <summary>Parses an incoming binary frame by treating it as UTF-8 JSON.</summary>
        public static HiveMessage Decode(byte[] data, string? cryptoKey = null)
        {
            string text;
            try
            {
                text = Encoding.UTF8.GetString(data);
            }
            catch (Exception)
            {
                throw new ThalovantConnectionException("HiveMind binary frame is not UTF-8 JSON.");
            }
            return Decode(text, cryptoKey);
        }

        /// <summary>
        /// True when a <c>handshake</c>/<c>shake</c> payload is a preshared-key challenge
        /// (the only handshake style the SDK supports).
        /// </summary>
        public static bool IsPresharedKeyHandshake(JsonObject payload)
        {
            return JsonUtil.IsTruthy(payload["preshared_key"])
                && !JsonUtil.IsTruthy(payload["handshake"])
                && !JsonUtil.IsTruthy(payload["envelope"]);
        }
    }
}
