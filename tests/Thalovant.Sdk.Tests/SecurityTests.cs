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
        // A POST /v1/clients response carrying every secret the finding enumerates
        // plus the cross-family gaps: initial_identify credentials (+ mqtt password,
        // username, broker_username, and userinfo embedded in the broker URL), the
        // bootstrap token, the echoed spec, and arbitrary metadata holding a secret.
        // Secret values are distinctive so they are easy to find.
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
              "endpoint": "mqtts://brokeruser:MQTT-USERINFO-SECRET@mqtt.hub-1.hubs.thalovant.com:8883",
              "username": "BROKER-USERNAME-SECRET",
              "broker_username": "BROKER-USERNAME-ALIAS-SECRET",
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
          },
          "metadata": {
            "note": "keep-me",
            "password": "META-PASSWORD-SECRET"
          }
        }
        """;

        private static readonly string[] ClientSecretValues =
        {
            "CLIENT-ACCESS-KEY", "CLIENT-PASSWORD", "CLIENT-CRYPTO-KEY", "CLIENT-MQTT-PASSWORD",
            "CLIENT-BOOTSTRAP-TOKEN", "SPEC-API-KEY", "SPEC-PASSWORD", "SPEC-CRYPTO-KEY",
            "MQTT-USERINFO-SECRET", "BROKER-USERNAME-SECRET", "BROKER-USERNAME-ALIAS-SECRET",
            "META-PASSWORD-SECRET",
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
            // MQTT username (and its broker_username alias) can equal the access
            // key, so both are redacted; the URL userinfo is stripped in place.
            Assert.False(mqtt.ContainsKey("username"));
            Assert.False(mqtt.ContainsKey("broker_username"));
            var endpoint = (string?)mqtt["endpoint"];
            Assert.NotNull(endpoint);
            Assert.Contains("mqtt.hub-1.hubs.thalovant.com:8883", endpoint!, StringComparison.Ordinal);
            Assert.DoesNotContain("brokeruser", endpoint!, StringComparison.Ordinal);

            Assert.False(client.ContainsKey("initial_identify_token"));

            var spec = (JsonObject)client["spec"]!;
            Assert.False(spec.ContainsKey("apiKey"));
            Assert.False(spec.ContainsKey("password"));
            Assert.False(spec.ContainsKey("cryptoKey"));
            Assert.Equal("1", (string?)spec["version"]);
            Assert.Equal("demo", (string?)spec["siteId"]);

            // Arbitrary metadata pass-through is scrubbed of secret-named keys,
            // non-secret entries survive.
            var metadata = (JsonObject)client["metadata"]!;
            Assert.False(metadata.ContainsKey("password"));
            Assert.Equal("keep-me", (string?)metadata["note"]);

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

            // The gap fixes (mqtt username, broker URL userinfo, metadata) must not
            // touch the include-secrets path.
            var fullMqtt = (JsonObject)fullIdentify["mqtt"]!;
            Assert.Equal("BROKER-USERNAME-SECRET", (string?)fullMqtt["username"]);
            Assert.Contains("MQTT-USERINFO-SECRET", (string?)fullMqtt["endpoint"]!, StringComparison.Ordinal);
            Assert.Equal("META-PASSWORD-SECRET", (string?)((JsonObject)fullClient["metadata"]!)["password"]);

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
        public void SummarizeServerDetailSurfacesOnlyKnownScalarFields()
        {
            Assert.Equal("", ThalovantControlPlane.SummarizeServerDetail(null));
            Assert.Equal("", ThalovantControlPlane.SummarizeServerDetail("  \r\n\t "));

            // A detail string is surfaced, with whitespace collapsed.
            Assert.Equal("Hub not found", ThalovantControlPlane.SummarizeServerDetail("""{"detail": "Hub not found"}"""));
            Assert.Equal("bad input", ThalovantControlPlane.SummarizeServerDetail("""{"detail": "  bad\n  input "}"""));
            // OAuth-style and {message|code} object detail forms.
            Assert.Equal("invalid_grant", ThalovantControlPlane.SummarizeServerDetail("""{"error": "invalid_grant"}"""));
            Assert.Equal("nope", ThalovantControlPlane.SummarizeServerDetail("""{"detail": {"code": "x", "message": "nope"}}"""));
            // FastAPI 422 array: only each entry's msg string, never its input.
            Assert.Equal(
                "field required",
                ThalovantControlPlane.SummarizeServerDetail("""{"detail": [{"msg": "field required", "input": "SECRET"}]}"""));

            // Non-whitelisted keys, validation input, and non-JSON bodies are dropped.
            Assert.Equal("", ThalovantControlPlane.SummarizeServerDetail("""{"apiKey": "SECRET"}"""));
            Assert.Equal("", ThalovantControlPlane.SummarizeServerDetail("""{"detail": [{"input": {"apiKey": "SECRET"}}]}"""));
            Assert.Equal("", ThalovantControlPlane.SummarizeServerDetail("plain text 502 bad gateway SECRET"));

            // A long detail string is whitespace-safe and length-bounded.
            var big = "{\"detail\": \"" + new string('x', 4000) + "\"}";
            var summary = ThalovantControlPlane.SummarizeServerDetail(big);
            Assert.True(summary.Length > 0);
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

        [Fact]
        public async Task ClientCreateValidationErrorDoesNotLaunderEchoedSecretsIntoMessage()
        {
            var handler = new StubHttpMessageHandler();
            var api = new ThalovantControlPlane(
                apiUrl: "https://api.example.com/v1",
                accessToken: "token",
                httpClient: new HttpClient(handler));

            // FastAPI 422: `detail` is an ARRAY whose `input` echoes the SUBMITTED
            // request, including the apiKey/password/cryptoKey the SDK generated.
            // Only the `msg` string may be surfaced; the echo must never reach the
            // exception message even though it sits at the start of the body.
            var body = """
            {
              "detail": [
                {
                  "type": "string_too_short",
                  "loc": ["body", "spec", "apiKey"],
                  "msg": "String should have at least 20 characters",
                  "input": {"spec": {"apiKey": "SECRET-SUBMITTED-APIKEY", "password": "SECRET-SUBMITTED-PW", "cryptoKey": "SECRET-SUBMITTED-CRYPTO"}}
                }
              ]
            }
            """;
            handler.Enqueue(422, body);

            var error = await Assert.ThrowsAsync<ThalovantApiException>(
                () => api.CreateClientAsync(new JsonObject { ["hub_id"] = "hub-1" }));

            foreach (var secret in new[] { "SECRET-SUBMITTED-APIKEY", "SECRET-SUBMITTED-PW", "SECRET-SUBMITTED-CRYPTO" })
            {
                Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
            }
            Assert.Contains("422", error.Message, StringComparison.Ordinal);
            // The safe `msg` string is surfaced for diagnostics.
            Assert.Contains("String should have at least 20 characters", error.Message, StringComparison.Ordinal);
            // The raw body (with the echo) is retained for programmatic use / ErrorCode.
            Assert.Contains("SECRET-SUBMITTED-APIKEY", error.Body!, StringComparison.Ordinal);
        }

        [Fact]
        public async Task TopLevelEchoedSecretAtStartDoesNotReachMessage()
        {
            var handler = new StubHttpMessageHandler();
            var api = new ThalovantControlPlane(
                apiUrl: "https://api.example.com/v1",
                accessToken: "token",
                httpClient: new HttpClient(handler));

            // The echoed secrets are the FIRST fields of the body, under
            // non-whitelisted keys; only the safe `detail` string is surfaced.
            var body = """{"apiKey": "SECRET-API-KEY-AT-START", "password": "SECRET-PW-AT-START", "detail": "Invalid spec."}""";
            handler.Enqueue(400, body);

            var error = await Assert.ThrowsAsync<ThalovantApiException>(
                () => api.CreateClientAsync(new JsonObject { ["hub_id"] = "hub-1" }));

            Assert.DoesNotContain("SECRET-API-KEY-AT-START", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET-PW-AT-START", error.Message, StringComparison.Ordinal);
            Assert.Contains("400", error.Message, StringComparison.Ordinal);
            Assert.Contains("Invalid spec.", error.Message, StringComparison.Ordinal);
            Assert.Equal(body, error.Body);
        }

        [Fact]
        public void IdentityMetadataSecretsAreRedactedByDefault()
        {
            var identity = new ThalovantIdentity(new JsonObject
            {
                ["access_key"] = "ak",
                ["password"] = "pw",
                ["default_master"] = "https://hub.example.com",
                ["site_id"] = "site-1",
                ["metadata"] = new JsonObject
                {
                    ["label"] = "keep-me",
                    ["password"] = "META-SECRET",
                    ["callback"] = "https://cbuser:META-URL-SECRET@cb.example.com",
                },
            });

            var redacted = identity.ToJsonObject();
            var meta = (JsonObject)redacted["metadata"]!;
            Assert.Equal("keep-me", (string?)meta["label"]);
            Assert.False(meta.ContainsKey("password"));
            var redactedJson = redacted.ToJsonString();
            Assert.DoesNotContain("META-SECRET", redactedJson, StringComparison.Ordinal);
            Assert.DoesNotContain("META-URL-SECRET", redactedJson, StringComparison.Ordinal);
            Assert.DoesNotContain("cbuser", redactedJson, StringComparison.Ordinal);

            // includeSecrets keeps metadata verbatim.
            var full = identity.ToJsonObject(includeSecrets: true);
            var fullMeta = (JsonObject)full["metadata"]!;
            Assert.Equal("META-SECRET", (string?)fullMeta["password"]);
            Assert.Contains("META-URL-SECRET", full.ToJsonString(), StringComparison.Ordinal);
        }
    }
}
