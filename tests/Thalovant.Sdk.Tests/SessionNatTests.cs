using Xunit;

namespace Thalovant.Sdk.Tests
{
    /// <summary>
    /// A hub rewrites a declared session id; replies must still be recognised.
    /// hivemind-core derives a Layer-1 identity for every client-declared session
    /// as "{conn_nonce}:{declared}" (HIVEMIND-BRIDGE-1 section 4). Comparing the
    /// returned id to the sent one for equality rejected every reply: Ask() timed
    /// out while the hub had already answered. Reproduced live on 2026-09-03.
    /// </summary>
    public class SessionNatTests
    {
        [Fact]
        public void NatRewrittenReplyIsRecognised() =>
            Assert.True(ThalovantEvent.SessionIdsMatch("my-session", "d41d8cd98f00b204:my-session"));

        [Fact]
        public void UnrewrittenReplyIsStillRecognised() =>
            Assert.True(ThalovantEvent.SessionIdsMatch("my-session", "my-session"));

        [Fact]
        public void ReplyForADifferentSessionIsRejected()
        {
            Assert.False(ThalovantEvent.SessionIdsMatch("my-session", "nonce:other"));
            Assert.False(ThalovantEvent.SessionIdsMatch("my-session", "other"));
        }

        [Fact]
        public void OnlyTheDeclaredHalfAfterTheFirstColonMatches()
        {
            // a bare EndsWith would wrongly accept these
            Assert.False(ThalovantEvent.SessionIdsMatch("abc", "nonce:xabc"));
            Assert.False(ThalovantEvent.SessionIdsMatch("abc", "nonce:abc:def"));
            // a declared id containing a colon still matches as a whole
            Assert.True(ThalovantEvent.SessionIdsMatch("a:b", "nonce:a:b"));
            Assert.False(ThalovantEvent.SessionIdsMatch("abc", ""));
            Assert.False(ThalovantEvent.SessionIdsMatch("abc", "nonce:"));
        }
    }
}
