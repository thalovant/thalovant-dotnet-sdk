using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Thalovant.Sdk.Tests
{
    /// <summary>
    /// The intent inventory, against a hub that behaves like the one observed.
    /// <para>
    /// Shapes copied from a live runtime on 2026-09-05: <c>ovos.intent.list.response</c>
    /// rows, <c>ovos.intent.describe.response</c> definitions carrying <c>samples</c>
    /// as the skill's locale files wrote them, <c>hive.policy.denied</c> for a type
    /// the connection may not publish, and every reply delivered twice.
    /// </para>
    /// </summary>
    public class IntentInventoryTests
    {
        private const string Weather = "thalovant-skill-weather.thalovant";
        private const string Shadow = "thalovant-skill-custos-shadow.thalovant";

        private static readonly string[] Allowed = { "recognizer_loop:utterance", "speak" };

        /// <summary>
        /// What the hub registered: per language, per intent, the sentences.
        /// Weather speaks both languages; the shadow skill only English.
        /// </summary>
        private static readonly (string Lang, string SkillId, string IntentName, string[] Samples)[] DefaultRegistrations =
        {
            ("en-us", Weather, "current.weather", new[]
            {
                "what is the weather",
                "what is the weather in {location}",
                "how is it outside",
            }),
            ("en-us", Shadow, "custos.incidents", new[] { "are there incidents", "any incidents" }),
            ("fr-fr", Weather, "current.weather", new[]
            {
                "quel temps fait-il",
                "quelle est la météo à {location}",
                "quelle est la météo",
            }),
        };

        /// <summary>A hub session: answers the manifest, or refuses it, twice over.</summary>
        internal class FakeHubBus : IHiveMindBus
        {
            private readonly object _lock = new object();
            private readonly Dictionary<Guid, Action<JsonObject>> _handlers = new Dictionary<Guid, Action<JsonObject>>();
            private int _window;

            public HashSet<string> Refuse { get; } = new HashSet<string>();
            public bool ForeignFirst { get; set; }
            public TimeSpan FallbackDelay { get; set; }
            public TimeSpan ConnectDelay { get; set; }
            public int CancelledDelayCount { get; private set; }
            private async Task PausedDelay(TimeSpan delay, CancellationToken cancellationToken)
            {
                try { await Task.Delay(delay, cancellationToken); }
                catch (OperationCanceledException) { CancelledDelayCount++; throw; }
            }
            /// <summary>
            /// What <c>ovos.skills.list</c> answers: a hub with no fallback skill by
            /// default. <c>ovos.skills.fallback.list</c> is never answered: ovos-core
            /// added it in #951 and took it out again in #988.
            /// </summary>
            public JsonObject SkillsPayload { get; set; } = new JsonObject { ["ok"] = true, ["skills"] = new JsonArray() };

            /// <summary>
            /// The hub's default language, stamped on the session of a reply to a
            /// listing that named no language -- what a live hub does.
            /// </summary>
            public string? HubLang { get; set; }
            public HashSet<string> Silent { get; } = new HashSet<string>();
            public bool DefinitionsInList { get; set; }
            public bool EchoRequestId { get; set; } = true;
            public int Repeats { get; set; } = 2;

            /// <summary>What this hub registered; the observed set unless a test says otherwise.</summary>
            public (string Lang, string SkillId, string IntentName, string[] Samples)[] Registrations { get; set; } = DefaultRegistrations;

            /// <summary>
            /// Keyword (adapt) registrations per language: listed, never
            /// described with sentences, exactly as ovos-core lists an
            /// IntentBuilder intent.
            /// </summary>
            public Dictionary<string, (string SkillId, string IntentName)[]> Keywords { get; set; } =
                new Dictionary<string, (string SkillId, string IntentName)[]>();

            /// <summary>Languages whose <c>ovos.intent.list</c> gets no answer; "" is the listing without a language.</summary>
            public HashSet<string> ListSilentIn { get; } = new HashSet<string>();

            /// <summary>Languages whose <c>ovos.intent.list</c> is answered <c>ok: false</c>; "" is the listing without a language.</summary>
            public HashSet<string> ListFailsIn { get; } = new HashSet<string>();
            public bool Connected { get; private set; }
            public List<(string Type, JsonObject Data, JsonObject Context)> Emitted { get; } =
                new List<(string Type, JsonObject Data, JsonObject Context)>();

            /// <summary>
            /// The subscription window each <c>ovos.intent.describe</c> went out in.
            /// A window opens when a handler is registered with none live, so one
            /// window is one <c>DescribeManyAsync</c> batch.
            /// </summary>
            public List<int> DescribeWindows { get; } = new List<int>();

            // -- transport surface -------------------------------------------

            public Task ConnectAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
            {
                Connected = true;
                return ConnectDelay > TimeSpan.Zero ? PausedDelay(ConnectDelay, cancellationToken) : Task.CompletedTask;
            }

            public Task DisconnectAsync()
            {
                Connected = false;
                return Task.CompletedTask;
            }

            public Guid AddBusHandler(Action<JsonObject> handler)
            {
                var id = Guid.NewGuid();
                lock (_lock)
                {
                    if (_handlers.Count == 0)
                    {
                        _window++;
                    }
                    _handlers[id] = handler;
                }
                return id;
            }

            public void RemoveBusHandler(Guid id)
            {
                lock (_lock)
                {
                    _handlers.Remove(id);
                }
            }

            // -- the hub -------------------------------------------------------

            public virtual Task EmitBusAsync(string type, JsonObject data, JsonObject context, CancellationToken cancellationToken = default)
            {
                if (type == ThalovantEvents.SkillsList && FallbackDelay > TimeSpan.Zero) return PausedDelay(FallbackDelay, cancellationToken);
                if (ForeignFirst) Deliver(ThalovantEvents.PolicyDenied, new JsonObject { ["denied_type"] = type }, new JsonObject { ["request_id"] = "foreign-request" });
                Record(type, data, context);
                if (Refuse.Contains(type))
                {
                    Deliver(ThalovantEvents.PolicyDenied, new JsonObject
                    {
                        ["denied_type"] = type,
                        ["code"] = "acl_disallowed_type",
                        ["reason"] = $"{type} not in allowed_types",
                        ["data"] = new JsonObject
                        {
                            ["msg_type"] = type,
                            ["allowed"] = new JsonArray(Allowed.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
                        },
                    }, context);
                    return Task.CompletedTask;
                }
                if (Silent.Contains(type))
                {
                    return Task.CompletedTask;
                }
                var lang = (string?)data["lang"] ?? "";
                if (type == ThalovantEvents.IntentList && ListSilentIn.Contains(lang))
                {
                    return Task.CompletedTask;
                }
                if (type == ThalovantEvents.IntentList && ListFailsIn.Contains(lang))
                {
                    Deliver(ThalovantEvents.IntentListResponse, new JsonObject { ["ok"] = false, ["error"] = "listing failed" }, context);
                    return Task.CompletedTask;
                }
                switch (type)
                {
                    case ThalovantEvents.SkillsList:
                        Deliver(ThalovantEvents.SkillsListResponse, SkillsPayload, context);
                        break;
                    case ThalovantEvents.IntentList when !data.ContainsKey("lang"):
                    {
                        // Every language the hub loaded, in the hub's spelling.
                        var rows = new JsonArray();
                        foreach (var registration in Registrations)
                        {
                            var parts = registration.Lang.Split('-');
                            var spelled = parts.Length > 1 ? parts[0] + "-" + parts[1].ToUpperInvariant() : registration.Lang;
                            var row = new JsonObject
                            {
                                ["skill_id"] = registration.SkillId,
                                ["intent_name"] = registration.IntentName,
                                ["lang"] = spelled,
                                ["method"] = "template",
                                ["enabled"] = true,
                                ["session_id"] = "default",
                            };
                            if ((bool?)data["include_definitions"] == true)
                            {
                                row["definition"] = Definition(registration.SkillId, registration.IntentName, spelled, registration.Samples);
                            }
                            rows.Add(row);
                        }
                        var replyContext = (JsonObject)context.DeepClone();
                        if (HubLang is not null)
                        {
                            replyContext["session"] = new JsonObject { ["session_id"] = "s", ["lang"] = HubLang };
                        }
                        Deliver(ThalovantEvents.IntentListResponse, new JsonObject { ["ok"] = true, ["intents"] = rows }, replyContext);
                        break;
                    }
                    case ThalovantEvents.IntentList:
                    {
                        var rows = new JsonArray();
                        foreach (var registration in Registrations.Where(entry => entry.Lang == lang))
                        {
                            var row = new JsonObject
                            {
                                ["skill_id"] = registration.SkillId,
                                ["intent_name"] = registration.IntentName,
                                // The runtime standardises what it stores.
                                ["lang"] = lang == "fr-fr" ? "fr-FR" : lang,
                                ["method"] = "template",
                                ["enabled"] = true,
                                ["session_id"] = "default",
                            };
                            if (DefinitionsInList && (bool?)data["include_definitions"] == true)
                            {
                                row["definition"] = Definition(registration.SkillId, registration.IntentName, lang, registration.Samples);
                            }
                            rows.Add(row);
                        }
                        if (Keywords.TryGetValue(lang, out var keywords))
                        {
                            foreach (var (skillId, intentName) in keywords)
                            {
                                rows.Add(new JsonObject
                                {
                                    ["skill_id"] = skillId,
                                    ["intent_name"] = intentName,
                                    ["lang"] = lang,
                                    ["method"] = "keyword",
                                    ["enabled"] = true,
                                    ["session_id"] = "default",
                                });
                            }
                        }
                        Deliver(ThalovantEvents.IntentListResponse, new JsonObject { ["ok"] = true, ["intents"] = rows }, context);
                        break;
                    }
                    case ThalovantEvents.IntentDescribe:
                    {
                        var skillId = (string?)data["skill_id"];
                        var intentName = (string?)data["intent_name"];
                        var registration = Registrations.FirstOrDefault(entry =>
                            entry.Lang == lang && entry.SkillId == skillId && entry.IntentName == intentName);
                        JsonObject payload;
                        if (registration.Samples is null)
                        {
                            payload = new JsonObject { ["ok"] = false, ["error"] = "unknown intent" };
                        }
                        else
                        {
                            var definition = Definition(registration.SkillId, registration.IntentName, lang, registration.Samples);
                            definition["blacklist"] = new JsonArray();
                            definition["slot_blacklist"] = new JsonObject();
                            payload = new JsonObject
                            {
                                ["ok"] = true,
                                ["definitions"] = new JsonArray
                                {
                                    new JsonObject { ["method"] = "template", ["definition"] = definition },
                                },
                            };
                        }
                        Deliver(ThalovantEvents.IntentDescribeResponse, payload, context);
                        break;
                    }
                    case ThalovantEvents.AdaptManifestGet:
                        Deliver(ThalovantEvents.AdaptManifest, new JsonObject { ["intents"] = new JsonArray() }, context);
                        break;
                    case ThalovantEvents.PadatiousManifestGet:
                    {
                        var names = new JsonArray();
                        foreach (var name in Registrations.Select(entry => $"{entry.SkillId}:{entry.IntentName}").Distinct().OrderBy(name => name, StringComparer.Ordinal))
                        {
                            names.Add(name);
                        }
                        Deliver(ThalovantEvents.PadatiousManifest, new JsonObject { ["intents"] = names }, context);
                        break;
                    }
                    default:
                        break;
                }
                return Task.CompletedTask;
            }

            protected void Record(string type, JsonObject data, JsonObject context)
            {
                Emitted.Add((type, (JsonObject)data.DeepClone(), (JsonObject)context.DeepClone()));
                if (type == ThalovantEvents.IntentDescribe)
                {
                    lock (_lock)
                    {
                        DescribeWindows.Add(_window);
                    }
                }
            }

            private static JsonObject Definition(string skillId, string intentName, string lang, string[] samples)
            {
                return new JsonObject
                {
                    ["skill_id"] = skillId,
                    ["intent_name"] = intentName,
                    ["lang"] = lang,
                    ["samples"] = new JsonArray(samples.Select(text => (JsonNode?)JsonValue.Create(text)).ToArray()),
                };
            }

            protected void Deliver(string eventName, JsonObject data, JsonObject context)
            {
                if (ForeignFirst && eventName == ThalovantEvents.IntentDescribeResponse && data["definitions"] is JsonArray definitions && definitions.Count > 0 && definitions[0]?["definition"] is JsonObject)
                {
                    var bad = (JsonObject)data.DeepClone();
                    bad["definitions"]![0]!["definition"]!["samples"] = new JsonArray("foreign phrase");
                    List<Action<JsonObject>> foreignHandlers; lock (_lock) foreignHandlers = new List<Action<JsonObject>>(_handlers.Values);
                    foreach (var handler in foreignHandlers) handler(new JsonObject { ["type"] = eventName, ["data"] = bad.DeepClone(), ["context"] = new JsonObject { ["request_id"] = "foreign-request" } });
                }
                for (var repeat = 0; repeat < Repeats; repeat++)
                {
                    List<Action<JsonObject>> handlers;
                    lock (_lock)
                    {
                        handlers = new List<Action<JsonObject>>(_handlers.Values);
                    }
                    foreach (var handler in handlers)
                    {
                        var replyContext = (JsonObject)context.DeepClone();
                        if (!EchoRequestId)
                        {
                            replyContext.Remove("request_id");
                        }
                        handler(new JsonObject
                        {
                            ["type"] = eventName,
                            ["data"] = data.DeepClone(),
                            ["context"] = replyContext,
                        });
                    }
                }
            }
        }

        /// <summary>A hub that never answers the describe for the shadow skill.</summary>
        private sealed class HalfDeafHub : FakeHubBus
        {
            public override Task EmitBusAsync(string type, JsonObject data, JsonObject context, CancellationToken cancellationToken = default)
            {
                if (type == ThalovantEvents.IntentDescribe && (string?)data["skill_id"] == Shadow)
                {
                    Record(type, data, context);
                    return Task.CompletedTask;
                }
                return base.EmitBusAsync(type, data, context, cancellationToken);
            }
        }

        /// <summary>A hub that answers the listing with <c>ok: false</c> instead of rows.</summary>
        private sealed class NegativeListingHub : FakeHubBus
        {
            /// <summary>The hub's <c>error</c> text; null omits the key entirely.</summary>
            public string? Error { get; set; } = "manifest unavailable";

            public override Task EmitBusAsync(string type, JsonObject data, JsonObject context, CancellationToken cancellationToken = default)
            {
                if (type == ThalovantEvents.IntentList)
                {
                    Record(type, data, context);
                    var payload = new JsonObject { ["ok"] = false };
                    if (Error is not null)
                    {
                        payload["error"] = Error;
                    }
                    Deliver(ThalovantEvents.IntentListResponse, payload, context);
                    return Task.CompletedTask;
                }
                return base.EmitBusAsync(type, data, context, cancellationToken);
            }
        }

        /// <summary>A hub that knows no registration it is asked to describe.</summary>
        private sealed class NegativeDescribeHub : FakeHubBus
        {
            public override Task EmitBusAsync(string type, JsonObject data, JsonObject context, CancellationToken cancellationToken = default)
            {
                if (type == ThalovantEvents.IntentDescribe)
                {
                    Record(type, data, context);
                    Deliver(
                        ThalovantEvents.IntentDescribeResponse,
                        new JsonObject { ["ok"] = false, ["error"] = "unknown intent" },
                        context);
                    return Task.CompletedTask;
                }
                return base.EmitBusAsync(type, data, context, cancellationToken);
            }
        }

        private static ThalovantClient Client(FakeHubBus hub)
        {
            var identity = new ThalovantIdentity((JsonObject)JsonNode.Parse(Fixtures.ClientIdentify)!);
            return new ThalovantClient(identity, hub, replySettle: TimeSpan.Zero);
        }

        private static IEnumerable<(string Type, JsonObject Data, JsonObject Context)> EmittedOf(FakeHubBus hub, string type)
        {
            return hub.Emitted.Where(entry => entry.Type == type);
        }

        [Fact]
        public async Task InventoryCarriesTheSentencesPerLanguage()
        {
            var hub = new FakeHubBus();
            var inventory = await Client(hub).IntentsAsync(new[] { "en-us", "fr-fr" });

            Assert.Equal(HubIntentInventory.SourceIntentManifest, inventory.Source);
            Assert.Empty(inventory.Denied);
            Assert.Equal(new[] { "en-us", "fr-fr" }, inventory.Languages);
            Assert.Equal(new[] { Shadow, Weather }, inventory.Skills.Select(skill => skill.SkillId));
            var weather = inventory.Skills[1].Intents[0];
            Assert.Equal($"{Weather}:current.weather", weather.Id);
            Assert.Equal("padatious", weather.Engine);
            Assert.True(weather.Enabled);
            Assert.Equal(
                new[] { "quel temps fait-il", "quelle est la météo à {location}", "quelle est la météo" },
                weather.PhrasesFor("fr-FR"));
            Assert.Equal(new[] { "en-us", "fr-fr" }, inventory.Skills[1].Languages);
            var shadow = inventory.Skills[0];
            // The hub said the skill has no French.
            Assert.Equal(new[] { "en-us" }, shadow.Languages);
            Assert.Empty(shadow.Intents[0].PhrasesFor("fr-fr"));
            Assert.True(inventory.HasPhrases);
            Assert.Equal(2, inventory.Intents.Count);
        }

        [Fact]
        public async Task ExamplesPreferWholeSentencesAndRespectTheLimit()
        {
            var inventory = await Client(new FakeHubBus()).IntentsAsync(new[] { "en-us" });
            var weather = inventory.Skills[1].Intents[0];
            Assert.Equal(new[] { "how is it outside", "what is the weather" }, weather.Examples("en-us", 2));
            Assert.Equal(weather.PhrasesFor("en-us"), weather.Examples("en-us", 0));
            Assert.Equal(new[] { "how is it outside" }, weather.Examples(limit: 1));
        }

        [Fact]
        public async Task EveryRegistrationIsDescribedAtOnceAndRepeatsAreDropped()
        {
            var hub = new FakeHubBus { Repeats = 3 };
            var inventory = await Client(hub).IntentsAsync(new[] { "en-us", "fr-fr" });

            var describes = EmittedOf(hub, ThalovantEvents.IntentDescribe)
                .Select(entry => ((string?)entry.Data["skill_id"], (string?)entry.Data["intent_name"], (string?)entry.Data["lang"]))
                .ToList();
            Assert.Equal(3, describes.Count);
            Assert.Equal(3, describes.Distinct().Count());
            Assert.Equal(2, inventory.Intents.Count);
            // Every query is correlated by request id.
            foreach (var entry in hub.Emitted.Where(e => e.Type == ThalovantEvents.IntentList || e.Type == ThalovantEvents.IntentDescribe))
            {
                Assert.False(string.IsNullOrEmpty((string?)entry.Context["request_id"]));
            }
        }

        [Fact]
        public async Task DefinitionsAttachedToTheListingSkipTheDescribes()
        {
            var hub = new FakeHubBus { DefinitionsInList = true };
            var inventory = await Client(hub).IntentsAsync(new[] { "fr-fr" });
            Assert.Empty(EmittedOf(hub, ThalovantEvents.IntentDescribe));
            Assert.Equal("quel temps fait-il", inventory.Intents[0].PhrasesFor("fr-fr")[0]);
            Assert.Equal("{\"lang\":\"fr-fr\",\"include_definitions\":true}", hub.Emitted[0].Data.ToJsonString());
        }

        [Fact]
        public async Task DescribeOffListsNamesEnginesAndLanguagesOnly()
        {
            var hub = new FakeHubBus();
            var inventory = await Client(hub).IntentsAsync(
                new[] { "en-us" },
                new IntentInventoryOptions { Describe = false });
            Assert.Empty(EmittedOf(hub, ThalovantEvents.IntentDescribe));
            Assert.Equal("{\"lang\":\"en-us\"}", hub.Emitted[0].Data.ToJsonString());
            Assert.Equal(HubIntentInventory.SourceIntentManifest, inventory.Source);
            Assert.False(inventory.HasPhrases);
            Assert.Equal(new[] { "en-us" }, inventory.Intents[0].Languages);
            Assert.Equal("padatious", inventory.Intents[0].Engine);
        }

        [Fact]
        public async Task ARefusalIsAnErrorNamingTheTypeNotATimeout()
        {
            var hub = new FakeHubBus { Refuse = { ThalovantEvents.IntentList } };
            var error = await Assert.ThrowsAsync<ThalovantPolicyDeniedException>(() => Client(hub).IntentsAsync(
                new[] { "en-us" },
                new IntentInventoryOptions { Fallback = false, Timeout = TimeSpan.FromSeconds(5) }));
            Assert.Equal("ovos.intent.list", error.DeniedType);
            Assert.Equal("acl_disallowed_type", error.Code);
            Assert.Equal("ovos.intent.list not in allowed_types", error.Reason);
            Assert.Equal(Allowed, error.Allowed);
            Assert.Contains("ovos.intent.list", error.Message);
            Assert.Contains("connection", error.Message);
            Assert.IsAssignableFrom<ThalovantRuntimeException>(error);
        }

        [Fact]
        public async Task TheFallbackListsNamesAndSaysWhatWasRefused()
        {
            var hub = new FakeHubBus { Refuse = { ThalovantEvents.IntentList } };
            var inventory = await Client(hub).IntentsAsync(new[] { "en-us", "fr-fr" });

            Assert.Equal(HubIntentInventory.SourceEngineManifests, inventory.Source);
            Assert.Equal(new[] { "ovos.intent.list" }, inventory.Denied);
            Assert.False(inventory.HasPhrases);
            Assert.Equal(new[] { "en-us", "fr-fr" }, inventory.Languages);
            Assert.Equal(
                new[] { $"{Shadow}:custos.incidents", $"{Weather}:current.weather" },
                inventory.Intents.Select(intent => intent.Id));
            Assert.All(inventory.Intents, intent => Assert.Equal("padatious", intent.Engine));
            // Names carry no language, so the engines are asked once, not per language.
            Assert.Single(EmittedOf(hub, ThalovantEvents.PadatiousManifestGet));
            Assert.Single(EmittedOf(hub, ThalovantEvents.AdaptManifestGet));
        }

        [Theory]
        [InlineData("intent.service.adapt.manifest.get", false)]
        [InlineData("intent.service.padatious.manifest.get", false)]
        [InlineData("intent.service.adapt.manifest.get", true)]
        [InlineData("intent.service.padatious.manifest.get", true)]
        public async Task EngineFallbackRetainsTheOtherEngineWhenOneIsUnavailable(string unavailable, bool silent)
        {
            var hub = new FakeHubBus { Refuse = { ThalovantEvents.IntentList } };
            (silent ? hub.Silent : hub.Refuse).Add(unavailable);
            var inventory = await Client(hub).IntentsAsync(new[] { "en-us" },
                new IntentInventoryOptions { Timeout = TimeSpan.FromMilliseconds(40) });
            Assert.Equal(HubIntentInventory.SourceEngineManifests, inventory.Source);
            Assert.Contains(hub.Emitted, entry => entry.Type == "intent.service.adapt.manifest.get");
            Assert.Contains(hub.Emitted, entry => entry.Type == "intent.service.padatious.manifest.get");
            if (unavailable.Contains("adapt")) Assert.Contains(inventory.Intents, item => item.Engine == "padatious");
        }

        [Fact]
        public async Task EngineFallbackDoesNotTurnCallerCancellationIntoPartialSuccess()
        {
            var hub = new FakeHubBus { Refuse = { ThalovantEvents.IntentList }, Silent = { "intent.service.padatious.manifest.get" } };
            using var cancelled = new CancellationTokenSource();
            var pending = Client(hub).IntentsAsync(new[] { "en-us" }, cancellationToken: cancelled.Token);
            while (!hub.Emitted.Any(item => item.Type == "intent.service.padatious.manifest.get")) await Task.Yield();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }

        [Fact]
        public async Task AHubRefusingEverythingThrowsEvenWithTheFallback()
        {
            var hub = new FakeHubBus { Refuse = { ThalovantEvents.IntentList, ThalovantEvents.AdaptManifestGet, ThalovantEvents.PadatiousManifestGet } };
            var error = await Assert.ThrowsAsync<ThalovantPolicyDeniedException>(() => Client(hub).IntentsAsync(new[] { "en-us" }));
            Assert.Equal("intent.service.adapt.manifest.get", error.DeniedType);
        }

        [Fact]
        public async Task ARefusedDescribeIsAnErrorTheFallbackDoesNotCover()
        {
            var hub = new FakeHubBus { Refuse = { ThalovantEvents.IntentDescribe } };
            var error = await Assert.ThrowsAsync<ThalovantPolicyDeniedException>(() => Client(hub).IntentsAsync(new[] { "en-us" }));
            Assert.Equal("ovos.intent.describe", error.DeniedType);
        }

        [Fact]
        public async Task ARefusedListingIsAnErrorNotAnEmptyHub()
        {
            // `ok: false` on a listing means the query failed. Reporting it as
            // no intents would show a person a device that can do nothing.
            var hub = new NegativeListingHub();
            var error = await Assert.ThrowsAsync<ThalovantRuntimeException>(
                () => Client(hub).IntentsAsync(new[] { "en-us" }));
            Assert.Contains("ovos.intent.list", error.Message);
            Assert.Contains("manifest unavailable", error.Message);
            // It is not a policy denial, so the engine-manifest fallback -- on by
            // default here -- does not cover it and nothing further was asked.
            Assert.Empty(EmittedOf(hub, ThalovantEvents.PadatiousManifestGet));
            Assert.Empty(EmittedOf(hub, ThalovantEvents.AdaptManifestGet));
            Assert.Empty(EmittedOf(hub, ThalovantEvents.IntentDescribe));
        }

        [Fact]
        public async Task ARefusedListingWithoutAnErrorStillSaysWhatHappened()
        {
            var silent = await Assert.ThrowsAsync<ThalovantRuntimeException>(
                () => Client(new NegativeListingHub { Error = null }).ListIntentsAsync("en-us"));
            Assert.Contains("the hub refused the listing", silent.Message);

            var blank = await Assert.ThrowsAsync<ThalovantRuntimeException>(
                () => Client(new NegativeListingHub { Error = "   " }).ListIntentsAsync("en-us"));
            Assert.Contains("the hub refused the listing", blank.Message);
        }

        [Fact]
        public async Task ADescribeThatDoesNotKnowTheIntentIsNotAnError()
        {
            // The other half of the rule: a describe answering `ok: false` has
            // answered -- the hub does not know that registration -- so the
            // intent is listed without sentences rather than failing the call.
            var hub = new NegativeDescribeHub();
            Assert.Empty(await Client(hub).DescribeIntentAsync(Weather, "current.weather", "en-us"));

            var inventory = await Client(hub).IntentsAsync(new[] { "en-us" });
            Assert.Equal(
                new[] { $"{Shadow}:custos.incidents", $"{Weather}:current.weather" },
                inventory.Intents.Select(intent => intent.Id));
            Assert.False(inventory.HasPhrases);
            Assert.Equal(HubIntentInventory.SourceIntentManifest, inventory.Source);
        }

        [Fact]
        public async Task SilentListingFallsBackWithoutProvingPolicyDenial()
        {
            var inventory = await Client(new FakeHubBus { Silent = { ThalovantEvents.IntentList } }).IntentsAsync(
                new[] { "en-us" }, new IntentInventoryOptions { Timeout = TimeSpan.FromMilliseconds(30) });
            Assert.Equal(HubIntentInventory.SourceEngineManifests, inventory.Source);
            Assert.Equal(new[] { ThalovantEvents.IntentList }, inventory.Denied);
            Assert.True(inventory.FallbacksKnown);
            Assert.False(inventory.MayAnswer("de-de"));
        }

        [Fact]
        public async Task SilentStrictListingAndSilentEngineStillFail()
        {
            var strict = new FakeHubBus { Silent = { ThalovantEvents.IntentList } };
            await Assert.ThrowsAsync<ThalovantTimeoutException>(() => Client(strict).IntentsAsync(new[] { "en-us" },
                new IntentInventoryOptions { Timeout = TimeSpan.FromMilliseconds(30), Fallback = false }));
            var silent = new FakeHubBus { Silent = { ThalovantEvents.IntentList, ThalovantEvents.AdaptManifestGet, ThalovantEvents.PadatiousManifestGet } };
            await Assert.ThrowsAsync<ThalovantTimeoutException>(() => Client(silent).IntentsAsync(new[] { "en-us" },
                new IntentInventoryOptions { Timeout = TimeSpan.FromMilliseconds(30) }));
        }

        [Fact]
        public async Task FallbackDiscoveryDistinguishesUnknownFromKnownEmpty()
        {
            foreach (var hub in new[] {
                new FakeHubBus { Silent = { ThalovantEvents.SkillsList } },
                new FakeHubBus { Refuse = { ThalovantEvents.SkillsList } },
                new FakeHubBus { SkillsPayload = new JsonObject { ["ok"] = true, ["skills"] = "invalid" } },
                new FakeHubBus { SkillsPayload = new JsonObject { ["ok"] = false, ["skills"] = new JsonArray() } },
            }) {
                var inventory = await Client(hub).IntentsAsync(new[] { "fr-fr" }, new IntentInventoryOptions { Timeout = TimeSpan.FromMilliseconds(30) });
                Assert.False(inventory.FallbacksKnown); Assert.True(inventory.MayAnswer("de-de"));
            }
            var known = await Client(new FakeHubBus()).IntentsAsync(new[] { "fr-fr" });
            Assert.True(known.FallbacksKnown); Assert.True(known.MayAnswer("fr-FR")); Assert.False(known.MayAnswer("de-de"));
        }

        [Fact]
        public async Task ForeignDenialsAndDescriptionsCannotCompleteOurRequests()
        {
            var inventory = await Client(new FakeHubBus { ForeignFirst = true }).IntentsAsync(new[] { "en-us", "fr-fr" });
            Assert.True(inventory.HasPhrases); Assert.True(inventory.FallbacksKnown);
            Assert.DoesNotContain("foreign phrase", inventory.Intents.SelectMany(item => item.Phrases.Values.SelectMany(value => value)));
        }

        [Fact]
        public async Task OptionalFallbackProbeIncludesSendBudgetAndPreservesCancellation()
        {
            var hub = new FakeHubBus { FallbackDelay = TimeSpan.FromMinutes(1) };
            using var sdk = Client(hub);
            Assert.Null(await sdk.ListFallbacksAsync(TimeSpan.FromMilliseconds(30)).WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(1, hub.CancelledDelayCount);
            using var cancelled = new CancellationTokenSource();
            var pending = sdk.ListFallbacksAsync(cancellationToken: cancelled.Token); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(2, hub.CancelledDelayCount);
            var connectingHub = new FakeHubBus { ConnectDelay = TimeSpan.FromMinutes(1) };
            using var connecting = Client(connectingHub);
            Assert.Null(await connecting.ListFallbacksAsync(TimeSpan.FromMilliseconds(30)).WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(1, connectingHub.CancelledDelayCount);
            var inventory = await sdk.IntentsAsync(new[] { "en-us" }).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(inventory.FallbacksKnown); Assert.Equal(3, hub.CancelledDelayCount);
        }

        [Fact]
        public async Task OnlyTheHubsOwnFallbackSkillsAreListed()
        {
            // ovos.skills.list names every skill the runtime loaded. A skill
            // without the fallback capability answers through its intents and is
            // in the manifest already; one loaded in somebody else's session is
            // not this hub's; a malformed row is skipped rather than taking the
            // rest down.
            var hub = new FakeHubBus { SkillsPayload = JsonNode.Parse("""
                {"ok":true,"skills":[
                {"skill_id":"thalovant-skill-memory.thalovant","session_id":"default","capabilities":["fallback","converse"],"intents":0},
                {"skill_id":"thalovant-skill-weather.thalovant","session_id":"default","capabilities":[],"intents":16},
                {"skill_id":"someone-elses.fallback","session_id":"another-session","capabilities":["fallback"],"intents":0},
                {"skill_id":"custos-fallback.thalovant","capabilities":["fallback"]},
                {"skill_id":"empty-session.thalovant","session_id":"","capabilities":["fallback"]},
                {"skill_id":"custos-fallback.thalovant","session_id":"default","capabilities":["fallback"]},
                {"skill_id":"","session_id":"default","capabilities":["fallback"]},
                {"skill_id":"no-capabilities.thalovant","session_id":"default","capabilities":null},
                {"skill_id":"numeric-session.thalovant","session_id":7,"capabilities":["fallback"]},
                "not a row"]}
                """)!.AsObject() };
            var handlers = await Client(hub).ListFallbacksAsync();
            Assert.Equal(
                new[] { "custos-fallback.thalovant", "empty-session.thalovant", "thalovant-skill-memory.thalovant" },
                handlers!.Select(item => item.SkillId));
            Assert.All(handlers!, item => Assert.Null(item.Priority));

            var inventory = await Client(hub).IntentsAsync(new[] { "fr-fr" });
            Assert.True(inventory.FallbacksKnown); Assert.True(inventory.MayAnswer("de-de"));
            var serialised = inventory.ToJsonObject()["fallbacks"]!.AsArray();
            Assert.Equal(3, serialised.Count);
            Assert.True(serialised[0]!.AsObject().ContainsKey("priority"));
            Assert.Null(serialised[0]!["priority"]);
        }

        [Fact]
        public async Task TheRetiredFallbackQueryIsNeverSent()
        {
            // No runtime answers ovos.skills.fallback.list, and asking cost every
            // inventory a second and a half.
            var hub = new FakeHubBus();
            await Client(hub).IntentsAsync(new[] { "en-us" });
            await Client(hub).ListFallbacksAsync();
            Assert.Empty(EmittedOf(hub, ThalovantEvents.FallbackList));
            Assert.Equal(2, EmittedOf(hub, ThalovantEvents.SkillsList).Count());
            Assert.Equal("ovos.skills.fallback.list", ThalovantEvents.FallbackList);
            Assert.Equal("ovos.skills.fallback.list.response", ThalovantEvents.FallbackListResponse);
        }

        [Fact]
        public void AFallbackBuiltWithAPriorityKeepsIt()
        {
            // Compatibility: a caller constructing one still may; the hub never does.
            Assert.Equal(10, new HubFallback("skill-a", 10).Priority);
            Assert.Null(new HubFallback("skill-a").Priority);
        }

        [Fact]
        public async Task ADescribeThatNeverComesLeavesThatIntentWithoutSentences()
        {
            var hub = new HalfDeafHub();
            var inventory = await Client(hub).IntentsAsync(
                new[] { "en-us" },
                new IntentInventoryOptions { Timeout = TimeSpan.FromMilliseconds(300) });
            var byId = inventory.Intents.ToDictionary(intent => intent.Id);
            Assert.NotEmpty(byId[$"{Weather}:current.weather"].PhrasesFor("en-us"));
            Assert.Empty(byId[$"{Shadow}:custos.incidents"].PhrasesFor("en-us"));
            // Both describes went out together, before the wait.
            Assert.Equal(2, EmittedOf(hub, ThalovantEvents.IntentDescribe).Count());
        }

        [Fact]
        public async Task AReplyWithoutARequestIdIsStillTaken()
        {
            // A hub that does not echo the request id is not evidence of anything.
            var hub = new FakeHubBus { EchoRequestId = false, Repeats = 1 };
            var inventory = await Client(hub).IntentsAsync(new[] { "en-us" });
            Assert.True(inventory.HasPhrases);
            Assert.Equal(2, inventory.Intents.Count);
        }

        [Fact]
        public async Task LowLevelCallsExposeTheManifestRowsAndDefinitions()
        {
            var hub = new FakeHubBus();
            var rows = await Client(hub).ListIntentsAsync("fr-fr");
            Assert.Equal(
                new[] { (Weather, "current.weather", "padatious", true) },
                rows.Select(row => (row.SkillId, row.IntentName, row.Engine, row.Enabled)));
            Assert.Equal("template", rows[0].Method);
            Assert.Equal("default", rows[0].SessionId);
            Assert.Null(rows[0].Definition);
            Assert.True(ThalovantContext.SameLanguage(rows[0].Lang, "fr-fr"));

            var definitions = await Client(hub).DescribeIntentAsync(Weather, "current.weather", "fr-fr");
            Assert.Single(definitions);
            Assert.Equal("quel temps fait-il", definitions[0].Samples[0]);
            Assert.Equal("padatious", definitions[0].Engine);
            Assert.Equal(Weather, definitions[0].SkillId);
            Assert.Empty(definitions[0].Raw["blacklist"]!.AsArray());
            Assert.Empty(await Client(hub).DescribeIntentAsync(Shadow, "custos.incidents", "fr-fr"));
        }

        [Fact]
        public async Task ListIntentsCanAskForTheDefinitions()
        {
            var hub = new FakeHubBus { DefinitionsInList = true };
            var rows = await Client(hub).ListIntentsAsync("en-us", new IntentListOptions { IncludeDefinitions = true });
            Assert.Equal(2, rows.Count);
            Assert.NotNull(rows[0].Definition);
            Assert.Equal("what is the weather", (string?)rows[0].Definition!["samples"]![0]);
            Assert.Equal("{\"lang\":\"en-us\",\"include_definitions\":true}", hub.Emitted[0].Data.ToJsonString());
        }

        [Fact]
        public async Task ToJsonObjectIsJsonReadyAndComplete()
        {
            var inventory = await Client(new FakeHubBus()).IntentsAsync(new[] { "en-us", "fr-fr" });
            var payload = inventory.ToJsonObject();
            Assert.Equal(HubIntentInventory.SourceIntentManifest, (string?)payload["source"]);
            Assert.Equal(new[] { "en-us", "fr-fr" }, payload["languages"]!.AsArray().Select(node => node!.GetValue<string>()));
            Assert.Empty(payload["denied"]!.AsArray());
            var weather = payload["skills"]!.AsArray().First(skill => (string?)skill!["skill_id"] == Weather)!;
            Assert.Equal(new[] { "en-us", "fr-fr" }, weather["languages"]!.AsArray().Select(node => node!.GetValue<string>()));
            var intent = weather["intents"]![0]!;
            Assert.Equal($"{Weather}:current.weather", (string?)intent["id"]);
            Assert.Equal("padatious", (string?)intent["engine"]);
            Assert.True((bool?)intent["enabled"]);
            Assert.Equal("quel temps fait-il", (string?)intent["phrases"]!["fr-fr"]![0]);
        }

        [Fact]
        public async Task LanguagesDefaultToEnglish()
        {
            var hub = new FakeHubBus();
            await Client(hub).IntentsAsync();
            Assert.Equal("en-us", (string?)hub.Emitted[0].Data["lang"]);
            hub.Emitted.Clear();
            await Client(hub).IntentsAsync(Array.Empty<string>());
            Assert.Equal("en-us", (string?)hub.Emitted[0].Data["lang"]);
            hub.Emitted.Clear();
            await Client(hub).ListIntentsAsync();
            Assert.Equal("en-us", (string?)hub.Emitted[0].Data["lang"]);
            hub.Emitted.Clear();
            await Client(hub).DescribeIntentAsync(Weather, "current.weather");
            Assert.Equal("en-us", (string?)hub.Emitted[0].Data["lang"]);
            Assert.Equal("en-us", (string?)hub.Emitted[0].Context["lang"]);
        }

        [Fact]
        public async Task LanguagesMadeOnlyOfWhitespaceAreRejected()
        {
            var hub = new FakeHubBus();
            await Assert.ThrowsAsync<ThalovantRuntimeException>(() => Client(hub).IntentsAsync(new[] { " ", "" }));
            Assert.Empty(hub.Emitted);
        }

        [Fact]
        public void PolicyDeniedExceptionIsBuiltFromTheEvent()
        {
            var denied = new ThalovantEvent(ThalovantEvents.PolicyDenied, new JsonObject
            {
                ["denied_type"] = "ovos.intent.list",
                ["code"] = "acl_disallowed_type",
                ["reason"] = "ovos.intent.list not in allowed_types",
                ["data"] = new JsonObject
                {
                    ["msg_type"] = "ovos.intent.list",
                    ["allowed"] = new JsonArray { "recognizer_loop:utterance", "speak" },
                },
            });
            var error = ThalovantPolicyDeniedException.FromEvent(denied);
            Assert.IsAssignableFrom<ThalovantRuntimeException>(error);
            Assert.Equal("ovos.intent.list", error.DeniedType);
            Assert.Equal("acl_disallowed_type", error.Code);
            Assert.Equal("ovos.intent.list not in allowed_types", error.Reason);
            Assert.Equal(new[] { "recognizer_loop:utterance", "speak" }, error.Allowed);
            Assert.Contains("ovos.intent.list not in allowed_types", error.Message);

            var bare = new ThalovantPolicyDeniedException("ovos.intent.describe");
            Assert.Equal("", bare.Code);
            Assert.Equal("", bare.Reason);
            Assert.Empty(bare.Allowed);
            Assert.Contains("refused by the hub's policy", bare.Message);

            var withoutList = ThalovantPolicyDeniedException.FromEvent(
                new ThalovantEvent(ThalovantEvents.PolicyDenied, new JsonObject { ["denied_type"] = "speak", ["code"] = "acl_disallowed_type" }));
            Assert.Equal("speak", withoutList.DeniedType);
            Assert.Empty(withoutList.Allowed);
            Assert.Contains("acl_disallowed_type", withoutList.Message);
        }

        /// <summary>The <c>Allowed</c> list a <c>hive.policy.denied</c> carrying these entries produces.</summary>
        private static IReadOnlyList<string> AllowedFrom(params JsonNode?[] entries)
        {
            return ThalovantPolicyDeniedException.FromEvent(new ThalovantEvent(ThalovantEvents.PolicyDenied, new JsonObject
            {
                ["denied_type"] = "ovos.intent.list",
                ["code"] = "acl_disallowed_type",
                ["data"] = new JsonObject { ["allowed"] = new JsonArray(entries) },
            })).Allowed;
        }

        [Fact]
        public void TheAllowedListKeepsNonEmptyStringEntriesTrimmed()
        {
            // The contract's wording, and the reference's: non-empty string
            // entries, trimmed.

            // A number, a null, a bare boolean or an object is not a message
            // type; stringifying one would put "3" in front of an operator
            // reading which types to allow.
            Assert.Equal(
                new[] { "speak" },
                AllowedFrom("speak", 3, null, true, new JsonObject { ["msg_type"] = "ovos.intent.list" }));

            // A blank entry names nothing at all.
            Assert.Equal(new[] { "speak" }, AllowedFrom("", "   ", "speak"));

            // What is left is trimmed, and the order the hub gave is kept.
            Assert.Equal(
                new[] { "recognizer_loop:utterance", "speak" },
                AllowedFrom(" recognizer_loop:utterance ", "speak"));

            // A hub whose list holds nothing usable leaves it empty rather than
            // showing the operator entries that name no type.
            Assert.Empty(AllowedFrom(0, null, " "));
        }

        [Fact]
        public void SameLanguageFoldsCaseAndSeparators()
        {
            Assert.True(ThalovantContext.SameLanguage("fr-fr", "fr_FR"));
            Assert.True(ThalovantContext.SameLanguage(" fr-FR ", "FR-fr"));
            Assert.False(ThalovantContext.SameLanguage("fr-fr", "fr-ca"));
            Assert.False(ThalovantContext.SameLanguage("en", "en-us"));
        }

        [Fact]
        public async Task HasPhrasesMeansAtLeastOneSentence()
        {
            // A listing whose describes all came back empty does not "have phrases".
            var hub = new FakeHubBus
            {
                Registrations = new[] { ("en-us", Shadow, "custos.incidents", Array.Empty<string>()) },
            };
            var inventory = await Client(hub).IntentsAsync(new[] { "en-us" });
            Assert.NotEmpty(inventory.Intents);
            Assert.Single(EmittedOf(hub, ThalovantEvents.IntentDescribe));
            Assert.False(inventory.HasPhrases);
        }

        [Fact]
        public async Task LanguagesAreFoldedAndDeduplicatedBeforeAsking()
        {
            var hub = new FakeHubBus();
            var inventory = await Client(hub).IntentsAsync(new[] { " en-us ", "en-US", "en_us", "fr-fr" });
            Assert.Equal(new[] { "en-us", "fr-fr" }, inventory.Languages);
            Assert.Equal(
                new[] { "en-us", "fr-fr" },
                EmittedOf(hub, ThalovantEvents.IntentList).Select(entry => entry.Data["lang"]!.GetValue<string>()));
        }

        private const string Scout = "thalovant-skill-source-scout.thalovant";

        private static readonly (string SkillId, string IntentName)[] ScoutKeywords =
        {
            (Scout, "SourceScoutTopicIntent"),
            (Scout, "SourceScoutRealPromptIntent"),
        };

        private static string[] ListingsAsked(FakeHubBus hub)
        {
            return EmittedOf(hub, ThalovantEvents.IntentList)
                .Where(entry => entry.Data.ContainsKey("lang"))
                .Select(entry => entry.Data["lang"]!.GetValue<string>())
                .ToArray();
        }

        [Fact]
        public async Task ATagWithOnlyKeywordRowsIsListedUnderItsUsualForm()
        {
            // Not empty is not the same as answered. The Pop Culture hub on
            // 2026-10-11: one skill registers its keyword vocabulary in 63
            // regional tags, every other skill only in en-US. Its en-CA listing
            // was five keyword rows, which carry no sentences, so a phone in
            // Toronto was shown five names and nothing it could say -- the
            // retry in the usual form only ran on an empty listing.
            var hub = new FakeHubBus
            {
                Keywords = { ["en-CA"] = ScoutKeywords, ["en-us"] = ScoutKeywords },
            };
            var inventory = await Client(hub).IntentsAsync(new[] { "en-CA" });

            // The usual form has sentences, en-CA has none.
            Assert.Equal(new[] { "en-us" }, inventory.ListedIn);
            Assert.True(inventory.HasPhrases);
            Assert.Equal(new[] { Shadow, Scout, Weather }, inventory.Skills.Select(skill => skill.SkillId));
            Assert.Equal(new[] { "en-CA", "en-us" }, ListingsAsked(hub));
        }

        [Fact]
        public async Task KeywordRowsAreKeptWhenTheUsualFormHasNoSentencesEither()
        {
            var hub = new FakeHubBus
            {
                Registrations = Array.Empty<(string Lang, string SkillId, string IntentName, string[] Samples)>(),
                Keywords = { ["en-CA"] = ScoutKeywords, ["en-us"] = ScoutKeywords.Take(1).ToArray() },
            };
            var inventory = await Client(hub).IntentsAsync(new[] { "en-CA" });

            // Nothing better to take.
            Assert.Equal(new[] { "en-CA" }, inventory.ListedIn);
            Assert.Equal(
                new[] { "SourceScoutRealPromptIntent", "SourceScoutTopicIntent" },
                inventory.Intents.Select(intent => intent.Name));
            Assert.Equal(new[] { "en-CA", "en-us" }, ListingsAsked(hub));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AFailedRetryKeepsTheNamesTheFirstListingGave(bool refused)
        {
            // A retry never makes the answer worse than the first listing.
            var hub = new FakeHubBus { Keywords = { ["en-CA"] = ScoutKeywords } };
            (refused ? hub.ListFailsIn : hub.ListSilentIn).Add("en-us");
            // "" is the listing without a language: it fails the same way.
            (refused ? hub.ListFailsIn : hub.ListSilentIn).Add("");
            var inventory = await Client(hub).IntentsAsync(
                new[] { "en-CA" },
                new IntentInventoryOptions { Timeout = TimeSpan.FromMilliseconds(200) });

            Assert.Equal(HubIntentInventory.SourceIntentManifest, inventory.Source);
            Assert.Equal(new[] { "en-CA" }, inventory.ListedIn);
            Assert.Equal(
                new[] { "SourceScoutRealPromptIntent", "SourceScoutTopicIntent" },
                inventory.Intents.Select(intent => intent.Name));
            Assert.Equal(new[] { "en-CA", "en-us" }, ListingsAsked(hub));
        }

        [Fact]
        public async Task AFailedRetryAfterAnEmptyListingStillFailsAsBefore()
        {
            var hub = new FakeHubBus
            {
                Registrations = Array.Empty<(string Lang, string SkillId, string IntentName, string[] Samples)>(),
                ListFailsIn = { "en-us" },
            };
            var error = await Assert.ThrowsAsync<ThalovantRuntimeException>(() => Client(hub).IntentsAsync(
                new[] { "en-CA" },
                new IntentInventoryOptions { Timeout = TimeSpan.FromMilliseconds(200) }));
            Assert.Contains("listing failed", error.Message);
        }

        [Fact]
        public async Task CancellingTheRetryIsNotKeptAsAFailedRetry()
        {
            // The first listing's names stand against a timeout or a refusal,
            // never against the caller giving up.
            var hub = new FakeHubBus { Keywords = { ["en-CA"] = ScoutKeywords }, ListSilentIn = { "en-us" } };
            using var cancelled = new CancellationTokenSource();
            var pending = Client(hub).IntentsAsync(
                new[] { "en-CA" },
                new IntentInventoryOptions { Timeout = TimeSpan.FromSeconds(30) },
                cancelled.Token);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (EmittedOf(hub, ThalovantEvents.IntentList).Count() < 2 && DateTime.UtcNow < deadline) await Task.Delay(1);
            // The retry went out before the cancel.
            Assert.Equal(new[] { "en-CA", "en-us" }, ListingsAsked(hub));
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }

        [Fact]
        public async Task AListingWithSentencesIsNotRetriedForItsKeywordRows()
        {
            var hub = new FakeHubBus
            {
                Registrations = DefaultRegistrations
                    .Where(entry => entry.Lang == "en-us")
                    .SelectMany(entry => new[] { entry, (Lang: "en-CA", entry.SkillId, entry.IntentName, entry.Samples) })
                    .ToArray(),
                Keywords = { ["en-CA"] = ScoutKeywords },
            };
            var inventory = await Client(hub).IntentsAsync(new[] { "en-CA" });

            Assert.Equal(new[] { "en-CA" }, ListingsAsked(hub));
            Assert.Equal(new[] { "en-CA" }, inventory.ListedIn);
        }

        /// <summary>
        /// One intent, two registrations in one language: the keyword row has no
        /// samples, and the template row's sentences survive whichever order the
        /// rows arrive in. The first row names the engine.
        /// </summary>
        private sealed class DualEngineHub : FakeHubBus
        {
            private readonly bool _keywordFirst;

            public DualEngineHub(bool keywordFirst)
            {
                _keywordFirst = keywordFirst;
            }

            public override Task EmitBusAsync(string type, JsonObject data, JsonObject context, CancellationToken cancellationToken = default)
            {
                if (type != ThalovantEvents.IntentList)
                {
                    return base.EmitBusAsync(type, data, context, cancellationToken);
                }
                Record(type, data, context);
                var lang = (string?)data["lang"] ?? "";
                var template = new JsonObject
                {
                    ["skill_id"] = Weather,
                    ["intent_name"] = "current.weather",
                    ["lang"] = lang,
                    ["method"] = "template",
                    ["enabled"] = true,
                    ["session_id"] = "default",
                    ["definition"] = new JsonObject
                    {
                        ["skill_id"] = Weather,
                        ["intent_name"] = "current.weather",
                        ["lang"] = lang,
                        ["samples"] = new JsonArray { "what is the weather" },
                    },
                };
                var keyword = new JsonObject
                {
                    ["skill_id"] = Weather,
                    ["intent_name"] = "current.weather",
                    ["lang"] = lang,
                    ["method"] = "keyword",
                    ["enabled"] = true,
                    ["session_id"] = "default",
                    ["definition"] = new JsonObject
                    {
                        ["skill_id"] = Weather,
                        ["intent_name"] = "current.weather",
                        ["lang"] = lang,
                        ["required"] = new JsonArray { new JsonArray { "WeatherKeyword" } },
                    },
                };
                var rows = _keywordFirst ? new JsonArray { keyword, template } : new JsonArray { template, keyword };
                Deliver(ThalovantEvents.IntentListResponse, new JsonObject { ["ok"] = true, ["intents"] = rows }, context);
                return Task.CompletedTask;
            }
        }

        [Theory]
        [InlineData(false, "padatious")]
        [InlineData(true, "adapt")]
        public async Task AKeywordRowDoesNotEraseTheTemplateRowsSentences(bool keywordFirst, string expectedEngine)
        {
            var inventory = await Client(new DualEngineHub(keywordFirst)).IntentsAsync(new[] { "en-us" });
            var intent = Assert.Single(inventory.Intents);
            Assert.Equal(new[] { "what is the weather" }, intent.PhrasesFor("en-us"));
            Assert.Equal(new[] { "en-us" }, intent.Languages);
            // The first row names the engine.
            Assert.Equal(expectedEngine, intent.Engine);
            Assert.True(inventory.HasPhrases);
        }

        /// <summary>A hub whose adapt manifest names the weather intent as well.</summary>
        private sealed class BothEnginesHub : FakeHubBus
        {
            public override Task EmitBusAsync(string type, JsonObject data, JsonObject context, CancellationToken cancellationToken = default)
            {
                if (type != ThalovantEvents.AdaptManifestGet)
                {
                    return base.EmitBusAsync(type, data, context, cancellationToken);
                }
                Record(type, data, context);
                Deliver(
                    ThalovantEvents.AdaptManifest,
                    new JsonObject { ["intents"] = new JsonArray { $"{Weather}:current.weather" } },
                    context);
                return Task.CompletedTask;
            }
        }

        [Fact]
        public async Task TheFallbackKeepsTheFirstEngineThatNamesAnIntent()
        {
            var hub = new BothEnginesHub { Refuse = { ThalovantEvents.IntentList } };
            var inventory = await Client(hub).IntentsAsync(new[] { "en-us" });
            Assert.Equal(HubIntentInventory.SourceEngineManifests, inventory.Source);
            var weather = inventory.Intents.Single(intent => intent.Name == "current.weather");
            // adapt is asked before padatious, so a name both list is adapt.
            Assert.Equal("adapt", weather.Engine);
            var shadow = inventory.Intents.Single(intent => intent.Name == "custos.incidents");
            Assert.Equal("padatious", shadow.Engine);
        }

        [Fact]
        public async Task DescribesGoOutInBoundedBatches()
        {
            // A hub with many intents must not put more requests in flight than a
            // bounded reply queue can hold: 69 intents is 69 describes, and every
            // reply arrives twice. They go out 32 at a time, each batch its own
            // subscription window.
            var many = ManyRegistrations(69);
            var hub = new FakeHubBus { Registrations = many };
            var inventory = await Client(hub).IntentsAsync(new[] { "en-us" });

            Assert.Equal(32, HubIntentQueries.DescribeBatch);
            Assert.Equal(69, hub.DescribeWindows.Count);
            var sizes = hub.DescribeWindows.GroupBy(window => window).Select(group => group.Count()).ToArray();
            Assert.Equal(new[] { 32, 32, 5 }, sizes);
            Assert.All(sizes, size => Assert.True(size <= HubIntentQueries.DescribeBatch));

            Assert.Equal(69, inventory.Intents.Count);
            Assert.All(inventory.Intents, intent => Assert.NotEmpty(intent.PhrasesFor("en-us")));
            Assert.Equal(new[] { "sentence 0" }, inventory.Intents[0].PhrasesFor("en-us"));
        }

        [Fact]
        public async Task DescribeManyReturnsEveryRegistrationAcrossBatches()
        {
            var many = ManyRegistrations(69);
            var hub = new FakeHubBus { Registrations = many };
            var wanted = Enumerable.Range(0, 69)
                .Select(n => new IntentKey(Weather, $"intent.{n:D3}", "en-us"))
                .ToArray();
            var described = await HubIntentQueries.DescribeManyAsync(
                Client(hub), wanted, TimeSpan.FromSeconds(5), HubIntentQueries.DescribeBatch, CancellationToken.None);
            Assert.Equal(69, described.Count);
            Assert.Equal(3, hub.DescribeWindows.Distinct().Count());

            // batch: 0 restores the old behaviour, every request in flight at once.
            var unbounded = new FakeHubBus { Registrations = many };
            var all = await HubIntentQueries.DescribeManyAsync(
                Client(unbounded), wanted, TimeSpan.FromSeconds(5), 0, CancellationToken.None);
            Assert.Equal(69, all.Count);
            Assert.Single(unbounded.DescribeWindows.Distinct());
        }

        private sealed class EmptyDescriptionHub : FakeHubBus
        {
            private readonly bool _refused;
            private readonly bool _allAnswered;
            public EmptyDescriptionHub(bool refused, bool allAnswered = false) { _refused = refused; _allAnswered = allAnswered; }
            public override Task EmitBusAsync(string type, JsonObject data, JsonObject context, CancellationToken cancellationToken = default)
            {
                if (type != ThalovantEvents.IntentDescribe) return base.EmitBusAsync(type, data, context, cancellationToken);
                Record(type, data, context);
                if (_allAnswered || (string?)data["intent_name"] == "first")
                    Deliver(ThalovantEvents.IntentDescribeResponse,
                        new JsonObject { ["ok"] = !_refused, ["definitions"] = new JsonArray() }, context);
                return Task.CompletedTask;
            }
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 0)]
        [InlineData(false, 1)]
        [InlineData(true, 1)]
        public async Task EmptyDescriptionCannotTurnLaterSilenceIntoPartialSuccess(bool refused, int batch)
        {
            var hub = new EmptyDescriptionHub(refused);
            var wanted = new[] { new IntentKey(Weather, "first", "en-us"), new IntentKey(Weather, "second", "en-us") };
            await Assert.ThrowsAsync<ThalovantTimeoutException>(() => HubIntentQueries.DescribeManyAsync(
                Client(hub), wanted, TimeSpan.FromMilliseconds(40), batch, CancellationToken.None));
            Assert.Equal(2, EmittedOf(hub, ThalovantEvents.IntentDescribe).Count());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task FullyAnsweredEmptyDescriptionsRemainSuccessful(bool refused)
        {
            var wanted = new[] { new IntentKey(Weather, "first", "en-us"), new IntentKey(Weather, "second", "en-us") };
            var found = await HubIntentQueries.DescribeManyAsync(Client(new EmptyDescriptionHub(refused, true)),
                wanted, TimeSpan.FromMilliseconds(40), 1, CancellationToken.None);
            Assert.Equal(2, found.Count);
            Assert.All(found.Values, Assert.Empty);
        }

        /// <summary>A hub that stops answering describes from one intent index on.</summary>
        private sealed class GoesQuietHub : FakeHubBus
        {
            private readonly int _quietFrom;

            public GoesQuietHub(int quietFrom)
            {
                _quietFrom = quietFrom;
            }

            public override Task EmitBusAsync(string type, JsonObject data, JsonObject context, CancellationToken cancellationToken = default)
            {
                if (type == ThalovantEvents.IntentDescribe)
                {
                    var name = (string?)data["intent_name"] ?? "";
                    var suffix = name.Substring(name.LastIndexOf('.') + 1);
                    if (int.TryParse(suffix, out var index) && index >= _quietFrom)
                    {
                        Record(type, data, context);
                        return Task.CompletedTask;
                    }
                }
                return base.EmitBusAsync(type, data, context, cancellationToken);
            }
        }

        private static (string Lang, string SkillId, string IntentName, string[] Samples)[] ManyRegistrations(int count)
        {
            return Enumerable.Range(0, count)
                .Select(n => ("en-us", Weather, $"intent.{n:D3}", new[] { $"sentence {n}" }))
                .ToArray();
        }

        [Fact]
        public async Task ASilentWindowKeepsWhatTheEarlierWindowsFound()
        {
            // Windows are contiguous slices, so a skill that stops answering can
            // own a whole window. Losing its sentences is right; losing the
            // inventory is not.
            const int quietFrom = 40;
            var hub = new GoesQuietHub(quietFrom) { Registrations = ManyRegistrations(69) };
            var inventory = await Client(hub).IntentsAsync(
                new[] { "en-us" },
                new IntentInventoryOptions { Timeout = TimeSpan.FromMilliseconds(300) });

            // Every intent is still listed.
            Assert.Equal(69, inventory.Intents.Count);
            // Windows are 0-31, 32-63, 64-68. The first answers in full, the
            // second in part, the third not at all -- and the third does not
            // discard the rest.
            var withSentences = inventory.Intents.Where(intent => intent.PhrasesFor("en-us").Count > 0).ToList();
            Assert.Equal(quietFrom, withSentences.Count);
            Assert.Equal(
                Enumerable.Range(0, quietFrom).Select(n => $"intent.{n:D3}"),
                withSentences.Select(intent => intent.Name));
            Assert.True(inventory.HasPhrases);
            Assert.Equal(3, hub.DescribeWindows.Distinct().Count());
        }

        [Fact]
        public async Task AHubSilentFromTheFirstWindowStillFailsFast()
        {
            var hub = new FakeHubBus
            {
                Registrations = ManyRegistrations(69),
                Silent = { ThalovantEvents.IntentDescribe },
            };
            var wanted = Enumerable.Range(0, 69)
                .Select(n => new IntentKey(Weather, $"intent.{n:D3}", "en-us"))
                .ToArray();
            await Assert.ThrowsAsync<ThalovantTimeoutException>(() => HubIntentQueries.DescribeManyAsync(
                Client(hub), wanted, TimeSpan.FromMilliseconds(200), HubIntentQueries.DescribeBatch, CancellationToken.None));
            // It gives up after one window, not after all 69.
            Assert.Equal(32, EmittedOf(hub, ThalovantEvents.IntentDescribe).Count());
        }

        [Fact]
        public void ModelsParseTheObservedShapesLeniently()
        {
            Assert.Null(IntentRegistration.FromJsonObject(new JsonObject { ["skill_id"] = Weather }));
            Assert.Null(IntentRegistration.FromJsonObject(new JsonObject { ["skill_id"] = " ", ["intent_name"] = "x" }));
            var keyword = IntentRegistration.FromJsonObject(new JsonObject
            {
                ["skill_id"] = Weather,
                ["intent_name"] = "keyword.intent",
                ["lang"] = "en-US",
                ["method"] = "keyword",
                ["enabled"] = false,
            })!;
            Assert.Equal("adapt", keyword.Engine);
            Assert.False(keyword.Enabled);
            Assert.Equal("default", keyword.SessionId);
            Assert.Equal("keyword.intent", (string?)keyword.ToJsonObject()["intent_name"]);
            Assert.Equal("unknown", new IntentRegistration(Weather, "x", "en-us", "").Engine);
            Assert.Equal("custom", new IntentRegistration(Weather, "x", "en-us", "custom").Engine);

            Assert.Null(IntentDefinition.FromJsonObject(new JsonObject { ["method"] = "template" }));
            var definition = IntentDefinition.FromJsonObject(new JsonObject
            {
                ["method"] = "template",
                ["definition"] = new JsonObject
                {
                    ["skill_id"] = Weather,
                    ["intent_name"] = "current.weather",
                    ["lang"] = "en-us",
                    ["samples"] = new JsonArray { "what is the weather", "  ", 42, "how is it outside " },
                },
            })!;
            Assert.Equal(new[] { "what is the weather", "how is it outside" }, definition.Samples);
            Assert.Equal("padatious", definition.Engine);
            Assert.Equal(Weather, (string?)definition.ToJsonObject()["definition"]!["skill_id"]);

            var intent = new HubIntent(Weather, "current.weather", "padatious", new Dictionary<string, IReadOnlyList<string>>
            {
                ["en-us"] = new[] { "what is the weather in {location}", "what is the weather" },
            });
            Assert.Equal(new[] { "what is the weather", "what is the weather in {location}" }, intent.Examples());
            Assert.Empty(intent.PhrasesFor("de-de"));
            Assert.Empty(new HubIntent(Weather, "x", "adapt").Examples());
        }
    
        // -- A language the hub never loaded --------------------------------
        //
        // Measured on all 13 public hubs on 2026-10-11: a hub loads its lang and
        // secondary_langs (en-US and fr-FR on most) and lists nothing under any
        // other tag. A phone set to Spanish, Japanese or German asked in its own
        // language, then its usual form, got nothing twice and showed an empty
        // list -- while the hub answered those people in its own language.

        /// <summary>The hub's own listing order on Daily Desk and Rewrite Forge: French first.</summary>
        private static readonly (string Lang, string SkillId, string IntentName, string[] Samples)[] FrenchFirst =
            DefaultRegistrations.Where(entry => entry.Lang == "fr-fr")
                .Concat(DefaultRegistrations.Where(entry => entry.Lang == "en-us"))
                .ToArray();

        private static int HubListingsAsked(FakeHubBus hub)
        {
            return EmittedOf(hub, ThalovantEvents.IntentList).Count(entry => !entry.Data.ContainsKey("lang"));
        }

        [Theory]
        [InlineData("es-ES")]
        [InlineData("ja-JP")]
        [InlineData("de-DE")]
        public async Task ALanguageTheHubNeverLoadedIsListedInTheHubsOwn(string phone)
        {
            var hub = new FakeHubBus { Registrations = FrenchFirst, HubLang = "en-US" };
            var inventory = await Client(hub).IntentsAsync(new[] { phone });

            Assert.Equal(new[] { phone }, inventory.Languages);
            // The language the hub answers that person in, though French is listed first.
            Assert.Equal(new[] { "en-US" }, inventory.ListedIn);
            Assert.True(inventory.HasPhrases);
            Assert.Equal(new[] { Shadow, Weather }, inventory.Skills.Select(skill => skill.SkillId));
            var weather = inventory.Skills[1].Intents[0];
            Assert.Equal("what is the weather", weather.PhrasesFor(inventory.ListedIn[0])[0]);
            Assert.Equal(1, HubListingsAsked(hub));
        }

        [Fact]
        public async Task TheSameLanguageInAnotherRegionComesFirst()
        {
            // en-CA's usual form is en-us; a hub that loaded en-GB has neither.
            var hub = new FakeHubBus
            {
                Registrations = DefaultRegistrations.Where(entry => entry.Lang == "fr-fr")
                    .Concat(DefaultRegistrations.Where(entry => entry.Lang == "en-us").Select(entry => (Lang: "en-gb", entry.SkillId, entry.IntentName, entry.Samples)))
                    .ToArray(),
                HubLang = "fr-FR",
            };
            var inventory = await Client(hub).IntentsAsync(new[] { "en-CA" });

            // English before the hub's own French.
            Assert.Equal(new[] { "en-GB" }, inventory.ListedIn);
        }

        [Fact]
        public async Task WithoutASessionHintTheLanguageWithMostSentencesWins()
        {
            // Two intents in English, one in French.
            var inventory = await Client(new FakeHubBus { Registrations = FrenchFirst }).IntentsAsync(new[] { "ja-JP" });
            Assert.Equal(new[] { "en-US" }, inventory.ListedIn);
        }

        [Fact]
        public void NearestHubLanguageOrdersSameLanguageThenTheHubsOwnThenMostSentences()
        {
            static KeyValuePair<string, IReadOnlyList<IntentRegistration>> Rows(string lang, string method = "template") =>
                new KeyValuePair<string, IReadOnlyList<IntentRegistration>>(lang, new[] { new IntentRegistration("s", "i", lang, method) });

            var listed = new[] { Rows("fr-FR"), Rows("en-US") };
            // A tie goes to the first language listed.
            Assert.Equal("fr-FR", ThalovantContext.NearestHubLanguage("ja-JP", listed));
            Assert.Equal("en-US", ThalovantContext.NearestHubLanguage("ja-JP", listed, "en-US"));
            // A hint the hub never listed is no hint.
            Assert.Equal("fr-FR", ThalovantContext.NearestHubLanguage("ja-JP", listed, "ko-KR"));
            Assert.Equal("fr-FR", ThalovantContext.NearestHubLanguage("fr-CA", listed, "en-US"));
            Assert.Null(ThalovantContext.NearestHubLanguage("ja-JP", new[] { Rows("en-US", "keyword") }));
            Assert.Null(ThalovantContext.NearestHubLanguage("ja-JP", Array.Empty<KeyValuePair<string, IReadOnlyList<IntentRegistration>>>()));
            var disabled = new KeyValuePair<string, IReadOnlyList<IntentRegistration>>("en-US", new[] { new IntentRegistration("s", "i", "en-US", "template", enabled: false) });
            Assert.Null(ThalovantContext.NearestHubLanguage("en-US", new[] { disabled }));
        }

        [Fact]
        public async Task ALanguageTheHubLoadedNeverAsksTheWholeManifest()
        {
            var hub = new FakeHubBus { HubLang = "en-US" };
            var inventory = await Client(hub).IntentsAsync(new[] { "fr-fr", "en-us" });

            Assert.Equal(new[] { "fr-fr", "en-us" }, inventory.ListedIn);
            Assert.Equal(0, HubListingsAsked(hub));
        }

        [Fact]
        public async Task TwoLanguagesThatLandOnTheSameHubLanguageShareOneListing()
        {
            var hub = new FakeHubBus { HubLang = "en-US" };
            var inventory = await Client(hub).IntentsAsync(new[] { "en-us", "ja-JP" });

            // One key per language, not en-us and en-US.
            Assert.Equal(new[] { "en-us", "en-us" }, inventory.ListedIn);
            var weather = inventory.Intents.First(intent => intent.SkillId == Weather);
            Assert.Equal(new[] { "en-us" }, weather.Phrases.Keys);
            Assert.NotEmpty(weather.Phrases["en-us"]);
        }

        [Fact]
        public async Task TheHubsOwnListingIsAskedOncePerInventory()
        {
            var hub = new FakeHubBus { Registrations = FrenchFirst, HubLang = "en-US" };
            var inventory = await Client(hub).IntentsAsync(new[] { "es-ES", "ja-JP", "de-DE" });

            Assert.Equal(new[] { "en-US", "en-US", "en-US" }, inventory.ListedIn);
            Assert.Equal(1, HubListingsAsked(hub));
        }

        /// <summary>A hub whose connection may not ask the listing without a language.</summary>
        private sealed class DeniedHubLanguagesHub : FakeHubBus
        {
            public override Task EmitBusAsync(string type, JsonObject data, JsonObject context, CancellationToken cancellationToken = default)
            {
                if (type == ThalovantEvents.IntentList && !data.ContainsKey("lang"))
                {
                    Record(type, data, context);
                    Deliver(ThalovantEvents.PolicyDenied, new JsonObject { ["denied_type"] = type }, context);
                    return Task.CompletedTask;
                }
                return base.EmitBusAsync(type, data, context, cancellationToken);
            }
        }

        [Theory]
        [InlineData("silent")]
        [InlineData("refused")]
        [InlineData("denied")]
        public async Task AHubThatCannotListItsLanguagesLeavesTheAnswerAsItWas(string failure)
        {
            var hub = failure == "denied" ? new DeniedHubLanguagesHub() : new FakeHubBus();
            hub.Keywords["ja-JP"] = ScoutKeywords;
            if (failure == "silent") hub.ListSilentIn.Add("");
            if (failure == "refused") hub.ListFailsIn.Add("");
            var inventory = await Client(hub).IntentsAsync(
                new[] { "ja-JP" },
                new IntentInventoryOptions { Timeout = TimeSpan.FromMilliseconds(200) });

            Assert.Equal(HubIntentInventory.SourceIntentManifest, inventory.Source);
            Assert.Equal(new[] { "ja-JP" }, inventory.ListedIn);
            Assert.Equal(
                new[] { "SourceScoutRealPromptIntent", "SourceScoutTopicIntent" },
                inventory.Intents.Select(intent => intent.Name));
            Assert.Equal(1, HubListingsAsked(hub));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task TheHubsOwnListingAnswersWhenTheUsualFormFails(bool refused)
        {
            var hub = new FakeHubBus { Keywords = { ["en-CA"] = ScoutKeywords } };
            (refused ? hub.ListFailsIn : hub.ListSilentIn).Add("en-us");
            var inventory = await Client(hub).IntentsAsync(
                new[] { "en-CA" },
                new IntentInventoryOptions { Timeout = TimeSpan.FromMilliseconds(200) });

            Assert.Equal(new[] { "en-US" }, inventory.ListedIn);
            Assert.True(inventory.HasPhrases);
        }

        [Fact]
        public async Task AnEmptyHubStaysEmptyRatherThanFailing()
        {
            var hub = new FakeHubBus { Registrations = Array.Empty<(string Lang, string SkillId, string IntentName, string[] Samples)>() };
            var inventory = await Client(hub).IntentsAsync(new[] { "ja-JP" });
            Assert.Empty(inventory.Skills);
            Assert.Equal(new[] { "ja-JP" }, inventory.ListedIn);
        }

        [Fact]
        public async Task TheHubsOwnListingIsNotAskedWhenNearestIsOff()
        {
            var hub = new FakeHubBus();
            var inventory = await Client(hub).IntentsAsync(new[] { "ja-JP" }, new IntentInventoryOptions { Nearest = false });
            Assert.Empty(inventory.Skills);
            var listing = Assert.Single(EmittedOf(hub, ThalovantEvents.IntentList));
            Assert.Equal("""{"lang":"ja-JP","include_definitions":true}""", listing.Data.ToJsonString());
        }

        [Fact]
        public async Task TheHubsOwnListingAsksForDefinitionsOnlyWhenDescribing()
        {
            var describing = new FakeHubBus { HubLang = "en-US" };
            var inventory = await Client(describing).IntentsAsync(new[] { "es-ES" });
            Assert.True(inventory.HasPhrases);
            // Definitions came with the listing, so nothing is described.
            Assert.Empty(EmittedOf(describing, ThalovantEvents.IntentDescribe));
            var asked = EmittedOf(describing, ThalovantEvents.IntentList).Single(entry => !entry.Data.ContainsKey("lang"));
            Assert.Equal("""{"include_definitions":true}""", asked.Data.ToJsonString());
            // No language on the request context either.
            Assert.False(asked.Context.ContainsKey("lang"));

            var names = new FakeHubBus { HubLang = "en-US" };
            var named = await Client(names).IntentsAsync(new[] { "es-ES" }, new IntentInventoryOptions { Describe = false });
            Assert.Equal(new[] { "en-US" }, named.ListedIn);
            Assert.Equal("{}", EmittedOf(names, ThalovantEvents.IntentList).Single(entry => !entry.Data.ContainsKey("lang")).Data.ToJsonString());
        }

        [Fact]
        public async Task CancellingTheHubsOwnListingPropagates()
        {
            var hub = new FakeHubBus { ListSilentIn = { "" } };
            using var cancelled = new CancellationTokenSource();
            var pending = Client(hub).IntentsAsync(
                new[] { "ja-JP" },
                new IntentInventoryOptions { Timeout = TimeSpan.FromSeconds(30) },
                cancelled.Token);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (HubListingsAsked(hub) < 1 && DateTime.UtcNow < deadline) await Task.Delay(1);
            Assert.Equal(1, HubListingsAsked(hub));
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }

        [Fact]
        public async Task TheSerialisedInventorySaysWhichLanguageItListedIn()
        {
            var payload = (await Client(new FakeHubBus { HubLang = "en-US" }).IntentsAsync(new[] { "es-ES" })).ToJsonObject();
            Assert.Equal("""["es-ES"]""", payload["languages"]!.ToJsonString());
            Assert.Equal("""["en-US"]""", payload["listed_in"]!.ToJsonString());
        }
}
}
