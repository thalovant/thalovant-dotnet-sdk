using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace Thalovant.Sdk.Tests
{
    /// <summary>
    /// What an ask does when the hub refuses it, against the vectors every SDK
    /// shares: contracts/conformance/refusal-vectors.json in the Python SDK,
    /// vendored here and pinned by the parity contract. A refusal becomes a
    /// typed exception carrying the hub's code and, for a spent quota, its
    /// numbers; an unmatched intent is an unanswered question; and a denial
    /// with no request id is taken only by the ask that can be the one it
    /// refused.
    /// </summary>
    public class RefusalVectorTests
    {
        private static JsonNode Vectors(string name) =>
            JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)))!;

        private static JsonNode Cases => Vectors("refusal-vectors.json");

        private static ThalovantEvent EventOf(JsonNode wire) => new ThalovantEvent(
            wire["type"]!.GetValue<string>(),
            wire["data"]?.AsObject().DeepClone().AsObject() ?? new JsonObject(),
            wire["context"]?.AsObject().DeepClone().AsObject() ?? new JsonObject());

        [Fact]
        public void EveryFailureEventBecomesTheErrorItsVectorNames()
        {
            foreach (var item in Cases["classification"]!.AsArray())
            {
                var vector = item!;
                var name = vector["name"]!.GetValue<string>();
                var expect = vector["expect"]!;
                var error = Refusal.ErrorFor(EventOf(vector["event"]!));
                if (expect["kind"]!.GetValue<string>() == "unanswered")
                {
                    var unanswered = Assert.IsType<ThalovantUnansweredException>(error);
                    // What the person said, which is what a caller shows.
                    Assert.Equal(expect["said"]!.GetValue<string>(), unanswered.Said);
                    continue;
                }
                var refused = Assert.IsType<ThalovantPolicyDeniedException>(error);
                var produced = new JsonObject
                {
                    ["kind"] = "refused",
                    ["denied_type"] = refused.DeniedType,
                    ["code"] = refused.Code,
                    ["reason"] = refused.Reason,
                    ["allowed"] = new JsonArray(refused.Allowed.Select(entry => (JsonNode)JsonValue.Create(entry)!).ToArray()),
                    ["quota"] = refused.Quota is null ? null : new JsonObject
                    {
                        ["period"] = refused.Quota.Period,
                        ["limit"] = refused.Quota.Limit,
                        ["used"] = refused.Quota.Used,
                        ["reset_after"] = refused.Quota.ResetAfter,
                    },
                };
                Assert.True(JsonNode.DeepEquals(produced, expect), $"{name}: produced {produced.ToJsonString()}");
            }
        }

        [Fact]
        public void ADenialIsTakenOnlyByTheAskItCanBelongTo()
        {
            foreach (var item in Cases["correlation"]!.AsArray())
            {
                var vector = item!;
                var requested = vector["request_id"]?.GetValue<string>();
                var requestId = requested switch { "own" => "req-own", "other" => "req-other", _ => null };
                var taken = Refusal.BelongsToAsk(
                    requestId,
                    "req-own",
                    vector["denied_type"]!.GetValue<string>(),
                    vector["asks_in_flight"]!.GetValue<int>(),
                    vector["queries_in_flight"]!.GetValue<int>(),
                    vector["sends_in_flight"]!.GetValue<int>());
                Assert.True(taken == vector["taken"]!.GetValue<bool>(), vector["name"]!.GetValue<string>());
            }
        }

        [Fact]
        public void TheGraceWindowIsTheOneTheVectorsName() =>
            Assert.Equal(
                TimeSpan.FromSeconds(Cases["untracked_grace_seconds"]!.GetValue<int>()),
                Refusal.UntrackedUtteranceGrace);

        [Fact]
        public void TheVectorsCoverEveryKindOfRefusal()
        {
            // A copy that quietly lost its quota or its unanswered case would still pass.
            var classification = Cases["classification"]!.AsArray();
            var codes = classification.Select(item => item!["expect"]!["code"]?.GetValue<string>()).ToHashSet();
            foreach (var code in new[] { "acl_disallowed_type", ThalovantPolicyDeniedException.QuotaExceededCode, ThalovantPolicyDeniedException.BackendUnavailableCode })
            {
                Assert.Contains(code, codes);
            }
            Assert.Contains("unanswered", classification.Select(item => item!["expect"]!["kind"]!.GetValue<string>()));
            var taken = Cases["correlation"]!.AsArray().Select(item => item!["taken"]!.GetValue<bool>()).ToHashSet();
            Assert.Equal(2, taken.Count);
        }

        /// <summary>The production shape: refused at once, no request id, numbers nested.</summary>
        private static ThalovantEvent QuotaDenial() => new ThalovantEvent(
            ThalovantEvents.PolicyDenied,
            new JsonObject
            {
                ["denied_type"] = ThalovantEvents.RecognizerLoopUtterance,
                ["code"] = "intent_quota_exceeded",
                ["reason"] = "daily intent quota exceeded",
                ["data"] = new JsonObject { ["period"] = "daily", ["limit"] = 50, ["used"] = 50, ["reset_after"] = 36120 },
            },
            new JsonObject { ["source"] = "hivemind-core" });

        public static TheoryData<JsonNode, long> NumericShapes => new()
        {
            { JsonValue.Create(50)!, 50 },                      // int
            { JsonValue.Create(50L)!, 50 },                     // long
            { JsonValue.Create(50u)!, 50 },                     // uint
            { JsonValue.Create(50ul)!, 50 },                    // ulong
            { JsonValue.Create((byte)50)!, 50 },                // byte
            { JsonValue.Create((short)50)!, 50 },               // short
            { JsonValue.Create(50m)!, 50 },                     // decimal
            { JsonValue.Create(50d)!, 50 },                     // double
            { JsonValue.Create(50f)!, 50 },                     // float
            { JsonValue.Create("50")!, 50 },                    // numeric string
            { JsonValue.Create(50.5d)!, 0 },                    // not whole
            { JsonValue.Create(1e20d)!, 0 },                    // past a long
            { JsonValue.Create(ulong.MaxValue)!, 0 },           // past a long
            { JsonValue.Create(-5)!, 0 },                       // no policy means this
            { JsonValue.Create(true)!, 0 },                     // not a number at all
        };

        [Theory]
        [MemberData(nameof(NumericShapes))]
        public void AQuotaReadsEveryNumericShapeAJsonValueHolds(JsonNode limit, long expected)
        {
            // TryGetValue<T> does not coerce: a value built in code answers only
            // its own T, so reading one shape made a quota come back as zeros.
            var denial = new ThalovantEvent(
                ThalovantEvents.PolicyDenied,
                new JsonObject
                {
                    ["denied_type"] = ThalovantEvents.RecognizerLoopUtterance,
                    ["code"] = "intent_quota_exceeded",
                    ["data"] = new JsonObject { ["limit"] = limit },
                },
                new JsonObject());
            Assert.Equal(expected, ThalovantPolicyDeniedException.FromEvent(denial).Quota!.Limit);
        }

        [Fact]
        public void ASendThatNeverLeftIsNotInFlight()
        {
            // A phantom would suppress a real refusal for the whole grace window.
            var client = new ThalovantClient(new ThalovantIdentity(new JsonObject
            {
                ["access_key"] = "k",
                ["password"] = "p",
                ["site_id"] = "s",
                // Nothing listens here, so the publish cannot happen.
                ["default_master"] = "wss://hub.invalid:1",
            }));
            Assert.ThrowsAny<Exception>(() => client.EmitAsync(
                ThalovantEvents.RecognizerLoopUtterance, new JsonObject()).GetAwaiter().GetResult());
            Assert.Equal(0, client.UtterancesInFlight().Sends);
        }

        [Fact]
        public void TheCollectorTakesAnUncorrelatedRefusalWhenItIsTheOnlyUtteranceOut()
        {
            var state = new AskState(utterancesInFlight: () => (1, 0, 0));
            state.Process(QuotaDenial(), "req-own");
            var failure = state.Snapshot().FailureEvent;
            Assert.NotNull(failure);
            var refused = Assert.IsType<ThalovantPolicyDeniedException>(Refusal.ErrorFor(failure!));
            Assert.Equal(50, refused.Quota!.Used);
            Assert.DoesNotContain("dashboard", refused.Message);
        }

        [Theory]
        [InlineData(2, 0, 0)]
        [InlineData(1, 1, 0)]
        [InlineData(1, 0, 1)]
        public void TheCollectorLeavesADenialThatCouldBeAnotherMessages(int asks, int queries, int sends)
        {
            // A second ask, a query, or a fire-and-forget utterance still inside
            // the grace window: either could be the one refused, and ending the
            // wrong ask fails a question the hub never refused.
            var state = new AskState(utterancesInFlight: () => (asks, queries, sends));
            state.Process(QuotaDenial(), "req-own");
            Assert.Null(state.Snapshot().FailureEvent);
        }
    }
}
