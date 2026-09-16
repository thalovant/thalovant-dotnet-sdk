using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Thalovant.Sdk.Tests;

/// <summary>
/// Record what this SDK produced for each conformance case.
/// </summary>
/// <remarks>
/// <para>
/// The parity gate can check that a test <em>names</em> a vector file. It
/// cannot check that the test ran it: a name reaching a loader call is
/// evidence of intent, not of execution. So the gate stopped asking about the
/// test and started asking about its output -- this writes what we computed,
/// and the checker compares it against what the Python reference computed for
/// the same case.
/// </para>
/// <para>
/// The digest has to agree across languages, so it is deliberately the same
/// recipe as the reference's tests/conformance_record.py: JSON with keys
/// sorted at every depth, no insignificant whitespace, non-ASCII left as
/// itself, SHA-256 of the UTF-8 bytes, and a whole number spelled without a
/// fractional part.
/// </para>
/// <para>
/// Set THALOVANT_CONFORMANCE_OUT to a path and run the suite; the results are
/// written when the test process exits.
/// </para>
/// </remarks>
internal static class ConformanceRecord {
    private static readonly object Gate = new();
    private static readonly SortedDictionary<string, SortedDictionary<string, string>> Results = new(StringComparer.Ordinal);
    private static readonly string? Target = Environment.GetEnvironmentVariable("THALOVANT_CONFORMANCE_OUT");
    private static bool _armed;

    /// <summary>Canonical JSON, built by hand.</summary>
    /// <remarks>
    /// JsonNode keeps insertion order, and System.Text.Json escapes every
    /// non-ASCII character unless told not to -- both of which are this
    /// platform's spelling of a value rather than the value.
    /// </remarks>
    private static void Canonical(JsonNode? node, StringBuilder into) {
        switch (node) {
            case null:
                into.Append("null");
                break;
            case JsonArray array: {
                into.Append('[');
                for (var index = 0; index < array.Count; index++) {
                    if (index > 0) into.Append(',');
                    Canonical(array[index], into);
                }
                into.Append(']');
                break;
            }
            case JsonObject map: {
                into.Append('{');
                var first = true;
                foreach (var key in map.Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal)) {
                    if (!first) into.Append(',');
                    first = false;
                    into.Append(QuoteString(key)).Append(':');
                    Canonical(map[key], into);
                }
                into.Append('}');
                break;
            }
            default: {
                var value = (JsonValue)node;
                if (value.TryGetValue<string>(out var text)) {
                    into.Append(QuoteString(text));
                } else if (value.TryGetValue<bool>(out var flag)) {
                    into.Append(flag ? "true" : "false");
                } else {
                    into.Append(WholeNumber(value.ToJsonString()));
                }
                break;
            }
        }
    }

    /// <summary>Spell a whole number the way every other language spells it.</summary>
    /// <remarks>
    /// conversation-vectors.json carries activated_at as 1.0, and every other
    /// SDK writes that value as 1.
    /// </remarks>
    private static string WholeNumber(string raw) {
        if (!raw.Contains('.') && !raw.Contains('e') && !raw.Contains('E')) return raw;
        return double.TryParse(raw, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var number)
               && Math.Floor(number) == number && !double.IsInfinity(number)
            ? ((long)number).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : raw;
    }

    private static string QuoteString(string value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    internal static string CanonicalDigest(JsonNode? node) {
        var builder = new StringBuilder();
        Canonical(node, builder);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    /// <summary>Record what this SDK produced for one case of one vector file.</summary>
    internal static void Record(string vectorFile, string name, JsonNode? produced) {
        if (Target is null) return;
        var digest = CanonicalDigest(produced);
        lock (Gate) {
            if (!_armed) {
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Write();
                _armed = true;
            }
            if (!Results.TryGetValue(vectorFile, out var cases)) {
                cases = new SortedDictionary<string, string>(StringComparer.Ordinal);
                Results[vectorFile] = cases;
            }
            if (cases.TryGetValue(name, out var previous) && previous != digest) {
                throw new InvalidOperationException($"{vectorFile}/{name}: recorded twice with different outputs");
            }
            cases[name] = digest;
        }
    }

    private static void Write() {
        if (Target is null) return;
        lock (Gate) {
            var results = new JsonObject();
            foreach (var (vectorFile, cases) in Results) {
                var parsed = JsonNode.Parse(File.ReadAllText(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures", vectorFile)));
                var recorded = new JsonObject();
                foreach (var (name, digest) in cases) recorded[name] = digest;
                // The parsed JSON, not the bytes: a vendored copy is allowed to
                // differ in indentation and line endings, and the checker
                // accepts it on the same terms.
                results[vectorFile] = new JsonObject {
                    ["digest"] = CanonicalDigest(parsed),
                    ["cases"] = recorded,
                };
            }
            var document = new JsonObject { ["schema_version"] = 1, ["results"] = results };
            var directory = Path.GetDirectoryName(Path.GetFullPath(Target));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(Target, document.ToJsonString(new JsonSerializerOptions {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }) + "\n");
        }
    }
}
