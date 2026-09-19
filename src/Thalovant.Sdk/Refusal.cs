using System;

namespace Thalovant
{
    /// <summary>
    /// What an ask does when the hub refuses it or cannot answer it.
    /// <para>
    /// The hub sends <c>hive.policy.denied</c> the instant it refuses, built
    /// with source and destination context only (hivemind-core
    /// <c>_send_policy_denied</c>), so it carries no request id and names the
    /// type it refused instead. The shared <c>refusal-vectors.json</c> pins
    /// every case here.
    /// </para>
    /// </summary>
    internal static class Refusal
    {
        /// <summary>
        /// How long a fire-and-forget utterance counts as possibly still being
        /// refused. Denials come back as fast as the hub admits a message --
        /// milliseconds -- so this is generous on purpose: a wrong "in flight"
        /// only costs an ask the deadline it always had, where a wrong "not in
        /// flight" ends a question the hub never refused. The shared refusal
        /// vectors name it (<c>untracked_grace_seconds</c>), so every SDK uses
        /// the same window.
        /// </summary>
        internal static readonly TimeSpan UntrackedUtteranceGrace = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Whether a <c>hive.policy.denied</c> is this ask's to throw. A denial
        /// carrying a request id is judged by it, like any reply. Without one it
        /// is taken when it names the type this ask sent and this ask is the
        /// only utterance the client has out: with a second ask, a query, or a
        /// fire-and-forget utterance still inside the grace window, either could
        /// be the one refused, and a wrong guess ends a question the hub never
        /// refused.
        /// </summary>
        internal static bool BelongsToAsk(
            string? requestId,
            string ownRequestId,
            string? deniedType,
            int asksInFlight,
            int queriesInFlight,
            int sendsInFlight)
        {
            if (!string.IsNullOrEmpty(requestId))
            {
                return requestId == ownRequestId;
            }
            return deniedType == ThalovantEvents.RecognizerLoopUtterance
                && asksInFlight == 1
                && queriesInFlight == 0
                && sendsInFlight == 0;
        }

        /// <summary>
        /// The typed exception an ask throws for the failure event it ended on:
        /// a refusal, a question the hub has nothing for, and a fault need three
        /// different sentences, and a bare runtime error allowed only one.
        /// </summary>
        internal static ThalovantRuntimeException ErrorFor(ThalovantEvent failure)
        {
            if (failure.Name == ThalovantEvents.PolicyDenied)
            {
                return ThalovantPolicyDeniedException.FromEvent(failure);
            }
            if (failure.Name == ThalovantEvents.IntentUnmatched || failure.Name == ThalovantEvents.IntentFailure)
            {
                var said = JsonUtil.OptionalString(failure.Data["reason"])
                    ?? JsonUtil.OptionalString(failure.Data["error"]);
                return new ThalovantUnansweredException(said?.Trim());
            }
            return new ThalovantRuntimeException($"Hub reported {failure.Name}.");
        }
    }
}
