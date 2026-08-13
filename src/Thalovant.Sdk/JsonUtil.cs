using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Thalovant
{
    /// <summary>
    /// Small helpers over <see cref="System.Text.Json.Nodes.JsonNode"/> shared by the
    /// SDK. JSON objects on the wire use snake_case field names exactly as the API
    /// pydantic schemas define them.
    /// </summary>
    internal static class JsonUtil
    {
        /// <summary>Parses <paramref name="text"/>, throwing when it is not a JSON object.</summary>
        internal static JsonObject ParseObject(string text)
        {
            var node = JsonNode.Parse(text);
            if (node is JsonObject parsed)
            {
                return parsed;
            }
            throw new JsonException("Expected a JSON object.");
        }

        /// <summary>Deep-clones a node so it can be attached to a new parent.</summary>
        internal static JsonNode? Clone(JsonNode? node)
        {
            return node?.DeepClone();
        }

        internal static JsonObject CloneObject(JsonObject? source)
        {
            return source is null ? new JsonObject() : (JsonObject)source.DeepClone();
        }

        internal static JsonArray CloneArray(JsonArray? source)
        {
            return source is null ? new JsonArray() : (JsonArray)source.DeepClone();
        }

        internal static JsonObject? AsObject(JsonNode? node)
        {
            return node as JsonObject;
        }

        /// <summary>First value present under any of <paramref name="keys"/>.</summary>
        internal static JsonNode? First(JsonObject source, params string[] keys)
        {
            foreach (var key in keys)
            {
                if (source.TryGetPropertyValue(key, out var value) && value is not null)
                {
                    return value;
                }
            }
            return null;
        }

        /// <summary>Whether the object carries the key, even with a JSON null value.</summary>
        internal static bool HasKey(JsonObject source, string key)
        {
            return source.ContainsKey(key);
        }

        internal static string? GetString(JsonNode? node)
        {
            return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        }

        /// <summary>
        /// Coerces scalars to a trimmed non-empty string (mirrors the sibling SDKs'
        /// lenient identity parsing), or null.
        /// </summary>
        internal static string? OptionalString(JsonNode? node)
        {
            if (node is not JsonValue value)
            {
                return null;
            }
            string? normalized = null;
            if (value.TryGetValue<string>(out var text))
            {
                normalized = text.Trim();
            }
            else if (value.TryGetValue<bool>(out var flag))
            {
                normalized = flag ? "true" : "false";
            }
            else if (value.TryGetValue<long>(out var integer))
            {
                normalized = integer.ToString(CultureInfo.InvariantCulture);
            }
            else if (value.TryGetValue<double>(out var number))
            {
                normalized = number.ToString(CultureInfo.InvariantCulture);
            }
            return string.IsNullOrEmpty(normalized) ? null : normalized;
        }

        internal static int? GetInt(JsonNode? node)
        {
            if (node is not JsonValue value)
            {
                return null;
            }
            if (value.TryGetValue<int>(out var integer))
            {
                return integer;
            }
            if (value.TryGetValue<long>(out var longValue) && longValue >= int.MinValue && longValue <= int.MaxValue)
            {
                return (int)longValue;
            }
            if (value.TryGetValue<double>(out var number) && number == Math.Floor(number)
                && number >= int.MinValue && number <= int.MaxValue)
            {
                return (int)number;
            }
            return null;
        }

        /// <summary>Boolean coercion for enabled-style flags ("1", "true", "yes", "on", nested {"enabled": ...}).</summary>
        internal static bool EnabledValue(JsonNode? node, bool fallback)
        {
            switch (node)
            {
                case JsonObject nested:
                    return EnabledValue(nested["enabled"], fallback);
                case JsonValue value when value.TryGetValue<bool>(out var flag):
                    return flag;
                case JsonValue value when value.TryGetValue<string>(out var text):
                {
                    var normalized = text.Trim().ToLowerInvariant();
                    if (normalized is "1" or "true" or "yes" or "on")
                    {
                        return true;
                    }
                    if (normalized is "0" or "false" or "no" or "off")
                    {
                        return false;
                    }
                    return fallback;
                }
                case JsonValue value when value.TryGetValue<double>(out var number):
                    return number != 0;
                default:
                    return fallback;
            }
        }

        /// <summary>JavaScript-style truthiness used by the handshake detection.</summary>
        internal static bool IsTruthy(JsonNode? node)
        {
            switch (node)
            {
                case null:
                    return false;
                case JsonObject:
                case JsonArray:
                    return true;
                case JsonValue value when value.TryGetValue<bool>(out var flag):
                    return flag;
                case JsonValue value when value.TryGetValue<string>(out var text):
                    return text.Length > 0;
                case JsonValue value when value.TryGetValue<double>(out var number):
                    return number != 0;
                default:
                    return false;
            }
        }
    }
}
