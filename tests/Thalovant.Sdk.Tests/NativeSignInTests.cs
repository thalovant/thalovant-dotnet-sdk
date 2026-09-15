using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Thalovant.Sdk.Tests
{
    /// <summary>
    /// The authorization-code grant, which every app that needed it wrote for
    /// itself until this existed.
    ///
    /// What is tested is what is a security bug when wrong and looks fine when
    /// wrong: that the challenge really is S256 of the verifier, that the
    /// verifier never reaches the browser, that a redirect answering a
    /// different attempt is refused, and that plain cannot be asked for.
    /// </summary>
    public class NativeSignInTests
    {
        private static Dictionary<string, string> Query(string url)
        {
            var found = new Dictionary<string, string>();
            var mark = url.IndexOf('?');
            if (mark < 0) return found;
            foreach (var pair in url.Substring(mark + 1).Split('&'))
            {
                var split = pair.IndexOf('=');
                if (split <= 0) continue;
                found[Uri.UnescapeDataString(pair.Substring(0, split))] =
                    Uri.UnescapeDataString(pair.Substring(split + 1));
            }
            return found;
        }

        [Fact]
        public void TheChallengeIsTheS256OfTheVerifier()
        {
            var begun = NativeSignIn.Begin("thalovant-maui", "thalovant://auth");
            using var sha256 = SHA256.Create();
            var expected = Convert.ToBase64String(sha256.ComputeHash(Encoding.ASCII.GetBytes(begun.Verifier)))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
            Assert.Equal(expected, Query(begun.AuthorizationUrl)["code_challenge"]);
            Assert.Equal(expected, NativeSignIn.ChallengeFor(begun.Verifier));
        }

        [Fact]
        public void TheVerifierNeverReachesTheBrowser()
        {
            var begun = NativeSignIn.Begin("app", "app://auth");
            Assert.DoesNotContain(begun.Verifier, begun.AuthorizationUrl);
        }

        [Fact]
        public void OnlyS256IsOffered()
        {
            var begun = NativeSignIn.Begin("app", "app://auth");
            Assert.Equal("S256", Query(begun.AuthorizationUrl)["code_challenge_method"]);
        }

        [Fact]
        public void EveryAttemptGetsItsOwnVerifierAndState()
        {
            var first = NativeSignIn.Begin("app", "app://auth");
            var second = NativeSignIn.Begin("app", "app://auth");
            Assert.NotEqual(first.Verifier, second.Verifier);
            Assert.NotEqual(first.State, second.State);
            Assert.NotEqual(NativeSignIn.NewVerifier(), NativeSignIn.NewVerifier());
        }

        [Fact]
        public void TheRequestCarriesWhatTheAuthorizeEndpointMatchesOn()
        {
            var begun = NativeSignIn.Begin(
                "thalovant-maui", "thalovant://auth", new[] { "hubs:read", "clients:write" });
            var parameters = Query(begun.AuthorizationUrl);
            Assert.Equal("thalovant-maui", parameters["client_id"]);
            Assert.Equal("thalovant://auth", parameters["redirect_uri"]);
            Assert.Equal("code", parameters["response_type"]);
            Assert.Equal("hubs:read clients:write", parameters["scope"]);
            Assert.Equal(begun.State, parameters["state"]);
            Assert.StartsWith("https://dash.thalovant.com/authorize?", begun.AuthorizationUrl);
        }

        [Fact]
        public void TheDefaultScopesAreTheThreeAFreePlanMayMint()
        {
            var begun = NativeSignIn.Begin("app", "app://auth");
            Assert.Equal(string.Join(" ", NativeSignIn.DefaultScopes), Query(begun.AuthorizationUrl)["scope"]);
        }

        [Fact]
        public void ARedirectAnsweringADifferentAttemptIsRefused()
        {
            var begun = NativeSignIn.Begin("app", "app://auth");
            Assert.Null(begun.CodeFrom("app://auth?code=abc&state=somebody-elses"));
            Assert.Equal("abc", begun.CodeFrom($"app://auth?code=abc&state={begun.State}"));
        }

        [Fact]
        public void NoCodeOrAnErrorInsteadIsNotSuccess()
        {
            var begun = NativeSignIn.Begin("app", "app://auth");
            Assert.Null(begun.CodeFrom($"app://auth?state={begun.State}"));
            Assert.Null(begun.CodeFrom($"app://auth?code=&state={begun.State}"));
            Assert.Null(begun.CodeFrom($"app://auth?error=access_denied&state={begun.State}"));
            Assert.Null(begun.CodeFrom("app://auth"));
        }

        [Fact]
        public void ACodeWithEscapedCharactersSurvivesTheRoundTrip()
        {
            var begun = NativeSignIn.Begin("app", "app://auth");
            Assert.Equal("a+b/c=", begun.CodeFrom($"app://auth?code=a%2Bb%2Fc%3D&state={begun.State}"));
        }

        [Fact]
        public void AnEmptyClientOrRedirectIsRefusedHereRatherThanAtTheApi()
        {
            Assert.Throws<ArgumentException>(() => NativeSignIn.Begin("", "app://auth"));
            Assert.Throws<ArgumentException>(() => NativeSignIn.Begin("app", "   "));
        }

        [Fact]
        public void AThalovantUrlIsRecognisedBySchemeAndHost()
        {
            Assert.True(NativeSignIn.IsThalovantUrl("https://dash.thalovant.com/authorize?x=1"));
            Assert.True(NativeSignIn.IsThalovantUrl("https://thalovant.com"));
            Assert.False(NativeSignIn.IsThalovantUrl("http://dash.thalovant.com"));
            // The one that matters: a lookalike host ending in the same letters.
            Assert.False(NativeSignIn.IsThalovantUrl("https://dash.thalovant.com.evil.test"));
            Assert.False(NativeSignIn.IsThalovantUrl("https://notthalovant.com"));
            Assert.False(NativeSignIn.IsThalovantUrl("nonsense"));
        }
    }
}
