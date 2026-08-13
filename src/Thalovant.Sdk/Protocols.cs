using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Thalovant
{
    /// <summary>Data-plane protocols a Thalovant hub can expose.</summary>
    public enum HubProtocol
    {
        Wss,
        Https,
        Mqtt,
    }

    public static class HubProtocolExtensions
    {
        /// <summary>The lowercase wire name used by the API ("wss", "https", "mqtt").</summary>
        public static string WireName(this HubProtocol hubProtocol)
        {
            switch (hubProtocol)
            {
                case HubProtocol.Wss:
                    return "wss";
                case HubProtocol.Https:
                    return "https";
                case HubProtocol.Mqtt:
                    return "mqtt";
                default:
                    throw new ArgumentOutOfRangeException(nameof(hubProtocol));
            }
        }
    }

    /// <summary>A protocol together with the concrete endpoint chosen for it.</summary>
    public sealed class SelectedHubEndpoint : IEquatable<SelectedHubEndpoint>
    {
        public HubProtocol Protocol { get; }
        public string Endpoint { get; }

        public SelectedHubEndpoint(HubProtocol protocol, string endpoint)
        {
            Protocol = protocol;
            Endpoint = endpoint;
        }

        public bool Equals(SelectedHubEndpoint? other)
        {
            return other is not null && Protocol == other.Protocol && Endpoint == other.Endpoint;
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as SelectedHubEndpoint);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Protocol, Endpoint);
        }
    }

    /// <summary>
    /// Which data-plane protocols a hub has enabled
    /// (<c>spec.protocols.{wss,http,mqtt}.enabled</c>; WSS defaults to enabled).
    /// </summary>
    public sealed class HubProtocolSettings : IEquatable<HubProtocolSettings>
    {
        public bool Wss { get; }
        public bool Http { get; }
        public bool Mqtt { get; }

        /// <summary>Alias for <see cref="Http"/> matching the protocol name the SDK selects.</summary>
        public bool Https => Http;

        public HubProtocolSettings(bool wss = true, bool http = false, bool mqtt = false)
        {
            Wss = wss;
            Http = http;
            Mqtt = mqtt;
        }

        /// <summary>
        /// Reads protocol settings from a hub resource, an identity document, or a
        /// bare <c>protocols</c> mapping. Missing values keep their defaults.
        /// </summary>
        public static HubProtocolSettings From(JsonObject? input)
        {
            if (input is null)
            {
                return new HubProtocolSettings();
            }
            var spec = JsonUtil.AsObject(input["spec"]) ?? input;
            var protocols = JsonUtil.AsObject(spec["protocols"]) ?? new JsonObject();
            var network = JsonUtil.AsObject(spec["network"]) ?? new JsonObject();
            return new HubProtocolSettings(
                wss: JsonUtil.EnabledValue(
                    JsonUtil.First(protocols, "wss", "websocket") ?? JsonUtil.First(network, "wss", "websocket"),
                    fallback: true),
                http: JsonUtil.EnabledValue(
                    JsonUtil.First(protocols, "http", "https") ?? JsonUtil.First(network, "http", "https"),
                    fallback: false),
                mqtt: JsonUtil.EnabledValue(
                    JsonUtil.First(protocols, "mqtt") ?? JsonUtil.First(network, "mqtt"),
                    fallback: false));
        }

        public IReadOnlyList<HubProtocol> EnabledProtocols()
        {
            var enabled = new List<HubProtocol>();
            if (Wss)
            {
                enabled.Add(HubProtocol.Wss);
            }
            if (Http)
            {
                enabled.Add(HubProtocol.Https);
            }
            if (Mqtt)
            {
                enabled.Add(HubProtocol.Mqtt);
            }
            return enabled;
        }

        public bool IsEnabled(HubProtocol hubProtocol)
        {
            switch (hubProtocol)
            {
                case HubProtocol.Wss:
                    return Wss;
                case HubProtocol.Https:
                    return Http;
                case HubProtocol.Mqtt:
                    return Mqtt;
                default:
                    return false;
            }
        }

        public JsonObject ToJsonObject()
        {
            return new JsonObject
            {
                ["wss"] = new JsonObject { ["enabled"] = Wss },
                ["http"] = new JsonObject { ["enabled"] = Http },
                ["mqtt"] = new JsonObject { ["enabled"] = Mqtt },
            };
        }

        public bool Equals(HubProtocolSettings? other)
        {
            return other is not null && Wss == other.Wss && Http == other.Http && Mqtt == other.Mqtt;
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as HubProtocolSettings);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Wss, Http, Mqtt);
        }
    }

    /// <summary>The concrete <c>data_plane_endpoints</c> (https, wss, mqtt) a hub exposes.</summary>
    public sealed class HubDataPlaneEndpoints : IEquatable<HubDataPlaneEndpoints>
    {
        public string? Https { get; }
        public string? Wss { get; }
        public string? Mqtt { get; }

        public HubDataPlaneEndpoints(string? https = null, string? wss = null, string? mqtt = null)
        {
            Https = HubEndpoints.NormalizeEndpoint(https);
            Wss = HubEndpoints.NormalizeEndpoint(wss);
            Mqtt = HubEndpoints.NormalizeEndpoint(mqtt);
        }

        /// <summary>
        /// Reads endpoints from a resource carrying <c>data_plane_endpoints</c>,
        /// <c>endpoints</c>, or a bare endpoint mapping.
        /// </summary>
        public static HubDataPlaneEndpoints From(JsonObject? input)
        {
            if (input is null)
            {
                return new HubDataPlaneEndpoints();
            }
            var source = JsonUtil.AsObject(input["data_plane_endpoints"])
                ?? JsonUtil.AsObject(input["endpoints"])
                ?? input;
            return new HubDataPlaneEndpoints(
                https: JsonUtil.OptionalString(JsonUtil.First(source, "https", "http")),
                wss: JsonUtil.OptionalString(JsonUtil.First(source, "wss", "ws")),
                mqtt: JsonUtil.OptionalString(JsonUtil.First(source, "mqtt", "mqtts")));
        }

        /// <summary>
        /// Reads endpoints from a hub resource, deriving missing WSS/HTTPS
        /// endpoints from the hub <c>domain</c> for enabled protocols.
        /// </summary>
        public static HubDataPlaneEndpoints FromHub(JsonObject hub)
        {
            var endpoints = From(hub);
            var protocols = HubProtocolSettings.From(hub);
            var domain = JsonUtil.OptionalString(hub["domain"]);
            if (domain is null)
            {
                return endpoints;
            }
            return new HubDataPlaneEndpoints(
                https: endpoints.Https ?? (protocols.Http ? HubEndpoints.EndpointFromDomain(domain, HubProtocol.Https) : null),
                wss: endpoints.Wss ?? (protocols.Wss ? HubEndpoints.EndpointFromDomain(domain, HubProtocol.Wss) : null),
                mqtt: endpoints.Mqtt);
        }

        public string? EndpointFor(HubProtocol hubProtocol)
        {
            switch (hubProtocol)
            {
                case HubProtocol.Https:
                    return Https;
                case HubProtocol.Wss:
                    return Wss;
                case HubProtocol.Mqtt:
                    return Mqtt;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Base URL used by the HTTP(S) data plane, falling back to the identity
        /// <c>default_master</c>/<c>default_port</c>/<c>default_path</c> when no HTTPS endpoint exists.
        /// </summary>
        public string HttpBase(string fallbackMaster, int fallbackPort, string fallbackPath)
        {
            if (Https is not null)
            {
                return HubEndpoints.EndpointBase(Https, fallbackPort, "");
            }
            var master = fallbackMaster;
            if (master.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                master = "https://" + master.Substring("wss://".Length);
            }
            else if (master.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
            {
                master = "http://" + master.Substring("ws://".Length);
            }
            return HubEndpoints.EndpointBase(master, fallbackPort, fallbackPath);
        }

        public JsonObject ToJsonObject(bool redactCredentials = false)
        {
            var data = new JsonObject();
            AddEndpoint(data, "https", Https, redactCredentials);
            AddEndpoint(data, "wss", Wss, redactCredentials);
            AddEndpoint(data, "mqtt", Mqtt, redactCredentials);
            return data;
        }

        private static void AddEndpoint(JsonObject data, string key, string? value, bool redactCredentials)
        {
            if (value is not null)
            {
                data[key] = redactCredentials ? HubEndpoints.RedactedCredentials(value) : value;
            }
        }

        public bool Equals(HubDataPlaneEndpoints? other)
        {
            return other is not null && Https == other.Https && Wss == other.Wss && Mqtt == other.Mqtt;
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as HubDataPlaneEndpoints);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Https, Wss, Mqtt);
        }
    }

    /// <summary>Endpoint derivation and selection helpers shared by the SDK.</summary>
    public static class HubEndpoints
    {
        /// <summary>Default protocol preference order used when bootstrapping a client.</summary>
        public static readonly IReadOnlyList<HubProtocol> DefaultProtocolPreference = new[]
        {
            HubProtocol.Wss, HubProtocol.Https, HubProtocol.Mqtt,
        };

        /// <summary>Picks the first preferred protocol that is both enabled and has an endpoint.</summary>
        public static SelectedHubEndpoint? SelectDataPlaneEndpoint(
            HubDataPlaneEndpoints endpoints,
            HubProtocolSettings protocols,
            IReadOnlyList<HubProtocol>? preferredProtocols = null)
        {
            foreach (var hubProtocol in preferredProtocols ?? DefaultProtocolPreference)
            {
                if (!protocols.IsEnabled(hubProtocol))
                {
                    continue;
                }
                var endpoint = endpoints.EndpointFor(hubProtocol);
                if (endpoint is not null)
                {
                    return new SelectedHubEndpoint(hubProtocol, endpoint);
                }
            }
            return null;
        }

        /// <summary>Derives a protocol endpoint from a bare hub domain or an existing URL.</summary>
        public static string EndpointFromDomain(string domain, HubProtocol hubProtocol)
        {
            var normalized = TrimTrailingSlashes(domain.Trim());
            var lowered = normalized.ToLowerInvariant();
            switch (hubProtocol)
            {
                case HubProtocol.Wss:
                    if (lowered.StartsWith("wss://", StringComparison.Ordinal) || lowered.StartsWith("ws://", StringComparison.Ordinal))
                    {
                        return NormalizeEndpoint(normalized) ?? "";
                    }
                    if (lowered.StartsWith("https://", StringComparison.Ordinal))
                    {
                        return NormalizeEndpoint("wss://" + normalized.Substring("https://".Length)) ?? "";
                    }
                    if (lowered.StartsWith("http://", StringComparison.Ordinal))
                    {
                        return NormalizeEndpoint("wss://" + normalized.Substring("http://".Length)) ?? "";
                    }
                    return NormalizeEndpoint("wss://" + normalized) ?? "";
                case HubProtocol.Https:
                    if (lowered.StartsWith("https://", StringComparison.Ordinal))
                    {
                        return NormalizeEndpoint(normalized) ?? "";
                    }
                    if (lowered.StartsWith("http://", StringComparison.Ordinal))
                    {
                        return NormalizeEndpoint("https://" + normalized.Substring("http://".Length)) ?? "";
                    }
                    if (lowered.StartsWith("wss://", StringComparison.Ordinal))
                    {
                        return NormalizeEndpoint("https://" + normalized.Substring("wss://".Length)) ?? "";
                    }
                    if (lowered.StartsWith("ws://", StringComparison.Ordinal))
                    {
                        return NormalizeEndpoint("https://" + normalized.Substring("ws://".Length)) ?? "";
                    }
                    return NormalizeEndpoint("https://" + normalized) ?? "";
                default:
                    return "";
            }
        }

        private static readonly HashSet<string> EndpointSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "http", "https", "ws", "wss", "mqtt", "mqtts",
        };

        /// <summary>Normalizes an endpoint URL, or null when it is not usable.</summary>
        internal static string? NormalizeEndpoint(string? value)
        {
            var raw = value?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                return null;
            }
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
                || !EndpointSchemes.Contains(uri.Scheme)
                || string.IsNullOrEmpty(uri.Host))
            {
                return null;
            }
            return TrimTrailingSlashes(uri.ToString());
        }

        /// <summary>
        /// Normalizes a master URL to <c>scheme://host[:port][/path]</c>, applying the
        /// default port and path when the URL does not carry its own.
        /// </summary>
        internal static string EndpointBase(string master, int defaultPort, string defaultPath)
        {
            if (!Uri.TryCreate(master, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            {
                return TrimTrailingSlashes(master) + ":" + defaultPort + defaultPath;
            }
            var builder = new UriBuilder(uri);
            if (uri.IsDefaultPort && !IsDefaultPort(defaultPort, uri.Scheme))
            {
                builder.Port = defaultPort;
            }
            var segments = new List<string>();
            foreach (var part in new[] { uri.AbsolutePath, defaultPath })
            {
                var trimmed = part.Trim('/');
                if (trimmed.Length > 0)
                {
                    segments.Add(trimmed);
                }
            }
            builder.Path = segments.Count == 0 ? "" : "/" + string.Join("/", segments);
            builder.Query = "";
            builder.Fragment = "";
            return TrimTrailingSlashes(builder.Uri.ToString());
        }

        private static bool IsDefaultPort(int port, string scheme)
        {
            switch (scheme.ToLowerInvariant())
            {
                case "https":
                case "wss":
                    return port == 443;
                case "http":
                case "ws":
                    return port == 80;
                default:
                    return false;
            }
        }

        internal static string TrimTrailingSlashes(string value)
        {
            return value.TrimEnd('/');
        }

        /// <summary>Removes embedded user-info credentials from an endpoint URL.</summary>
        internal static string RedactedCredentials(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo))
            {
                return value;
            }
            var builder = new UriBuilder(uri) { UserName = "", Password = "" };
            return TrimTrailingSlashes(builder.Uri.ToString());
        }
    }
}
