using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Thalovant.Sdk.Tests
{
    /// <summary>
    /// Security-hardening regression tests:
    /// <list type="bullet">
    /// <item>F1 — <see cref="BootstrapIdentityResult.ToJsonObject(bool)"/> gates the
    /// passed-through hub/client secrets behind <c>includeSecrets</c>.</item>
    /// <item>F8 — the secret-bearing types stay plain sealed classes whose
    /// <c>ToString()</c> never renders a secret (a future <c>record</c> refactor
    /// fails these tests).</item>
    /// <item>F9 — a failed request's exception message keeps only the status and a
    /// bounded, single-line server detail, never the raw body, while the full body
    /// stays on <see cref="ThalovantApiException.Body"/>.</item>
    /// </list>
    /// </summary>
    public class SecurityTests
    {
        // A POST /v1/clients response carrying every secret the finding enumerates:
        // initial_identify credentials (+ mqtt.password), the bootstrap token, and
        // the echoed spec. Secret values are distinctive so they are easy to find.
        private const string SecretClientJson = """
        {
          "id": "client-1",
          "name": "demo",
          "active": true,
          "initial_identify": {
            "access_key": "CLIENT-ACCESS-KEY",
            "password": "CLIENT-PASSWORD",
            "crypto_key": "CLIENT-CRYPTO-KEY",
            "site_id": "demo",
            "default_master": "https://hub-1.hubs.thalovant.com",
            "default_port": 443,
            "mqtt": {
              "endpoint": "mqtts://mqtt.hub-1.hubs.thalovant.com:8883",
              "username": "mqtt-user",
              "password": "CLIENT-MQTT-PASSWORD",
              "tls": true
            }
          },
          "initial_identify_token": "CLIENT-BOOTSTRAP-TOKEN",
          "spec": {
            "version": "1",
            "siteId": "demo",
            "apiKey": "SPEC-API-KEY",
            "password": "SPEC-PASSWORD",
            "cryptoKey": "SPEC-CRYPTO-KEY"
          }
        }
        """;

        private static readonly string[] ClientSecretValues =
        {
            "CLIENT-ACCESS-KEY", "CLIENT-PASSWORD", "CLIENT-CRYPTO-KEY", "CLIENT-MQTT-PASSWORD",
            "CLIENT-BOOTSTRAP-TOKEN", "SPEC-API-KEY", "SPEC-PASSWORD", "SPEC-CRYPTO-KEY",
        };

        private static BootstrapIdentityResult MakeResult()
        {
            var identity = new ThalovantIdentity((JsonObject)JsonNode.Parse(Fixtures.ClientIdentify)!);
            var hub = (JsonObject)JsonNode.Parse(Fixtures.Hub)!;
            var client = (JsonObject)JsonNode.Parse(SecretClientJson)!;
            return new BootstrapIdentityResult(identity, hub, client, endpoint: null);
        }

        // -- F1 --------------------------------------------------------------

        [Fact]
        public void BootstrapResultRedactsHubAndClientSecretsByDefault()
        {
            var result = MakeResult();

            var redacted = result.ToJsonObject();
            var redactedJson = redacted.ToJsonString();
            foreach (var secret in ClientSecretValues)
            {
                Assert.DoesNotContain(secret, redactedJson, StringComparison.Ordinal);
            }

            var client = (JsonObject)redacted["client"]!;
            var identify = (JsonObject)client["initial_identify"]!;
            Assert.False(identify.ContainsKey("access_key"));
            Assert.False(identify.ContainsKey("password"));
            Assert.False(identify.ContainsKey("crypto_key"));

            var mqtt = (JsonObject)identify["mqtt"]!;
            Assert.False(mqtt.ContainsKey("password"));
            // Redaction is keyed on the enumerated secret keys, so non-secret
            // fields survive rather than the whole object being blanked.
            Assert.True(mqtt.ContainsKey("username"));
            Assert.Equal("mqtts://mqtt.hub-1.hubs.thalovant.com:8883", (string?)mqtt["endpoint"]);

            Assert.False(client.ContainsKey("initial_identify_token"));

            var spec = (JsonObject)client["spec"]!;
            Assert.False(spec.ContainsKey("apiKey"));
            Assert.False(spec.ContainsKey("password"));
            Assert.False(spec.ContainsKey("cryptoKey"));
            Assert.Equal("1", (string?)spec["version"]);
            Assert.Equal("demo", (string?)spec["siteId"]);

            // The identity half is redacted the same way it always was.
            Assert.False(((JsonObject)redacted["identity"]!).ContainsKey("access_key"));
        }

        [Fact]
        public void BootstrapResultIncludeSecretsAndSourceObjectsKeepRealSecrets()
        {
            var result = MakeResult();

            // The explicit include-secrets path must still return the real secrets.
            var full = result.ToJsonObject(includeSecrets: true);
            var fullClient = (JsonObject)full["client"]!;
            var fullIdentify = (JsonObject)fullClient["initial_identify"]!;
            Assert.Equal("CLIENT-ACCESS-KEY", (string?)fullIdentify["access_key"]);
            Assert.Equal("CLIENT-MQTT-PASSWORD", (string?)((JsonObject)fullIdentify["mqtt"]!)["password"]);
            Assert.Equal("CLIENT-BOOTSTRAP-TOKEN", (string?)fullClient["initial_identify_token"]);
            Assert.Equal("SPEC-API-KEY", (string?)((JsonObject)fullClient["spec"]!)["apiKey"]);

            // The identity round-trip still carries real secrets on the include path.
            Assert.Equal("identity-access-key", (string?)full["identity"]!["access_key"]);
            Assert.Equal("mqtt-pass", (string?)full["identity"]!["mqtt"]!["password"]);

            // Redaction only ever touched the display copy: the source Client is
            // untouched, and a second default serialization is still redacted.
            Assert.Equal("CLIENT-ACCESS-KEY", (string?)((JsonObject)result.Client["initial_identify"]!)["access_key"]);
            Assert.Equal("CLIENT-BOOTSTRAP-TOKEN", (string?)result.Client["initial_identify_token"]);
            Assert.DoesNotContain("CLIENT-ACCESS-KEY", result.ToJsonObject().ToJsonString(), StringComparison.Ordinal);
        }

        // -- F8 --------------------------------------------------------------

        [Fact]
        public void IdentitySecretTypesToStringDoNotLeakAndAreNotOverridden()
        {
            var identity = new ThalovantIdentity((JsonObject)JsonNode.Parse(Fixtures.ClientIdentify)!);
            var mqtt = identity.Mqtt!;
            var secrets = new[] { "identity-access-key", "identity-password", "0123456789abcdefextra", "mqtt-pass" };

            foreach (var rendered in new[] { identity.ToString(), $"{identity}", mqtt.ToString(), $"{mqtt}" })
            {
                foreach (var secret in secrets)
                {
                    Assert.DoesNotContain(secret, rendered, StringComparison.Ordinal);
                }
            }

            // Pin the plain-sealed-class behavior: ToString stays the default
            // (the type name), so converting either type to a `record` — whose
            // synthesized ToString dumps every property — fails here.
            Assert.Equal(identity.GetType().ToString(), identity.ToString());
            Assert.Equal(mqtt.GetType().ToString(), mqtt.ToString());
        }

        [Fact]
        public void BootstrapResultToStringDoesNotLeakSecrets()
        {
            var result = MakeResult();

            foreach (var rendered in new[] { result.ToString(), $"{result}" })
            {
                foreach (var secret in ClientSecretValues)
                {
                    Assert.DoesNotContain(secret, rendered, StringComparison.Ordinal);
                }
                Assert.DoesNotContain("identity-access-key", rendered, StringComparison.Ordinal);
            }
            Assert.Equal(result.GetType().ToString(), result.ToString());
        }

        // -- F9 --------------------------------------------------------------

        [Fact]
        public void SummarizeServerDetailCollapsesWhitespaceAndBounds()
        {
            Assert.Equal("", ThalovantControlPlane.SummarizeServerDetail(null));
            Assert.Equal("", ThalovantControlPlane.SummarizeServerDetail("  \r\n\t "));
            Assert.Equal("a b c", ThalovantControlPlane.SummarizeServerDetail("  a\n\t b    c  "));

            var big = new string('x', 4000);
            var summary = ThalovantControlPlane.SummarizeServerDetail(big);
            Assert.True(summary.Length <= ThalovantControlPlane.MaxServerDetailLength);
            Assert.DoesNotContain("\n", summary, StringComparison.Ordinal);
        }

        [Fact]
        public void FormatRequestFailedKeepsStatusAndOmitsBlankDetail()
        {
            Assert.Equal("Thalovant API request failed with HTTP 500.", ThalovantControlPlane.FormatRequestFailed(500, ""));
            Assert.Equal("Thalovant API request failed with HTTP 503.", ThalovantControlPlane.FormatRequestFailed(503, "   \n  "));

            var message = ThalovantControlPlane.FormatRequestFailed(400, """{"detail": "bad request"}""");
            Assert.Contains("400", message, StringComparison.Ordinal);
            Assert.Contains("bad request", message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task RequestErrorMessageIsBoundedWhileBodyKeepsTheRawSecret()
        {
            var handler = new StubHttpMessageHandler();
            var api = new ThalovantControlPlane(
                apiUrl: "https://api.example.com/v1",
                accessToken: "token",
                httpClient: new HttpClient(handler));

            // A validation error that reflects a sent secret PAST the summary bound
            // and across newlines: the human-facing message must not dump the body
            // nor reach the secret, but Body must still carry it verbatim.
            var rawBody = "{\n  \"detail\": \"" + new string('a', 250)
                + "\",\n  \"echoed\": {\"apiKey\": \"LEAKED-SECRET-PAST-BOUND\"}\n}";
            handler.Enqueue(422, rawBody);

            var error = await Assert.ThrowsAsync<ThalovantApiException>(() => api.GetHubAsync("hub-1"));

            Assert.Contains("422", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", error.Message, StringComparison.Ordinal);
            Assert.True(error.Message.Length <= 64 + ThalovantControlPlane.MaxServerDetailLength);
            Assert.DoesNotContain("LEAKED-SECRET-PAST-BOUND", error.Message, StringComparison.Ordinal);
            Assert.Equal(rawBody, error.Body);
            Assert.Contains("LEAKED-SECRET-PAST-BOUND", error.Body!, StringComparison.Ordinal);
        }

        [Fact]
        public async Task DevicePollUnexpectedErrorMessageIsBoundedWhileBodyKeepsRaw()
        {
            var handler = new StubHttpMessageHandler();
            var api = new ThalovantControlPlane(
                apiUrl: "https://api.example.com/v1",
                httpClient: new HttpClient(handler));

            var rawBody = "{\n  \"error\": \"invalid_grant\",\n  \"padding\": \""
                + new string('b', 300) + "SECRET-PAST-BOUND\"\n}";
            handler.Enqueue(400, rawBody);

            var error = await Assert.ThrowsAsync<ThalovantApiException>(
                () => api.PollDeviceTokenAsync(
                    "device-code-1",
                    interval: TimeSpan.FromSeconds(5),
                    timeout: TimeSpan.FromSeconds(900),
                    delay: (_, _) => Task.CompletedTask,
                    clock: () => TimeSpan.Zero));

            Assert.Equal(400, error.StatusCode);
            Assert.DoesNotContain("\n", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET-PAST-BOUND", error.Message, StringComparison.Ordinal);
            Assert.Contains("SECRET-PAST-BOUND", error.Body!, StringComparison.Ordinal);
        }
    }
}
