# Thalovant .NET SDK

.NET SDK for connecting enterprise .NET and Unity apps to Thalovant hubs.

The control API is used to discover hubs and provision a client identity. After
that, the SDK talks directly to the hub data plane over WSS. (HTTPS and MQTTS
data-plane transports are available in the Node and Go SDKs and are not part of
this .NET SDK yet.)

Full docs: <https://docs.thalovant.com/developers/sdks/>

## What You Need

- A Thalovant account with API access for authenticated control-plane actions.
- A hub id or slug.
- A client identity for that hub. You can create one through the API or use one
  downloaded from the dashboard.

## Install

```bash
dotnet add package Thalovant.Sdk
```

The library multi-targets `net8.0` and `netstandard2.1` (Unity 2021+
compatible). The only dependency on the `netstandard2.1` target is the
`System.Text.Json` package; the `net8.0` build has zero external dependencies.

## Quick Start

```csharp
using Thalovant;

var api = new ThalovantControlPlane();

// Public hub discovery does not require auth.
var publicHubs = await api.ListPublicHubsAsync(limit: 12);
foreach (var hub in publicHubs["data"]!.AsArray())
{
    Console.WriteLine($"{hub!["id"]} {hub["slug"]} {hub["title"]}");
}

// Auth is required when creating a client identity.
await api.LoginAsync("you@example.com", "password");

var result = await api.CreateClientIdentityAsync(
    "hub-id",
    new CreateClientIdentityOptions("dotnet-demo-client"));

using var client = new ThalovantClient(result.Identity);
await client.ConnectAsync();
var reply = await client.AskAsync("Tell me a short clean joke.");
Console.WriteLine(reply.Text);
await client.CloseAsync();
```

`new ThalovantControlPlane()` uses `https://api.thalovant.com` by default. Pass
a different URL only for local development or a self-hosted control plane.

Keep `result.Identity` secret. It contains the client credentials used by the
hub. Do not log `result.ToJsonObject(includeSecrets: true)`.

## Sign In Through the Browser (Device Flow)

Accounts without a password (for example Google sign-in) can authenticate with
the device flow. The SDK prints the verification URI and a short user code,
opens the browser (best-effort; set `OpenBrowser = false` to disable), and
polls until you approve the request:

```csharp
var result = await api.LoginWithBrowserAsync(new DeviceLoginOptions
{
    Scopes = new[] { "hubs:read", "clients:write" },
    ClientName = "my-tool",
});
Console.WriteLine($"{result.TokenId} expires {result.ExpiresAt}");
```

The returned `access_token` is a durable scoped API token, stored on
`api.AccessToken` exactly like `LoginAsync`. Pass a `Prompt` callback to
present the code yourself, set `Timeout` (default 15 minutes), and pass a
`CancellationToken` to abort polling. A denied request throws
`ThalovantDeviceAccessDeniedException`, an expired code throws
`ThalovantDeviceCodeExpiredException`, and running past the timeout throws
`ThalovantTimeoutException`.

## Use a Pre-Provisioned API Token (CI)

Non-interactive environments can skip login entirely by constructing the
client with a token minted earlier (for example through the device flow or the
dashboard):

```csharp
var api = new ThalovantControlPlane(accessToken: Environment.GetEnvironmentVariable("THALOVANT_TOKEN"));
// or later: api.AccessToken = "...";
```

## Log In With MFA

Accounts with multi-factor authentication enabled must include a TOTP code or a
recovery code with the login. Without one the API responds with HTTP 401 and
code `mfa_required` (surfaced as `ThalovantApiException.ErrorCode`).

```csharp
await api.LoginAsync("you@example.com", "password", otpCode: "123456");

// Or use a one-time recovery code instead:
await api.LoginAsync("you@example.com", "password", recoveryCode: "abcd-efgh-ijkl");
```

## List Your Hubs

Authenticated accounts can list owned or visible hubs:

```csharp
var page = await api.ListHubsAsync(limit: 50);
foreach (var hub in page["data"]!.AsArray())
{
    Console.WriteLine($"{hub!["id"]} {hub["title"]}");
}
```

## Operations

Mutating endpoints return durable operations you can poll:

```csharp
var operation = await api.GetOperationAsync("operation-id");
Console.WriteLine(operation.Status);  // Requested, Committed, Applied, Ready, Failed, TimedOut
```

## Workspace Analytics

Authenticated accounts can read the same overview used by the dashboard:

```csharp
var overview = await api.AnalyticsOverviewAsync(new AnalyticsOverviewOptions
{
    Range = "7d",
    HubId = "hub-id",
});
Console.WriteLine(overview["totals"]);
```

Admins can set `Admin = true` (and optionally `OwnerId`) to read the
platform-wide `/v1/admin/analytics/overview` rollup instead.

## Durable Memory

Private Daily Desk and workspace assistants can manage explicit opt-in memory:

```csharp
var memory = await api.CreateMemoryItemAsync(new MemoryCreatePayload("Prefer America/Toronto for scheduling.")
{
    Scope = MemoryScope.Workspace,
    Kind = MemoryKind.Preference,
    Tags = new[] { "timezone" },
});
Console.WriteLine(memory.Id);

var items = await api.ListMemoryItemsAsync(new MemoryListOptions
{
    Scope = MemoryScope.Workspace,
    Query = "timezone",
});
Console.WriteLine($"{items.Data.Count} {items.Meta.Count}");

var summary = await api.GetMemorySummaryAsync();
Console.WriteLine($"{summary.Total} {summary.ByScope}");

await api.DeleteMemoryItemAsync(memory.Id);
```

## Identities

Identities can be built from JSON or loaded from a JSON file. On POSIX
platforms the file must not be group- or world-readable; run
`chmod 600 <path>` first. The check is skipped on Windows (and on the
netstandard2.1/Unity build, which has no portable file-mode API).

```csharp
var identity = ThalovantIdentity.FromFile("/path/to/identity.json");
using var client = new ThalovantClient(identity);
```

The identity document uses the same snake_case fields the API returns from
`initial_identify`: `access_key`, `password`, `crypto_key`, `site_id`,
`default_master`, `default_port`, plus optional `data_plane_endpoints`,
`protocols`, and `mqtt` broker credentials.

## Events

Handlers can observe hub bus events directly:

```csharp
var subscription = client.On("speak", e => Console.WriteLine(e.DisplayText));
// later:
subscription.Close();
```

## Protocol Selection

Hubs advertise enabled protocols (`spec.protocols.{wss,http,mqtt}.enabled`,
WSS enabled by default) and concrete `data_plane_endpoints`. The SDK prefers
`wss`, then `https`, then `mqtt`:

```csharp
var selected = HubEndpoints.SelectDataPlaneEndpoint(
    HubDataPlaneEndpoints.FromHub(hub),
    HubProtocolSettings.From(hub));
```

`ThalovantClient` itself is WSS-only in 0.1.x; constructing it with
`HubProtocol.Https` or `HubProtocol.Mqtt` throws
`ThalovantUnsupportedProtocolException`.

## Errors

- `ThalovantApiException` — control API failures, with `StatusCode`, raw
  `Body`, and the decoded `ErrorCode` where the API provides one.
- `ThalovantConnectionException` / `ThalovantTimeoutException` /
  `ThalovantRuntimeException` — data-plane connection, deadline, and hub
  failures.
- `ThalovantDeviceAccessDeniedException` / `ThalovantDeviceCodeExpiredException`
  — the browser device sign-in was denied or its code expired.
- `ThalovantIdentityException` — malformed or insecure identity documents.
- `ThalovantUnsupportedProtocolException` — the protocol is disabled, missing
  an endpoint, or not supported by this SDK.

## Development

```bash
dotnet build
dotnet test
```

The test suite is fully offline: HTTP requests are intercepted with a stub
`HttpMessageHandler` and the WSS wire protocol is tested through its pure
encode/decode functions. The in-tree AES-128-GCM implementation is validated
against NIST vectors plus known-answer vectors generated with Node.js `crypto`
(the exact configuration the Node SDK uses on the HiveMind wire).

## License

MIT — see [LICENSE](LICENSE).
