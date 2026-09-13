using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Thalovant
{
    public sealed class Intent
    {
        public string Id { get; }
        public string Name { get; }
        public string SkillId { get; }
        public string Engine { get; }
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Phrases { get; }
        public IReadOnlyList<string> Languages { get; }
        public Intent(string id, string name, string skillId, string engine, IReadOnlyDictionary<string, IReadOnlyList<string>>? phrases = null, IEnumerable<string>? languages = null)
        {
            Id = id; Name = name; SkillId = skillId; Engine = engine;
            Phrases = new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<string>>((phrases ?? new Dictionary<string, IReadOnlyList<string>>()).ToDictionary(p => p.Key, p => (IReadOnlyList<string>)Array.AsReadOnly(p.Value.ToArray())));
            Languages = Array.AsReadOnly((languages ?? Array.Empty<string>()).Concat(Phrases.Keys).Distinct().Where(Phrases.ContainsKey).ToArray());
        }
        public IReadOnlyList<string> Examples(string? language = null, int limit = 3)
        {
            var tag = string.IsNullOrEmpty(language) ? Languages.FirstOrDefault() : ThalovantContext.ClosestLanguage(language!, Languages);
            var pool = tag != null && Phrases.TryGetValue(tag, out var found) ? found : Array.Empty<string>();
            return limit <= 0 ? pool.ToArray() : ListingRules.Default.Rank(pool, language).Take(limit).ToArray();
        }
    }
    public sealed class Skill
    {
        public string Id { get; }
        public string Title { get; }
        public IReadOnlyList<string> Locales { get; }
        public IReadOnlyList<Intent> Intents { get; }
        public Skill(string id, string title, IEnumerable<string>? locales = null, IEnumerable<Intent>? intents = null) { Id = id; Title = title; Locales = Array.AsReadOnly((locales ?? Array.Empty<string>()).ToArray()); Intents = Array.AsReadOnly((intents ?? Array.Empty<Intent>()).ToArray()); }
        public bool DeclaresLocales => Locales.Count > 0;
        public bool? Speaks(string language) => Locales.Count == 0 ? (bool?)null : ThalovantContext.ClosestLanguage(language, Locales) != null;
    }
    public sealed class Inventory
    {
        public const int CacheVersion = 1;
        public string HubId { get; }
        public string HubName { get; }
        public string Source { get; }
        public string GeneratedAt { get; }
        public IReadOnlyList<Skill> Skills { get; }
        public IReadOnlyList<string> Notes { get; }
        public Inventory(string hubId, string hubName, string source, string generatedAt, IEnumerable<Skill>? skills = null, IEnumerable<string>? notes = null) { HubId = hubId; HubName = hubName; Source = source; GeneratedAt = generatedAt; Skills = Array.AsReadOnly((skills ?? Array.Empty<Skill>()).ToArray()); Notes = Array.AsReadOnly((notes ?? Array.Empty<string>()).ToArray()); }
        public bool Live => Source == "hub" || Source == "ovos-runtime";
        public IEnumerable<Intent> Intents => Skills.SelectMany(s => s.Intents);
        public bool HasPhrases => Intents.Any(i => i.Phrases.Count > 0);
        public JsonObject AsObject() => new JsonObject
        {
            ["cache_version"] = CacheVersion,
            ["hub_id"] = HubId,
            ["hub_name"] = HubName,
            ["source"] = Source,
            ["generated_at"] = GeneratedAt,
            ["notes"] = Strings(Notes),
            ["skills"] = new JsonArray(Skills.Select(skill => (JsonNode)new JsonObject
            {
                ["id"] = skill.Id,
                ["title"] = skill.Title,
                ["locales"] = Strings(skill.Locales),
                ["intents"] = new JsonArray(skill.Intents.Select(intent => (JsonNode)new JsonObject
                {
                    ["id"] = intent.Id,
                    ["name"] = intent.Name,
                    ["skill_id"] = intent.SkillId,
                    ["engine"] = intent.Engine,
                    ["languages"] = Strings(intent.Languages),
                    ["phrases"] = new JsonObject(intent.Phrases.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, Strings(pair.Value))))
                }).ToArray())
            }).ToArray())
        };
        private static JsonArray Strings(IEnumerable<string> values) => new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        private static JsonObject Object(JsonNode? node) => node as JsonObject ?? throw new FormatException("Expected inventory object");
        private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw new FormatException("Expected inventory string");
        private static JsonArray List(JsonNode? node) => node as JsonArray ?? throw new FormatException("Expected inventory list");
        public static Inventory FromObject(JsonNode? raw)
        {
            var data = Object(raw); if (data["cache_version"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number != CacheVersion) throw new FormatException("Not a current inventory cache");
            return new Inventory(Text(data["hub_id"]), Text(data["hub_name"]), Text(data["source"]), Text(data["generated_at"]), List(data["skills"]).Select(rawSkill =>
            {
                var skill = Object(rawSkill); return new Skill(Text(skill["id"]), Text(skill["title"]), List(skill["locales"]).Select(Text), List(skill["intents"]).Select(rawIntent =>
                {
                    var intent = Object(rawIntent); return new Intent(Text(intent["id"]), Text(intent["name"]), Text(intent["skill_id"]), Text(intent["engine"]), Object(intent["phrases"]).ToDictionary(p => p.Key, p => (IReadOnlyList<string>)List(p.Value).Select(Text).ToArray()), intent.ContainsKey("languages") ? List(intent["languages"]).Select(Text) : null);
                }));
            }), List(data["notes"]).Select(Text));
        }
    }
    public static class InventoryHelpers
    {
        public static IReadOnlyList<string> LanguagesPresent(Inventory inventory) => inventory.Skills.SelectMany(s => s.Locales.Concat(s.Intents.SelectMany(i => i.Phrases.Keys))).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
        public static string FriendlyTitle(string skillId)
        {
            var name = skillId; foreach (var prefix in new[] { "thalovant-skill-", "ovos-skill-", "skill-" }) if (name.StartsWith(prefix, StringComparison.Ordinal)) { name = name.Substring(prefix.Length); break; }
            var dot = name.LastIndexOf('.'); if (dot >= 0) name = name.Substring(0, dot); name = name.Replace('-', ' ').Replace('_', ' ').Trim(); return name.Length == 0 ? skillId : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
        }
        private static string[] Tokens(string name) => name.Split(new[] { '.', '_' }, StringSplitOptions.RemoveEmptyEntries);
        public static string Humanize(string name) => string.Join(" ", Tokens(name));
        public static (string? Kind, string Token) CommonAffix(IEnumerable<string> names)
        {
            var parts = names.Select(Tokens).ToArray(); if (parts.Length < 2 || parts.Any(p => p.Length < 2)) return (null, "");
            if (parts.Select(p => p[p.Length - 1]).Distinct().Count() == 1) return ("suffix", parts[0][parts[0].Length - 1]);
            if (parts.Select(p => p[0]).Distinct().Count() == 1) return ("prefix", parts[0][0]); return (null, "");
        }
        public static string StripAffix(string name, string? kind, string token)
        {
            if (kind == null) return name; var parts = Tokens(name).ToList(); if (kind == "suffix" && parts.LastOrDefault() == token) parts.RemoveAt(parts.Count - 1); else if (kind == "prefix" && parts.FirstOrDefault() == token) parts.RemoveAt(0); return parts.Count == 0 ? name : string.Join(" ", parts);
        }
        public static int CompareNames(string left, string right)
        {
            string[] Chunks(string value) => Regex.Matches(value.ToLowerInvariant(), "[0-9]+|[^0-9]+").Cast<Match>().Select(m => m.Value).ToArray(); var a = Chunks(left); var b = Chunks(right);
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++) { var x = a[i]; var y = b[i]; bool nx = x[0] >= '0' && x[0] <= '9', ny = y[0] >= '0' && y[0] <= '9'; int compare = nx && ny ? BigInteger.Parse(x, CultureInfo.InvariantCulture).CompareTo(BigInteger.Parse(y, CultureInfo.InvariantCulture)) : nx != ny ? (nx ? -1 : 1) : StringComparer.Ordinal.Compare(x, y); if (compare != 0) return compare; }
            return a.Length.CompareTo(b.Length);
        }
        public static string? IdentityHost(string path) { try { var value = JsonNode.Parse(File.ReadAllText(path))?["default_master"]?.GetValue<string>(); var host = OriginPreference.HubHostname(value); return host.Length == 0 ? null : host; } catch { return null; } }
    }
    public sealed class InventoryCache
    {
        public string DirectoryPath { get; }
        public TimeSpan Ttl { get; }
        public InventoryCache(string? directory = null, TimeSpan? ttl = null) { DirectoryPath = directory ?? Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"), "thalovant"); Ttl = ttl ?? TimeSpan.FromHours(1); if (Ttl < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl)); }
        public static string Key(string mode, string? identity = null)
        {
            var host = identity == null ? null : InventoryHelpers.IdentityHost(identity); var readable = Regex.Replace(host ?? "local", "[^A-Za-z0-9._-]", "-"); if (readable.Length > 40) readable = readable.Substring(0, 40);
            using var hash = SHA256.Create(); var digest = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(mode + "|" + (identity ?? "") + "|" + (host ?? "local")))).Replace("-", "").ToLowerInvariant().Substring(0, 8); return mode + "-" + readable + "-" + digest;
        }
        public string PathFor(string key) { if (key.Length > 160 || !Regex.IsMatch(key, "\\A[A-Za-z0-9._-]+\\z")) throw new ArgumentException("Invalid inventory cache key", nameof(key)); return Path.Combine(DirectoryPath, "intents-" + key + ".json"); }
        public Inventory? Load(string key)
        {
            try
            {
                var path = PathFor(key);
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                const int limit = 8 * 1024 * 1024;
                if (file.Length > limit || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > Ttl) return null;
                using var contents = new MemoryStream();
                var buffer = new byte[8192];
                int count;
                while ((count = file.Read(buffer, 0, Math.Min(buffer.Length, limit + 1 - (int)contents.Length))) > 0)
                {
                    contents.Write(buffer, 0, count);
                    if (contents.Length > limit) return null;
                }
                return Inventory.FromObject(JsonNode.Parse(contents.ToArray()));
            }
            catch { return null; }
        }
        [DllImport("libc", SetLastError = true)] private static extern int fchmod(int descriptor, uint mode);
        public void Store(string key, Inventory inventory)
        {
            string? scratch = null;
            try
            {
                var target = PathFor(key); Directory.CreateDirectory(DirectoryPath); scratch = Path.Combine(DirectoryPath, ".intents-" + Guid.NewGuid().ToString("N") + ".partial");
                using (var file = new FileStream(scratch, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && fchmod(file.SafeFileHandle.DangerousGetHandle().ToInt32(), 384) != 0) throw new IOException("Cannot make inventory cache private");
                    var bytes = Encoding.UTF8.GetBytes(inventory.AsObject().ToJsonString()); file.Write(bytes, 0, bytes.Length); file.Flush(true);
                }
                if (File.Exists(target)) File.Replace(scratch, target, null); else File.Move(scratch, target);
            }
            catch { /* An optional cache must not break a call. */ }
            finally { if (scratch != null) try { File.Delete(scratch); } catch { } }
        }
    }
}
