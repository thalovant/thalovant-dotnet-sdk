using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Thalovant.Sdk.Tests
{
    public sealed class ControlPlaneSecurityTests
    {
        [Fact]
        public void DeviceBrowserLauncherAcceptsOnlyWebUrlsWithoutUserinfo()
        {
            var launches = 0;
            foreach (var input in new[] { "file:///tmp/program", "javascript:alert(1)", "calc.exe", "--help", "https://user:PRIVATE-CREDENTIAL@example.test", "https://@example.test", "https://example.test/\n--help" })
                ThalovantControlPlane.TryOpenBrowser(input, _ => launches++);
            Assert.Equal(0, launches);
            ThalovantControlPlane.TryOpenBrowser("https://example.test/verify?code=a&next=b", info => {
                launches++;
                if (info.UseShellExecute) Assert.StartsWith("https://", info.FileName);
                else { Assert.Single(info.ArgumentList); Assert.StartsWith("https://", info.ArgumentList[0]); }
            });
            Assert.Equal(1, launches);
        }
        private sealed class PassThrough : DelegatingHandler { public PassThrough(HttpMessageHandler inner) : base(inner) { } }
        [Theory]
        [InlineData(307)]
        [InlineData(308)]
        public async Task LoginRedirectCannotReachDestinationWithDefaultOrInjectedHandler(int status)
        {
            foreach (var injected in new[] { false, true }) {
                using var source = new TcpListener(IPAddress.Loopback, 0);
                using var destination = new TcpListener(IPAddress.Loopback, 0);
                source.Start(); destination.Start();
                var sourcePort = ((IPEndPoint)source.LocalEndpoint).Port;
                var destinationPort = ((IPEndPoint)destination.LocalEndpoint).Port;
                var reply = Task.Run(async () => {
                    using var socket = await source.AcceptTcpClientAsync();
                    using var stream = socket.GetStream();
                    var request = new byte[4096];
                    _ = await stream.ReadAsync(request).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                    var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Redirect\r\nLocation: http://127.0.0.1:{destinationPort}/capture\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response);
                });
                using var handler = new HttpClientHandler { AllowAutoRedirect = true };
                var api = new ThalovantControlPlane($"http://127.0.0.1:{sourcePort}", httpMessageHandler: injected ? new PassThrough(handler) : null);
                var error = await Assert.ThrowsAsync<ThalovantApiException>(() => api.LoginAsync("fixture@example.test", "PRIVATE-CREDENTIAL").WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(status, error.StatusCode); Assert.DoesNotContain("PRIVATE-CREDENTIAL", error.Message);
                await reply.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(destination.Pending());
                if (injected) Assert.False(handler.AllowAutoRedirect);
            }
        }
        [Fact]
        public async Task UncontrolledInjectedClientRejectsCredentialsBeforeIo()
        {
            using var handler = new StubHttpMessageHandler(); using var http = new HttpClient(handler);
            var api = new ThalovantControlPlane("https://api.example.test", accessToken: "PRIVATE-CREDENTIAL", httpClient: http);
            foreach (Func<Task> action in new Func<Task>[] { async () => { await api.LoginAsync("fixture@example.test", "PRIVATE-CREDENTIAL"); }, async () => { await api.ListHubsAsync(); } }) {
                var error = await Assert.ThrowsAsync<ThalovantApiException>(action);
                Assert.Contains("httpMessageHandler", error.Message); Assert.DoesNotContain("PRIVATE-CREDENTIAL", error.Message);
            }
            Assert.Empty(handler.Requests);
            handler.Enqueue(body: "{}"); await api.ListPublicHubsAsync(); Assert.Single(handler.Requests);
        }
        [Fact]
        public async Task InjectedDefaultHeadersCookiesAndCredentialsRequireHttps()
        {
            foreach (var header in new[] { "Authorization", "Proxy-Authorization", "Cookie" }) {
                using var handler = new StubHttpMessageHandler(); using var http = new HttpClient(handler);
                http.DefaultRequestHeaders.TryAddWithoutValidation(header, "PRIVATE-CREDENTIAL");
                var api = new ThalovantControlPlane("http://api.example.test", httpClient: http);
                var error = await Assert.ThrowsAsync<ThalovantApiException>(() => api.ListPublicHubsAsync());
                Assert.DoesNotContain("PRIVATE-CREDENTIAL", error.Message); Assert.Empty(handler.Requests);
            }
            foreach (var useCookies in new[] { false, true }) {
                using var handler = new HttpClientHandler();
                if (useCookies) handler.CookieContainer.Add(new Uri("http://api.example.test"), new Cookie("session", "PRIVATE-CREDENTIAL"));
                else handler.Credentials = new NetworkCredential("fixture", "PRIVATE-CREDENTIAL");
                var api = new ThalovantControlPlane("http://api.example.test", httpMessageHandler: handler);
                var error = await Assert.ThrowsAsync<ThalovantApiException>(() => api.ListPublicHubsAsync());
                Assert.DoesNotContain("PRIVATE-CREDENTIAL", error.Message);
            }
        }
        [Theory]
        [InlineData("http://api.example.test")]
        [InlineData("http://localhost.example.test")]
        [InlineData("http://127.1")]
        [InlineData("https://user:PRIVATE-CREDENTIAL@api.example.test")]
        public async Task CredentialHttpAndUserinfoFailBeforeIo(string endpoint)
        {
            using var handler = new StubHttpMessageHandler();
            var api = new ThalovantControlPlane(endpoint, accessToken: "PRIVATE-CREDENTIAL", httpMessageHandler: handler);
            var login = await Assert.ThrowsAsync<ThalovantApiException>(() => api.LoginAsync("fixture@example.test", "PRIVATE-CREDENTIAL"));
            Assert.DoesNotContain("PRIVATE-CREDENTIAL", login.Message);
            var token = await Assert.ThrowsAsync<ThalovantApiException>(() => api.ListHubsAsync());
            Assert.DoesNotContain("PRIVATE-CREDENTIAL", token.Message); Assert.Empty(handler.Requests);
        }
        [Theory]
        [InlineData("localhost")]
        [InlineData("127.0.0.1")]
        [InlineData("[::1]")]
        public async Task ExplicitLoopbackAllowsCredentialDevelopment(string host)
        {
            using var handler = new StubHttpMessageHandler(); handler.Enqueue(body: "{\"access_token\":\"fixture\"}");
            await new ThalovantControlPlane($"http://{host}:1234", httpMessageHandler: handler).LoginAsync("fixture@example.test", "fixture");
            Assert.Single(handler.Requests);
        }
    }
}
