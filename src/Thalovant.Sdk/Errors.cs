using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Thalovant
{
    /// <summary>Base type for every exception thrown by the Thalovant SDK.</summary>
    public abstract class ThalovantException : Exception
    {
        protected ThalovantException(string message) : base(message)
        {
        }

        protected ThalovantException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    /// <summary>The Thalovant control API rejected a request or returned an unusable response.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="Exception.Message"/> is one bounded line for display. It can
    /// be shortened, so it is never where to read what the API said: read
    /// <see cref="ErrorCode"/>, <see cref="Detail"/> and <see cref="Problem"/>
    /// instead. A value the body echoed back from the request (a validation
    /// error repeats what it was sent) never reaches the message, only
    /// <see cref="Problem"/> and <see cref="Body"/>.
    /// </para>
    /// <para>
    /// A refused image, for example, answers HTTP 403 with
    /// <c>platform_image_required</c>, a sentence longer than the message keeps,
    /// and <c>refused_images</c>, <c>allowed_images</c> and
    /// <c>allowed_repositories</c>; a plan limit answers <c>plan_limit</c> with
    /// <c>resource</c>, <c>limit</c>, <c>used</c> and <c>plan</c>. All of them
    /// are on <see cref="Problem"/>.
    /// </para>
    /// </remarks>
    public sealed class ThalovantApiException : ThalovantException
    {
        /// <summary>
        /// The body parsed once, kept private: <see cref="Problem"/> hands out
        /// copies of it, so no caller can change what the next one reads.
        /// </summary>
        private readonly JsonObject? _problem;

        /// <summary>HTTP status code, when the server produced a response.</summary>
        public int? StatusCode { get; }

        /// <summary>Raw response body, when the server produced a response.</summary>
        public string? Body { get; }

        /// <summary>
        /// The body's machine-readable code, such as <c>platform_image_required</c>
        /// or <c>plan_limit</c>, or null. It is the body's <c>code</c> when that is
        /// a string with a non-whitespace character; otherwise, when the body's
        /// <c>detail</c> is itself an object (FastAPI's own envelope, for example
        /// <c>mfa_required</c>), that object's <c>code</c> under the same rule.
        /// Returned exactly as sent. A code passed to the constructor wins.
        /// </summary>
        public string? ErrorCode { get; }

        /// <summary>
        /// The API's own sentence, whole and exactly as sent -- never trimmed,
        /// collapsed or shortened, unlike the message -- or null. It is the
        /// body's <c>detail</c> when that is a string with a non-whitespace
        /// character; otherwise, when <c>detail</c> is itself an object, that
        /// object's <c>detail</c> under the same rule.
        /// </summary>
        public string? Detail { get; }

        /// <summary>
        /// The whole error body parsed, exactly when it is a JSON object (the
        /// Problem+JSON document every Thalovant API refusal is), or null for a
        /// body that is empty, not JSON, or JSON that is not an object. Every
        /// structured field the API sends is here, including ones added after
        /// this SDK was released. The body is read as UTF-8 whatever the
        /// response's Content-Type says, and a name the body repeats keeps its
        /// last value.
        /// </summary>
        /// <remarks>
        /// Each read returns a new copy, so changing what one read returned
        /// never changes what the next read, or another caller, sees. Keep the
        /// copy in a local when reading several fields.
        /// </remarks>
        public JsonObject? Problem => _problem is null ? null : (JsonObject)_problem.DeepClone();

        public ThalovantApiException(string message, int? statusCode = null, string? body = null, string? errorCode = null)
            : this(message, statusCode, body, errorCode, ParseProblem(body))
        {
        }

        private ThalovantApiException(string message, int? statusCode, string? body, string? errorCode, JsonObject? problem)
            : base(message)
        {
            StatusCode = statusCode;
            Body = body;
            _problem = problem;
            ErrorCode = errorCode ?? ProblemText(problem, "code");
            Detail = ProblemText(problem, "detail");
        }

        /// <summary>
        /// The error for a response the API answered with a failure status,
        /// from the body the caller already parsed with <see cref="ParseProblem"/>
        /// so that it is parsed once.
        /// </summary>
        internal static ThalovantApiException FromResponse(string message, int statusCode, string body, JsonObject? problem) =>
            new ThalovantApiException(message, statusCode, body, null, problem);

        /// <summary>
        /// A member of an error body that is a string with a non-whitespace
        /// character, exactly as sent; otherwise the same member of a
        /// <c>detail</c> that is itself an object; otherwise null.
        /// </summary>
        internal static string? ProblemText(JsonObject? problem, string member)
        {
            if (problem is null)
            {
                return null;
            }
            return Text(problem[member]) ?? Text((problem["detail"] as JsonObject)?[member]);
        }

        private static string? Text(JsonNode? node) =>
            node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
                ? text
                : null;

        /// <summary>
        /// An error body parsed, exactly when it is a JSON object; null for a
        /// body that is empty, is not JSON, or is JSON that is not an object.
        /// </summary>
        /// <remarks>
        /// Built node by node from a <see cref="JsonDocument"/> rather than by
        /// <c>JsonNode.Parse</c>, whose objects are filled lazily: a name
        /// repeated in the body then throws <see cref="ArgumentException"/> on
        /// first access, out of whatever was reading it -- a property getter,
        /// or the constructor of this exception. Here a repeated name keeps its
        /// last value, as the other SDKs' decoders do, and the whole tree is
        /// built before it is returned.
        /// </remarks>
        internal static JsonObject? ParseProblem(string? body)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }
            try
            {
                using var document = JsonDocument.Parse(body!);
                return document.RootElement.ValueKind == JsonValueKind.Object
                    ? (JsonObject?)Materialize(document.RootElement)
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static JsonNode? Materialize(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    var map = new JsonObject();
                    foreach (var property in element.EnumerateObject())
                    {
                        // The indexer replaces: a repeated name keeps its last value.
                        map[property.Name] = Materialize(property.Value);
                    }
                    return map;
                case JsonValueKind.Array:
                    var list = new JsonArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        list.Add(Materialize(item));
                    }
                    return list;
                case JsonValueKind.Null:
                    return null;
                default:
                    // A string, number or boolean, backed by its element as
                    // JsonNode.Parse would leave it, so a number stays exactly
                    // as written; cloned, because the document it came from is
                    // disposed on return.
                    return JsonValue.Create(element.Clone());
            }
        }
    }

    /// <summary>The browser device sign-in request was denied by the user.</summary>
    public sealed class ThalovantDeviceAccessDeniedException : ThalovantException
    {
        public ThalovantDeviceAccessDeniedException(string message) : base(message)
        {
        }
    }

    /// <summary>The device sign-in code expired before it was approved.</summary>
    public sealed class ThalovantDeviceCodeExpiredException : ThalovantException
    {
        public ThalovantDeviceCodeExpiredException(string message) : base(message)
        {
        }
    }

    /// <summary>The provided identity document is missing fields or unreadable.</summary>
    public sealed class ThalovantIdentityException : ThalovantException
    {
        public ThalovantIdentityException(string message) : base(message)
        {
        }
    }

    /// <summary>The hub data-plane connection could not be established or was lost.</summary>
    public sealed class ThalovantConnectionException : ThalovantException
    {
        public ThalovantConnectionException(string message) : base(message)
        {
        }

        public ThalovantConnectionException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }

    /// <summary>The hub reported a runtime failure while handling a request.</summary>
    public class ThalovantRuntimeException : ThalovantException
    {
        public ThalovantRuntimeException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// The numbers behind a refusal that is a spent allowance, not a policy.
    /// <para>
    /// The intent-quota policy denies with <c>intent_quota_exceeded</c> and
    /// sends which counter ran out (<c>daily</c>, <c>monthly</c>), what it
    /// allows, how much was used, and how many seconds until it resets.
    /// Without them a caller can only say "refused", which is what an app
    /// showed somebody who had simply used up the day.
    /// </para>
    /// </summary>
    public sealed class ThalovantQuota
    {
        public ThalovantQuota(string period, long limit, long used, long resetAfter)
        {
            Period = period;
            Limit = limit;
            Used = used;
            ResetAfter = resetAfter;
        }

        /// <summary>The counter that ran out, as the hub names it.</summary>
        public string Period { get; }

        /// <summary>What that counter allows in its period.</summary>
        public long Limit { get; }

        /// <summary>How much of it was used.</summary>
        public long Used { get; }

        /// <summary>Seconds until the counter resets, or 0 when the hub did not say.</summary>
        public long ResetAfter { get; }
    }

    /// <summary>
    /// The hub refused a message, the instant it did.
    /// <para>
    /// Three different things arrive as <c>hive.policy.denied</c>, and each
    /// needs something different said about it: an allow-list refusal
    /// (<c>acl_disallowed_type</c>, with <see cref="Allowed"/>), a spent
    /// allowance (<c>intent_quota_exceeded</c>, with <see cref="Quota"/>), and
    /// a hub whose agent bus is down (<c>backend_unavailable</c>), which
    /// nothing the caller does will fix.
    /// </para>
    /// </summary>
    public sealed class ThalovantPolicyDeniedException : ThalovantRuntimeException
    {
        /// <summary>The hub's code for a refusal that is a spent quota, not a policy.</summary>
        public const string QuotaExceededCode = "intent_quota_exceeded";

        /// <summary>The hub's code for a refusal because its own agent bus is down.</summary>
        public const string BackendUnavailableCode = "backend_unavailable";

        /// <summary>The message type the hub refused, for example <c>recognizer_loop:utterance</c>.</summary>
        public string DeniedType { get; }

        /// <summary>The hub's machine-readable code; empty when absent.</summary>
        public string Code { get; }

        /// <summary>The hub's human-readable reason; empty when absent.</summary>
        public string Reason { get; }

        /// <summary>
        /// The message types the connection is allowed to publish, as the hub
        /// listed them: non-empty string entries, trimmed. Anything else the
        /// hub put in the list is not a message type and is dropped.
        /// </summary>
        public IReadOnlyList<string> Allowed { get; }

        /// <summary>The numbers behind a spent allowance; null for any other refusal.</summary>
        public ThalovantQuota? Quota { get; }

        public ThalovantPolicyDeniedException(
            string deniedType,
            string? code = null,
            string? reason = null,
            IReadOnlyList<string>? allowed = null,
            ThalovantQuota? quota = null)
            : base(Describe(deniedType, code, reason, quota))
        {
            DeniedType = deniedType;
            Code = code ?? "";
            Reason = reason ?? "";
            Allowed = allowed ?? Array.Empty<string>();
            Quota = quota;
        }

        private static string Describe(string deniedType, string? code, string? reason, ThalovantQuota? quota)
        {
            // Advice follows the kind of refusal. Telling somebody who used up
            // their day to "allow this connection to publish
            // recognizer_loop:utterance" sent them to a page that could not help.
            if (quota != null)
            {
                if (quota.Limit == 0 && quota.Used == 0 && quota.ResetAfter == 0 && quota.Period.Length == 0)
                {
                    // Refused on a quota, with none of the numbers. "All
                    // questions used" would be inventing one.
                    return $"The hub refused '{deniedType}': a quota has run out.";
                }
                var used = quota.Limit > 0 ? $"{quota.Used} of {quota.Limit}" : "all";
                var period = quota.Period.Length > 0 ? $" {quota.Period}" : "";
                var resets = quota.ResetAfter > 0 ? $"; it resets in {quota.ResetAfter}s" : "";
                return $"The hub refused '{deniedType}': {used}{period} questions used{resets}.";
            }
            if (code == BackendUnavailableCode)
            {
                var detail = !string.IsNullOrEmpty(reason) ? $": {reason}" : "";
                return $"The hub could not reach its assistant{detail}. Try again shortly.";
            }
            var fallback = !string.IsNullOrEmpty(reason) ? reason
                : !string.IsNullOrEmpty(code) ? code
                : "refused by the hub's policy";
            return $"The hub refused '{deniedType}': {fallback}. Allow this connection to publish "
                + $"'{deniedType}' in the dashboard's connection settings.";
        }

        /// <summary>
        /// Builds the exception from a <c>hive.policy.denied</c> event. The
        /// policy's own detail rides nested under <c>data.data</c>
        /// (hivemind-core <c>_send_policy_denied</c>).
        /// </summary>
        public static ThalovantPolicyDeniedException FromEvent(ThalovantEvent busEvent)
        {
            var data = busEvent.Data;
            var allowed = new List<string>();
            var inner = JsonUtil.AsObject(data["data"]) as JsonObject;
            if (inner?["allowed"] is JsonArray listed)
            {
                foreach (var item in listed)
                {
                    // Non-empty string entries, trimmed -- the platform
                    // contract's wording. A number or a null in the list is not
                    // a message type, and stringifying one would put "3" or
                    // "null" in front of an operator reading which types to
                    // allow; a blank one names nothing at all.
                    if (JsonUtil.GetString(item)?.Trim() is string type && type.Length > 0)
                    {
                        allowed.Add(type);
                    }
                }
            }
            var code = JsonUtil.OptionalString(data["code"]);
            ThalovantQuota? quota = null;
            if (code == QuotaExceededCode)
            {
                quota = new ThalovantQuota(
                    JsonUtil.OptionalString(inner?["period"]) ?? "",
                    WholeCount(inner?["limit"]),
                    WholeCount(inner?["used"]),
                    WholeCount(inner?["reset_after"]));
            }
            return new ThalovantPolicyDeniedException(
                JsonUtil.OptionalString(data["denied_type"]) ?? "",
                code,
                JsonUtil.OptionalString(data["reason"]),
                allowed,
                quota);
        }

        /// <summary>
        /// A whole, non-negative count from the wire, or 0: never a bool, never
        /// a guess. A negative limit, usage or reset time is not something a
        /// policy can mean, and passing one through would have an app say
        /// "-1 of -5 questions used".
        /// </summary>
        private static long WholeCount(JsonNode? value)
        {
            var whole = ReadWhole(value);
            return whole >= 0 && whole <= MaxCount ? whole : 0;
        }

        /// <summary>
        /// The largest count the wire can carry, being the largest whole number
        /// every JSON decoder holds exactly. Above it a decoder backed by a
        /// double can no longer tell one whole number from the next, so two
        /// SDKs would report different allowances for the same denial -- and a
        /// count nobody can agree on is worse than none.
        /// </summary>
        internal const long MaxCount = (1L << 53) - 1;

        /// <summary>Reads the number out of whatever shape holds it; the range is WholeCount's.</summary>
        private static long ReadWhole(JsonNode? value)
        {
            if (value is not JsonValue node) return 0;
            // Every numeric shape a JsonValue can hold: TryGetValue<T> does not
            // coerce, so one built in code from an int, a uint, a decimal or a
            // float answers only its own T -- and reading only long made a
            // quota assembled in memory come back as zeros. Whole,
            // and inside a signed 64-bit integer, which is as far as this can
            // read; WholeCount then applies the count rule's own ceiling.
            if (node.TryGetValue<long>(out var number)) return Math.Max(number, 0);
            if (node.TryGetValue<int>(out var small)) return Math.Max((long)small, 0);
            if (node.TryGetValue<uint>(out var unsignedSmall)) return unsignedSmall;
            if (node.TryGetValue<ulong>(out var unsigned)) return unsigned <= long.MaxValue ? (long)unsigned : 0;
            if (node.TryGetValue<byte>(out var byteValue)) return byteValue;
            if (node.TryGetValue<short>(out var shortValue)) return Math.Max((long)shortValue, 0);
            if (node.TryGetValue<decimal>(out var exact))
            {
                return exact == Math.Truncate(exact) && exact >= 0 && exact <= long.MaxValue ? (long)exact : 0;
            }
            if (node.TryGetValue<double>(out var real)) return WholeInRange(real);
            if (node.TryGetValue<float>(out var single)) return WholeInRange(single);
            if (node.TryGetValue<string>(out var text) && long.TryParse(text.Trim(), out var parsed))
            {
                return Math.Max(parsed, 0);
            }
            return 0;
        }

        private static long WholeInRange(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value) && value == Math.Truncate(value)
                && value >= 0 && value <= long.MaxValue
                ? (long)value
                : 0;

    }

    /// <summary>
    /// The hub understood a question and has nothing for it.
    /// <para>
    /// <c>ovos.intent.unmatched</c> (<c>complete_intent_failure</c> from older
    /// hubs) is neither a refusal nor a fault: nothing went wrong, the question
    /// is outside what this hub can do. As a bare runtime error a caller could
    /// only report that something failed.
    /// </para>
    /// </summary>
    public sealed class ThalovantUnansweredException : ThalovantRuntimeException
    {
        /// <summary>The hub's own words, when it sent any.</summary>
        public string Said { get; }

        public ThalovantUnansweredException(string? said = null)
            : base(string.IsNullOrEmpty(said) ? "The hub has no skill that answers this." : said!)
        {
            Said = said ?? "";
        }
    }

    /// <summary>The hub did not respond within the allotted time.</summary>
    public sealed class ThalovantTimeoutException : ThalovantException
    {
        public ThalovantTimeoutException(string message) : base(message)
        {
        }
    }

    /// <summary>The requested data-plane protocol is not usable with this identity or SDK.</summary>
    public sealed class ThalovantUnsupportedProtocolException : ThalovantException
    {
        public ThalovantUnsupportedProtocolException(string message) : base(message)
        {
        }
    }
}
