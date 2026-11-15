using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Thalovant.Sdk.Tests
{
    /// <summary>
    /// A secret-bearing type must not hand its secrets to a serializer that walks
    /// public properties (<c>System.Text.Json.JsonSerializer.Serialize</c>, or a
    /// logger that does it for the caller). The explicit
    /// <c>ToJsonObject(includeSecrets: true)</c> stays the one way to get them.
    /// </summary>
    public class SerializationRedactionTests
    {
        private static readonly string[] Secrets =
        {
            "LEAK-ACCESS-KEY", "LEAK-PASSWORD", "LEAK-CRYPTO", "LEAK-MQTT-USER", "LEAK-MQTT-PASSWORD",
            "LEAK-SPEC-KEY", "LEAK-TOKEN", "LEAK-DEVICE-CODE", "LEAK-VERIFIER", "LEAK-STATE", "LEAK-RAW", "LEAK-MASTER",
        };

        private static BootstrapIdentityResult MakeResult()
        {
            var identity = new ThalovantIdentity((JsonObject)JsonNode.Parse("""
            {
              "access_key": "LEAK-ACCESS-KEY", "password": "LEAK-PASSWORD", "crypto_key": "LEAK-CRYPTO",
              "site_id": "bridge", "default_master": "wss://hubuser:LEAK-MASTER@home.example.com", "default_port": 443,
              "mqtt": { "endpoint": "mqtts://m.example.com:8883", "username": "LEAK-MQTT-USER", "password": "LEAK-MQTT-PASSWORD" },
              "metadata": { "password": "LEAK-RAW" }
            }
            """)!);
            var hub = (JsonObject)JsonNode.Parse("""{ "id": "hub-1", "domain": "home.example.com" }""")!;
            var client = (JsonObject)JsonNode.Parse("""
            { "id": "client-1", "spec": { "connection_type": "connector", "connectors": ["jellyfin"], "apiKey": "LEAK-SPEC-KEY" },
              "initial_identify": { "access_key": "LEAK-ACCESS-KEY", "password": "LEAK-PASSWORD" },
              "initial_identify_token": "LEAK-TOKEN" }
            """)!;
            return new BootstrapIdentityResult(identity, hub, client, endpoint: null);
        }

        private static void AssertClean(string label, string text)
        {
            foreach (var secret in Secrets)
            {
                Assert.False(text.Contains(secret, StringComparison.Ordinal), $"{label} printed {secret}: {text}");
            }
        }

        [Fact]
        public void SerializingACreateResultOrItsIdentityNeverWritesASecret()
        {
            var result = MakeResult();
            AssertClean("result", JsonSerializer.Serialize(result));
            AssertClean("result as object", JsonSerializer.Serialize<object>(result));
            AssertClean("wrapped", JsonSerializer.Serialize(new { result }));
            AssertClean("list", JsonSerializer.Serialize(new List<object> { result }));
            AssertClean("identity", JsonSerializer.Serialize(result.Identity));
            AssertClean("mqtt", JsonSerializer.Serialize(result.Identity.Mqtt));
            AssertClean("indented", JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, IncludeFields = true }));
        }

        [Fact]
        public void TheNonSecretMembersOfAResultStillSerialize()
        {
            var json = JsonSerializer.Serialize(MakeResult());
            Assert.Contains("client-1", json, StringComparison.Ordinal);
            Assert.Contains("connector", json, StringComparison.Ordinal);
        }

        [Fact]
        public void TheExplicitFormsStillCarryTheSecretsOnRequest()
        {
            var result = MakeResult();
            Assert.Equal("LEAK-ACCESS-KEY", result.Identity.AccessKey);
            Assert.Contains("LEAK-ACCESS-KEY", result.ToJsonObject(includeSecrets: true).ToJsonString(), StringComparison.Ordinal);
            AssertClean("ToJsonObject()", result.ToJsonObject().ToJsonString());
        }

        [Fact]
        public void SerializingAControlPlaneOrASignInNeverWritesTheToken()
        {
            var api = new ThalovantControlPlane("https://api.example.com", "LEAK-TOKEN");
            AssertClean("control plane", JsonSerializer.Serialize(api));
            Assert.Equal("LEAK-TOKEN", api.AccessToken);

            var device = new DeviceAuthorization("LEAK-DEVICE-CODE", "ABCD-EFGH", "https://example.com/activate", null, 600, TimeSpan.FromSeconds(5),
                new JsonObject { ["device_code"] = "LEAK-DEVICE-CODE" });
            AssertClean("device authorization", JsonSerializer.Serialize(device));
            Assert.Contains("ABCD-EFGH", JsonSerializer.Serialize(device), StringComparison.Ordinal);

            var login = new DeviceLoginResult("LEAK-TOKEN", "bearer", new[] { "hubs:read" }, null, "tok-1",
                new JsonObject { ["access_token"] = "LEAK-TOKEN" });
            AssertClean("device login result", JsonSerializer.Serialize(login));
            Assert.Contains("tok-1", JsonSerializer.Serialize(login), StringComparison.Ordinal);
        }
    }
}
