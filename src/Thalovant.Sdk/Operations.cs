using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Thalovant
{
    /// <summary>Lifecycle status of a durable asynchronous operation.</summary>
    [JsonConverter(typeof(OperationStatusConverter))]
    public enum OperationStatus
    {
        Requested,
        Committed,
        Applied,
        Ready,
        Failed,
        TimedOut,
    }

    internal sealed class OperationStatusConverter : JsonConverter<OperationStatus>
    {
        public override OperationStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            switch (value)
            {
                case "requested":
                    return OperationStatus.Requested;
                case "committed":
                    return OperationStatus.Committed;
                case "applied":
                    return OperationStatus.Applied;
                case "ready":
                    return OperationStatus.Ready;
                case "failed":
                    return OperationStatus.Failed;
                case "timed_out":
                    return OperationStatus.TimedOut;
                default:
                    throw new JsonException($"Unknown operation status: {value}");
            }
        }

        public override void Write(Utf8JsonWriter writer, OperationStatus value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(WireName(value));
        }

        internal static string WireName(OperationStatus value)
        {
            switch (value)
            {
                case OperationStatus.Requested:
                    return "requested";
                case OperationStatus.Committed:
                    return "committed";
                case OperationStatus.Applied:
                    return "applied";
                case OperationStatus.Ready:
                    return "ready";
                case OperationStatus.Failed:
                    return "failed";
                case OperationStatus.TimedOut:
                    return "timed_out";
                default:
                    throw new ArgumentOutOfRangeException(nameof(value));
            }
        }
    }

    /// <summary>
    /// A durable asynchronous operation, exactly as returned by
    /// <c>GET /v1/operations/{operation_id}</c>.
    /// </summary>
    public sealed class OperationResource
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = "";

        [JsonPropertyName("aggregate_type")]
        public string AggregateType { get; set; } = "";

        [JsonPropertyName("aggregate_id")]
        public string? AggregateId { get; set; }

        [JsonPropertyName("status")]
        public OperationStatus Status { get; set; }

        [JsonPropertyName("details")]
        public JsonObject Details { get; set; } = new JsonObject();

        [JsonPropertyName("git_commit_sha")]
        public string? GitCommitSha { get; set; }

        [JsonPropertyName("error_code")]
        public string? ErrorCode { get; set; }

        [JsonPropertyName("error_message")]
        public string? ErrorMessage { get; set; }

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = "";

        [JsonPropertyName("updated_at")]
        public string UpdatedAt { get; set; } = "";

        [JsonPropertyName("committed_at")]
        public string? CommittedAt { get; set; }

        [JsonPropertyName("applied_at")]
        public string? AppliedAt { get; set; }

        [JsonPropertyName("ready_at")]
        public string? ReadyAt { get; set; }

        [JsonPropertyName("terminal_at")]
        public string? TerminalAt { get; set; }

        [JsonPropertyName("links")]
        public Dictionary<string, string?> Links { get; set; } = new Dictionary<string, string?>();
    }
}
