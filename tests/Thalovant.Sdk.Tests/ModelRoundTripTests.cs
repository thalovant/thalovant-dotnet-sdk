using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Thalovant.Sdk.Tests
{
    /// <summary>
    /// JSON round-trips for every typed model against fixture JSON copied from
    /// the API pydantic schemas: decode -> encode -> decode must be lossless.
    /// </summary>
    public class ModelRoundTripTests
    {
        private static T RoundTrip<T>(string json)
        {
            var decoded = JsonSerializer.Deserialize<T>(json)!;
            var encoded = JsonSerializer.Serialize(decoded);
            var redecoded = JsonSerializer.Deserialize<T>(encoded)!;
            Assert.True(JsonNode.DeepEquals(
                JsonNode.Parse(JsonSerializer.Serialize(decoded)),
                JsonNode.Parse(JsonSerializer.Serialize(redecoded))));
            return decoded;
        }

        [Fact]
        public void OperationResourceRoundTrip()
        {
            var operation = RoundTrip<OperationResource>(Fixtures.Operation);
            Assert.Equal("0b849a6c-3d3f-49a5-9f10-8f4dbb2f3d10", operation.Id);
            Assert.Equal("hub.release", operation.Kind);
            Assert.Equal("hub", operation.AggregateType);
            Assert.Equal("b3b1f5a0-91b8-4a71-a2e5-53422dd0f841", operation.AggregateId);
            Assert.Equal(OperationStatus.TimedOut, operation.Status);
            Assert.Equal(3, (int?)operation.Details["attempt"]);
            Assert.Equal("ca-central-1", (string?)operation.Details["region"]);
            Assert.False((bool?)operation.Details["dry_run"]);
            Assert.Equal("0f4f9c8f2a1b3c4d5e6f7a8b9c0d1e2f3a4b5c6d", operation.GitCommitSha);
            Assert.Equal("reconcile_timeout", operation.ErrorCode);
            Assert.Equal("The hub did not reach Ready within the deadline.", operation.ErrorMessage);
            Assert.Equal("2026-08-12T15:04:05Z", operation.CreatedAt);
            Assert.Equal("2026-08-12T15:24:05Z", operation.UpdatedAt);
            Assert.Equal("2026-08-12T15:05:00Z", operation.CommittedAt);
            Assert.Equal("2026-08-12T15:06:00Z", operation.AppliedAt);
            Assert.Null(operation.ReadyAt);
            Assert.Equal("2026-08-12T15:24:05Z", operation.TerminalAt);
            Assert.Equal("/v1/operations/operation-1", operation.Links["self"]);
            Assert.True(operation.Links.ContainsKey("aggregate"));
            Assert.Null(operation.Links["aggregate"]);
        }

        [Fact]
        public void OperationResourceEncodesSnakeCaseFields()
        {
            var operation = JsonSerializer.Deserialize<OperationResource>(Fixtures.Operation)!;
            var encoded = JsonSerializer.Serialize(operation);
            foreach (var field in new[]
            {
                "aggregate_type", "aggregate_id", "git_commit_sha", "error_code", "error_message",
                "created_at", "updated_at", "committed_at", "applied_at", "terminal_at",
            })
            {
                Assert.Contains($"\"{field}\"", encoded, StringComparison.Ordinal);
            }
            Assert.Contains("\"timed_out\"", encoded, StringComparison.Ordinal);
        }

        [Fact]
        public void PendingOperationNullFields()
        {
            var operation = RoundTrip<OperationResource>(Fixtures.OperationPending);
            Assert.Equal(OperationStatus.Requested, operation.Status);
            Assert.Null(operation.AggregateId);
            Assert.Null(operation.GitCommitSha);
            Assert.Null(operation.ErrorCode);
            Assert.Null(operation.CommittedAt);
            Assert.Null(operation.TerminalAt);
            Assert.Empty(operation.Details);
            Assert.Equal("/v1/operations/operation-1", operation.Links["self"]);
        }

        [Fact]
        public void OperationStatusWireNames()
        {
            Assert.Equal("requested", OperationStatusConverter.WireName(OperationStatus.Requested));
            Assert.Equal("committed", OperationStatusConverter.WireName(OperationStatus.Committed));
            Assert.Equal("applied", OperationStatusConverter.WireName(OperationStatus.Applied));
            Assert.Equal("ready", OperationStatusConverter.WireName(OperationStatus.Ready));
            Assert.Equal("failed", OperationStatusConverter.WireName(OperationStatus.Failed));
            Assert.Equal("timed_out", OperationStatusConverter.WireName(OperationStatus.TimedOut));
        }

        [Fact]
        public void MemoryItemRoundTrip()
        {
            var item = RoundTrip<MemoryItemResource>(Fixtures.MemoryItem);
            Assert.Equal(MemoryScope.Workspace, item.Scope);
            Assert.Equal(MemoryKind.Preference, item.Kind);
            Assert.Equal("Timezone", item.Title);
            Assert.Equal("Prefer America/Toronto for scheduling.", item.Content);
            Assert.Equal(new[] { "timezone", "scheduling" }, item.Tags);
            Assert.True((bool?)item.Metadata["pinned"]);
            Assert.Equal("daily_desk_memory", item.ConsentScope);
            Assert.Null(item.ConsentVersion);
            Assert.Equal("user_controlled", item.RetentionPolicy);
            Assert.Null(item.HubId);
            Assert.Null(item.ExpiresAt);
            Assert.Null(item.DeletedAt);
        }

        [Fact]
        public void MemoryListRoundTrip()
        {
            var list = RoundTrip<MemoryListResponse>(Fixtures.MemoryList);
            Assert.Single(list.Data);
            Assert.Equal(1, list.Meta.Count);
            Assert.Null(list.Meta.Next);
            Assert.Equal(1, (int?)list.Meta.Extra?["total"]);
            Assert.Equal("/v1/memory?limit=50", list.Links["self"]);
            Assert.True(list.Links.ContainsKey("next"));
            Assert.Null(list.Links["next"]);
        }

        [Fact]
        public void MemorySummaryRoundTrip()
        {
            var summary = RoundTrip<MemorySummaryResponse>(Fixtures.MemorySummary);
            Assert.Equal(12, summary.Total);
            Assert.Equal(8, summary.ByScope["workspace"]);
            Assert.Equal(5, summary.ByKind["preference"]);
            Assert.Equal(1, summary.Expired);
            Assert.Equal(2, summary.Deleted);
        }

        [Fact]
        public void IdentityFromInitialIdentify()
        {
            var identity = new ThalovantIdentity((JsonObject)JsonNode.Parse(Fixtures.ClientIdentify)!);
            Assert.Equal("identity-access-key", identity.AccessKey);
            Assert.Equal("identity-password", identity.Password);
            Assert.Equal("0123456789abcdefextra", identity.CryptoKey);
            Assert.Equal("dotnet-demo-client", identity.SiteId);
            Assert.Equal(443, identity.DefaultPort);
            Assert.Equal("https://hub-1.hubs.thalovant.com", identity.DefaultMaster);
            var mqtt = identity.Mqtt!;
            Assert.Equal("mqtts://mqtt.hub-1.hubs.thalovant.com:8883", mqtt.Endpoint);
            Assert.Equal("mqtt-user", mqtt.Username);
            Assert.Equal("mqtt-pass", mqtt.Password);
            Assert.Equal("hivemind/hub-1", mqtt.TopicPrefix);
            Assert.True(mqtt.Tls);
        }

        [Fact]
        public void IdentityDefaultsAndAliases()
        {
            var identity = new ThalovantIdentity(new JsonObject
            {
                ["api_key"] = "aliased-key",
                ["password"] = "pw",
                ["host"] = "wss://hub.example.com",
                ["site"] = "site-1",
            });
            Assert.Equal("aliased-key", identity.AccessKey);
            Assert.Equal("wss://hub.example.com", identity.DefaultMaster);
            Assert.Equal("site-1", identity.SiteId);
            Assert.Equal(5679, identity.DefaultPort);
            Assert.Equal("", identity.DefaultPath);
            // WSS default master is usable as the WSS endpoint.
            Assert.Equal("wss://hub.example.com", identity.EndpointFor(HubProtocol.Wss));
            // Protocol defaults: wss enabled, http/mqtt disabled.
            Assert.Equal(new[] { HubProtocol.Wss }, identity.EnabledProtocols());
        }

        [Fact]
        public void IdentityMissingFieldThrows()
        {
            Assert.Throws<ThalovantIdentityException>(() => new ThalovantIdentity(new JsonObject
            {
                ["password"] = "pw",
            }));
        }

        [Fact]
        public void IdentityToJsonRedactsSecretsByDefault()
        {
            var identity = new ThalovantIdentity((JsonObject)JsonNode.Parse(Fixtures.ClientIdentify)!);
            var redacted = identity.ToJsonObject();
            Assert.False(redacted.ContainsKey("access_key"));
            Assert.False(redacted.ContainsKey("password"));
            Assert.False(redacted.ContainsKey("crypto_key"));
            Assert.False(((JsonObject)redacted["mqtt"]!).ContainsKey("username"));
            var full = identity.ToJsonObject(includeSecrets: true);
            Assert.Equal("identity-access-key", (string?)full["access_key"]);
            Assert.Equal("mqtt-pass", (string?)full["mqtt"]!["password"]);
        }

        [Fact]
        public void IdentityFileLoadingEnforcesPermissionsOnPosix()
        {
            var directory = Path.Combine(Path.GetTempPath(), "thalovant-sdk-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "identity.json");
                File.WriteAllText(path, Fixtures.ClientIdentify);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // Windows: no chmod enforcement; the file loads as-is.
                    var loaded = ThalovantIdentity.FromFile(path);
                    Assert.Equal("dotnet-demo-client", loaded.SiteId);
                    return;
                }
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var identity = ThalovantIdentity.FromFile(path);
                Assert.Equal("dotnet-demo-client", identity.SiteId);

                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                var error = Assert.Throws<ThalovantIdentityException>(() => ThalovantIdentity.FromFile(path));
                Assert.Contains("too permissive", error.Message, StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void IdentityFromInvalidJsonFileThrows()
        {
            var directory = Path.Combine(Path.GetTempPath(), "thalovant-sdk-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "identity.json");
                File.WriteAllText(path, "not json");
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                var error = Assert.Throws<ThalovantIdentityException>(() => ThalovantIdentity.FromFile(path));
                Assert.Contains("not valid JSON", error.Message, StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void ApiExceptionDecodesErrorCode()
        {
            var mfa = new ThalovantApiException(
                "HTTP 401",
                401,
                """{"detail": {"code": "mfa_required", "recovery_available": false}}""");
            Assert.Equal("mfa_required", mfa.ErrorCode);
            var topLevel = new ThalovantApiException("HTTP 409", 409, """{"code": "conflict"}""");
            Assert.Equal("conflict", topLevel.ErrorCode);
            var plain = new ThalovantApiException("HTTP 500", 500, "boom");
            Assert.Null(plain.ErrorCode);
        }
    }
}
