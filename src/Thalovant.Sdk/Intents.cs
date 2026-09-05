using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Thalovant
{
    // What a hub can be asked: the intent inventory, over the client's own session.
    //
    // The hub runtime keeps an intent manifest (OVOS-INTENT-4 section 10): every
    // intent a skill registered, per language, and on request the registration
    // itself, which for a template intent carries the sentences from the skill's
    // locale files, slots and all -- "what is the weather in {location}". This
    // file asks that manifest and shapes the answer, so a satellite, an installer
    // or an agent shows a person what they can say without a control-plane token.
    //
    // Two queries, correlated by context.request_id like every other request:
    //
    // - ovos.intent.list {"lang": <tag>} -> ovos.intent.list.response
    //   {"ok", "intents": [{skill_id, intent_name, lang, method, enabled,
    //   session_id}]}. "method" is "template" (sample sentences) or "keyword"
    //   (keyword sets). A runtime may attach each entry's "definition" when asked
    //   with "include_definitions"; when it does not, the client describes each
    //   intent individually.
    // - ovos.intent.describe {"skill_id", "intent_name", "lang"} ->
    //   ovos.intent.describe.response {"ok", "definitions": [{method,
    //   definition}]} or {"ok": false, "error"}.
    //
    // A hub whose connection may not publish a type answers hive.policy.denied
    // naming it; that becomes ThalovantPolicyDeniedException at once rather than
    // a timeout. The engines' own manifests (intent.service.adapt.manifest.get
    // and intent.service.padatious.manifest.get, names only, no language) are
    // the fallback for a hub allowed for those alone.

    /// <summary>Options for <see cref="ThalovantClient.IntentsAsync"/>.</summary>
    public sealed class IntentInventoryOptions
    {
        /// <summary>
        /// Deadline for each query: the listing per language, then the describe
        /// batch as a whole. Defaults to 5 seconds.
        /// </summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Whether to fetch the sentences: asks the listing for
        /// <c>include_definitions</c> and describes every registration it did not
        /// define. On by default; off yields names, engines, and languages only.
        /// </summary>
        public bool Describe { get; set; } = true;

        /// <summary>
        /// Whether to fall back to the engines' own manifests when the hub refuses
        /// <c>ovos.intent.list</c>. On by default; off surfaces the refusal as
        /// <see cref="ThalovantPolicyDeniedException"/>.
        /// </summary>
        public bool Fallback { get; set; } = true;
    }

    /// <summary>Options for <see cref="ThalovantClient.ListIntentsAsync"/>.</summary>
    public sealed class IntentListOptions
    {
        /// <summary>How long to wait for the hub's reply. Defaults to 5 seconds.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Asks the runtime to attach each row's <c>definition</c>. A runtime that
        /// does not honour it answers the rows alone, and
        /// <see cref="IntentRegistration.Definition"/> stays null.
        /// </summary>
        public bool IncludeDefinitions { get; set; }
    }

    /// <summary>Options for <see cref="ThalovantClient.DescribeIntentAsync"/>.</summary>
    public sealed class IntentDescribeOptions
    {
        /// <summary>How long to wait for the hub's reply. Defaults to 5 seconds.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
    }

    /// <summary>One row of the hub's intent manifest (<c>ovos.intent.list.response</c>).</summary>
    public sealed class IntentRegistration
    {
        public string SkillId { get; }
        public string IntentName { get; }

        /// <summary>The language tag as the runtime stores it (<c>fr-fr</c> is answered as <c>fr-FR</c>).</summary>
        public string Lang { get; }

        /// <summary><c>template</c> (sample sentences) or <c>keyword</c> (keyword sets), as the runtime names it.</summary>
        public string Method { get; }

        public bool Enabled { get; }
        public string SessionId { get; }

        /// <summary>The registration itself, when the runtime attached it to the listing; null otherwise.</summary>
        public JsonObject? Definition { get; }

        /// <summary>The engine behind <see cref="Method"/>: <c>padatious</c> for template, <c>adapt</c> for keyword.</summary>
        public string Engine => HubIntentQueries.EngineFor(Method);

        public IntentRegistration(
            string skillId,
            string intentName,
            string lang,
            string method,
            bool enabled = true,
            string sessionId = "default",
            JsonObject? definition = null)
        {
            SkillId = skillId;
            IntentName = intentName;
            Lang = lang;
            Method = method;
            Enabled = enabled;
            SessionId = sessionId;
            Definition = definition is null ? null : JsonUtil.CloneObject(definition);
        }

        /// <summary>Parses one manifest row; null when it names no skill or no intent.</summary>
        public static IntentRegistration? FromJsonObject(JsonObject row)
        {
            var skillId = JsonUtil.OptionalString(row["skill_id"]);
            var intentName = JsonUtil.OptionalString(row["intent_name"]);
            if (skillId is null || intentName is null)
            {
                return null;
            }
            // Only an explicit false disables a row; the flag is normally absent or true.
            var disabled = row["enabled"] is JsonValue flag && flag.TryGetValue<bool>(out var enabled) && !enabled;
            return new IntentRegistration(
                skillId,
                intentName,
                JsonUtil.OptionalString(row["lang"]) ?? "",
                JsonUtil.OptionalString(row["method"]) ?? "",
                !disabled,
                JsonUtil.OptionalString(row["session_id"]) ?? "default",
                JsonUtil.AsObject(row["definition"]));
        }

        public JsonObject ToJsonObject()
        {
            return new JsonObject
            {
                ["skill_id"] = SkillId,
                ["intent_name"] = IntentName,
                ["lang"] = Lang,
                ["method"] = Method,
                ["engine"] = Engine,
                ["enabled"] = Enabled,
                ["session_id"] = SessionId,
                ["definition"] = Definition is null ? null : JsonUtil.CloneObject(Definition),
            };
        }
    }

    /// <summary>A registration as the skill made it, from <c>ovos.intent.describe</c>.</summary>
    public sealed class IntentDefinition
    {
        public string SkillId { get; }
        public string IntentName { get; }
        public string Lang { get; }

        /// <summary><c>template</c> or <c>keyword</c>, as the runtime names it.</summary>
        public string Method { get; }

        /// <summary>
        /// The sentences as the skill's locale files wrote them, slots in braces
        /// (<c>what is the weather in {location}</c>); empty for a keyword registration.
        /// </summary>
        public IReadOnlyList<string> Samples { get; }

        /// <summary>The whole definition as the hub sent it.</summary>
        public JsonObject Raw { get; }

        /// <summary>The engine behind <see cref="Method"/>: <c>padatious</c> for template, <c>adapt</c> for keyword.</summary>
        public string Engine => HubIntentQueries.EngineFor(Method);

        public IntentDefinition(
            string skillId,
            string intentName,
            string lang,
            string method,
            IReadOnlyList<string>? samples = null,
            JsonObject? raw = null)
        {
            SkillId = skillId;
            IntentName = intentName;
            Lang = lang;
            Method = method;
            Samples = samples ?? Array.Empty<string>();
            Raw = JsonUtil.CloneObject(raw);
        }

        /// <summary>
        /// Parses one <c>definitions</c> item, <c>{method, definition}</c>; null when
        /// the definition names no skill or no intent.
        /// </summary>
        public static IntentDefinition? FromJsonObject(JsonObject item)
        {
            if (JsonUtil.AsObject(item["definition"]) is not JsonObject definition)
            {
                return null;
            }
            var skillId = JsonUtil.OptionalString(definition["skill_id"]);
            var intentName = JsonUtil.OptionalString(definition["intent_name"]);
            if (skillId is null || intentName is null)
            {
                return null;
            }
            return new IntentDefinition(
                skillId,
                intentName,
                JsonUtil.OptionalString(definition["lang"]) ?? "",
                JsonUtil.OptionalString(item["method"]) ?? JsonUtil.OptionalString(definition["method"]) ?? "",
                HubIntentQueries.Samples(definition),
                definition);
        }

        public JsonObject ToJsonObject()
        {
            var samples = new JsonArray();
            foreach (var sample in Samples)
            {
                samples.Add(sample);
            }
            return new JsonObject
            {
                ["skill_id"] = SkillId,
                ["intent_name"] = IntentName,
                ["lang"] = Lang,
                ["method"] = Method,
                ["engine"] = Engine,
                ["samples"] = samples,
                ["definition"] = JsonUtil.CloneObject(Raw),
            };
        }
    }

    /// <summary>One thing a hub can be asked, with the sentences that ask it, per language.</summary>
    public sealed class HubIntent
    {
        private readonly List<string> _languages = new List<string>();
        private readonly Dictionary<string, IReadOnlyList<string>> _phrases =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        public string SkillId { get; }
        public string Name { get; }

        /// <summary><c>padatious</c> (sample sentences) or <c>adapt</c> (keyword sets).</summary>
        public string Engine { get; }

        public bool Enabled { get; }

        /// <summary><c>skill_id:name</c>, the form the engines' manifests use.</summary>
        public string Id => SkillId + ":" + Name;

        /// <summary>
        /// The sentences per language tag, keyed as the listing was asked. A
        /// language the intent is registered in but the hub gave no sentences for
        /// is present with an empty list.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Phrases => _phrases;

        /// <summary>The languages the intent is registered in, in the order they were asked.</summary>
        public IReadOnlyList<string> Languages => _languages;

        public HubIntent(
            string skillId,
            string name,
            string engine,
            IEnumerable<KeyValuePair<string, IReadOnlyList<string>>>? phrases = null,
            bool enabled = true)
        {
            SkillId = skillId;
            Name = name;
            Engine = engine;
            Enabled = enabled;
            if (phrases is not null)
            {
                foreach (var pair in phrases)
                {
                    if (!_phrases.ContainsKey(pair.Key))
                    {
                        _languages.Add(pair.Key);
                    }
                    _phrases[pair.Key] = pair.Value;
                }
            }
        }

        /// <summary>The sentences for one language; <c>fr-FR</c> and <c>fr_fr</c> find the same ones.</summary>
        public IReadOnlyList<string> PhrasesFor(string lang)
        {
            foreach (var candidate in _languages)
            {
                if (ThalovantContext.SameLanguage(candidate, lang))
                {
                    return _phrases[candidate];
                }
            }
            return Array.Empty<string>();
        }

        /// <summary>
        /// A few sentences worth showing: whole ones before ones with a slot, and
        /// shorter ones first. Without a language the first registered language is
        /// used; a <paramref name="limit"/> of zero or less returns the whole pool.
        /// </summary>
        public IReadOnlyList<string> Examples(string? lang = null, int limit = 2)
        {
            var pool = string.IsNullOrEmpty(lang)
                ? (_languages.Count > 0 ? _phrases[_languages[0]] : Array.Empty<string>())
                : PhrasesFor(lang!);
            if (limit <= 0)
            {
                return pool;
            }
            return pool
                .OrderBy(text => text.IndexOf('{') >= 0)
                .ThenBy(text => text.Length)
                .Take(limit)
                .ToArray();
        }

        public JsonObject ToJsonObject()
        {
            var phrases = new JsonObject();
            foreach (var language in _languages)
            {
                var sentences = new JsonArray();
                foreach (var text in _phrases[language])
                {
                    sentences.Add(text);
                }
                phrases[language] = sentences;
            }
            return new JsonObject
            {
                ["id"] = Id,
                ["skill_id"] = SkillId,
                ["name"] = Name,
                ["engine"] = Engine,
                ["enabled"] = Enabled,
                ["phrases"] = phrases,
            };
        }
    }

    /// <summary>The intents one skill registered.</summary>
    public sealed class HubSkillIntents
    {
        public string SkillId { get; }
        public IReadOnlyList<HubIntent> Intents { get; }

        /// <summary>Every language any of the skill's intents is registered in, in first-seen order.</summary>
        public IReadOnlyList<string> Languages { get; }

        public HubSkillIntents(string skillId, IReadOnlyList<HubIntent> intents)
        {
            SkillId = skillId;
            Intents = intents;
            var languages = new List<string>();
            foreach (var intent in intents)
            {
                foreach (var language in intent.Languages)
                {
                    if (!languages.Contains(language))
                    {
                        languages.Add(language);
                    }
                }
            }
            Languages = languages;
        }

        public JsonObject ToJsonObject()
        {
            var languages = new JsonArray();
            foreach (var language in Languages)
            {
                languages.Add(language);
            }
            var intents = new JsonArray();
            foreach (var intent in Intents)
            {
                intents.Add(intent.ToJsonObject());
            }
            return new JsonObject
            {
                ["skill_id"] = SkillId,
                ["languages"] = languages,
                ["intents"] = intents,
            };
        }
    }

    /// <summary>
    /// Everything a hub can be asked, grouped by skill.
    /// <para>
    /// <see cref="Source"/> says how it was read: <see cref="SourceIntentManifest"/>
    /// carries sentences per language; <see cref="SourceEngineManifests"/> is the
    /// names-only fallback, and <see cref="Denied"/> then names the query the hub
    /// refused.
    /// </para>
    /// </summary>
    public sealed class HubIntentInventory
    {
        /// <summary>Read from the runtime's intent manifest: sentences per language.</summary>
        public const string SourceIntentManifest = "intent-manifest";

        /// <summary>Read from the engines' own manifests: names only, no language.</summary>
        public const string SourceEngineManifests = "engine-manifests";

        /// <summary>The languages that were asked, as given.</summary>
        public IReadOnlyList<string> Languages { get; }

        /// <summary>Skills in <c>skill_id</c> order, each with its intents in name order.</summary>
        public IReadOnlyList<HubSkillIntents> Skills { get; }

        /// <summary><see cref="SourceIntentManifest"/> or <see cref="SourceEngineManifests"/>.</summary>
        public string Source { get; }

        /// <summary>The queries the hub refused on the way to this result; empty unless the fallback was taken.</summary>
        public IReadOnlyList<string> Denied { get; }

        /// <summary>Every intent of every skill, flattened in the same order.</summary>
        public IReadOnlyList<HubIntent> Intents { get; }

        /// <summary>
        /// True only when at least one intent carries at least one sentence. False
        /// for the names-only fallback, and for a listing whose describes all came
        /// back empty.
        /// </summary>
        public bool HasPhrases
        {
            get
            {
                foreach (var intent in Intents)
                {
                    foreach (var language in intent.Languages)
                    {
                        if (intent.Phrases[language].Count > 0)
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
        }

        public HubIntentInventory(
            IReadOnlyList<string> languages,
            IReadOnlyList<HubSkillIntents> skills,
            string source = SourceIntentManifest,
            IReadOnlyList<string>? denied = null)
        {
            Languages = languages;
            Skills = skills;
            Source = source;
            Denied = denied ?? Array.Empty<string>();
            var intents = new List<HubIntent>();
            foreach (var skill in skills)
            {
                intents.AddRange(skill.Intents);
            }
            Intents = intents;
        }

        public JsonObject ToJsonObject()
        {
            var languages = new JsonArray();
            foreach (var language in Languages)
            {
                languages.Add(language);
            }
            var denied = new JsonArray();
            foreach (var type in Denied)
            {
                denied.Add(type);
            }
            var skills = new JsonArray();
            foreach (var skill in Skills)
            {
                skills.Add(skill.ToJsonObject());
            }
            return new JsonObject
            {
                ["languages"] = languages,
                ["source"] = Source,
                ["denied"] = denied,
                ["skills"] = skills,
            };
        }
    }

    /// <summary>One registration to describe: a skill's intent in one language.</summary>
    internal readonly struct IntentKey : IEquatable<IntentKey>
    {
        public string SkillId { get; }
        public string IntentName { get; }
        public string Lang { get; }

        public IntentKey(string skillId, string intentName, string lang)
        {
            SkillId = skillId;
            IntentName = intentName;
            Lang = lang;
        }

        public bool Equals(IntentKey other)
        {
            return string.Equals(SkillId, other.SkillId, StringComparison.Ordinal)
                && string.Equals(IntentName, other.IntentName, StringComparison.Ordinal)
                && string.Equals(Lang, other.Lang, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return obj is IntentKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(SkillId, IntentName, Lang);
        }
    }

    /// <summary>
    /// The wire side of the intent inventory: one bus query and its correlated
    /// reply, the batch of describes, and the shaping into
    /// <see cref="HubIntentInventory"/>. Mirrors the Python SDK's
    /// <c>thalovant.intents</c> module, the reference implementation.
    /// </summary>
    internal static class HubIntentQueries
    {
        /// <summary>
        /// How many describes may be in flight at once. A hub with 69 intents in
        /// two languages is 138 requests and, with every reply delivered twice,
        /// 276 inbound events; an SDK whose reply queue is bounded drops replies
        /// past its capacity and the inventory comes back missing sentences.
        /// Batching also spares the hub a burst it never asked for, and the
        /// per-batch deadline means a hub that answers nothing fails after one
        /// batch rather than holding every request open.
        /// </summary>
        internal const int DescribeBatch = 32;

        private const string MethodTemplate = "template";
        private const string MethodKeyword = "keyword";
        private const string EngineAdapt = "adapt";
        private const string EnginePadatious = "padatious";

        /// <summary>The engines' manifests, in the order they are asked and merged.</summary>
        private static readonly (string Engine, string QueryType, string ReplyType)[] EngineManifests =
        {
            (EngineAdapt, ThalovantEvents.AdaptManifestGet, ThalovantEvents.AdaptManifest),
            (EnginePadatious, ThalovantEvents.PadatiousManifestGet, ThalovantEvents.PadatiousManifest),
        };

        internal static string EngineFor(string method)
        {
            switch (method)
            {
                case MethodTemplate:
                    return EnginePadatious;
                case MethodKeyword:
                    return EngineAdapt;
                default:
                    return method.Length == 0 ? "unknown" : method;
            }
        }

        /// <summary>The non-empty, trimmed <c>samples</c> of a template definition.</summary>
        internal static IReadOnlyList<string> Samples(JsonObject definition)
        {
            var samples = new List<string>();
            if (definition["samples"] is JsonArray raw)
            {
                foreach (var item in raw)
                {
                    if (JsonUtil.GetString(item) is string text && text.Trim().Length > 0)
                    {
                        samples.Add(text.Trim());
                    }
                }
            }
            return samples;
        }

        // -- one query, one reply -------------------------------------------

        /// <summary>
        /// Sends one bus query and returns its reply, matched by request id.
        /// A reply may arrive more than once; the first one wins and repeats are
        /// dropped. A <c>hive.policy.denied</c> naming the query throws at once.
        /// </summary>
        internal static async Task<ThalovantEvent> RequestReplyAsync(
            ThalovantClient client,
            string queryType,
            string replyType,
            JsonObject data,
            string? lang,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var requestId = ThalovantContext.NewRequestId();
            var context = new JsonObject { ["request_id"] = requestId };
            if (!string.IsNullOrEmpty(lang))
            {
                context["lang"] = lang;
            }
            var answer = new TaskCompletionSource<ThalovantEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            await client.ConnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            using (client.On(ThalovantEvents.PolicyDenied, denied => FailIfDenied(answer, denied, queryType)))
            using (client.On(replyType, reply => answer.TrySetResult(reply), requestId: requestId))
            {
                await client.EmitAsync(queryType, data, context, cancellationToken).ConfigureAwait(false);
                return await AwaitReplyAsync(answer.Task, timeout, queryType, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Fails the pending reply when the denial names one of <paramref name="queryTypes"/>.</summary>
        private static void FailIfDenied<T>(TaskCompletionSource<T> pending, ThalovantEvent denied, params string[] queryTypes)
        {
            var deniedType = JsonUtil.OptionalString(denied.Data["denied_type"]);
            if (deniedType is null)
            {
                return;
            }
            foreach (var queryType in queryTypes)
            {
                if (string.Equals(queryType, deniedType, StringComparison.Ordinal))
                {
                    pending.TrySetException(ThalovantPolicyDeniedException.FromEvent(denied));
                    return;
                }
            }
        }

        /// <summary>Awaits the reply, a matching denial, or the deadline.</summary>
        private static async Task<T> AwaitReplyAsync<T>(Task<T> reply, TimeSpan timeout, string what, CancellationToken cancellationToken)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(timeout, timeoutSource.Token);
            var completed = await Task.WhenAny(reply, delay).ConfigureAwait(false);
            if (completed == reply)
            {
                timeoutSource.Cancel();
                return await reply.ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw new ThalovantTimeoutException($"Hub did not answer {what} within {(int)timeout.TotalMilliseconds}ms.");
        }

        private static bool IsRefused(JsonObject data)
        {
            return data["ok"] is JsonValue value && value.TryGetValue<bool>(out var ok) && !ok;
        }

        private static JsonObject DescribeData(string skillId, string intentName, string lang)
        {
            return new JsonObject
            {
                ["skill_id"] = skillId,
                ["intent_name"] = intentName,
                ["lang"] = lang,
            };
        }

        private static IReadOnlyList<IntentDefinition> Definitions(JsonObject data)
        {
            var definitions = new List<IntentDefinition>();
            if (data["definitions"] is JsonArray items)
            {
                foreach (var item in items)
                {
                    if (item is JsonObject entry && IntentDefinition.FromJsonObject(entry) is IntentDefinition definition)
                    {
                        definitions.Add(definition);
                    }
                }
            }
            return definitions;
        }

        // -- the two queries --------------------------------------------------

        /// <summary>The hub's intent manifest for one language.</summary>
        internal static async Task<IReadOnlyList<IntentRegistration>> ListIntentsAsync(
            ThalovantClient client,
            string lang,
            IntentListOptions options,
            CancellationToken cancellationToken)
        {
            var data = new JsonObject { ["lang"] = lang };
            if (options.IncludeDefinitions)
            {
                data["include_definitions"] = true;
            }
            var reply = await RequestReplyAsync(
                client,
                ThalovantEvents.IntentList,
                ThalovantEvents.IntentListResponse,
                data,
                lang,
                options.Timeout,
                cancellationToken).ConfigureAwait(false);
            var rows = new List<IntentRegistration>();
            if (reply.Data["intents"] is JsonArray listed)
            {
                foreach (var item in listed)
                {
                    if (item is JsonObject row && IntentRegistration.FromJsonObject(row) is IntentRegistration entry)
                    {
                        rows.Add(entry);
                    }
                }
            }
            return rows;
        }

        /// <summary>Every registration behind one intent in one language; empty for an unknown one.</summary>
        internal static async Task<IReadOnlyList<IntentDefinition>> DescribeIntentAsync(
            ThalovantClient client,
            string skillId,
            string intentName,
            string lang,
            IntentDescribeOptions options,
            CancellationToken cancellationToken)
        {
            var reply = await RequestReplyAsync(
                client,
                ThalovantEvents.IntentDescribe,
                ThalovantEvents.IntentDescribeResponse,
                DescribeData(skillId, intentName, lang),
                lang,
                options.Timeout,
                cancellationToken).ConfigureAwait(false);
            return IsRefused(reply.Data) ? Array.Empty<IntentDefinition>() : Definitions(reply.Data);
        }

        /// <summary>
        /// Describes many registrations, at most <paramref name="batch"/> of them
        /// in flight: one subscription per batch, one request id per registration,
        /// replies matched by that id (or by the definition's own names when a hub
        /// does not echo the id), repeats dropped. The deadline covers each batch,
        /// and a batch the hub only partly answered in time is returned as far as
        /// it got. A <paramref name="batch"/> of zero sends them all at once.
        /// </summary>
        internal static async Task<Dictionary<IntentKey, IReadOnlyList<IntentDefinition>>> DescribeManyAsync(
            ThalovantClient client,
            IEnumerable<IntentKey> requested,
            TimeSpan timeout,
            int batch,
            CancellationToken cancellationToken)
        {
            var wanted = new List<IntentKey>();
            foreach (var key in requested)
            {
                if (!wanted.Contains(key))
                {
                    wanted.Add(key);
                }
            }
            var found = new Dictionary<IntentKey, IReadOnlyList<IntentDefinition>>();
            if (wanted.Count == 0)
            {
                return found;
            }
            if (batch > 0 && wanted.Count > batch)
            {
                for (var start = 0; start < wanted.Count; start += batch)
                {
                    var slice = wanted.GetRange(start, Math.Min(batch, wanted.Count - start));
                    try
                    {
                        var described = await DescribeManyAsync(client, slice, timeout, 0, cancellationToken).ConfigureAwait(false);
                        foreach (var pair in described)
                        {
                            found[pair.Key] = pair.Value;
                        }
                    }
                    catch (ThalovantTimeoutException)
                    {
                        // A partial answer is an answer, across windows as within
                        // one: windows are contiguous slices, so an unresponsive
                        // skill with more than one window's worth of intents would
                        // otherwise turn the whole inventory into a timeout while
                        // the same skill with fewer intents only loses its
                        // sentences. A hub silent from the start still fails at
                        // the first window, since nothing is found.
                        if (found.Count == 0)
                        {
                            throw;
                        }
                    }
                }
                return found;
            }
            var byRequest = new Dictionary<string, IntentKey>(StringComparer.Ordinal);
            var sync = new object();
            var complete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            void Keep(ThalovantEvent reply)
            {
                var definitions = Definitions(reply.Data);
                IntentKey? key = null;
                lock (sync)
                {
                    if (reply.RequestId is string requestId && byRequest.TryGetValue(requestId, out var matched))
                    {
                        key = matched;
                    }
                }
                if (key is null && definitions.Count > 0)
                {
                    // No request id came back: the definition names what it describes.
                    var first = definitions[0];
                    foreach (var candidate in wanted)
                    {
                        if (string.Equals(candidate.SkillId, first.SkillId, StringComparison.Ordinal)
                            && string.Equals(candidate.IntentName, first.IntentName, StringComparison.Ordinal)
                            && ThalovantContext.SameLanguage(candidate.Lang, first.Lang))
                        {
                            key = candidate;
                            break;
                        }
                    }
                }
                if (key is null)
                {
                    return;
                }
                lock (sync)
                {
                    if (found.ContainsKey(key.Value))
                    {
                        return;
                    }
                    found[key.Value] = IsRefused(reply.Data) ? Array.Empty<IntentDefinition>() : definitions;
                    if (found.Count == wanted.Count)
                    {
                        complete.TrySetResult(true);
                    }
                }
            }

            await client.ConnectAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            using (client.On(ThalovantEvents.PolicyDenied, denied => FailIfDenied(complete, denied, ThalovantEvents.IntentDescribe)))
            using (client.On(ThalovantEvents.IntentDescribeResponse, Keep))
            {
                foreach (var key in wanted)
                {
                    var requestId = ThalovantContext.NewRequestId();
                    lock (sync)
                    {
                        byRequest[requestId] = key;
                    }
                    await client.EmitAsync(
                        ThalovantEvents.IntentDescribe,
                        DescribeData(key.SkillId, key.IntentName, key.Lang),
                        new JsonObject { ["request_id"] = requestId, ["lang"] = key.Lang },
                        cancellationToken).ConfigureAwait(false);
                }
                try
                {
                    await AwaitReplyAsync(complete.Task, timeout, ThalovantEvents.IntentDescribe, cancellationToken).ConfigureAwait(false);
                }
                catch (ThalovantTimeoutException)
                {
                    lock (sync)
                    {
                        if (found.Count == 0)
                        {
                            throw;
                        }
                    }
                    // A partial answer is still an answer: the intents the hub did
                    // not describe in time simply carry no sentences.
                }
            }
            lock (sync)
            {
                return new Dictionary<IntentKey, IReadOnlyList<IntentDefinition>>(found);
            }
        }

        /// <summary>
        /// The engines' own manifests, <c>adapt</c> and <c>padatious</c> to their
        /// intent names. Names only, and the same names whatever the language
        /// asked, because an intent's name is the same in every language.
        /// </summary>
        internal static async Task<Dictionary<string, IReadOnlyList<string>>> IntentNamesAsync(
            ThalovantClient client,
            string lang,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var names = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var manifest in EngineManifests)
            {
                var reply = await RequestReplyAsync(
                    client,
                    manifest.QueryType,
                    manifest.ReplyType,
                    new JsonObject { ["lang"] = lang },
                    lang,
                    timeout,
                    cancellationToken).ConfigureAwait(false);
                var listed = new List<string>();
                if (reply.Data["intents"] is JsonArray raw)
                {
                    foreach (var item in raw)
                    {
                        if (JsonUtil.GetString(item) is string text && text.Length > 0)
                        {
                            listed.Add(text);
                        }
                    }
                }
                names[manifest.Engine] = listed;
            }
            return names;
        }

        // -- the inventory ----------------------------------------------------

        /// <summary>
        /// Everything the hub can be asked, in each language, grouped by skill.
        /// Asks the intent manifest per language and, unless the runtime attached
        /// definitions to the listing, describes every registration at once. When
        /// the hub refuses <c>ovos.intent.list</c> and the fallback is on, the
        /// engines' manifests give the names and the result says so.
        /// </summary>
        internal static async Task<HubIntentInventory> InventoryAsync(
            ThalovantClient client,
            IEnumerable<string> languages,
            IntentInventoryOptions options,
            CancellationToken cancellationToken)
        {
            // Tags are trimmed and folded before asking: en-us, en-US and en_us
            // are one language, asked once, under the first spelling given.
            var asked = new List<string>();
            foreach (var language in languages)
            {
                var tag = language?.Trim() ?? "";
                if (tag.Length == 0)
                {
                    continue;
                }
                var seen = false;
                foreach (var candidate in asked)
                {
                    if (ThalovantContext.SameLanguage(candidate, tag))
                    {
                        seen = true;
                        break;
                    }
                }
                if (!seen)
                {
                    asked.Add(tag);
                }
            }
            if (asked.Count == 0)
            {
                throw new ThalovantRuntimeException("IntentsAsync() needs at least one language.");
            }

            var listed = new List<KeyValuePair<string, IReadOnlyList<IntentRegistration>>>();
            try
            {
                var listOptions = new IntentListOptions { Timeout = options.Timeout, IncludeDefinitions = options.Describe };
                foreach (var lang in asked)
                {
                    var rows = await ListIntentsAsync(client, lang, listOptions, cancellationToken).ConfigureAwait(false);
                    listed.Add(new KeyValuePair<string, IReadOnlyList<IntentRegistration>>(lang, rows));
                }
            }
            catch (ThalovantPolicyDeniedException denied)
                when (options.Fallback && string.Equals(denied.DeniedType, ThalovantEvents.IntentList, StringComparison.Ordinal))
            {
                var names = await IntentNamesAsync(client, asked[0], options.Timeout, cancellationToken).ConfigureAwait(false);
                return FromNames(names, asked, denied.DeniedType);
            }

            var wanted = new List<IntentKey>();
            foreach (var pair in listed)
            {
                foreach (var entry in pair.Value)
                {
                    if (entry.Enabled && entry.Definition is null && entry.Method == MethodTemplate)
                    {
                        wanted.Add(new IntentKey(entry.SkillId, entry.IntentName, pair.Key));
                    }
                }
            }
            var described = options.Describe && wanted.Count > 0
                ? await DescribeManyAsync(client, wanted, options.Timeout, DescribeBatch, cancellationToken).ConfigureAwait(false)
                : new Dictionary<IntentKey, IReadOnlyList<IntentDefinition>>();

            var order = new List<(string SkillId, string Name)>();
            var builders = new Dictionary<(string SkillId, string Name), IntentBuilder>();
            foreach (var pair in listed)
            {
                foreach (var entry in pair.Value)
                {
                    var key = (entry.SkillId, entry.IntentName);
                    if (!builders.TryGetValue(key, out var builder))
                    {
                        builder = new IntentBuilder(entry.SkillId, entry.IntentName, entry.Engine);
                        builders[key] = builder;
                        order.Add(key);
                    }
                    builder.Enabled = builder.Enabled || entry.Enabled;
                    IReadOnlyList<string> sentences = Array.Empty<string>();
                    if (entry.Definition is JsonObject definition)
                    {
                        sentences = Samples(definition);
                    }
                    else if (described.TryGetValue(new IntentKey(entry.SkillId, entry.IntentName, pair.Key), out var definitions))
                    {
                        foreach (var candidate in definitions)
                        {
                            if (candidate.Samples.Count > 0)
                            {
                                sentences = candidate.Samples;
                                break;
                            }
                        }
                    }
                    builder.SetPhrases(pair.Key, sentences);
                }
            }

            var bySkill = new SortedDictionary<string, List<HubIntent>>(StringComparer.Ordinal);
            foreach (var key in order)
            {
                var intent = builders[key].Build();
                if (!bySkill.TryGetValue(intent.SkillId, out var intents))
                {
                    intents = new List<HubIntent>();
                    bySkill[intent.SkillId] = intents;
                }
                intents.Add(intent);
            }
            return new HubIntentInventory(asked, Skills(bySkill), HubIntentInventory.SourceIntentManifest);
        }

        /// <summary>The names-only inventory the engines' manifests allow, naming the refused query.</summary>
        private static HubIntentInventory FromNames(
            Dictionary<string, IReadOnlyList<string>> names,
            IReadOnlyList<string> languages,
            string denied)
        {
            var bySkill = new SortedDictionary<string, Dictionary<string, HubIntent>>(StringComparer.Ordinal);
            foreach (var manifest in EngineManifests)
            {
                if (!names.TryGetValue(manifest.Engine, out var entries))
                {
                    continue;
                }
                foreach (var raw in entries)
                {
                    // "<skill_id>:<intent_name>"; a name without a skill keeps the whole text.
                    var separator = raw.IndexOf(':');
                    var skillId = separator > 0 && separator < raw.Length - 1 ? raw.Substring(0, separator) : "";
                    var intentName = separator >= 0 && separator < raw.Length - 1 ? raw.Substring(separator + 1) : raw;
                    if (!bySkill.TryGetValue(skillId, out var intents))
                    {
                        intents = new Dictionary<string, HubIntent>(StringComparer.Ordinal);
                        bySkill[skillId] = intents;
                    }
                    // First engine to name it wins, as on the manifest path.
                    if (!intents.ContainsKey(intentName))
                    {
                        intents[intentName] = new HubIntent(skillId, intentName, manifest.Engine);
                    }
                }
            }
            var grouped = new SortedDictionary<string, List<HubIntent>>(StringComparer.Ordinal);
            foreach (var pair in bySkill)
            {
                grouped[pair.Key] = new List<HubIntent>(pair.Value.Values);
            }
            return new HubIntentInventory(languages, Skills(grouped), HubIntentInventory.SourceEngineManifests, new[] { denied });
        }

        /// <summary>Skills in id order, each with its intents in name order.</summary>
        private static IReadOnlyList<HubSkillIntents> Skills(SortedDictionary<string, List<HubIntent>> bySkill)
        {
            var skills = new List<HubSkillIntents>();
            foreach (var pair in bySkill)
            {
                pair.Value.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
                skills.Add(new HubSkillIntents(pair.Key, pair.Value));
            }
            return skills;
        }

        /// <summary>
        /// Accumulates one intent across the rows it was listed in. The first row
        /// seen names the engine; any row can enable it.
        /// </summary>
        private sealed class IntentBuilder
        {
            private readonly List<KeyValuePair<string, IReadOnlyList<string>>> _phrases =
                new List<KeyValuePair<string, IReadOnlyList<string>>>();

            internal string SkillId { get; }
            internal string Name { get; }
            internal string Engine { get; }
            internal bool Enabled { get; set; }

            internal IntentBuilder(string skillId, string name, string engine)
            {
                SkillId = skillId;
                Name = name;
                Engine = engine;
            }

            /// <summary>
            /// Records the sentences for one language. An intent registered under
            /// both engines has two rows for the language; the keyword row carries
            /// no sentences and must not erase the template row's, whichever
            /// order the rows arrive in.
            /// </summary>
            internal void SetPhrases(string lang, IReadOnlyList<string> sentences)
            {
                for (var index = 0; index < _phrases.Count; index++)
                {
                    if (string.Equals(_phrases[index].Key, lang, StringComparison.Ordinal))
                    {
                        if (sentences.Count > 0)
                        {
                            _phrases[index] = new KeyValuePair<string, IReadOnlyList<string>>(lang, sentences);
                        }
                        return;
                    }
                }
                _phrases.Add(new KeyValuePair<string, IReadOnlyList<string>>(lang, sentences));
            }

            internal HubIntent Build()
            {
                return new HubIntent(SkillId, Name, Engine, _phrases, Enabled);
            }
        }
    }
}
