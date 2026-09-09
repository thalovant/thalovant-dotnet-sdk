using System.Text.Json.Nodes;
using Thalovant;

if (args.Length != 1 || !args[0].StartsWith("ws://127.0.0.1:", StringComparison.Ordinal))
    throw new ArgumentException("Supply one explicit loopback endpoint.");
var endpoint = args[0];
var directory = Path.Combine(Path.GetTempPath(), "thalovant-dotnet-interop-" + Guid.NewGuid().ToString("N"));
var identity = new ThalovantIdentity(new JsonObject {
    ["access_key"] = "fixture", ["password"] = "fixture-password", ["default_master"] = endpoint, ["site_id"] = "fixture" });
using var sdk = new ThalovantClient(identity, noiseStore: new HiveMindFileNoiseStore(directory));
var count = 0;
using var replies = sdk.On("fixture.pong", _ => Interlocked.Increment(ref count));
try {
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    for (var attempt = 0; attempt < 2; attempt++) {
        await Task.WhenAll(Enumerable.Range(0, 3).Select(async n => {
            await sdk.ConnectAsync(TimeSpan.FromSeconds(20), deadline.Token);
            await sdk.EmitAsync("fixture.ping", new JsonObject { ["n"] = n }, cancellationToken: deadline.Token);
        }));
        while (Volatile.Read(ref count) != (attempt + 1) * 3) await Task.Delay(10, deadline.Token);
        if (!(await sdk.HealthcheckAsync(cancellationToken: deadline.Token)).Ok) throw new Exception("Premature authenticated readiness.");
        if ((await sdk.QueryAsync("hello", cancellationToken: deadline.Token)).Text != "query answer") throw new Exception("Incorrect correlated query reply.");
        await sdk.CloseAsync();
        if (attempt == 0) {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            while (true) {
                var status = JsonNode.Parse(await http.GetStringAsync(endpoint.Replace("ws://", "http://") + "/fixture/status", deadline.Token))!;
                if ((int?)status["closed"] == 1) break;
                await Task.Delay(10, deadline.Token);
            }
        }
    }
    Console.WriteLine(".NET WSS XX to KK, six encrypted messages and two scoped queries: passed");
} finally {
    await sdk.CloseAsync();
    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
}
