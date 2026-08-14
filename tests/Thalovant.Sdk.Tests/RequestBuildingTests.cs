using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Thalovant.Sdk.Tests
{
    /// <summary>
    /// HttpMessageHandler stub that records every request (method, URL, headers,
    /// body) and replays queued responses, so no test touches the network.
    /// </summary>
    internal sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        internal sealed class RecordedRequest
        {
            internal string Method { get; }
            internal Uri Url { get; }
            internal Dictionary<string, string> Headers { get; }
            internal string? Body { get; }

            internal RecordedRequest(string method, Uri url, Dictionary<string, string> headers, string? body)
            {
                Method = method;
                Url = url;
                Headers = headers;
                Body = body;
            }

            internal string? Header(string name)
            {
                foreach (var pair in Headers)
                {
                    if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return pair.Value;
                    }
                }
                return null;
            }

            internal JsonObject? BodyObject()
            {
                return Body is null ? null : JsonNode.Parse(Body) as JsonObject;
            }
        }

        internal sealed class StubResponse
        {
            internal int Status { get; set; } = 200;
            internal string Body { get; set; } = "{}";
        }

        private readonly List<RecordedRequest> _requests = new List<RecordedRequest>();
        private readonly Queue<StubResponse> _queue = new Queue<StubResponse>();

        internal IReadOnlyList<RecordedRequest> Requests => _requests;

        internal void Enqueue(int status = 200, string body = "{}")
        {
            _queue.Enqueue(new StubResponse { Status = status, Body = body });
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>();
            foreach (var header in request.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }
            string? body = null;
            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                {
                    headers[header.Key] = string.Join(",", header.Value);
                }
                body = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            _requests.Add(new RecordedRequest(request.Method.Method, request.RequestUri!, headers, body));
            var stub = _queue.Count > 0 ? _queue.Dequeue() : new StubResponse();
            return new HttpResponseMessage((HttpStatusCode)stub.Status)
            {
                Content = new StringContent(stub.Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    public class RequestBuildingTests
    {
        private readonly StubHttpMessageHandler _handler;
        private readonly ThalovantControlPlane _api;

        public RequestBuildingTests()
        {
            _handler = new StubHttpMessageHandler();
            _api = new ThalovantControlPlane(
                apiUrl: "https://api.example.com/v1",
                httpClient: new HttpClient(_handler));
        }

        private StubHttpMessageHandler.RecordedRequest LastRequest()
        {
            Assert.NotEmpty(_handler.Requests);
            return _handler.Requests[_handler.Requests.Count - 1];
        }

        [Fact]
        public async Task LoginSendsOnlyEmailAndPasswordByDefault()
        {
            _handler.Enqueue(body: """{"access_token": "token-1", "token_type": "bearer"}""");
            var token = await _api.LoginAsync("dev@example.com", "secret");
            Assert.Equal("token-1", (string?)token["access_token"]);
            Assert.Equal("token-1", _api.AccessToken);

            var request = LastRequest();
            Assert.Equal("POST", request.Method);
            Assert.Equal("https://api.example.com/v1/auth/token", request.Url.AbsoluteUri);
            Assert.Equal("ThalovantDotNetSDK/0.1.2", request.Header("User-Agent"));
            Assert.Equal("application/json", request.Header("Accept"));
            Assert.StartsWith("application/json", request.Header("Content-Type"));
            Assert.Null(request.Header("Authorization"));
            var body = request.BodyObject()!;
            Assert.Equal("dev@example.com", (string?)body["email"]);
            Assert.Equal("secret", (string?)body["password"]);
            Assert.False(body.ContainsKey("scope"));
            Assert.False(body.ContainsKey("otp_code"));
            Assert.False(body.ContainsKey("recovery_code"));
        }

        [Fact]
        public async Task LoginIncludesMfaFieldsOnlyWhenSet()
        {
            _handler.Enqueue(body: """{"access_token": "token-2"}""");
            await _api.LoginAsync("dev@example.com", "secret", scope: "hubs:read", otpCode: "123456");
            var body = LastRequest().BodyObject()!;
            Assert.Equal("hubs:read", (string?)body["scope"]);
            Assert.Equal("123456", (string?)body["otp_code"]);
            Assert.False(body.ContainsKey("recovery_code"));

            _handler.Enqueue(body: """{"access_token": "token-3"}""");
            await _api.LoginAsync("dev@example.com", "secret", recoveryCode: "abcd-efgh");
            body = LastRequest().BodyObject()!;
            Assert.Equal("abcd-efgh", (string?)body["recovery_code"]);
            Assert.False(body.ContainsKey("otp_code"));
        }

        [Fact]
        public async Task LoginMfaRequiredSurfacesErrorCode()
        {
            _handler.Enqueue(401, """{"detail": {"code": "mfa_required", "recovery_available": true}}""");
            var error = await Assert.ThrowsAsync<ThalovantApiException>(
                () => _api.LoginAsync("dev@example.com", "secret"));
            Assert.Equal(401, error.StatusCode);
            Assert.Equal("mfa_required", error.ErrorCode);
            Assert.NotNull(error.Body);
        }

        [Fact]
        public async Task ListHubsRequiresAuthAndBuildsQuery()
        {
            _api.AccessToken = "token";
            await _api.ListHubsAsync(limit: 50, cursor: "abc", ownerId: "owner-1");
            var request = LastRequest();
            Assert.Equal("GET", request.Method);
            Assert.Equal("https://api.example.com/v1/hubs?limit=50&cursor=abc&owner_id=owner-1", request.Url.AbsoluteUri);
            Assert.Equal("Bearer token", request.Header("Authorization"));
        }

        [Fact]
        public async Task ListHubsWithoutTokenThrowsBeforeSendingAnything()
        {
            await Assert.ThrowsAsync<ThalovantApiException>(() => _api.ListHubsAsync());
            Assert.Empty(_handler.Requests);
        }

        [Fact]
        public async Task PublicHubsAreUnauthenticated()
        {
            await _api.ListPublicHubsAsync();
            var request = LastRequest();
            Assert.Equal("https://api.example.com/v1/public/hubs?limit=24", request.Url.AbsoluteUri);
            Assert.Null(request.Header("Authorization"));

            await _api.GetPublicHubAsync("hub-1");
            request = LastRequest();
            Assert.Equal("https://api.example.com/v1/public/hubs/hub-1", request.Url.AbsoluteUri);
            Assert.Null(request.Header("Authorization"));
        }

        [Fact]
        public async Task GetOperationPathAndDecoding()
        {
            _api.AccessToken = "token";
            _handler.Enqueue(body: Fixtures.Operation);
            var operation = await _api.GetOperationAsync("operation-1");
            Assert.Equal(OperationStatus.TimedOut, operation.Status);
            Assert.Equal("/v1/operations/operation-1", operation.Links["self"]);
            Assert.Equal("https://api.example.com/v1/operations/operation-1", LastRequest().Url.AbsoluteUri);
        }

        [Fact]
        public async Task MemoryListBuildsAllFilters()
        {
            _api.AccessToken = "token";
            _handler.Enqueue(body: Fixtures.MemoryList);
            await _api.ListMemoryItemsAsync(new MemoryListOptions
            {
                Scope = MemoryScope.Workspace,
                Kind = MemoryKind.Preference,
                OwnerId = "owner-1",
                HubId = "hub-1",
                Query = "timezone",
                IncludeDeleted = true,
                IncludeExpired = true,
                Limit = 25,
                Offset = 5,
            });
            Assert.Equal(
                "https://api.example.com/v1/memory?scope=workspace&kind=preference&owner_id=owner-1&hub_id=hub-1&q=timezone&include_deleted=true&include_expired=true&limit=25&offset=5",
                LastRequest().Url.AbsoluteUri);
        }

        [Fact]
        public async Task MemoryListDefaultsHaveNoQuery()
        {
            _api.AccessToken = "token";
            _handler.Enqueue(body: Fixtures.MemoryList);
            await _api.ListMemoryItemsAsync();
            Assert.Equal("https://api.example.com/v1/memory", LastRequest().Url.AbsoluteUri);
        }

        [Fact]
        public async Task MemoryCreateUpdateDeleteAndSummary()
        {
            _api.AccessToken = "token";
            _handler.Enqueue(body: Fixtures.MemoryItem);
            await _api.CreateMemoryItemAsync(new MemoryCreatePayload("Prefer America/Toronto for scheduling.")
            {
                Scope = MemoryScope.Workspace,
                Kind = MemoryKind.Preference,
                Tags = new[] { "timezone" },
            });
            var request = LastRequest();
            Assert.Equal("POST", request.Method);
            Assert.Equal("https://api.example.com/v1/memory", request.Url.AbsoluteUri);
            var body = request.BodyObject()!;
            Assert.Equal("workspace", (string?)body["scope"]);
            Assert.Equal("preference", (string?)body["kind"]);
            Assert.Equal("Prefer America/Toronto for scheduling.", (string?)body["content"]);
            Assert.Equal("timezone", (string?)((JsonArray)body["tags"]!)[0]);
            Assert.False(body.ContainsKey("title"));
            Assert.False(body.ContainsKey("owner_id"));
            Assert.False(body.ContainsKey("clear_expires_at"));

            _handler.Enqueue(body: Fixtures.MemoryItem);
            await _api.UpdateMemoryItemAsync("mem-1", new MemoryUpdatePayload
            {
                Content = "Updated.",
                ClearExpiresAt = true,
            });
            request = LastRequest();
            Assert.Equal("PATCH", request.Method);
            Assert.Equal("https://api.example.com/v1/memory/mem-1", request.Url.AbsoluteUri);
            body = request.BodyObject()!;
            Assert.Equal("Updated.", (string?)body["content"]);
            Assert.True((bool?)body["clear_expires_at"]);
            Assert.False(body.ContainsKey("kind"));

            _handler.Enqueue(204, "");
            await _api.DeleteMemoryItemAsync("mem-1");
            request = LastRequest();
            Assert.Equal("DELETE", request.Method);
            Assert.Equal("https://api.example.com/v1/memory/mem-1", request.Url.AbsoluteUri);

            _handler.Enqueue(body: Fixtures.MemorySummary);
            var summary = await _api.GetMemorySummaryAsync("owner-1");
            Assert.Equal(12, summary.Total);
            Assert.Equal("https://api.example.com/v1/memory/summary?owner_id=owner-1", LastRequest().Url.AbsoluteUri);
        }

        [Fact]
        public async Task AnalyticsOverviewWorkspaceIgnoresOwnerId()
        {
            _api.AccessToken = "token";
            await _api.AnalyticsOverviewAsync(new AnalyticsOverviewOptions
            {
                Range = "7d",
                Bucket = "1h",
                OwnerId = "owner-1",
                HubId = "hub-1",
                ClientId = "client-1",
                Country = "CA",
                Message = "msg",
                Utterance = "utt",
                Intent = "intent-1",
                TimeStart = "2026-08-01T00:00:00Z",
                TimeEnd = "2026-08-08T00:00:00Z",
                Weekday = 2,
                Hour = 13,
            });
            var request = LastRequest();
            Assert.StartsWith("https://api.example.com/v1/analytics/overview?", request.Url.AbsoluteUri, StringComparison.Ordinal);
            Assert.DoesNotContain("owner_id", request.Url.Query, StringComparison.Ordinal);
            Assert.Equal(
                "https://api.example.com/v1/analytics/overview"
                    + "?range=7d&bucket=1h&hub_id=hub-1&client_id=client-1&country=CA&message=msg&utterance=utt"
                    + "&intent=intent-1&time_start=2026-08-01T00%3A00%3A00Z&time_end=2026-08-08T00%3A00%3A00Z&weekday=2&hour=13",
                request.Url.AbsoluteUri);
        }

        [Fact]
        public async Task AnalyticsOverviewAdminSwitchesEndpointAndSendsOwnerId()
        {
            _api.AccessToken = "token";
            await _api.AnalyticsOverviewAsync(new AnalyticsOverviewOptions
            {
                Admin = true,
                Range = "24h",
                OwnerId = "owner-1",
            });
            Assert.Equal(
                "https://api.example.com/v1/admin/analytics/overview?range=24h&owner_id=owner-1",
                LastRequest().Url.AbsoluteUri);
        }

        [Fact]
        public async Task CreateClientSendsIdempotencyKey()
        {
            _api.AccessToken = "token";
            await _api.CreateClientAsync(new JsonObject { ["hub_id"] = "hub-1" }, "fixed-key");
            var request = LastRequest();
            Assert.Equal("POST", request.Method);
            Assert.Equal("https://api.example.com/v1/clients", request.Url.AbsoluteUri);
            Assert.Equal("fixed-key", request.Header("Idempotency-Key"));

            await _api.CreateClientAsync(new JsonObject { ["hub_id"] = "hub-1" });
            request = LastRequest();
            var generated = request.Header("Idempotency-Key");
            Assert.False(string.IsNullOrEmpty(generated));
            Assert.NotEqual("fixed-key", generated);
        }

        [Fact]
        public async Task CreateClientIdentityFlow()
        {
            _api.AccessToken = "token";
            _handler.Enqueue(body: Fixtures.Hub);
            _handler.Enqueue(body: $$"""
            {
              "id": "11111111-aaaa-bbbb-cccc-222222222222",
              "name": "dotnet demo client",
              "active": false,
              "initial_identify": {{Fixtures.ClientIdentify}}
            }
            """);
            var result = await _api.CreateClientIdentityAsync(
                "hub-1",
                new CreateClientIdentityOptions("dotnet demo client")
                {
                    OwnerId = "owner-1",
                    Active = false,
                    IdempotencyKey = "bootstrap-key",
                });

            var requests = _handler.Requests;
            Assert.Equal(2, requests.Count);
            Assert.Equal("GET", requests[0].Method);
            Assert.Equal("https://api.example.com/v1/hubs/hub-1", requests[0].Url.AbsoluteUri);
            Assert.Equal("POST", requests[1].Method);
            Assert.Equal("https://api.example.com/v1/clients", requests[1].Url.AbsoluteUri);
            Assert.Equal("bootstrap-key", requests[1].Header("Idempotency-Key"));

            var body = requests[1].BodyObject()!;
            Assert.Equal("b3b1f5a0-91b8-4a71-a2e5-53422dd0f841", (string?)body["hub_id"]);
            Assert.Equal("dotnet demo client", (string?)body["name"]);
            Assert.False((bool?)body["active"]);
            Assert.Equal("owner-1", (string?)body["owner_id"]);
            var spec = (JsonObject)body["spec"]!;
            Assert.Equal("1", (string?)spec["version"]);
            Assert.Equal("dotnet-demo-client", (string?)spec["siteId"]);
            Assert.False(string.IsNullOrEmpty((string?)spec["apiKey"]));
            Assert.False(string.IsNullOrEmpty((string?)spec["password"]));
            Assert.False(string.IsNullOrEmpty((string?)spec["cryptoKey"]));

            // Identity is parsed from initial_identify and enriched with the hub's
            // endpoints and protocol settings.
            Assert.Equal("identity-access-key", result.Identity.AccessKey);
            Assert.Equal("dotnet-demo-client", result.Identity.SiteId);
            Assert.Equal("wss://hub-1.hubs.thalovant.com/ws", result.Identity.DataPlaneEndpoints.Wss);
            Assert.Equal(HubProtocol.Wss, result.SelectedProtocol);
            Assert.Equal("wss://hub-1.hubs.thalovant.com/ws", result.Endpoint?.Endpoint);

            // Runtime protocol resolution and unsupported-protocol errors.
            var wss = _api.RequireRuntimeProtocol(result);
            Assert.Equal(HubProtocol.Wss, wss.Protocol);
            Assert.Equal("wss://hub-1.hubs.thalovant.com/ws", wss.Endpoint);

            // Redacted output never leaks secrets.
            var redacted = result.ToJsonObject();
            Assert.False(((JsonObject)redacted["identity"]!).ContainsKey("access_key"));
            Assert.Equal("wss", (string?)redacted["selectedProtocol"]);
        }

        [Fact]
        public async Task CreateClientIdentityActiveDefaultsTrue()
        {
            _api.AccessToken = "token";
            _handler.Enqueue(body: Fixtures.Hub);
            _handler.Enqueue(body: """{"id": "client-1"}""");
            var result = await _api.CreateClientIdentityAsync(
                "hub-1",
                new CreateClientIdentityOptions("demo_client"));
            var body = LastRequest().BodyObject()!;
            Assert.True((bool?)body["active"]);
            Assert.False(body.ContainsKey("owner_id"));
            // Without initial_identify, the locally generated secrets are used.
            var spec = (JsonObject)body["spec"]!;
            Assert.Equal((string?)spec["apiKey"], result.Identity.AccessKey);
            Assert.Equal("demo-client", result.Identity.SiteId);
            Assert.Equal(443, result.Identity.DefaultPort);
            Assert.Equal("https://hub-1.hubs.thalovant.com", result.Identity.DefaultMaster);
        }

        [Fact]
        public async Task ErrorPropagatesStatusAndBody()
        {
            _api.AccessToken = "token";
            _handler.Enqueue(404, """{"detail": "Hub not found"}""");
            var error = await Assert.ThrowsAsync<ThalovantApiException>(() => _api.GetHubAsync("missing"));
            Assert.Equal(404, error.StatusCode);
            Assert.Null(error.ErrorCode);
            Assert.Equal("""{"detail": "Hub not found"}""", error.Body);
            Assert.Contains("404", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task RequireRuntimeProtocolThrowsForMissingMqttCredentials()
        {
            _api.AccessToken = "token";
            _handler.Enqueue(body: Fixtures.Hub);
            _handler.Enqueue(body: """{"id": "client-1"}""");
            var result = await _api.CreateClientIdentityAsync("hub-1", new CreateClientIdentityOptions("demo"));
            Assert.Throws<ThalovantUnsupportedProtocolException>(
                () => _api.RequireRuntimeProtocol(result, HubProtocol.Mqtt));
        }
    }
}
