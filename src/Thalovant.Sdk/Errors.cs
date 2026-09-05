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
    /// The hub refused a message type this connection may not publish.
    /// <para>
    /// The hub answers <c>hive.policy.denied</c> at once, naming the type and the
    /// list it does allow; surfacing that as an exception saves the caller a
    /// timeout and tells the operator exactly what to add to the connection's
    /// allow-list.
    /// </para>
    /// </summary>
    public sealed class ThalovantPolicyDeniedException : ThalovantRuntimeException
    {
        /// <summary>The message type the hub refused, for example <c>ovos.intent.list</c>.</summary>
        public string DeniedType { get; }

        /// <summary>The hub's machine-readable code, for example <c>acl_disallowed_type</c>; empty when absent.</summary>
        public string Code { get; }

        /// <summary>The hub's human-readable reason; empty when absent.</summary>
        public string Reason { get; }

        /// <summary>
        /// The message types the connection is allowed to publish, as the hub
        /// listed them: non-empty string entries, trimmed. Anything else the
        /// hub put in the list is not a message type and is dropped.
        /// </summary>
        public IReadOnlyList<string> Allowed { get; }

        public ThalovantPolicyDeniedException(
            string deniedType,
            string? code = null,
            string? reason = null,
            IReadOnlyList<string>? allowed = null)
            : base(Describe(deniedType, code, reason))
        {
            DeniedType = deniedType;
            Code = code ?? "";
            Reason = reason ?? "";
            Allowed = allowed ?? Array.Empty<string>();
        }

        private static string Describe(string deniedType, string? code, string? reason)
        {
            var detail = !string.IsNullOrEmpty(reason) ? reason
                : !string.IsNullOrEmpty(code) ? code
                : "refused by the hub's policy";
            return $"The hub refused '{deniedType}': {detail}. Allow this connection to publish "
                + $"'{deniedType}' in the dashboard's connection settings.";
        }

        /// <summary>
        /// Builds the exception from a <c>hive.policy.denied</c> event:
        /// <c>{denied_type, code, reason, data: {msg_type, allowed}}</c>.
        /// </summary>
        public static ThalovantPolicyDeniedException FromEvent(ThalovantEvent busEvent)
        {
            var data = busEvent.Data;
            var allowed = new List<string>();
            if (JsonUtil.AsObject(data["data"]) is JsonObject inner && inner["allowed"] is JsonArray listed)
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
            return new ThalovantPolicyDeniedException(
                JsonUtil.OptionalString(data["denied_type"]) ?? "",
                JsonUtil.OptionalString(data["code"]),
                JsonUtil.OptionalString(data["reason"]),
                allowed);
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
