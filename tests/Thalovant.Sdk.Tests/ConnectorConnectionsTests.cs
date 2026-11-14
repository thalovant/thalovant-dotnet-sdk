using System;
using System.Collections.Generic;
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
    /// Home bridges (<c>connection_type: connector</c>) and the refusals that came
    /// with them, written against the API's <c>Idempotent-Replayed</c> header,
    /// <c>replay</c> marker, <c>serves</c> block and the problem codes of
    /// <see cref="ConnectionProblemCode"/>. Each test also holds for an API that
    /// does not have them yet.
    /// </summary>
    public class ConnectorConnectionsTests
    {
        private sealed class Answering : HttpMessageHandler
        {
            private readonly int _status;
            private readonly string _body;
            private readonly Dictionary<string, string> _headers;

            internal List<(string Method, string? Body)> Sent { get; } = new List<(string, string?)>();

            internal Answering(int status, JsonObject body, Dictionary<string, string>? headers = null)
            {
                _status = status;
                _body = body.ToJsonString();
                _headers = headers ?? new Dictionary<string, string>();
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Sent.Add((request.Method.Method, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
                var response = new HttpResponseMessage((HttpStatusCode)_status)
                {
                    Content = new StringContent(_body, Encoding.UTF8, "application/json"),
                };
                foreach (var header in _headers)
                {
                    response.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                return response;
            }
        }

        private static JsonObject Hub() => new JsonObject
        {
            ["id"] = "hub-1",
            ["name"] = "Home",
            ["domain"] = "home.example.com",
            ["wss_enabled"] = true,
            ["http_enabled"] = true,
        };

        private static (ThalovantControlPlane Api, Answering Handler) Plane(int status, JsonObject body, Dictionary<string, string>? headers = null)
        {
            var handler = new Answering(status, body, headers);
            return (new ThalovantControlPlane(apiUrl: "https://api.example.com", accessToken: "t", httpMessageHandler: handler), handler);
        }

        private static JsonObject Created(string connectionType, bool identity = true, JsonObject? serves = null, JsonObject? replay = null)
        {
            var body = new JsonObject
            {
                ["id"] = "client-1",
                ["name"] = "Bridge",
                ["hub_id"] = "hub-1",
                ["spec"] = new JsonObject { ["version"] = "1", ["connection_type"] = connectionType },
                ["etag"] = "e1",
            };
            if (identity)
            {
                body["initial_identify"] = new JsonObject
                {
                    ["password"] = "platform-password",
                    ["access_key"] = "platform-key",
                    ["site_id"] = "bridge",
                    ["default_port"] = 443,
                    ["default_master"] = "wss://home.example.com",
                };
            }
            if (serves is not null)
            {
                body["serves"] = serves;
            }
            if (replay is not null)
            {
                body["replay"] = replay;
            }
            return body;
        }

        private static JsonObject Problem(string code, JsonObject? extra = null)
        {
            var body = new JsonObject { ["detail"] = "refused", ["code"] = code };
            if (extra is not null)
            {
                foreach (var pair in extra)
                {
                    body[pair.Key] = pair.Value?.DeepClone();
                }
            }
            return body;
        }

        private static JsonArray Strings(params string[] values)
        {
            var array = new JsonArray();
            foreach (var value in values)
            {
                array.Add(value);
            }
            return array;
        }

        private static Task<BootstrapIdentityResult> Create(ThalovantControlPlane api, string? connectionType, JsonObject? spec = null, string? key = null) =>
            api.CreateClientIdentityAsync(Hub(), new CreateClientIdentityOptions("Bridge")
            {
                ConnectionType = connectionType,
                Spec = spec,
                IdempotencyKey = key,
            });

        [Fact]
        public void TheKindConstantsAreTheWireValues()
        {
            Assert.Equal("connector", ThalovantConnectionTypes.Connector);
            Assert.Equal("home_assistant", ThalovantConnectionTypes.HomeAssistant);
        }

        [Fact]
        public async Task ABridgeIsCreatedWithItsConnectorListAndThePlatformsCredentials()
        {
            var serves = new JsonObject
            {
                ["devices"] = new JsonArray(new JsonObject { ["id"] = "d1", ["name"] = "Kitchen", ["connection_type"] = "voice_satellite", ["active"] = true }),
                ["count"] = 1,
                ["warning"] = null,
            };
            var (api, handler) = Plane(201, Created("connector", serves: serves));
            var result = await Create(api, ThalovantConnectionTypes.Connector, new JsonObject { ["connectors"] = Strings("music_assistant") });
            var spec = (JsonObject)JsonNode.Parse(handler.Sent[0].Body!)!["spec"]!;
            Assert.Equal("connector", (string?)spec["connection_type"]);
            Assert.Equal("music_assistant", (string?)((JsonArray)spec["connectors"]!)[0]);
            // The answer's credentials are the connection's; the ones minted locally are not.
            Assert.Equal("platform-key", result.Identity.AccessKey);
            Assert.Equal("platform-password", result.Identity.Password);
            Assert.Equal("connector", result.ConnectionType);
            Assert.False(result.Replayed);
            Assert.True(JsonNode.DeepEquals(serves, result.Serves));
            Assert.Null(result.ServesWarning);
        }

        [Fact]
        public async Task ALinkThatServesNobodySaysSo()
        {
            var serves = new JsonObject { ["devices"] = new JsonArray(), ["count"] = 0, ["warning"] = "No device of your account is on this hub." };
            var (api, _) = Plane(201, Created("home_assistant", serves: serves));
            var result = await Create(api, "home_assistant");
            Assert.Equal("No device of your account is on this hub.", result.ServesWarning);
        }

        [Fact]
        public async Task AnApiWithoutServesStillWorks()
        {
            var (api, _) = Plane(201, Created("connector"));
            var result = await Create(api, "connector");
            Assert.Null(result.Serves);
            Assert.Null(result.ServesWarning);
        }

        [Theory]
        [InlineData("header")]
        [InlineData("marker")]
        public async Task AReplayedCreateNeverHandsBackAnIdentityTheSdkMadeUp(string how)
        {
            var replay = new JsonObject { ["code"] = "idempotent_replay", ["credentials_included"] = false, ["detail"] = "x" };
            var (api, _) = how == "header"
                ? Plane(201, Created("connector", identity: false), new Dictionary<string, string> { ["Idempotent-Replayed"] = "true" })
                : Plane(201, Created("connector", identity: false, replay: replay));
            var caught = await Assert.ThrowsAsync<ThalovantCredentialsNotReplayedException>(() => Create(api, "connector", key: "k1"));
            Assert.Equal("client-1", caught.ClientId);
            Assert.False(caught.Client.ContainsKey("initial_identify"));
        }

        [Fact]
        public async Task AReplayWithCredentialsInTheBodyStillKeepsThemOutOfTheException()
        {
            var (api, _) = Plane(201, Created("connector"), new Dictionary<string, string> { ["Idempotent-Replayed"] = "true" });
            var caught = await Assert.ThrowsAsync<ThalovantCredentialsNotReplayedException>(() => Create(api, "connector"));
            Assert.False(caught.Client.ContainsKey("initial_identify"));
        }

        [Fact]
        public async Task AnAnswerWithoutCredentialsThatIsNotAReplayKeepsTheOldBehaviour()
        {
            var (api, _) = Plane(201, Created("connector", identity: false));
            var result = await Create(api, "connector");
            Assert.False(string.IsNullOrEmpty(result.Identity.AccessKey));
        }

        [Fact]
        public async Task ALinkOnAnotherAccountsHubNamesTheReason()
        {
            var (api, handler) = Plane(422, Problem("connection_type_unavailable", new JsonObject { ["reason"] = "hub_operated_by_another_account" }));
            var caught = await Assert.ThrowsAsync<ThalovantConnectionTypeUnavailableException>(() => Create(api, "home_assistant"));
            Assert.Equal("hub_operated_by_another_account", caught.Reason);
            Assert.Equal(ConnectionProblemCode.ConnectionTypeUnavailable, caught.ErrorCode);
            Assert.Equal(422, caught.StatusCode);
            Assert.IsAssignableFrom<ThalovantUnsupportedConnectionTypeException>(caught);
            Assert.Single(handler.Sent);
        }

        [Theory]
        [InlineData("connection_type_unknown")]
        [InlineData("connection_type_ambiguous")]
        public async Task AnUnknownOrAmbiguousKindIsUnsupported(string code)
        {
            var (api, _) = Plane(422, Problem(code, new JsonObject { ["known"] = Strings("voice_satellite", "connector") }));
            var caught = await Assert.ThrowsAsync<ThalovantUnsupportedConnectionTypeException>(() => Create(api, "plex"));
            Assert.Equal(code, caught.ErrorCode);
        }

        [Theory]
        [InlineData("duplicate")]
        [InlineData("own_kind")]
        [InlineData("unknown_or_malformed")]
        public async Task ARefusedConnectorListSaysWhyAndWhatIsKnown(string reason)
        {
            var (api, _) = Plane(422, Problem("connectors_invalid", new JsonObject { ["reason"] = reason, ["known"] = Strings("music_assistant") }));
            var caught = await Assert.ThrowsAsync<ThalovantConnectorsInvalidException>(
                () => Create(api, "connector", new JsonObject { ["connectors"] = Strings("music_assistant", "music_assistant") }));
            Assert.Equal(reason, caught.Reason);
            Assert.Equal(new[] { "music_assistant" }, caught.Known);
        }

        [Theory]
        [InlineData("device_owner_only_beside_home_link")]
        [InlineData("setup_session_owner_only_beside_home_link")]
        [InlineData("device_identity_owner_only_beside_home_link")]
        public async Task AnOwnerOnlyRefusalNamesTheLink(string code)
        {
            var (api, _) = Plane(403, Problem(code, new JsonObject { ["link_client_id"] = "client-9" }));
            var caught = await Assert.ThrowsAsync<ThalovantOwnerOnlyBesideLinkException>(() => Create(api, null));
            Assert.Equal("client-9", caught.LinkClientId);
            Assert.Equal(403, caught.StatusCode);
            Assert.Equal(code, caught.ErrorCode);
        }

        [Fact]
        public async Task TheOtherNewCodesAreTyped()
        {
            var (api, _) = Plane(409, Problem("hub_scale_out_breaks_links", new JsonObject { ["links"] = 2 }));
            var scale = await Assert.ThrowsAsync<ThalovantHubScaleOutBreaksLinksException>(() => Create(api, null));
            Assert.Equal(2, scale.Links);
            Assert.Equal("hub_scale_out_breaks_links", scale.ErrorCode);

            (api, _) = Plane(409, Problem("idempotency_key_spent"));
            var spent = await Assert.ThrowsAsync<ThalovantIdempotencyKeySpentException>(() => Create(api, null));
            Assert.Equal("idempotency_key_spent", spent.ErrorCode);

            (api, _) = Plane(422, Problem("skill_config_private_hub_only", new JsonObject { ["settings"] = Strings("url", "token") }));
            var hubOnly = await Assert.ThrowsAsync<ThalovantPrivateHubOnlyException>(() => Create(api, null));
            Assert.Equal(new[] { "url", "token" }, hubOnly.Settings);
            Assert.Equal("skill_config_private_hub_only", hubOnly.ErrorCode);
        }

        [Fact]
        public async Task AStructuredRefusalInsideDetailIsReadToo()
        {
            var detail = new JsonObject { ["code"] = "connectors_invalid", ["reason"] = "duplicate", ["known"] = Strings("music_assistant") };
            var (api, _) = Plane(422, new JsonObject { ["detail"] = detail });
            var caught = await Assert.ThrowsAsync<ThalovantConnectorsInvalidException>(() => Create(api, "connector"));
            Assert.Equal("duplicate", caught.Reason);
            Assert.Equal(new[] { "music_assistant" }, caught.Known);
        }

        [Fact]
        public async Task AnUnlistedCodeStaysAPlainApiError()
        {
            var (api, _) = Plane(409, Problem("something_new"));
            var caught = await Assert.ThrowsAsync<ThalovantApiException>(() => Create(api, null));
            Assert.Equal(typeof(ThalovantApiException), caught.GetType());
            Assert.Equal("something_new", caught.ErrorCode);
        }
    }
}
