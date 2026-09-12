using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Thalovant {
    public static partial class ThalovantContext {
        /// <summary>Build the request location shape read by OVOS. City is required.</summary>
        public static JsonObject? BuildLocation(string city = "", string region = "", string country = "",
            double? latitude = null, double? longitude = null, string timezone = "") {
            if (string.IsNullOrWhiteSpace(city)) return null;
            var result = new JsonObject { ["city"] = city.Trim() };
            if (!string.IsNullOrWhiteSpace(region)) result["region"] = region.Trim();
            if (!string.IsNullOrWhiteSpace(country)) result["country_code"] = country.Trim().ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(timezone)) result["timezone"] = new JsonObject { ["code"] = timezone.Trim() };
            if (latitude is double lat && longitude is double lon && (lat != 0 || lon != 0) && lat >= -90 && lat <= 90 && lon >= -180 && lon <= 180)
                result["coordinate"] = new JsonObject { ["latitude"] = lat, ["longitude"] = lon };
            return result;
        }
        public static JsonObject? RequestContext(JsonObject? context = null, string? sttLang = null,
            IEnumerable<string>? pipeline = null, JsonObject? location = null) {
            var result = JsonUtil.CloneObject(context);
            var stages = pipeline?.Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            if (stages?.Length > 0) {
                var session = JsonUtil.CloneObject(result["session"] as JsonObject);
                var array = new JsonArray(); foreach (var stage in stages) array.Add(stage);
                session["pipeline"] = array; result["session"] = session;
            }
            if (!string.IsNullOrWhiteSpace(sttLang)) result["stt_lang"] = sttLang!.Trim();
            if (location?.Count > 0) result["location"] = JsonUtil.CloneObject(location);
            return result.Count == 0 ? null : result;
        }
        private static readonly Regex OptionalPattern = new Regex(@"\[[^\[\]]*\]");
        private static readonly Regex GroupPattern = new Regex(@"\(([^()]*)\)");
        private static readonly Regex SlotPattern = new Regex(@"\{([a-z_][a-z0-9_]*)\}");
        public static string Speakable(string pattern, IReadOnlyDictionary<string,string>? slots = null) {
            var text = pattern;
            while (OptionalPattern.IsMatch(text)) text = OptionalPattern.Replace(text, "");
            while (GroupPattern.IsMatch(text)) text = GroupPattern.Replace(text, m => {
                var options = m.Groups[1].Value.Split('|').Select(s => s.Trim()).ToArray();
                var real = options.Where(s => s.Length > 0).ToArray();
                return real.Length < options.Length && real.Length <= 1 ? "" : real.FirstOrDefault() ?? "";
            });
            text = SlotPattern.Replace(text, m => slots != null && slots.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Groups[1].Value.Replace('_', ' '));
            return Regex.Replace(text, @"\s{2,}", " ").Trim(' ', ',');
        }
    }
    public partial class ThalovantClient {
        public Task<ThalovantReply> AskWithHintsAsync(string text, string? sttLang = null,
            IEnumerable<string>? pipeline = null, JsonObject? location = null,
            TimeSpan? timeout = null, string lang = "en-us", JsonObject? context = null,
            string? sessionId = null, string? requestId = null, TimeSpan? replySettle = null,
            TimeSpan? emptyReplyWait = null, CancellationToken cancellationToken = default) =>
            AskAsync(text, timeout, lang, ThalovantContext.RequestContext(context, sttLang, pipeline, location),
                sessionId, requestId, replySettle, emptyReplyWait, cancellationToken);
    }
    internal sealed class ReplyMediaBudget {
        internal int Dropped { get; private set; }
        private int _chars;
        private readonly HashSet<ThalovantEvent> _seen = new HashSet<ThalovantEvent>();
        internal bool Accept(ThalovantEvent e) {
            if (!e.IsAudio) return true;
            if (_seen.Contains(e)) return false;
            var encoded = JsonUtil.GetString(e.Data["binary_data"]);
            if (encoded == null || encoded.Length > ThalovantEvents.MaxAudioClipBytes * 2 || _chars + encoded.Length > ThalovantEvents.MaxReplyMediaBytes * 2) {
                Dropped++; return false;
            }
            _seen.Add(e); _chars += encoded.Length; return true;
        }
    }
}
