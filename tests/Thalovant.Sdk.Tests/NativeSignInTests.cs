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
            // A host that passes, reached through credentials reading as another.
            Assert.False(NativeSignIn.IsThalovantUrl("https://evil.test@dash.thalovant.com"));
            Assert.False(NativeSignIn.IsThalovantUrl("https://notthalovant.com"));
            Assert.False(NativeSignIn.IsThalovantUrl("nonsense"));
        }
        [Fact]
        public void ARefusalThatAlsoCarriesACodeIsStillARefusal()
        {
            // CodeRabbit caught this: checking only for a missing code accepted
            // error=access_denied&code=... and would have started an exchange
            // on a code the authorization server had just declined to issue.
            var begun = NativeSignIn.Begin("app", "app://auth");
            Assert.Null(begun.CodeFrom($"app://auth?error=access_denied&code=abc&state={begun.State}"));
            Assert.Null(begun.CodeFrom($"app://auth?code=abc&error=server_error&state={begun.State}"));
        }

        [Fact]
        public void TheTokenExchangeRefusesCleartextAndAllowsLoopback()
        {
            Assert.Throws<ThalovantApiException>(
                () => NativeSignIn.RequireSecureTokenExchange("http://control.example.test"));
            // Loopback has no cleartext to observe, and is how the API is run locally.
            foreach (var allowed in new[] { "http://localhost:8080", "http://127.0.0.1:8080", "https://api.thalovant.com" })
            {
                NativeSignIn.RequireSecureTokenExchange(allowed);
            }
        }
        [Fact]
        public void ACallbackArrivingSomewhereElseIsRefused()
        {
            // CodeRabbit: state proves the answer belongs to this request; it
            // does not prove it came back to the app that made it.
            var begun = NativeSignIn.Begin("app", "app://auth");
            Assert.Equal("abc", begun.CodeFrom($"app://auth?code=abc&state={begun.State}"));
            Assert.Null(begun.CodeFrom($"app://elsewhere?code=abc&state={begun.State}"));
            Assert.Null(begun.CodeFrom($"https://evil.test/auth?code=abc&state={begun.State}"));
        }

        [Theory]
        [InlineData("http://dash.example.test")]
        [InlineData("https://evil.test@dash.thalovant.com")]
        [InlineData("ftp://dash.thalovant.com")]
        // A fragment puts every parameter somewhere a browser never sends.
        [InlineData("https://dash.example.test#section")]
        [InlineData("https://dash.example.test?next=/x")]
        public void ADashboardThatIsNotSafeIsRefused(string dashboard)
        {
            Assert.Throws<ThalovantApiException>(
                () => NativeSignIn.Begin("app", "app://auth", null, dashboard));
        }

        [Theory]
        [InlineData("https://dash.example.test")]
        [InlineData("http://localhost:9000")]
        [InlineData("http://[::1]:9000")]
        public void ASelfHostedOrLoopbackDashboardIsAllowed(string dashboard)
        {
            var begun = NativeSignIn.Begin("app", "app://auth", null, dashboard);
            Assert.StartsWith(dashboard, begun.AuthorizationUrl);
        }

        [Fact]
        public void TheDefaultScopesCannotBeRewrittenByACaller()
        {
            Assert.IsNotType<string[]>(NativeSignIn.DefaultScopes);
        }
    }
}
