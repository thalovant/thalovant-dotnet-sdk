using System.Text.Json.Nodes;
using Xunit;

namespace Thalovant.Sdk.Tests
{
    public class ProtocolSelectionTests
    {
        [Fact]
        public void DefaultsWssEnabled()
        {
            var settings = HubProtocolSettings.From(new JsonObject());
            Assert.True(settings.Wss);
            Assert.False(settings.Http);
            Assert.False(settings.Mqtt);
            Assert.Equal(new[] { HubProtocol.Wss }, settings.EnabledProtocols());
        }

        [Fact]
        public void ReadsHubSpecProtocols()
        {
            var hub = (JsonObject)JsonNode.Parse(Fixtures.Hub)!;
            var settings = HubProtocolSettings.From(hub);
            Assert.True(settings.Wss);
            Assert.True(settings.Http);
            Assert.True(settings.Https);
            Assert.False(settings.Mqtt);
            Assert.Equal(new[] { HubProtocol.Wss, HubProtocol.Https }, settings.EnabledProtocols());
        }

        [Fact]
        public void EnabledFlagCoercions()
        {
            var settings = HubProtocolSettings.From(new JsonObject
            {
                ["protocols"] = new JsonObject
                {
                    ["wss"] = "off",
                    ["http"] = "yes",
                    ["mqtt"] = true,
                },
            });
            Assert.False(settings.Wss);
            Assert.True(settings.Http);
            Assert.True(settings.Mqtt);
        }

        [Fact]
        public void EndpointsFromHubPrefersExplicitEndpoints()
        {
            var hub = (JsonObject)JsonNode.Parse(Fixtures.Hub)!;
            var endpoints = HubDataPlaneEndpoints.FromHub(hub);
            Assert.Equal("https://hub-1.hubs.thalovant.com", endpoints.Https);
            Assert.Equal("wss://hub-1.hubs.thalovant.com/ws", endpoints.Wss);
            Assert.Null(endpoints.Mqtt);
        }

        [Fact]
        public void EndpointsDerivedFromDomain()
        {
            var endpoints = HubDataPlaneEndpoints.FromHub(new JsonObject
            {
                ["domain"] = "hub-2.hubs.thalovant.com",
                ["spec"] = new JsonObject
                {
                    ["protocols"] = new JsonObject
                    {
                        ["http"] = new JsonObject { ["enabled"] = true },
                    },
                },
            });
            Assert.Equal("wss://hub-2.hubs.thalovant.com", endpoints.Wss);
            Assert.Equal("https://hub-2.hubs.thalovant.com", endpoints.Https);
        }

        [Fact]
        public void SelectionPrefersWssByDefault()
        {
            var hub = (JsonObject)JsonNode.Parse(Fixtures.Hub)!;
            var selected = HubEndpoints.SelectDataPlaneEndpoint(
                HubDataPlaneEndpoints.FromHub(hub),
                HubProtocolSettings.From(hub));
            Assert.Equal(HubProtocol.Wss, selected?.Protocol);
            Assert.Equal("wss://hub-1.hubs.thalovant.com/ws", selected?.Endpoint);
        }

        [Fact]
        public void SelectionSkipsDisabledProtocols()
        {
            var endpoints = new HubDataPlaneEndpoints(https: "https://hub.example.com", wss: "wss://hub.example.com");
            var protocols = new HubProtocolSettings(wss: false, http: true, mqtt: false);
            var selected = HubEndpoints.SelectDataPlaneEndpoint(endpoints, protocols);
            Assert.Equal(HubProtocol.Https, selected?.Protocol);
        }

        [Fact]
        public void SelectionHonorsCustomPreference()
        {
            var endpoints = new HubDataPlaneEndpoints(https: "https://hub.example.com", wss: "wss://hub.example.com");
            var protocols = new HubProtocolSettings(wss: true, http: true, mqtt: false);
            var selected = HubEndpoints.SelectDataPlaneEndpoint(
                endpoints,
                protocols,
                new[] { HubProtocol.Https, HubProtocol.Wss });
            Assert.Equal(HubProtocol.Https, selected?.Protocol);
        }

        [Fact]
        public void SelectionReturnsNullWithoutUsableEndpoint()
        {
            var endpoints = new HubDataPlaneEndpoints();
            var protocols = new HubProtocolSettings(wss: true, http: true, mqtt: true);
            Assert.Null(HubEndpoints.SelectDataPlaneEndpoint(endpoints, protocols));
        }

        [Fact]
        public void EndpointFromDomainConversions()
        {
            Assert.Equal("wss://hub.example.com", HubEndpoints.EndpointFromDomain("hub.example.com", HubProtocol.Wss));
            Assert.Equal("wss://hub.example.com", HubEndpoints.EndpointFromDomain("https://hub.example.com/", HubProtocol.Wss));
            Assert.Equal("https://hub.example.com", HubEndpoints.EndpointFromDomain("wss://hub.example.com", HubProtocol.Https));
            Assert.Equal("https://hub.example.com", HubEndpoints.EndpointFromDomain("http://hub.example.com", HubProtocol.Https));
            Assert.Equal("", HubEndpoints.EndpointFromDomain("hub.example.com", HubProtocol.Mqtt));
        }

        [Fact]
        public void IdentityEndpointBaseUsesHttpsEndpointFirst()
        {
            var identity = new ThalovantIdentity(new JsonObject
            {
                ["access_key"] = "k",
                ["password"] = "p",
                ["site_id"] = "s",
                ["default_master"] = "wss://hub.example.com",
                ["default_port"] = 8443,
                ["data_plane_endpoints"] = new JsonObject { ["https"] = "https://hub.example.com/api" },
            });
            // The fallback port is applied when the endpoint does not pin one,
            // mirroring the sibling SDKs.
            Assert.Equal("https://hub.example.com:8443/api", identity.EndpointBase());
        }

        [Fact]
        public void IdentityEndpointBaseKeepsExplicitPort()
        {
            var identity = new ThalovantIdentity(new JsonObject
            {
                ["access_key"] = "k",
                ["password"] = "p",
                ["site_id"] = "s",
                ["default_master"] = "wss://hub.example.com",
                ["default_port"] = 8443,
                ["data_plane_endpoints"] = new JsonObject { ["https"] = "https://hub.example.com:9443/api" },
            });
            Assert.Equal("https://hub.example.com:9443/api", identity.EndpointBase());
        }

        [Fact]
        public void IdentityEndpointBaseFallsBackToMaster()
        {
            var identity = new ThalovantIdentity(new JsonObject
            {
                ["access_key"] = "k",
                ["password"] = "p",
                ["site_id"] = "s",
                ["default_master"] = "wss://hub.example.com",
                ["default_port"] = 5679,
                ["default_path"] = "hive",
            });
            Assert.Equal("https://hub.example.com:5679/hive", identity.EndpointBase());
        }

        [Fact]
        public void ControlApiUrlNormalization()
        {
            Assert.Equal("https://api.thalovant.com/", ThalovantControlPlane.NormalizeControlApiUrl("https://api.thalovant.com"));
            Assert.Equal("https://api.thalovant.com/", ThalovantControlPlane.NormalizeControlApiUrl("https://api.thalovant.com/v1"));
            Assert.Equal("https://api.thalovant.com/", ThalovantControlPlane.NormalizeControlApiUrl("https://api.thalovant.com/v1///"));
            Assert.Equal("http://localhost:8000/", ThalovantControlPlane.NormalizeControlApiUrl("http://localhost:8000/"));
        }

        [Fact]
        public void CleanSiteIdCollapsesRuns()
        {
            Assert.Equal("My-Dotnet-Client", ThalovantControlPlane.CleanSiteId("My Dotnet  Client"));
            Assert.Equal("under-scored", ThalovantControlPlane.CleanSiteId("under__scored"));
            Assert.StartsWith("thalovant-client-", ThalovantControlPlane.CleanSiteId("   "));
        }
    }
}
