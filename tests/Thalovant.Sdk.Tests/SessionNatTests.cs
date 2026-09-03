using Xunit;

namespace Thalovant.Sdk.Tests
{
    /// <summary>
    /// A hub substitutes its own session id; the request id is what correlates.
    /// Observed against a live hub on 2026-09-03: a client declaring
    /// session_id="observe-me" gets every reply back carrying the hub's own
    /// uuid. Comparing session ids rejected replies the request id had already
    /// identified as ours, so Ask() timed out while the hub had answered.
    /// The filter lives inline in Client; this pins the decision table.
    /// </summary>
    public class SessionNatTests
    {
        private static bool Accepts(string? askedSession, string? askedRequest,
                                    string? replySession, string? replyRequest)
        {
            if (askedRequest is not null && replyRequest is not null)
            {
                return replyRequest == askedRequest;
            }
            return !(askedSession is not null && replySession is not null && replySession != askedSession);
        }

        [Fact]
        public void MatchingRequestIdWinsOverSubstitutedSession() =>
            Assert.True(Accepts("observe-me", "req-1", "71048b7f-e7b0", "req-1"));

        [Fact]
        public void WrongRequestIdRejectedEvenIfSessionsAgree() =>
            Assert.False(Accepts("same", "req-1", "same", "req-2"));

        [Fact]
        public void WithoutRequestIdsTheSessionStillDecides()
        {
            Assert.True(Accepts("s1", null, "s1", null));
            Assert.False(Accepts("s1", null, "s2", null));
        }
    }
}
