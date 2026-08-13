using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;

namespace Thalovant
{
    /// <summary>Bus event names used by the HiveMind runtime.</summary>
    public static class ThalovantEvents
    {
        public const string RecognizerLoopUtterance = "recognizer_loop:utterance";
        public const string Speak = "speak";
        public const string OvosUtteranceSpeak = "ovos.utterance.speak";
        public const string UtteranceHandled = "ovos.utterance.handled";
        public const string IntentFailure = "complete_intent_failure";
        public const string PolicyDenied = "hive.policy.denied";
        public const string QueryTimeout = "hive.query.timeout";

        internal static readonly HashSet<string> FailureEventSet = new HashSet<string>
        {
            IntentFailure,
            PolicyDenied,
            QueryTimeout,
        };

        public static IReadOnlyCollection<string> FailureEvents => FailureEventSet;
    }

    /// <summary>
    /// A bus event emitted by the hub (<c>{type, data, context}</c> payloads on
    /// <c>msg_type: "bus"</c> frames).
    /// </summary>
    public sealed class ThalovantEvent
    {
        public string Name { get; }
        public JsonObject Data { get; }
        public JsonObject Context { get; }

        public ThalovantEvent(string name, JsonObject? data = null, JsonObject? context = null)
        {
            Name = name;
            Data = data ?? new JsonObject();
            Context = context ?? new JsonObject();
        }

        /// <summary>Builds an event from a bus payload, or null when the payload has no <c>type</c>.</summary>
        public static ThalovantEvent? FromBusPayload(JsonObject payload)
        {
            var type = JsonUtil.GetString(payload["type"]);
            if (type is null)
            {
                return null;
            }
            return new ThalovantEvent(
                type,
                JsonUtil.CloneObject(JsonUtil.AsObject(payload["data"])),
                JsonUtil.CloneObject(JsonUtil.AsObject(payload["context"])));
        }

        public string Text
        {
            get
            {
                var direct = JsonUtil.GetString(Data["utterance"]) ?? JsonUtil.GetString(Data["text"]);
                if (direct is not null)
                {
                    return direct;
                }
                var utterances = Utterances;
                return utterances.Count > 0 ? utterances[0] : "";
            }
        }

        public IReadOnlyList<string> Utterances
        {
            get
            {
                if (JsonUtil.GetString(Data["utterances"]) is string single)
                {
                    return new[] { single };
                }
                if (Data["utterances"] is JsonArray list)
                {
                    var values = new List<string>();
                    foreach (var item in list)
                    {
                        if (JsonUtil.GetString(item) is string text)
                        {
                            values.Add(text);
                        }
                    }
                    return values;
                }
                if (JsonUtil.GetString(Data["utterance"]) is string utterance)
                {
                    return new[] { utterance };
                }
                return Array.Empty<string>();
            }
        }

        public string DisplayText => ThalovantContext.StripSsml(Text);

        public string? SessionId => SessionIdFromContext(Context);

        public string? RequestId => RequestIdFromContext(Context) ?? RequestIdFromMapping(Data);

        public bool IsFailure => ThalovantEvents.FailureEventSet.Contains(Name);

        public JsonObject ToJsonObject()
        {
            return new JsonObject
            {
                ["name"] = Name,
                ["data"] = JsonUtil.CloneObject(Data),
                ["context"] = JsonUtil.CloneObject(Context),
                ["text"] = Text,
                ["display_text"] = DisplayText,
                ["session_id"] = SessionId is null ? null : JsonValue.Create(SessionId),
                ["request_id"] = RequestId is null ? null : JsonValue.Create(RequestId),
            };
        }

        internal static string? SessionIdFromContext(JsonObject context)
        {
            if (JsonUtil.AsObject(context["session"]) is JsonObject session
                && CoerceIdentifier(session["session_id"]) is string fromSession)
            {
                return fromSession;
            }
            return CoerceIdentifier(context["session_id"]);
        }

        internal static string? RequestIdFromContext(JsonObject context)
        {
            if (RequestIdFromMapping(context) is string direct)
            {
                return direct;
            }
            if (JsonUtil.AsObject(context["session"]) is JsonObject session)
            {
                return RequestIdFromMapping(session);
            }
            return null;
        }

        internal static string? RequestIdFromMapping(JsonObject mapping)
        {
            foreach (var key in new[] { "request_id", "thalovant_request_id", "correlation_id" })
            {
                if (CoerceIdentifier(mapping[key]) is string identifier)
                {
                    return identifier;
                }
            }
            return null;
        }

        private static string? CoerceIdentifier(JsonNode? node)
        {
            if (node is not JsonValue value)
            {
                return null;
            }
            if (value.TryGetValue<string>(out var text))
            {
                return text;
            }
            if (value.TryGetValue<long>(out var integer))
            {
                return integer.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (value.TryGetValue<double>(out var number))
            {
                return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return null;
        }
    }

    /// <summary>The aggregated reply produced by <see cref="ThalovantClient.AskAsync"/>.</summary>
    public sealed class ThalovantReply
    {
        public string Text { get; }
        public string DisplayText { get; }
        public IReadOnlyList<string> Utterances { get; }
        public bool Handled { get; }
        public bool Ok { get; }
        public string? SessionId { get; }
        public string? RequestId { get; }
        public IReadOnlyList<ThalovantEvent> Events { get; }
        public ThalovantEvent? FailureEvent { get; }

        public ThalovantReply(
            string text,
            string displayText,
            IReadOnlyList<string> utterances,
            bool handled,
            bool ok,
            string? sessionId,
            string? requestId,
            IReadOnlyList<ThalovantEvent> events,
            ThalovantEvent? failureEvent)
        {
            Text = text;
            DisplayText = displayText;
            Utterances = utterances;
            Handled = handled;
            Ok = ok;
            SessionId = sessionId;
            RequestId = requestId;
            Events = events;
            FailureEvent = failureEvent;
        }
    }

    /// <summary>Correlation and payload helpers shared by the data-plane client.</summary>
    public static class ThalovantContext
    {
        public static string NewSessionId()
        {
            return "thalovant-session-" + CompactUuid();
        }

        public static string NewRequestId()
        {
            return "thalovant-request-" + CompactUuid();
        }

        internal static string CompactUuid()
        {
            return Guid.NewGuid().ToString("N");
        }

        /// <summary>Removes SSML/XML tags, mirroring the sibling SDKs.</summary>
        public static string StripSsml(string text)
        {
            var result = new StringBuilder(text.Length);
            var insideTag = false;
            foreach (var character in text)
            {
                if (character == '<')
                {
                    insideTag = true;
                }
                else if (character == '>')
                {
                    insideTag = false;
                }
                else if (!insideTag)
                {
                    result.Append(character);
                }
            }
            return result.ToString();
        }

        /// <summary>The <c>recognizer_loop:utterance</c> data payload.</summary>
        public static JsonObject UtterancePayload(string text, string lang)
        {
            return new JsonObject
            {
                ["utterances"] = new JsonArray { text },
                ["lang"] = lang,
            };
        }

        /// <summary>
        /// Merges correlation identifiers into an event context, matching the
        /// structure produced by the sibling SDKs (<c>request_id</c>,
        /// <c>thalovant_request_id</c>, and a <c>session</c> block).
        /// </summary>
        public static JsonObject WithCorrelation(
            JsonObject? context,
            string? sessionId = null,
            string? siteId = null,
            string? lang = null,
            string? requestId = null)
        {
            var next = JsonUtil.CloneObject(context);
            var session = JsonUtil.AsObject(next["session"]) is JsonObject existing
                ? JsonUtil.CloneObject(existing)
                : new JsonObject();
            if (sessionId is not null)
            {
                session["session_id"] = sessionId;
            }
            if (siteId is not null && !session.ContainsKey("site_id"))
            {
                session["site_id"] = siteId;
            }
            if (lang is not null && !session.ContainsKey("lang"))
            {
                session["lang"] = lang;
            }
            if (requestId is not null)
            {
                next["request_id"] = requestId;
                next["thalovant_request_id"] = requestId;
                session["request_id"] = requestId;
            }
            if (session.Count > 0)
            {
                next["session"] = session;
            }
            return next;
        }
    }
}
