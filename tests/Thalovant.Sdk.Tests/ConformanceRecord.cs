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
    private static void Canonical(JsonNode? node, StringBuilder into, bool verbatimFractions) {
        switch (node) {
            case null:
                into.Append("null");
                break;
            case JsonArray array: {
                into.Append('[');
                for (var index = 0; index < array.Count; index++) {
                    if (index > 0) into.Append(',');
                    Canonical(array[index], into, verbatimFractions);
                }
                into.Append(']');
                break;
            }
            case JsonObject map: {
                into.Append('{');
                var first = true;
                foreach (var key in map.Select(pair => pair.Key).OrderBy(key => key, CodePointOrder.Instance)) {
                    if (!first) into.Append(',');
                    first = false;
                    into.Append(QuoteString(key)).Append(':');
                    Canonical(map[key], into, verbatimFractions);
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
                    into.Append(WholeNumber(value.ToJsonString(), verbatimFractions));
                }
                break;
            }
        }
    }

    /// <summary>
    /// Keys in Unicode code point order, as Python's <c>sort_keys</c> sorts them.
    /// Ordinal comparison orders UTF-16 code units, which puts a character above
    /// U+FFFF (a surrogate pair, D800-DFFF) before U+E000-U+FFFF.
    /// </summary>
    private sealed class CodePointOrder : IComparer<string> {
        internal static readonly CodePointOrder Instance = new();

        public int Compare(string? left, string? right) {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var a = left.EnumerateRunes().GetEnumerator();
            var b = right.EnumerateRunes().GetEnumerator();
            while (true) {
                var more = a.MoveNext();
                var others = b.MoveNext();
                if (!more || !others) return more.CompareTo(others);
                var order = a.Current.Value.CompareTo(b.Current.Value);
                if (order != 0) return order;
            }
        }
    }

    /// <summary>Spell a whole number the way every other language spells it.</summary>
    /// <remarks>
    /// conversation-vectors.json carries activated_at as 1.0, and every other
    /// SDK writes that value as 1.
    /// </remarks>
    private static string WholeNumber(string raw, bool verbatimFractions) {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        // Exactly, through BigInteger and decimal rather than double: a double
        // loses integer precision above 2^53 and a cast clamps rather than
        // failing, which would record a digest for a value nobody produced.
        if (System.Numerics.BigInteger.TryParse(
                raw, System.Globalization.NumberStyles.Integer, invariant, out var whole)) {
            return whole.ToString(invariant);
        }
        if (decimal.TryParse(raw, System.Globalization.NumberStyles.Float, invariant, out var exact)
            && decimal.Truncate(exact) == exact) {
            return ((System.Numerics.BigInteger)exact).ToString(invariant);
        }
        // A vector file's own number, spelled as its author wrote it. The
        // file is vendored byte for byte from the reference, whose json.dumps
        // wrote it, so the token is already Python's spelling -- which is what
        // the reference digests. connection-admission-vectors.json carries
        // poll intervals of 0.01 s.
        if (verbatimFractions) return raw;
        // Refused rather than passed through for anything this SDK produced.
        // Only a whole number is written the same way by every language here
        // -- 1.5 and 1E-07 (which Python spells 1e-07) have per-language
        // spellings -- so a produced fraction should stop rather than lie.
        throw new InvalidOperationException(
            $"conformance: cannot canonicalise {raw}: only whole numbers are "
            + "spelled the same way in every language");
    }

    /// <summary>A string spelled exactly as the reference's <c>json.dumps(..., ensure_ascii=False)</c> spells it.</summary>
    /// <remarks>
    /// Only the quote, the backslash and the C0 controls are escaped -- the
    /// latter as <c>\n</c>, <c>\r</c>, <c>\t</c>, <c>\b</c>, <c>\f</c>, or <c>\u00xx</c> in lower
    /// case -- and everything else is written as itself. System.Text.Json, even
    /// with UnsafeRelaxedJsonEscaping, also escapes U+007F, the C1 controls and
    /// the line and paragraph separators, so home-link-vectors.json, whose
    /// speech cases hold U+2028, digested differently from the reference's
    /// although every case matched.
    /// </remarks>
    private static string QuoteString(string value) {
        var into = new StringBuilder(value.Length + 2);
        into.Append('"');
        foreach (var character in value) {
            switch (character) {
                case '"': into.Append("\\\""); break;
                case '\\': into.Append("\\\\"); break;
                case '\n': into.Append("\\n"); break;
                case '\r': into.Append("\\r"); break;
                case '\t': into.Append("\\t"); break;
                case '\b': into.Append("\\b"); break;
                case '\f': into.Append("\\f"); break;
                default:
                    if (character < ' ') into.Append("\\u").Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else into.Append(character);
                    break;
            }
        }
        return into.Append('"').ToString();
    }

    internal static string CanonicalDigest(JsonNode? node) => Digest(node, verbatimFractions: false);

    /// <summary>The digest of a vendored vector file, whose fractions are the reference's own tokens.</summary>
    private static string VectorDigest(JsonNode? node) => Digest(node, verbatimFractions: true);

    private static string Digest(JsonNode? node, bool verbatimFractions) {
        var builder = new StringBuilder();
        Canonical(node, builder, verbatimFractions);
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
                    ["digest"] = VectorDigest(parsed),
                    ["cases"] = recorded,
                };
            }
            var document = new JsonObject { ["schema_version"] = 1, ["results"] = results };
            var directory = Path.GetDirectoryName(Path.GetFullPath(Target));
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var text = new StringBuilder();
            Pretty(document, text, 0);
            File.WriteAllText(Target, text.Append('\n').ToString());
        }
    }

    /// <summary>
    /// The results file as the reference writes it --
    /// <c>json.dumps(..., indent=2, sort_keys=True)</c> and a final newline -- so
    /// that a diff between the two reads as a diff of results. Only the digests
    /// inside are compared; keys go in code point order, non-ASCII is escaped as
    /// Python's default <c>ensure_ascii</c> escapes it.
    /// </summary>
    private static void Pretty(JsonNode? node, StringBuilder into, int depth) {
        switch (node) {
            case JsonObject map when map.Count > 0: {
                into.Append("{\n");
                var first = true;
                foreach (var key in map.Select(pair => pair.Key).OrderBy(key => key, CodePointOrder.Instance)) {
                    if (!first) into.Append(",\n");
                    first = false;
                    into.Append(' ', 2 * (depth + 1)).Append(AsciiString(key)).Append(": ");
                    Pretty(map[key], into, depth + 1);
                }
                into.Append('\n').Append(' ', 2 * depth).Append('}');
                break;
            }
            case JsonObject:
                into.Append("{}");
                break;
            case JsonValue value when value.TryGetValue<string>(out var text):
                into.Append(AsciiString(text));
                break;
            default:
                into.Append(node?.ToJsonString() ?? "null");
                break;
        }
    }

    /// <summary>A string as Python's <c>json.dumps</c> writes it by default: every character from U+007F up escaped as <c>\uxxxx</c>.</summary>
    private static string AsciiString(string value) {
        var quoted = QuoteString(value);
        var into = new StringBuilder(quoted.Length);
        foreach (var character in quoted) {
            if (character < 0x7F) into.Append(character);
            else into.Append("\\u").Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
        }
        return into.ToString();
    }
}
