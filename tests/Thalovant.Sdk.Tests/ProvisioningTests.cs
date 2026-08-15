using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace Thalovant.Sdk.Tests
{
    /// <summary>
    /// Request-shape tests for the hub-provisioning and skill-discovery surface.
    /// <para>
    /// The contract these pin was verified against the API routes themselves
    /// (<c>app/routes/hubs.py</c>, <c>runtime_groups.py</c>, <c>marketplace.py</c>),
    /// not just against the sibling SDKs: hub <c>PATCH</c>/<c>DELETE</c> compare
    /// <c>If-Match</c> against the hub etag and answer 412 when it is missing, hub
    /// create is the only route in the set that reads <c>Idempotency-Key</c>, and no
    /// runtime-group route reads either header.
    /// </para>
    /// </summary>
    public class ProvisioningTests
    {
        private readonly StubHttpMessageHandler _handler;
        private readonly ThalovantControlPlane _api;

        public ProvisioningTests()
        {
            _handler = new StubHttpMessageHandler();
            _api = new ThalovantControlPlane(
                apiUrl: "https://api.example.com/v1",
                accessToken: "token",
                httpClient: new HttpClient(_handler));
        }

        private StubHttpMessageHandler.RecordedRequest LastRequest()
        {
            Assert.NotEmpty(_handler.Requests);
            return _handler.Requests[_handler.Requests.Count - 1];
        }

        // -- Hubs ------------------------------------------------------------

        [Fact]
        public async Task CreateHubSendsSnakeCaseBodyAndGeneratesAnIdempotencyKey()
        {
            _handler.Enqueue(201, """{"id": "hub-1", "etag": "etag-1"}""");
            var hub = await _api.CreateHubAsync(new CreateHubOptions(
                "joke-garden",
                new JsonObject { ["protocols"] = new JsonObject { ["wss"] = new JsonObject { ["enabled"] = true } } })
            {
                RuntimeGroupId = "group-1",
                CapacityProfile = "autoscaling",
                Visibility = "public",
                OwnerId = "owner-1",
                Slug = "joke-garden",
                Namespace = "tenant-ns",
                Domain = "jokes.example.com",
                Active = false,
            });
            Assert.Equal("hub-1", (string?)hub["id"]);

            var request = LastRequest();
            Assert.Equal("POST", request.Method);
            Assert.Equal("https://api.example.com/v1/hubs", request.Url.AbsoluteUri);
            Assert.Equal("Bearer token", request.Header("Authorization"));

            var generated = request.Header("Idempotency-Key");
            Assert.False(string.IsNullOrEmpty(generated));

            var body = request.BodyObject()!;
            Assert.Equal("joke-garden", (string?)body["name"]);
            Assert.Equal("group-1", (string?)body["runtime_group_id"]);
            Assert.Equal("autoscaling", (string?)body["capacity_profile"]);
            Assert.Equal("public", (string?)body["visibility"]);
            Assert.Equal("owner-1", (string?)body["owner_id"]);
            Assert.Equal("tenant-ns", (string?)body["namespace"]);
            Assert.Equal("jokes.example.com", (string?)body["domain"]);
            Assert.False((bool?)body["active"]);
            Assert.True((bool?)((JsonObject)((JsonObject)body["spec"]!)["protocols"]!)["wss"]!["enabled"]);

            // A second create gets its own key, so retries are opt-in, not accidental.
            _handler.Enqueue(201, """{"id": "hub-2"}""");
            await _api.CreateHubAsync(new CreateHubOptions("second", new JsonObject()));
            Assert.NotEqual(generated, LastRequest().Header("Idempotency-Key"));
        }

        [Fact]
        public async Task CreateHubOmitsUnsetOptionalFieldsAndHonorsAnExplicitIdempotencyKey()
        {
            _handler.Enqueue(201, """{"id": "hub-1"}""");
            await _api.CreateHubAsync(new CreateHubOptions("minimal", new JsonObject())
            {
                IdempotencyKey = "fixed-key",
            });

            var request = LastRequest();
            Assert.Equal("fixed-key", request.Header("Idempotency-Key"));

            var body = request.BodyObject()!;
            Assert.Equal("minimal", (string?)body["name"]);
            Assert.True(body.ContainsKey("spec"));
            Assert.False(body.ContainsKey("slug"));
            Assert.False(body.ContainsKey("namespace"));
            Assert.False(body.ContainsKey("runtime_group_id"));
            Assert.False(body.ContainsKey("domain"));
            Assert.False(body.ContainsKey("active"));
            Assert.False(body.ContainsKey("visibility"));
            Assert.False(body.ContainsKey("capacity_profile"));
            Assert.False(body.ContainsKey("owner_id"));
        }

        [Fact]
        public async Task UpdateAndDeleteHubSendIfMatchAndNoIdempotencyKey()
        {
            _handler.Enqueue(body: """{"id": "hub-1", "etag": "etag-2"}""");
            await _api.UpdateHubAsync(
                "hub-1",
                new UpdateHubOptions { Active = false, IsLocked = true, CapacityProfile = "standard" },
                "etag-1");

            var request = LastRequest();
            Assert.Equal("PATCH", request.Method);
            Assert.Equal("https://api.example.com/v1/hubs/hub-1", request.Url.AbsoluteUri);
            Assert.Equal("etag-1", request.Header("If-Match"));
            Assert.Null(request.Header("Idempotency-Key"));

            var body = request.BodyObject()!;
            Assert.False((bool?)body["active"]);
            Assert.True((bool?)body["is_locked"]);
            Assert.Equal("standard", (string?)body["capacity_profile"]);
            Assert.False(body.ContainsKey("name"));
            Assert.False(body.ContainsKey("spec"));

            _handler.Enqueue(204, "");
            await _api.DeleteHubAsync("hub-1", "etag-2");
            request = LastRequest();
            Assert.Equal("DELETE", request.Method);
            Assert.Equal("https://api.example.com/v1/hubs/hub-1", request.Url.AbsoluteUri);
            Assert.Equal("etag-2", request.Header("If-Match"));
            Assert.Null(request.Body);
        }

        [Fact]
        public async Task StaleEtagSurfacesAs412()
        {
            _handler.Enqueue(412, """{"detail": "ETag mismatch"}""");
            var error = await Assert.ThrowsAsync<ThalovantApiException>(
                () => _api.UpdateHubAsync("hub-1", new UpdateHubOptions { Active = false }, "stale"));
            Assert.Equal(412, error.StatusCode);
            Assert.Contains("ETag mismatch", error.Body!, StringComparison.Ordinal);

            _handler.Enqueue(412, """{"detail": "ETag mismatch"}""");
            var deleteError = await Assert.ThrowsAsync<ThalovantApiException>(
                () => _api.DeleteHubAsync("hub-1", "stale"));
            Assert.Equal(412, deleteError.StatusCode);
        }

        [Fact]
        public async Task ReleaseHubSendsOnlyTheOptionsGiven()
        {
            _handler.Enqueue(body: """{"id": "hub-1"}""");
            await _api.ReleaseHubAsync("hub-1", new ReleaseOptions { Channel = "stable" });

            var request = LastRequest();
            Assert.Equal("POST", request.Method);
            Assert.Equal("https://api.example.com/v1/hubs/hub-1/release", request.Url.AbsoluteUri);
            Assert.Null(request.Header("If-Match"));
            var body = request.BodyObject()!;
            Assert.Equal("stable", (string?)body["channel"]);
            Assert.False(body.ContainsKey("mode"));
            Assert.False(body.ContainsKey("version"));
            Assert.False(body.ContainsKey("images"));
            Assert.False(body.ContainsKey("reason"));

            _handler.Enqueue(body: """{"id": "hub-1"}""");
            await _api.ReleaseHubAsync("hub-1", new ReleaseOptions
            {
                Channel = "beta",
                Mode = "custom",
                Version = "1.4.0",
                Images = new Dictionary<string, string> { ["core"] = "registry/core:1.4.0" },
                Reason = "pin the kiosk fleet",
            });
            body = LastRequest().BodyObject()!;
            Assert.Equal("beta", (string?)body["channel"]);
            Assert.Equal("custom", (string?)body["mode"]);
            Assert.Equal("1.4.0", (string?)body["version"]);
            Assert.Equal("registry/core:1.4.0", (string?)((JsonObject)body["images"]!)["core"]);
            Assert.Equal("pin the kiosk fleet", (string?)body["reason"]);

            // No options at all still posts an empty body, letting the workspace
            // release policy decide everything.
            _handler.Enqueue(body: """{"id": "hub-1"}""");
            await _api.ReleaseHubAsync("hub-1");
            Assert.Empty(LastRequest().BodyObject()!);
        }

        [Fact]
        public async Task SetAndClearHubRating()
        {
            _handler.Enqueue(body: """{"id": "hub-1", "rating_average": 4.5}""");
            await _api.SetHubRatingAsync("hub-1", 5);
            var request = LastRequest();
            Assert.Equal("PUT", request.Method);
            Assert.Equal("https://api.example.com/v1/hubs/hub-1/rating", request.Url.AbsoluteUri);
            Assert.Equal(5, (int?)request.BodyObject()!["rating"]);

            // Clearing returns the hub body, not 204, so it decodes to an object.
            _handler.Enqueue(body: """{"id": "hub-1", "viewer_rating": null}""");
            var hub = await _api.ClearHubRatingAsync("hub-1");
            request = LastRequest();
            Assert.Equal("DELETE", request.Method);
            Assert.Equal("https://api.example.com/v1/hubs/hub-1/rating", request.Url.AbsoluteUri);
            Assert.Null(request.Body);
            Assert.Equal("hub-1", (string?)hub["id"]);
        }

        [Fact]
        public async Task HubRuntimeCapabilitiesPathAndConflictWhenNothingIsConnected()
        {
            _handler.Enqueue(body: """{"hub_id": "hub-1", "counts": {"total_intents": 12}}""");
            var capabilities = await _api.GetHubRuntimeCapabilitiesAsync("hub-1");
            Assert.Equal(
                "https://api.example.com/v1/hubs/hub-1/runtime-capabilities",
                LastRequest().Url.AbsoluteUri);
            Assert.Equal(12, (int?)((JsonObject)capabilities["counts"]!)["total_intents"]);

            // This is the one discovery read that fails when nothing is reporting.
            _handler.Enqueue(409, """{"detail": "No connected client can report runtime capabilities."}""");
            var error = await Assert.ThrowsAsync<ThalovantApiException>(
                () => _api.GetHubRuntimeCapabilitiesAsync("hub-1"));
            Assert.Equal(409, error.StatusCode);
        }

        // -- Runtime groups --------------------------------------------------

        [Fact]
        public async Task RuntimeGroupListGetCreateAndUpdate()
        {
            _handler.Enqueue(body: """{"data": []}""");
            await _api.ListRuntimeGroupsAsync();
            Assert.Equal("https://api.example.com/v1/runtime-groups", LastRequest().Url.AbsoluteUri);

            _handler.Enqueue(body: """{"data": []}""");
            await _api.ListRuntimeGroupsAsync("owner-1");
            Assert.Equal(
                "https://api.example.com/v1/runtime-groups?owner_id=owner-1",
                LastRequest().Url.AbsoluteUri);

            // Blank owner ids are dropped rather than sent empty.
            _handler.Enqueue(body: """{"data": []}""");
            await _api.ListRuntimeGroupsAsync("   ");
            Assert.Equal("https://api.example.com/v1/runtime-groups", LastRequest().Url.AbsoluteUri);

            _handler.Enqueue(body: """{"id": "group-1"}""");
            await _api.GetRuntimeGroupAsync("group-1");
            Assert.Equal("https://api.example.com/v1/runtime-groups/group-1", LastRequest().Url.AbsoluteUri);

            _handler.Enqueue(201, """{"id": "group-1"}""");
            await _api.CreateRuntimeGroupAsync(new CreateRuntimeGroupOptions("kiosks")
            {
                Description = "Lobby kiosks",
                Environment = "prod",
                OwnerId = "owner-1",
                CloneFromDefault = true,
            });
            var request = LastRequest();
            Assert.Equal("POST", request.Method);
            Assert.Equal("https://api.example.com/v1/runtime-groups", request.Url.AbsoluteUri);
            // This route reads no idempotency header, so the SDK must not invent one.
            Assert.Null(request.Header("Idempotency-Key"));
            var body = request.BodyObject()!;
            Assert.Equal("kiosks", (string?)body["name"]);
            Assert.Equal("Lobby kiosks", (string?)body["description"]);
            Assert.Equal("prod", (string?)body["environment"]);
            Assert.Equal("owner-1", (string?)body["owner_id"]);
            Assert.True((bool?)body["clone_from_default"]);

            _handler.Enqueue(201, """{"id": "group-2"}""");
            await _api.CreateRuntimeGroupAsync(new CreateRuntimeGroupOptions("bare"));
            body = LastRequest().BodyObject()!;
            Assert.Equal("bare", (string?)body["name"]);
            Assert.False(body.ContainsKey("description"));
            Assert.False(body.ContainsKey("environment"));
            Assert.False(body.ContainsKey("owner_id"));
            Assert.False(body.ContainsKey("clone_from_default"));

            _handler.Enqueue(body: """{"id": "group-1"}""");
            await _api.UpdateRuntimeGroupAsync("group-1", new UpdateRuntimeGroupOptions
            {
                Description = "Lobby and cafeteria kiosks",
                Spec = new JsonObject { ["replicas"] = 3 },
            });
            request = LastRequest();
            Assert.Equal("PATCH", request.Method);
            Assert.Equal("https://api.example.com/v1/runtime-groups/group-1", request.Url.AbsoluteUri);
            // Runtime-group writes read no If-Match, so there is no etag to send.
            Assert.Null(request.Header("If-Match"));
            body = request.BodyObject()!;
            Assert.Equal("Lobby and cafeteria kiosks", (string?)body["description"]);
            Assert.Equal(3, (int?)((JsonObject)body["spec"]!)["replicas"]);
            Assert.False(body.ContainsKey("name"));
        }

        [Fact]
        public async Task RuntimeGroupConfigIsReadAndMerged()
        {
            _handler.Enqueue(body: """{"runtime_group_id": "group-1", "config": {"lang": "en-us"}, "personas": {}}""");
            var config = await _api.GetRuntimeGroupConfigAsync("group-1");
            Assert.Equal("https://api.example.com/v1/runtime-groups/group-1/config", LastRequest().Url.AbsoluteUri);
            Assert.Equal("en-us", (string?)((JsonObject)config["config"]!)["lang"]);

            _handler.Enqueue(body: """{"runtime_group_id": "group-1"}""");
            await _api.UpdateRuntimeGroupConfigAsync("group-1", new JsonObject { ["lang"] = "fr-fr" });
            var request = LastRequest();
            Assert.Equal("PATCH", request.Method);
            Assert.Equal("https://api.example.com/v1/runtime-groups/group-1/config", request.Url.AbsoluteUri);
            var body = request.BodyObject()!;
            Assert.Equal("fr-fr", (string?)((JsonObject)body["config"]!)["lang"]);
            Assert.False(body.ContainsKey("personas"));

            _handler.Enqueue(body: """{"runtime_group_id": "group-1"}""");
            await _api.UpdateRuntimeGroupConfigAsync(
                "group-1",
                new JsonObject { ["lang"] = "fr-fr" },
                new JsonObject { ["default"] = "concierge" });
            body = LastRequest().BodyObject()!;
            Assert.Equal("concierge", (string?)((JsonObject)body["personas"]!)["default"]);
        }

        [Fact]
        public async Task ReleaseAndDeleteRuntimeGroup()
        {
            _handler.Enqueue(body: """{"id": "group-1"}""");
            await _api.ReleaseRuntimeGroupAsync("group-1", new ReleaseOptions { Channel = "stable" });
            var request = LastRequest();
            Assert.Equal("POST", request.Method);
            Assert.Equal("https://api.example.com/v1/runtime-groups/group-1/release", request.Url.AbsoluteUri);
            Assert.Equal("stable", (string?)request.BodyObject()!["channel"]);

            _handler.Enqueue(204, "");
            await _api.DeleteRuntimeGroupAsync("group-1");
            request = LastRequest();
            Assert.Equal("DELETE", request.Method);
            Assert.Equal("https://api.example.com/v1/runtime-groups/group-1", request.Url.AbsoluteUri);
            Assert.Null(request.Header("If-Match"));

            // The default group and groups with hubs attached are refused.
            _handler.Enqueue(409, """{"detail": "Runtime group still has hubs attached."}""");
            var error = await Assert.ThrowsAsync<ThalovantApiException>(
                () => _api.DeleteRuntimeGroupAsync("group-1"));
            Assert.Equal(409, error.StatusCode);
        }

        [Fact]
        public async Task InstallAndUninstallRuntimeGroupSkill()
        {
            _handler.Enqueue(body: """{"id": "desired-1", "skill_id": "skill-weather"}""");
            await _api.InstallRuntimeGroupSkillAsync("group-1", new InstallRuntimeGroupSkillOptions("skill-weather"));
            var request = LastRequest();
            Assert.Equal("POST", request.Method);
            Assert.Equal("https://api.example.com/v1/runtime-groups/group-1/skills", request.Url.AbsoluteUri);
            var body = request.BodyObject()!;
            Assert.Equal("skill-weather", (string?)body["skill_id"]);
            Assert.Equal("catalog", (string?)body["source_type"]);
            Assert.True((bool?)body["active"]);
            Assert.False(body.ContainsKey("marketplace_skill_id"));
            Assert.False(body.ContainsKey("source_ref"));
            Assert.False(body.ContainsKey("version_pin"));

            _handler.Enqueue(body: """{"id": "desired-2"}""");
            await _api.InstallRuntimeGroupSkillAsync("group-1", new InstallRuntimeGroupSkillOptions("skill-news")
            {
                MarketplaceSkillId = "11111111-2222-3333-4444-555555555555",
                SourceType = "git",
                SourceRef = "https://github.com/example/skill-news",
                VersionPin = "v1.2.3",
                Active = false,
            });
            body = LastRequest().BodyObject()!;
            Assert.Equal("skill-news", (string?)body["skill_id"]);
            Assert.Equal("git", (string?)body["source_type"]);
            Assert.Equal("https://github.com/example/skill-news", (string?)body["source_ref"]);
            Assert.Equal("v1.2.3", (string?)body["version_pin"]);
            Assert.Equal("11111111-2222-3333-4444-555555555555", (string?)body["marketplace_skill_id"]);
            Assert.False((bool?)body["active"]);

            _handler.Enqueue(204, "");
            await _api.UninstallRuntimeGroupSkillAsync("group-1", "skill-weather");
            request = LastRequest();
            Assert.Equal("DELETE", request.Method);
            Assert.Equal(
                "https://api.example.com/v1/runtime-groups/group-1/skills/skill-weather",
                request.Url.AbsoluteUri);
            Assert.Null(request.Body);
        }

        [Fact]
        public async Task PathSegmentsAreEscaped()
        {
            _handler.Enqueue(204, "");
            await _api.UninstallRuntimeGroupSkillAsync("group/1", "skill weather");
            Assert.Equal(
                "https://api.example.com/v1/runtime-groups/group%2F1/skills/skill%20weather",
                LastRequest().Url.AbsoluteUri);
        }

        // -- Skill discovery -------------------------------------------------

        [Fact]
        public async Task MarketplaceSkillsOmitsFalseAndBlankParams()
        {
            _handler.Enqueue(body: """{"data": []}""");
            await _api.ListMarketplaceSkillsAsync();
            Assert.Equal("https://api.example.com/v1/marketplace/skills", LastRequest().Url.AbsoluteUri);

            _handler.Enqueue(body: """{"data": []}""");
            await _api.ListMarketplaceSkillsAsync(new MarketplaceSkillListOptions
            {
                OwnerId = "   ",
                IncludeInactive = false,
                ForceRefresh = false,
            });
            Assert.Equal("https://api.example.com/v1/marketplace/skills", LastRequest().Url.AbsoluteUri);
        }

        [Fact]
        public async Task MarketplaceSkillsSendsEveryParamWhenSet()
        {
            _handler.Enqueue(body: """{"data": [{"skill_id": "skill-weather", "access_tier": "free"}]}""");
            var catalog = await _api.ListMarketplaceSkillsAsync(new MarketplaceSkillListOptions
            {
                OwnerId = "owner-1",
                IncludeInactive = true,
                ForceRefresh = true,
            });
            var request = LastRequest();
            Assert.Equal("GET", request.Method);
            Assert.Equal(
                "https://api.example.com/v1/marketplace/skills?owner_id=owner-1&include_inactive=true&force_refresh=true",
                request.Url.AbsoluteUri);
            Assert.Equal("skill-weather", (string?)((JsonArray)catalog["data"]!)[0]!["skill_id"]);
        }

        [Fact]
        public async Task RuntimeGroupMarketplaceAndInventorySendTheirRefreshFlags()
        {
            _handler.Enqueue(body: """{"runtime_group_id": "group-1", "source": "runtime-group-cache-empty", "data": []}""");
            await _api.ListRuntimeGroupMarketplaceAsync("group-1");
            Assert.Equal(
                "https://api.example.com/v1/runtime-groups/group-1/marketplace",
                LastRequest().Url.AbsoluteUri);

            _handler.Enqueue(body: """{"runtime_group_id": "group-1", "source": "ovos-runtime-operator-pending", "data": []}""");
            await _api.ListRuntimeGroupMarketplaceAsync("group-1", refreshInventory: true);
            Assert.Equal(
                "https://api.example.com/v1/runtime-groups/group-1/marketplace?refresh_inventory=true",
                LastRequest().Url.AbsoluteUri);

            _handler.Enqueue(body: """{"runtime_group_id": "group-1", "source": "runtime-group-cache", "data": []}""");
            await _api.ListRuntimeGroupInventoryAsync("group-1");
            Assert.Equal(
                "https://api.example.com/v1/runtime-groups/group-1/inventory",
                LastRequest().Url.AbsoluteUri);

            _handler.Enqueue(body: """{"runtime_group_id": "group-1", "source": "ovos-runtime-operator", "data": []}""");
            await _api.ListRuntimeGroupInventoryAsync("group-1", refresh: true);
            Assert.Equal(
                "https://api.example.com/v1/runtime-groups/group-1/inventory?refresh=true",
                LastRequest().Url.AbsoluteUri);
        }

        [Fact]
        public async Task InventoryReturnsPendingSourceInsteadOfConflictWhenNothingIsConnected()
        {
            // Unlike GetHubRuntimeCapabilitiesAsync, this route answers 200 with an
            // empty list and a pending source rather than 409.
            _handler.Enqueue(body: """
            {
              "runtime_group_id": "group-1",
              "observed_at": null,
              "source": "ovos-runtime-operator-pending",
              "operator_phase": "pending",
              "data": []
            }
            """);
            var inventory = await _api.ListRuntimeGroupInventoryAsync("group-1", refresh: true);
            Assert.Equal("ovos-runtime-operator-pending", (string?)inventory["source"]);
            Assert.Empty((JsonArray)inventory["data"]!);
        }

        // -- Gates -----------------------------------------------------------

        [Fact]
        public async Task FreePlanGateSurfacesAs402OnEveryPaidWrite()
        {
            const string PaidDetail = """{"detail": "API access requires a paid plan."}""";

            _handler.Enqueue(402, PaidDetail);
            var error = await Assert.ThrowsAsync<ThalovantApiException>(
                () => _api.CreateHubAsync(new CreateHubOptions("hub", new JsonObject())));
            Assert.Equal(402, error.StatusCode);
            Assert.Contains("paid plan", error.Body!, StringComparison.Ordinal);

            _handler.Enqueue(402, PaidDetail);
            Assert.Equal(
                402,
                (await Assert.ThrowsAsync<ThalovantApiException>(
                    () => _api.CreateRuntimeGroupAsync(new CreateRuntimeGroupOptions("kiosks")))).StatusCode);

            _handler.Enqueue(402, PaidDetail);
            Assert.Equal(
                402,
                (await Assert.ThrowsAsync<ThalovantApiException>(
                    () => _api.InstallRuntimeGroupSkillAsync(
                        "group-1",
                        new InstallRuntimeGroupSkillOptions("skill-weather")))).StatusCode);
        }

        [Fact]
        public async Task ScopeGateSurfacesAs403()
        {
            _handler.Enqueue(403, """{"detail": "Insufficient scopes"}""");
            var error = await Assert.ThrowsAsync<ThalovantApiException>(
                () => _api.ReleaseRuntimeGroupAsync("group-1", new ReleaseOptions { Channel = "stable" }));
            Assert.Equal(403, error.StatusCode);
            Assert.Contains("Insufficient scopes", error.Body!, StringComparison.Ordinal);

            // The inspect-scoped discovery reads fail the same way.
            _handler.Enqueue(403, """{"detail": "Insufficient scopes"}""");
            Assert.Equal(
                403,
                (await Assert.ThrowsAsync<ThalovantApiException>(
                    () => _api.ListRuntimeGroupInventoryAsync("group-1"))).StatusCode);
        }

        [Fact]
        public async Task DiscoveryReadsAreNotPaidGatedSoTheySucceedOnAFreeToken()
        {
            // The catalog needs only hubs:read and is deliberately not paid-gated:
            // free-tier callers browse before upgrading, and only the install pays.
            _handler.Enqueue(body: """{"data": [{"skill_id": "skill-weather"}]}""");
            var catalog = await _api.ListMarketplaceSkillsAsync();
            Assert.Single((JsonArray)catalog["data"]!);

            // Rating is hubs:write but likewise not paid-gated.
            _handler.Enqueue(body: """{"id": "hub-1"}""");
            var rated = await _api.SetHubRatingAsync("hub-1", 4);
            Assert.Equal("hub-1", (string?)rated["id"]);
        }

        [Fact]
        public async Task ProvisioningCallsRequireAToken()
        {
            var anonymous = new ThalovantControlPlane(
                apiUrl: "https://api.example.com/v1",
                httpClient: new HttpClient(_handler));
            await Assert.ThrowsAsync<ThalovantApiException>(
                () => anonymous.CreateHubAsync(new CreateHubOptions("hub", new JsonObject())));
            Assert.Empty(_handler.Requests);
        }
    }
}
