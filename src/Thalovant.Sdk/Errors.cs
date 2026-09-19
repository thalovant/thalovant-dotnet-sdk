using System;
using System.Collections.Generic;
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
    public sealed class ThalovantApiException : ThalovantException
    {
        /// <summary>HTTP status code, when the server produced a response.</summary>
        public int? StatusCode { get; }

        /// <summary>Raw response body, when the server produced a response.</summary>
        public string? Body { get; }

        /// <summary>
        /// Machine-readable error code decoded from the body, when present
        /// (top-level <c>code</c>, or <c>detail.code</c> for FastAPI error envelopes).
        /// </summary>
        public string? ErrorCode { get; }

        public ThalovantApiException(string message, int? statusCode = null, string? body = null, string? errorCode = null)
            : base(message)
        {
            StatusCode = statusCode;
            Body = body;
            ErrorCode = errorCode ?? DecodeErrorCode(body);
        }

        internal static string? DecodeErrorCode(string? body)
        {
            if (string.IsNullOrEmpty(body))
            {
                return null;
            }
            JsonObject? parsed;
            try
            {
                parsed = JsonNode.Parse(body) as JsonObject;
            }
            catch (Exception)
            {
                return null;
            }
            if (parsed is null)
            {
                return null;
            }
            if (parsed["code"] is JsonValue topLevel && topLevel.TryGetValue<string>(out var code))
            {
                return code;
            }
            if (parsed["detail"] is JsonObject detail
                && detail["code"] is JsonValue nested
                && nested.TryGetValue<string>(out var detailCode))
            {
                return detailCode;
            }
            return null;
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
