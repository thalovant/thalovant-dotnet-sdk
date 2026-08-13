using System;
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
    public sealed class ThalovantRuntimeException : ThalovantException
    {
        public ThalovantRuntimeException(string message) : base(message)
        {
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
