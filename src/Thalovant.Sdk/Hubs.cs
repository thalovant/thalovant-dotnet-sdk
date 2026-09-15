using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Thalovant.Sdk
{
    /// <summary>
    /// What to call a hub on a screen somebody is reading.
    /// </summary>
    /// <remarks>
    /// Every control-plane read in this SDK returns raw JSON, so each caller
    /// picks its own fields -- and on 2026-09-15 a phone offered somebody a
    /// list of rooms called "ops-copilot", "daily-desk", "news-stream". Those
    /// are slugs. The app was not careless: it read <c>name</c> and preferred
    /// it over <c>slug</c>, and on that deployment <c>name</c> holds the slug.
    /// The name a person was shown when the hub was made lives in
    /// <c>spec.catalog.title</c>. One place to get that wrong is better than
    /// one per app.
    /// </remarks>
    public static class Hubs
    {
        /// <summary>The readable name of a hub, never a slug when anything better exists.</summary>
        public static string DisplayName(JsonObject hub)
        {
            if (hub is null) throw new ArgumentNullException(nameof(hub));

            var catalog = (hub["spec"] as JsonObject)?["catalog"] as JsonObject;
            var title = Text(catalog?["title"]);
            if (title is not null) return title;

            var name = Text(hub["name"]);
            var slug = Text(hub["slug"]);
            // A name that is exactly the slug is the slug.
            if (name is not null && name != slug) return name;

            var identifier = name ?? slug;
            if (identifier is null) return "A Thalovant hub";
            var words = identifier
                .Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(word => char.ToUpperInvariant(word[0]) + word.Substring(1));
            var readable = string.Join(" ", words);
            return string.IsNullOrEmpty(readable) ? "A Thalovant hub" : readable;
        }

        private static string? Text(JsonNode? node)
        {
            var raw = JsonUtil.GetString(node);
            if (string.IsNullOrWhiteSpace(raw)) return null;
            return raw!.Trim();
        }
    }
}
