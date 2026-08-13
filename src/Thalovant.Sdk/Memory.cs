using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Thalovant
{
    [JsonConverter(typeof(MemoryScopeConverter))]
    public enum MemoryScope
    {
        Personal,
        Workspace,
        Hub,
    }

    [JsonConverter(typeof(MemoryKindConverter))]
    public enum MemoryKind
    {
        Note,
        Preference,
        Fact,
    }

    internal sealed class MemoryScopeConverter : JsonConverter<MemoryScope>
    {
        public override MemoryScope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            switch (value)
            {
                case "personal":
                    return MemoryScope.Personal;
                case "workspace":
                    return MemoryScope.Workspace;
                case "hub":
                    return MemoryScope.Hub;
                default:
                    throw new JsonException($"Unknown memory scope: {value}");
            }
        }

        public override void Write(Utf8JsonWriter writer, MemoryScope value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(WireName(value));
        }

        internal static string WireName(MemoryScope value)
        {
            switch (value)
            {
                case MemoryScope.Personal:
                    return "personal";
                case MemoryScope.Workspace:
                    return "workspace";
                case MemoryScope.Hub:
                    return "hub";
                default:
                    throw new ArgumentOutOfRangeException(nameof(value));
            }
        }
    }

    internal sealed class MemoryKindConverter : JsonConverter<MemoryKind>
    {
        public override MemoryKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            switch (value)
            {
                case "note":
                    return MemoryKind.Note;
                case "preference":
                    return MemoryKind.Preference;
                case "fact":
                    return MemoryKind.Fact;
                default:
                    throw new JsonException($"Unknown memory kind: {value}");
            }
        }

        public override void Write(Utf8JsonWriter writer, MemoryKind value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(WireName(value));
        }

        internal static string WireName(MemoryKind value)
        {
            switch (value)
            {
                case MemoryKind.Note:
                    return "note";
                case MemoryKind.Preference:
                    return "preference";
                case MemoryKind.Fact:
                    return "fact";
                default:
                    throw new ArgumentOutOfRangeException(nameof(value));
            }
        }
    }

    /// <summary>Filters for <c>GET /v1/memory</c>.</summary>
    public sealed class MemoryListOptions
    {
        public MemoryScope? Scope { get; set; }
        public MemoryKind? Kind { get; set; }
        public string? OwnerId { get; set; }
        public string? HubId { get; set; }
        /// <summary>Free-text filter, sent as <c>q</c>.</summary>
        public string? Query { get; set; }
        public bool IncludeDeleted { get; set; }
        public bool IncludeExpired { get; set; }
        public int? Limit { get; set; }
        public int? Offset { get; set; }
    }

    /// <summary>Body for <c>POST /v1/memory</c>. Optional fields are sent only when set.</summary>
    public sealed class MemoryCreatePayload
    {
        public string Content { get; }
        public MemoryScope? Scope { get; set; }
        public MemoryKind? Kind { get; set; }
        public string? Title { get; set; }
        public IReadOnlyList<string>? Tags { get; set; }
        public string? OwnerId { get; set; }
        public string? HubId { get; set; }
        public string? Source { get; set; }
        public JsonObject? Metadata { get; set; }
        public string? ConsentScope { get; set; }
        public string? ConsentVersion { get; set; }
        public string? RetentionPolicy { get; set; }
        public string? ExpiresAt { get; set; }

        public MemoryCreatePayload(string content)
        {
            Content = content;
        }

        public JsonObject ToJsonObject()
        {
            var body = new JsonObject { ["content"] = Content };
            if (Scope is MemoryScope scope)
            {
                body["scope"] = MemoryScopeConverter.WireName(scope);
            }
            if (Kind is MemoryKind kind)
            {
                body["kind"] = MemoryKindConverter.WireName(kind);
            }
            if (Title is not null)
            {
                body["title"] = Title;
            }
            if (Tags is not null)
            {
                var tags = new JsonArray();
                foreach (var tag in Tags)
                {
                    tags.Add(tag);
                }
                body["tags"] = tags;
            }
            if (OwnerId is not null)
            {
                body["owner_id"] = OwnerId;
            }
            if (HubId is not null)
            {
                body["hub_id"] = HubId;
            }
            if (Source is not null)
            {
                body["source"] = Source;
            }
            if (Metadata is not null)
            {
                body["metadata"] = JsonUtil.CloneObject(Metadata);
            }
            if (ConsentScope is not null)
            {
                body["consent_scope"] = ConsentScope;
            }
            if (ConsentVersion is not null)
            {
                body["consent_version"] = ConsentVersion;
            }
            if (RetentionPolicy is not null)
            {
                body["retention_policy"] = RetentionPolicy;
            }
            if (ExpiresAt is not null)
            {
                body["expires_at"] = ExpiresAt;
            }
            return body;
        }
    }

    /// <summary>Body for <c>PATCH /v1/memory/{memory_id}</c>. Optional fields are sent only when set.</summary>
    public sealed class MemoryUpdatePayload
    {
        public MemoryKind? Kind { get; set; }
        public string? Title { get; set; }
        public string? Content { get; set; }
        public IReadOnlyList<string>? Tags { get; set; }
        public JsonObject? Metadata { get; set; }
        public string? ConsentScope { get; set; }
        public string? ConsentVersion { get; set; }
        public string? RetentionPolicy { get; set; }
        public string? ExpiresAt { get; set; }
        public bool ClearExpiresAt { get; set; }

        public JsonObject ToJsonObject()
        {
            var body = new JsonObject();
            if (Kind is MemoryKind kind)
            {
                body["kind"] = MemoryKindConverter.WireName(kind);
            }
            if (Title is not null)
            {
                body["title"] = Title;
            }
            if (Content is not null)
            {
                body["content"] = Content;
            }
            if (Tags is not null)
            {
                var tags = new JsonArray();
                foreach (var tag in Tags)
                {
                    tags.Add(tag);
                }
                body["tags"] = tags;
            }
            if (Metadata is not null)
            {
                body["metadata"] = JsonUtil.CloneObject(Metadata);
            }
            if (ConsentScope is not null)
            {
                body["consent_scope"] = ConsentScope;
            }
            if (ConsentVersion is not null)
            {
                body["consent_version"] = ConsentVersion;
            }
            if (RetentionPolicy is not null)
            {
                body["retention_policy"] = RetentionPolicy;
            }
            if (ExpiresAt is not null)
            {
                body["expires_at"] = ExpiresAt;
            }
            if (ClearExpiresAt)
            {
                body["clear_expires_at"] = true;
            }
            return body;
        }
    }

    /// <summary>A memory item, exactly as returned by the <c>/v1/memory</c> endpoints.</summary>
    public sealed class MemoryItemResource
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("owner_id")]
        public string OwnerId { get; set; } = "";

        [JsonPropertyName("created_by_id")]
        public string CreatedById { get; set; } = "";

        [JsonPropertyName("hub_id")]
        public string? HubId { get; set; }

        [JsonPropertyName("scope")]
        public MemoryScope Scope { get; set; }

        [JsonPropertyName("kind")]
        public MemoryKind Kind { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("content")]
        public string Content { get; set; } = "";

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new List<string>();

        [JsonPropertyName("source")]
        public string Source { get; set; } = "";

        [JsonPropertyName("metadata")]
        public JsonObject Metadata { get; set; } = new JsonObject();

        [JsonPropertyName("consent_scope")]
        public string ConsentScope { get; set; } = "";

        [JsonPropertyName("consent_version")]
        public string? ConsentVersion { get; set; }

        [JsonPropertyName("retention_policy")]
        public string RetentionPolicy { get; set; } = "";

        [JsonPropertyName("expires_at")]
        public string? ExpiresAt { get; set; }

        [JsonPropertyName("deleted_at")]
        public string? DeletedAt { get; set; }

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = "";

        [JsonPropertyName("updated_at")]
        public string UpdatedAt { get; set; } = "";
    }

    /// <summary>Pagination envelope metadata shared by list endpoints.</summary>
    public sealed class PaginationMeta
    {
        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("next")]
        public string? Next { get; set; }

        [JsonPropertyName("prev")]
        public string? Prev { get; set; }

        [JsonPropertyName("extra")]
        public JsonObject? Extra { get; set; }
    }

    /// <summary>Response of <c>GET /v1/memory</c>.</summary>
    public sealed class MemoryListResponse
    {
        [JsonPropertyName("data")]
        public List<MemoryItemResource> Data { get; set; } = new List<MemoryItemResource>();

        [JsonPropertyName("meta")]
        public PaginationMeta Meta { get; set; } = new PaginationMeta();

        [JsonPropertyName("links")]
        public Dictionary<string, string?> Links { get; set; } = new Dictionary<string, string?>();
    }

    /// <summary>Response of <c>GET /v1/memory/summary</c>.</summary>
    public sealed class MemorySummaryResponse
    {
        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("by_scope")]
        public Dictionary<string, int> ByScope { get; set; } = new Dictionary<string, int>();

        [JsonPropertyName("by_kind")]
        public Dictionary<string, int> ByKind { get; set; } = new Dictionary<string, int>();

        [JsonPropertyName("expired")]
        public int Expired { get; set; }

        [JsonPropertyName("deleted")]
        public int Deleted { get; set; }
    }
}
