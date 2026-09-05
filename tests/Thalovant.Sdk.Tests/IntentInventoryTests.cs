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
            public HashSet<string> Silent { get; } = new HashSet<string>();
            public bool DefinitionsInList { get; set; }
            public bool EchoRequestId { get; set; } = true;
            public int Repeats { get; set; } = 2;

            /// <summary>What this hub registered; the observed set unless a test says otherwise.</summary>
            public (string Lang, string SkillId, string IntentName, string[] Samples)[] Registrations { get; set; } = DefaultRegistrations;
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
                return Task.CompletedTask;
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
                switch (type)
                {
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

        [Fact]
        public async Task AHubRefusingEverythingThrowsEvenWithTheFallback()
        {
            var hub = new FakeHubBus { Refuse = { ThalovantEvents.IntentList, ThalovantEvents.AdaptManifestGet } };
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
        public async Task ASilentHubTimesOutOnTheListing()
        {
            var hub = new FakeHubBus { Silent = { ThalovantEvents.IntentList } };
            var error = await Assert.ThrowsAsync<ThalovantTimeoutException>(() => Client(hub).IntentsAsync(
                new[] { "en-us" },
                new IntentInventoryOptions { Timeout = TimeSpan.FromMilliseconds(200) }));
            Assert.Contains("ovos.intent.list", error.Message);
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
            var many = Enumerable.Range(0, 69)
                .Select(n => ("en-us", Weather, $"intent.{n:D3}", new[] { $"sentence {n}" }))
                .ToArray();
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
            var many = Enumerable.Range(0, 69)
                .Select(n => ("en-us", Weather, $"intent.{n:D3}", new[] { $"sentence {n}" }))
                .ToArray();
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
    }
}
