using System.Text.Json.Nodes;
using Xunit;

namespace Thalovant.Sdk.Tests
{
    /// <summary>
    /// Unit tests for the AskAsync reply-aggregation state machine: request-id
    /// correlation, fragment dedupe, handled/failure transitions.
    /// </summary>
    public class AskCorrelationTests
    {
        private static ThalovantEvent Speak(string text, string? requestId)
        {
            var context = requestId is null
                ? new JsonObject()
                : ThalovantContext.WithCorrelation(null, requestId: requestId);
            return new ThalovantEvent(ThalovantEvents.Speak, new JsonObject { ["utterance"] = text }, context);
        }

        [Fact]
        public void IgnoresEventsWithOtherOrMissingRequestIds()
        {
            var state = new AskState();
            state.Process(Speak("wrong", "other"), "r-1");
            state.Process(Speak("uncorrelated", null), "r-1");
            Assert.Empty(state.Snapshot().Fragments);
            Assert.False(state.ProgressGate.IsOpen);
        }

        [Fact]
        public void CollectsAndDedupesFragments()
        {
            var state = new AskState();
            state.Process(Speak("Hello   there", "r-1"), "r-1");
            state.Process(Speak("Hello there", "r-1"), "r-1");
            state.Process(Speak("Second line", "r-1"), "r-1");
            var snapshot = state.Snapshot();
            Assert.Equal(new[] { "Hello there", "Second line" }, snapshot.Fragments);
            Assert.True(state.ProgressGate.IsOpen);
            Assert.True(state.ReplyGate.IsOpen);
            Assert.Null(snapshot.FailureEvent);
        }

        [Fact]
        public void HandledEventOpensProgressWithoutFragments()
        {
            var state = new AskState();
            var handled = new ThalovantEvent(
                ThalovantEvents.UtteranceHandled,
                new JsonObject(),
                ThalovantContext.WithCorrelation(null, requestId: "r-1"));
            state.Process(handled, "r-1");
            var snapshot = state.Snapshot();
            Assert.True(snapshot.Handled);
            Assert.True(state.ProgressGate.IsOpen);
            Assert.False(state.ReplyGate.IsOpen);
            Assert.Empty(snapshot.Fragments);
        }

        [Fact]
        public void PolicyDeniedBecomesFailure()
        {
            var state = new AskState();
            var denied = new ThalovantEvent(
                ThalovantEvents.PolicyDenied,
                new JsonObject { ["utterance"] = "Denied by policy." },
                ThalovantContext.WithCorrelation(null, requestId: "r-1"));
            state.Process(denied, "r-1");
            var snapshot = state.Snapshot();
            Assert.Equal("hive.policy.denied", snapshot.FailureEvent?.Name);
            Assert.True(state.ProgressGate.IsOpen);
        }

        [Theory]
        // Legacy Mycroft name and current OVOS name for "no intent matched".
        [InlineData("complete_intent_failure")]
        [InlineData("ovos.intent.unmatched")]
        public void IntentFailureAndUnmatchedFailFastAndOpenGates(string eventName)
        {
            // An utterance matching no intent is terminal (issue #22): the ask
            // must complete promptly with the failure event set and the progress
            // gate opened, rather than waiting out its timeout.
            var state = new AskState();
            var failure = new ThalovantEvent(
                eventName,
                new JsonObject(),
                ThalovantContext.WithCorrelation(null, requestId: "r-1"));
            state.Process(failure, "r-1");
            var snapshot = state.Snapshot();
            Assert.Single(snapshot.Events);
            Assert.Equal(eventName, snapshot.FailureEvent?.Name);
            Assert.True(snapshot.Handled);
            Assert.True(state.ProgressGate.IsOpen);
        }

        [Fact]
        public void QueryTimeoutBecomesFailure()
        {
            var state = new AskState();
            var timedOut = new ThalovantEvent(
                ThalovantEvents.QueryTimeout,
                new JsonObject(),
                ThalovantContext.WithCorrelation(null, requestId: "r-1"));
            state.Process(timedOut, "r-1");
            Assert.Equal("hive.query.timeout", state.Snapshot().FailureEvent?.Name);
        }

        [Fact]
        public void RequestIdFallsBackToSessionAndData()
        {
            var inContext = new ThalovantEvent("speak", context: new JsonObject { ["request_id"] = "r-1" });
            Assert.Equal("r-1", inContext.RequestId);
            var inSession = new ThalovantEvent("speak", context: new JsonObject
            {
                ["session"] = new JsonObject { ["request_id"] = "r-2" },
            });
            Assert.Equal("r-2", inSession.RequestId);
            var inData = new ThalovantEvent("speak", data: new JsonObject { ["request_id"] = "r-3" });
            Assert.Equal("r-3", inData.RequestId);
            var correlation = new ThalovantEvent("speak", context: new JsonObject { ["correlation_id"] = "r-4" });
            Assert.Equal("r-4", correlation.RequestId);
        }

        [Fact]
        public void EventTextAndUtterances()
        {
            var busEvent = new ThalovantEvent(
                "speak",
                new JsonObject
                {
                    ["utterance"] = "<speak>Hello</speak>",
                    ["utterances"] = new JsonArray { "<speak>Hello</speak>" },
                });
            Assert.Equal("<speak>Hello</speak>", busEvent.Text);
            Assert.Equal("Hello", busEvent.DisplayText);
            Assert.Equal(new[] { "<speak>Hello</speak>" }, busEvent.Utterances);
            Assert.True(new ThalovantEvent(ThalovantEvents.IntentFailure).IsFailure);
            Assert.False(new ThalovantEvent("speak").IsFailure);
        }

        [Fact]
        public void IntentUnmatchedAndLegacyIntentFailureAreBothFailures()
        {
            // OVOS renamed the "no intent matched" bus event from the legacy
            // Mycroft "complete_intent_failure" to "ovos.intent.unmatched".
            // Both names must classify as a failure (issue #22).
            Assert.True(new ThalovantEvent("ovos.intent.unmatched").IsFailure);
            Assert.True(new ThalovantEvent(ThalovantEvents.IntentUnmatched).IsFailure);
            Assert.True(new ThalovantEvent("complete_intent_failure").IsFailure);
            Assert.True(new ThalovantEvent(ThalovantEvents.IntentFailure).IsFailure);
        }

        [Fact]
        public void ClientRejectsNonWssProtocols()
        {
            var identity = new ThalovantIdentity((JsonObject)JsonNode.Parse(Fixtures.ClientIdentify)!);
            Assert.Throws<ThalovantUnsupportedProtocolException>(() => new ThalovantClient(identity, HubProtocol.Mqtt));
            Assert.Throws<ThalovantUnsupportedProtocolException>(() => new ThalovantClient(identity, HubProtocol.Https));
        }

        [Fact]
        public void ClientRequiresWssEndpoint()
        {
            // https default_master only, no wss endpoint anywhere.
            var identity = new ThalovantIdentity(new JsonObject
            {
                ["access_key"] = "k",
                ["password"] = "p",
                ["site_id"] = "s",
                ["default_master"] = "https://hub.example.com",
            });
            Assert.Throws<ThalovantUnsupportedProtocolException>(() => new ThalovantClient(identity));
        }
    }
}
