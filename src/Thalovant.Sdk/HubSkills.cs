using System;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Thalovant
{
    /// <summary>Optional polling after one accepted shared-runtime skill write.</summary>
    public sealed class HubSkillWaitOptions
    {
        public bool Wait { get; set; }
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(120);
        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
        internal void Validate()
        {
            if (Timeout <= TimeSpan.Zero || PollInterval <= TimeSpan.Zero || PollInterval.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(Timeout), "Hub skill wait durations must be positive; poll interval must fit Task.Delay.");
        }
    }

    public sealed partial class ThalovantControlPlane
    {
        private static string HubSkillsPath(string hubId) => "/v1/hubs/" + Uri.EscapeDataString(hubId) + "/skills";

        /// <summary>Read the hub's shared-runtime skills; requires hubs:inspect.</summary>
        public Task<JsonObject> ListHubSkillsAsync(string hubId, CancellationToken cancellationToken = default) =>
            RequestObjectAsync("GET", HubSkillsPath(hubId), cancellationToken: cancellationToken);

        /// <summary>Read newest-first shared-runtime history. Limit is 1–200.</summary>
        public Task<JsonObject> ListHubSkillHistoryAsync(string hubId, int limit = 50, CancellationToken cancellationToken = default)
        {
            if (limit < 1 || limit > 200) throw new ArgumentOutOfRangeException(nameof(limit));
            return RequestObjectAsync("GET", HubSkillsPath(hubId) + "/history?limit=" + limit.ToString(System.Globalization.CultureInfo.InvariantCulture), cancellationToken: cancellationToken);
        }

        /// <summary>Install on the shared runtime, affecting all its hubs. Requires hubs:write and a paid plan.</summary>
        public Task<JsonObject> InstallHubSkillAsync(string hubId, string skill, string version = "latest", HubSkillWaitOptions? options = null, CancellationToken cancellationToken = default) =>
            ChangeHubSkillAsync("POST", HubSkillsPath(hubId), new JsonObject { ["skill"] = skill, ["version"] = version }, options, cancellationToken);

        /// <summary>Move a shared-runtime skill to an exact version or latest.</summary>
        public Task<JsonObject> UpdateHubSkillAsync(string hubId, string skill, string version, HubSkillWaitOptions? options = null, CancellationToken cancellationToken = default) =>
            ChangeHubSkillAsync("PATCH", HubSkillsPath(hubId) + "/" + Uri.EscapeDataString(skill), new JsonObject { ["version"] = version }, options, cancellationToken);

        /// <summary>Remove the shared-runtime attachment, affecting all served hubs.</summary>
        public Task<JsonObject> RemoveHubSkillAsync(string hubId, string skill, HubSkillWaitOptions? options = null, CancellationToken cancellationToken = default) =>
            ChangeHubSkillAsync("DELETE", HubSkillsPath(hubId) + "/" + Uri.EscapeDataString(skill), null, options, cancellationToken);

        private async Task<JsonObject> ChangeHubSkillAsync(string method, string path, JsonObject? body, HubSkillWaitOptions? options, CancellationToken cancellationToken)
        {
            options ??= new HubSkillWaitOptions();
            options.Validate();
            var accepted = await RequestObjectAsync(method, path, body, cancellationToken: cancellationToken).ConfigureAwait(false);
            return options.Wait ? await WaitForHubSkillOperationAsync(accepted, options, cancellationToken).ConfigureAwait(false) : accepted;
        }

        /// <summary>Resume an accepted operation without repeating its write. Retain accepted before waiting when cancellation is possible.</summary>
        public async Task<JsonObject> WaitForHubSkillOperationAsync(JsonObject accepted, HubSkillWaitOptions? options = null, CancellationToken cancellationToken = default)
        {
            options ??= new HubSkillWaitOptions();
            options.Validate();
            var id = accepted["operation_id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id)) throw new ThalovantApiException("Missing accepted operation_id.");
            var state = accepted["state"]?.GetValue<string>();
            var converged = state == "removing" || state == "removed" ? "removed" : "installed";
            var clock = Stopwatch.StartNew();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clock.Elapsed >= options.Timeout) throw new ThalovantTimeoutException($"Timed out waiting for accepted operation {id}");
                JsonObject operation;
                try { operation = await RequestObjectAsync("GET", "/v1/operations/" + Uri.EscapeDataString(id), cancellationToken: cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { throw new ThalovantApiException($"Could not read accepted operation {id}; resume using its ID."); }
                var status = operation["status"]?.GetValue<string>();
                if (status == "ready")
                {
                    var result = JsonUtil.ParseObject(accepted.ToJsonString());
                    result["state"] = converged;
                    result["operation"] = operation;
                    return result;
                }
                if (status == "failed" || status == "timed_out") throw new ThalovantApiException($"Accepted operation {id} failed; inspect GetOperationAsync for details.");
                var remaining = options.Timeout - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new ThalovantTimeoutException($"Timed out waiting for accepted operation {id}");
                await Task.Delay(remaining < options.PollInterval ? remaining : options.PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
