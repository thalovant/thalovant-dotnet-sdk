using System;
using System.IO;
using System.Text.Json.Nodes;

namespace Thalovant
{
    /// <summary>
    /// Client-scoped MQTT broker credentials
    /// (<c>mqtt.{endpoint,username,password,topic_prefix,tls}</c> per the API clients schema).
    /// </summary>
    public sealed class MqttBrokerCredentials
    {
        public string Endpoint { get; }
        public string Username { get; }
        public string Password { get; }
        public string? TopicPrefix { get; }
        public string? HubId { get; }
        public string? C2sTopic { get; }
        public string? S2cTopic { get; }
        public string? StatusTopic { get; }
        public bool HashTopics { get; }
        public int Qos { get; }
        public bool Tls { get; }

        public MqttBrokerCredentials(JsonObject json)
        {
            Endpoint = Required(json, "mqtt.endpoint", "endpoint", "broker_url", "brokerUrl");
            Username = Required(json, "mqtt.username", "username", "broker_username", "brokerUsername");
            Password = Required(json, "mqtt.password", "password", "broker_password", "brokerPassword");
            TopicPrefix = JsonUtil.OptionalString(JsonUtil.First(json, "topic_prefix", "topicPrefix"));
            HubId = JsonUtil.OptionalString(JsonUtil.First(json, "hub_id", "hubId"));
            C2sTopic = JsonUtil.OptionalString(JsonUtil.First(json, "c2s_topic", "c2sTopic"));
            S2cTopic = JsonUtil.OptionalString(JsonUtil.First(json, "s2c_topic", "s2cTopic"));
            StatusTopic = JsonUtil.OptionalString(JsonUtil.First(json, "status_topic", "statusTopic"));
            HashTopics = JsonUtil.EnabledValue(JsonUtil.First(json, "hash_topics", "hashTopics"), fallback: false);
            Qos = JsonUtil.GetInt(json["qos"]) ?? 1;
            Tls = JsonUtil.EnabledValue(json["tls"], fallback: Endpoint.StartsWith("mqtts://", StringComparison.Ordinal));
        }

        private static string Required(JsonObject json, string field, params string[] keys)
        {
            var value = JsonUtil.OptionalString(JsonUtil.First(json, keys));
            if (value is null)
            {
                throw new ThalovantIdentityException($"Missing required identity field: {field}");
            }
            return value;
        }

        public static MqttBrokerCredentials? From(JsonNode? node)
        {
            if (JsonUtil.AsObject(node) is not JsonObject json)
            {
                return null;
            }
            try
            {
                return new MqttBrokerCredentials(json);
            }
            catch (ThalovantIdentityException)
            {
                return null;
            }
        }

        public JsonObject ToJsonObject(bool includeSecrets = false)
        {
            var data = new JsonObject
            {
                ["endpoint"] = Endpoint,
                ["tls"] = Tls,
            };
            if (includeSecrets)
            {
                data["username"] = Username;
                data["password"] = Password;
                if (TopicPrefix is not null)
                {
                    data["topic_prefix"] = TopicPrefix;
                }
                if (HubId is not null)
                {
                    data["hub_id"] = HubId;
                }
                if (C2sTopic is not null)
                {
                    data["c2s_topic"] = C2sTopic;
                }
                if (S2cTopic is not null)
                {
                    data["s2c_topic"] = S2cTopic;
                }
                if (StatusTopic is not null)
                {
                    data["status_topic"] = StatusTopic;
                }
                if (HashTopics)
                {
                    data["hash_topics"] = true;
                }
                if (Qos != 1)
                {
                    data["qos"] = Qos;
                }
            }
            return data;
        }
    }

    /// <summary>
    /// A client identity provisioned by the control plane
    /// (<c>access_key</c>, <c>password</c>, <c>crypto_key</c>, <c>site_id</c>,
    /// <c>default_port</c>, <c>default_master</c>, and optional <c>mqtt</c> credentials
    /// per the API clients schema).
    /// </summary>
    public sealed class ThalovantIdentity
    {
        public string AccessKey { get; }
        public string Password { get; }
        public string DefaultMaster { get; }
        public int DefaultPort { get; }
        public string DefaultPath { get; }
        public string SiteId { get; }
        public string? CryptoKey { get; }
        public HubDataPlaneEndpoints DataPlaneEndpoints { get; }
        public HubProtocolSettings Protocols { get; }
        public string? PublicKey { get; }
        public JsonObject Metadata { get; }
        public MqttBrokerCredentials? Mqtt { get; }

        public ThalovantIdentity(JsonObject json)
        {
            AccessKey = Required(json, "access_key", "access_key", "accessKey", "api_key", "key");
            Password = Required(json, "password", "password");
            var master = Required(json, "default_master", "default_master", "defaultMaster", "hub_http_host", "host", "master");
            SiteId = Required(json, "site_id", "site_id", "siteId", "site");
            DefaultMaster = HubEndpoints.TrimTrailingSlashes(master);
            DefaultPort = PositivePort(JsonUtil.First(json, "default_port", "defaultPort", "hub_http_port", "port"), fallback: 5679);
            DefaultPath = NormalizeIdentityPath(
                JsonUtil.OptionalString(JsonUtil.First(json, "default_path", "defaultPath", "hub_http_path", "path", "uri_path")));
            CryptoKey = JsonUtil.OptionalString(JsonUtil.First(json, "crypto_key", "cryptoKey"));
            DataPlaneEndpoints = HubDataPlaneEndpoints.From(json);
            Protocols = HubProtocolSettings.From(json);
            PublicKey = JsonUtil.OptionalString(JsonUtil.First(json, "public_key", "publicKey"));
            Metadata = JsonUtil.CloneObject(JsonUtil.AsObject(json["metadata"]));
            Mqtt = MqttBrokerCredentials.From(json["mqtt"]);
        }

        /// <summary>Parses an identity from a JSON document.</summary>
        public static ThalovantIdentity FromJson(string json)
        {
            JsonObject parsed;
            try
            {
                parsed = JsonUtil.ParseObject(json);
            }
            catch (Exception)
            {
                throw new ThalovantIdentityException("Identity document is not a valid JSON object.");
            }
            return new ThalovantIdentity(parsed);
        }

        /// <summary>
        /// Loads an identity from a JSON file. On POSIX platforms (net8.0 target) the
        /// file must not be group- or world-accessible; run <c>chmod 600 &lt;path&gt;</c>
        /// first. The check is skipped on Windows and on the netstandard2.1 (Unity)
        /// build, which has no portable file-mode API.
        /// </summary>
        public static ThalovantIdentity FromFile(string path)
        {
            AssertSecureIdentityFile(path);
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new ThalovantIdentityException($"Unable to read identity file: {path}");
            }
            try
            {
                return FromJson(text);
            }
            catch (ThalovantIdentityException) when (!IsValidJsonObject(text))
            {
                throw new ThalovantIdentityException($"Identity file is not valid JSON: {path}");
            }
        }

        private static bool IsValidJsonObject(string text)
        {
            try
            {
                JsonUtil.ParseObject(text);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void AssertSecureIdentityFile(string path)
        {
            if (!File.Exists(path))
            {
                throw new ThalovantIdentityException($"Unable to read identity file: {path}");
            }
#if NET8_0_OR_GREATER
            if (!OperatingSystem.IsWindows())
            {
                const UnixFileMode groupOrWorld = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
                UnixFileMode mode;
                try
                {
                    mode = File.GetUnixFileMode(path);
                }
                catch (Exception)
                {
                    throw new ThalovantIdentityException($"Unable to read identity file: {path}");
                }
                if ((mode & groupOrWorld) != 0)
                {
                    throw new ThalovantIdentityException(
                        $"Identity file is too permissive: {path}. Run `chmod 600 {path}`.");
                }
            }
#endif
        }

        /// <summary>Base URL used by the HTTP(S) data plane for this identity.</summary>
        public string EndpointBase()
        {
            return DataPlaneEndpoints.HttpBase(DefaultMaster, DefaultPort, DefaultPath);
        }

        /// <summary>Endpoint for a protocol, or null when the identity does not expose one.</summary>
        public string? EndpointFor(HubProtocol hubProtocol)
        {
            if (hubProtocol == HubProtocol.Https)
            {
                return EndpointBase();
            }
            var endpoint = DataPlaneEndpoints.EndpointFor(hubProtocol);
            if (endpoint is not null)
            {
                return endpoint;
            }
            if (hubProtocol == HubProtocol.Wss
                && (DefaultMaster.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)
                    || DefaultMaster.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)))
            {
                return DefaultMaster;
            }
            return null;
        }

        public System.Collections.Generic.IReadOnlyList<HubProtocol> EnabledProtocols()
        {
            return Protocols.EnabledProtocols();
        }

        public bool SupportsProtocol(HubProtocol hubProtocol)
        {
            return Protocols.IsEnabled(hubProtocol);
        }

        /// <summary>Serializes the identity; secrets are redacted unless <paramref name="includeSecrets"/> is set.</summary>
        public JsonObject ToJsonObject(bool includeSecrets = false)
        {
            var data = new JsonObject
            {
                ["site_id"] = SiteId,
                ["default_master"] = DefaultMaster,
                ["default_port"] = DefaultPort,
                ["default_path"] = DefaultPath,
            };
            var endpoints = DataPlaneEndpoints.ToJsonObject(redactCredentials: !includeSecrets);
            if (endpoints.Count > 0)
            {
                data["data_plane_endpoints"] = endpoints;
            }
            if (Metadata.Count > 0)
            {
                data["metadata"] = JsonUtil.CloneObject(Metadata);
            }
            if (includeSecrets)
            {
                data["access_key"] = AccessKey;
                data["password"] = Password;
                if (CryptoKey is not null)
                {
                    data["crypto_key"] = CryptoKey;
                }
            }
            if (Mqtt is not null)
            {
                data["mqtt"] = Mqtt.ToJsonObject(includeSecrets);
            }
            return data;
        }

        private static string Required(JsonObject json, string field, params string[] keys)
        {
            var value = JsonUtil.OptionalString(JsonUtil.First(json, keys));
            if (value is null)
            {
                throw new ThalovantIdentityException($"Missing required identity field: {field}");
            }
            return value;
        }

        private static int PositivePort(JsonNode? node, int fallback)
        {
            var parsed = JsonUtil.GetInt(node);
            if (parsed is null && JsonUtil.OptionalString(node) is string text
                && int.TryParse(text, out var fromText))
            {
                parsed = fromText;
            }
            return parsed is int value && value > 0 ? value : fallback;
        }

        private static string NormalizeIdentityPath(string? value)
        {
            var trimmed = value?.Trim('/');
            return string.IsNullOrEmpty(trimmed) ? "" : "/" + trimmed;
        }
    }
}
