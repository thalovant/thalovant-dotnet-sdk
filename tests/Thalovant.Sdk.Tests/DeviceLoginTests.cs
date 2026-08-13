using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Thalovant.Sdk.Tests
{
    /// <summary>
    /// Browser device-flow sign-in (POST /v1/auth/device/authorize +
    /// /v1/auth/device/token polling). Mirrors the Python SDK's
    /// login_with_browser test coverage; the stub handler keeps every test
    /// off the network and the injectable delay/clock keep them instant.
    /// </summary>
    public class DeviceLoginTests
    {
        private readonly StubHttpMessageHandler _handler;
        private readonly ThalovantControlPlane _api;

        public DeviceLoginTests()
        {
            _handler = new StubHttpMessageHandler();
            _api = new ThalovantControlPlane(
                apiUrl: "https://api.example.com/v1",
                httpClient: new HttpClient(_handler));
        }

        private static string Pending => """{"error": "authorization_pending"}""";

        [Fact]
        public async Task LoginWithBrowserPollsUntilTokenAndStoresIt()
        {
            _handler.Enqueue(body: Fixtures.DeviceGrant);
            _handler.Enqueue(400, Pending);
            _handler.Enqueue(400, Pending);
            _handler.Enqueue(body: Fixtures.DeviceToken);
            var opened = new List<string>();

            var original = Console.Out;
            var console = new StringWriter();
            DeviceLoginResult result;
            try
            {
                Console.SetOut(console);
                result = await _api.LoginWithBrowserAsync(new DeviceLoginOptions
                {
                    Scopes = new[] { "hubs:read" },
                    ClientName = "xunit",
                    BrowserLauncher = opened.Add,
                });
            }
            finally
            {
                Console.SetOut(original);
            }

            Assert.Equal("device-token", result.AccessToken);
            Assert.Equal("device-token", _api.AccessToken);
            Assert.Equal("bearer", result.TokenType);
            Assert.Equal(new[] { "hubs:read", "clients:write" }, result.Scopes);
            Assert.Equal("2027-08-13T00:00:00Z", result.ExpiresAt);
            Assert.Equal("token-1", result.TokenId);
            Assert.Equal("device-token", (string?)result.Raw["access_token"]);

            Assert.Equal(new[] { "https://dash.thalovant.com/activate?user_code=WDJB-MJHT" }, opened);
            Assert.Contains(
                "To sign in, visit https://dash.thalovant.com/activate and enter the code WDJB-MJHT",
                console.ToString(),
                StringComparison.Ordinal);

            var requests = _handler.Requests;
            Assert.Equal(4, requests.Count);
            Assert.Equal("POST", requests[0].Method);
            Assert.Equal("https://api.example.com/v1/auth/device/authorize", requests[0].Url.AbsoluteUri);
            Assert.Null(requests[0].Header("Authorization"));
            var authorize = requests[0].BodyObject()!;
            Assert.Equal("hubs:read", (string?)authorize["scopes"]![0]);
            Assert.Equal("xunit", (string?)authorize["client_name"]);
            for (var index = 1; index < 4; index++)
            {
                Assert.Equal("POST", requests[index].Method);
                Assert.Equal("https://api.example.com/v1/auth/device/token", requests[index].Url.AbsoluteUri);
                Assert.Null(requests[index].Header("Authorization"));
                Assert.Equal("device-code-1", (string?)requests[index].BodyObject()!["device_code"]);
            }
        }

        [Fact]
        public async Task LoginWithBrowserCustomPromptAndNoBrowser()
        {
            _handler.Enqueue(body: Fixtures.DeviceGrant);
            _handler.Enqueue(body: Fixtures.DeviceToken);
            var grants = new List<DeviceAuthorization>();

            await _api.LoginWithBrowserAsync(new DeviceLoginOptions
            {
                OpenBrowser = false,
                Prompt = grants.Add,
                BrowserLauncher = url => throw new InvalidOperationException("browser must not open"),
            });

            var grant = Assert.Single(grants);
            Assert.Equal("device-code-1", grant.DeviceCode);
            Assert.Equal("WDJB-MJHT", grant.UserCode);
            Assert.Equal("https://dash.thalovant.com/activate", grant.VerificationUri);
            Assert.Equal("https://dash.thalovant.com/activate?user_code=WDJB-MJHT", grant.VerificationUriComplete);
            Assert.Equal(900, grant.ExpiresIn);
            Assert.Equal(TimeSpan.Zero, grant.Interval);

            // No scopes/client name were set, so the authorize body is empty.
            Assert.Empty(_handler.Requests[0].BodyObject()!);
        }

        [Fact]
        public async Task DevicePollSlowDownGrowsInterval()
        {
            _handler.Enqueue(400, Pending);
            _handler.Enqueue(400, """{"error": "slow_down"}""");
            _handler.Enqueue(400, Pending);
            _handler.Enqueue(body: Fixtures.DeviceToken);
            var delays = new List<TimeSpan>();

            var token = await _api.PollDeviceTokenAsync(
                "device-code-1",
                interval: TimeSpan.FromSeconds(5),
                timeout: TimeSpan.FromSeconds(900),
                delay: (wait, _) =>
                {
                    delays.Add(wait);
                    return Task.CompletedTask;
                },
                clock: () => TimeSpan.Zero);

            Assert.Equal("device-token", (string?)token["access_token"]);
            Assert.Equal(
                new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10) },
                delays);
        }

        [Fact]
        public async Task LoginWithBrowserThrowsOnAccessDenied()
        {
            _handler.Enqueue(body: Fixtures.DeviceGrant);
            _handler.Enqueue(400, """{"error": "access_denied"}""");

            await Assert.ThrowsAsync<ThalovantDeviceAccessDeniedException>(
                () => _api.LoginWithBrowserAsync(new DeviceLoginOptions { OpenBrowser = false, Prompt = _ => { } }));
            Assert.Null(_api.AccessToken);
        }

        [Fact]
        public async Task LoginWithBrowserThrowsOnExpiredToken()
        {
            _handler.Enqueue(body: Fixtures.DeviceGrant);
            _handler.Enqueue(400, """{"error": "expired_token"}""");

            var error = await Assert.ThrowsAsync<ThalovantDeviceCodeExpiredException>(
                () => _api.LoginWithBrowserAsync(new DeviceLoginOptions { OpenBrowser = false, Prompt = _ => { } }));
            Assert.Contains("again", error.Message, StringComparison.Ordinal);
            Assert.Null(_api.AccessToken);
        }

        [Fact]
        public async Task DevicePollTimesOut()
        {
            _handler.Enqueue(400, Pending);
            _handler.Enqueue(400, Pending);
            _handler.Enqueue(400, Pending);
            var now = TimeSpan.Zero;

            await Assert.ThrowsAsync<ThalovantTimeoutException>(
                () => _api.PollDeviceTokenAsync(
                    "device-code-1",
                    interval: TimeSpan.FromSeconds(5),
                    timeout: TimeSpan.FromSeconds(10),
                    delay: (wait, _) =>
                    {
                        now += wait;
                        return Task.CompletedTask;
                    },
                    clock: () => now));

            Assert.Equal(3, _handler.Requests.Count);
            Assert.Equal(TimeSpan.FromSeconds(10), now);
        }

        [Fact]
        public async Task DevicePollHonorsCancellation()
        {
            _handler.Enqueue(400, Pending);
            _handler.Enqueue(400, Pending);
            using var cancellation = new CancellationTokenSource();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => _api.PollDeviceTokenAsync(
                    "device-code-1",
                    interval: TimeSpan.FromSeconds(5),
                    timeout: TimeSpan.FromSeconds(900),
                    delay: (wait, token) =>
                    {
                        cancellation.Cancel();
                        return Task.Delay(wait, token);
                    },
                    clock: () => TimeSpan.Zero,
                    cancellationToken: cancellation.Token));

            // The delay observed the cancellation before a second poll went out.
            Assert.Single(_handler.Requests);
        }

        [Fact]
        public async Task LoginWithBrowserRejectsIncompleteAuthorization()
        {
            _handler.Enqueue(body: """{"device_code": "device-code-1"}""");

            var error = await Assert.ThrowsAsync<ThalovantApiException>(
                () => _api.LoginWithBrowserAsync(new DeviceLoginOptions { OpenBrowser = false, Prompt = _ => { } }));
            Assert.Contains("incomplete", error.Message, StringComparison.Ordinal);
            Assert.Single(_handler.Requests);
        }

        [Fact]
        public async Task DevicePollSurfacesUnexpectedErrors()
        {
            _handler.Enqueue(400, """{"error": "invalid_grant"}""");

            var error = await Assert.ThrowsAsync<ThalovantApiException>(
                () => _api.PollDeviceTokenAsync(
                    "device-code-1",
                    interval: TimeSpan.FromSeconds(5),
                    timeout: TimeSpan.FromSeconds(900),
                    delay: (_, _) => Task.CompletedTask,
                    clock: () => TimeSpan.Zero));
            Assert.Equal(400, error.StatusCode);
            Assert.Contains("invalid_grant", error.Body, StringComparison.Ordinal);
        }
    }
}
