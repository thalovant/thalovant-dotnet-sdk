using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
namespace Thalovant.Sdk.Tests
{
    public class HubSkillsTests
    {
        private const string Accepted = """{"operation_id":"op-1","state":"installing","skill":"s/1"}""";
        [Fact] public async Task RoutesAndHistoryPreserveSharedRuntimeDataAndResumeWithoutReplay()
        {
            var handler = new StubHttpMessageHandler();
            var api = new ThalovantControlPlane(apiUrl: "https://api.example.com", accessToken: "token", httpMessageHandler: handler);
            handler.Enqueue(200, """{"data":[]}""");
            handler.Enqueue(200, """{"data":[{"kind":"event","actor_email":null}]}""");
            handler.Enqueue(202, Accepted); handler.Enqueue(200, """{"status":"ready"}"""); handler.Enqueue(202, Accepted); handler.Enqueue(202, Accepted);
            await api.ListHubSkillsAsync("h/1");
            var history = await api.ListHubSkillHistoryAsync("h/1", 200);
            Assert.Null(history["data"]![0]!["actor_email"]);
            var accepted = await api.InstallHubSkillAsync("h/1", "s/1", "1.2.0");
            Assert.Equal("installed", (string?)(await api.WaitForHubSkillOperationAsync(accepted))["state"]);
            Assert.Equal("installing", (string?)accepted["state"]);
            await api.UpdateHubSkillAsync("h/1", "s/1", "1.2.0"); await api.RemoveHubSkillAsync("h/1", "s/1");
            Assert.Equal(new[] { "GET", "GET", "POST", "GET", "PATCH", "DELETE" }, handler.Requests.Select(r => r.Method));
            Assert.Equal("/v1/hubs/h%2F1/skills", handler.Requests[0].Url.AbsolutePath);
            Assert.Equal("?limit=200", handler.Requests[1].Url.Query);
            Assert.Equal("/v1/hubs/h%2F1/skills/s%2F1", handler.Requests[4].Url.AbsolutePath);
            Assert.Equal("1.2.0", (string?)handler.Requests[2].BodyObject()!["version"]);
        }
        [Theory][InlineData("failed")][InlineData("timed_out")][InlineData("applied")][InlineData("http-error")]
        public async Task PollFailureRetainsIdAndDoesNotReplay(string status)
        {
            var handler = new StubHttpMessageHandler();
            var api = new ThalovantControlPlane(apiUrl: "https://api.example.com", accessToken: "token", httpMessageHandler: handler);
            handler.Enqueue(202, Accepted);
            handler.Enqueue(status == "http-error" ? 503 : 200, status == "http-error" ? """{"detail":"private-data"}""" : "{\"status\":\"" + status + "\"}");
            var error = await Assert.ThrowsAnyAsync<ThalovantException>(() => api.InstallHubSkillAsync("h", "s", options: new HubSkillWaitOptions { Wait = true, Timeout = TimeSpan.FromMilliseconds(50) }));
            Assert.Contains("op-1", error.Message); Assert.DoesNotContain("private-data", error.Message); Assert.Equal(2, handler.Requests.Count);
        }
        [Theory]
        [InlineData("operation_id", "{}")][InlineData("operation_id", "null")][InlineData("operation_id", "123")]
        [InlineData("state", "{}")][InlineData("state", "null")][InlineData("state", "123")]
        public async Task MalformedAcceptedFieldsFailWithoutPolling(string field, string value)
        {
            var handler = new StubHttpMessageHandler();
            var api = new ThalovantControlPlane(apiUrl: "https://api.example.com", accessToken: "token", httpMessageHandler: handler);
            var accepted = JsonNode.Parse(Accepted)!.AsObject();
            accepted[field] = JsonNode.Parse(value);
            await Assert.ThrowsAsync<ThalovantApiException>(() => api.WaitForHubSkillOperationAsync(accepted));
            Assert.Empty(handler.Requests);
        }
        [Theory][InlineData("{}")][InlineData("null")][InlineData("123")]
        public async Task MalformedPollStatusRetainsOperationId(string value)
        {
            var handler = new StubHttpMessageHandler();
            var api = new ThalovantControlPlane(apiUrl: "https://api.example.com", accessToken: "token", httpMessageHandler: handler);
            handler.Enqueue(200, "{\"status\":" + value + "}");
            var error = await Assert.ThrowsAsync<ThalovantApiException>(() => api.WaitForHubSkillOperationAsync(JsonNode.Parse(Accepted)!.AsObject()));
            Assert.Contains("op-1", error.Message);
            Assert.Single(handler.Requests);
        }
        [Fact] public async Task ValidationAndCancellationDoNotSend()
        {
            var handler = new StubHttpMessageHandler();
            var api = new ThalovantControlPlane(apiUrl: "https://api.example.com", accessToken: "token", httpMessageHandler: handler);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => api.ListHubSkillHistoryAsync("h", 0));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => api.ListHubSkillHistoryAsync("h", 201));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => api.InstallHubSkillAsync("h", "s", options: new HubSkillWaitOptions { Timeout = TimeSpan.Zero }));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.WaitForHubSkillOperationAsync(JsonNode.Parse(Accepted)!.AsObject(), cancellationToken: cancellation.Token));
            Assert.Empty(handler.Requests);
        }
    }
}
