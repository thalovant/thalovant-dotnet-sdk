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

Keep `result.Identity` secret: it holds the client credentials the hub trusts.
`result.ToJsonObject()` redacts every secret it carries — the identity and the
secret subkeys of the raw hub/client resources — so that default form is safe
to log or persist for display. Only `result.ToJsonObject(includeSecrets: true)`
returns the credentials in the clear; never log or print that form.

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
var api = new ThalovantControlPlane(accessToken: Environment.GetEnvironmentVariable("THALOVANT_API_TOKEN"));
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

## Provision Hubs

Hubs, runtime groups, and skills can be created and managed from code. These
routes need a **paid plan** and a token with the **`hubs:write`** scope ("Create
and update your hubs" on the dashboard's API Tokens page). A missing scope fails
first with HTTP 403 `Insufficient scopes`; a free-plan token with the right scope
fails with HTTP 402 `API access requires a paid plan.` Both surface as
`ThalovantApiException` with the status code on `StatusCode`.

```csharp
using Thalovant;
using System.Text.Json.Nodes;  // hub and runtime-group specs are JsonObject

var api = new ThalovantControlPlane(
    accessToken: Environment.GetEnvironmentVariable("THALOVANT_API_TOKEN"));

// 1. Discover what is installable before committing to anything. This read is
//    NOT paid-gated, so a free-plan token can browse the catalog first.
var catalog = await api.ListMarketplaceSkillsAsync();
foreach (var skill in catalog["data"]!.AsArray())
{
    Console.WriteLine($"{skill!["skill_id"]} {skill["access_tier"]}");
}

// 2. Create a runtime group to run the skills.
var group = await api.CreateRuntimeGroupAsync(
    new CreateRuntimeGroupOptions("kiosks") { Description = "Lobby kiosks" });
var groupId = (string)group["id"]!;

// 3. Create a hub attached to it.
var hub = await api.CreateHubAsync(new CreateHubOptions(
    "joke-garden",
    new JsonObject { ["protocols"] = new JsonObject { ["wss"] = new JsonObject { ["enabled"] = true } } })
{
    RuntimeGroupId = groupId,
});
var hubId = (string)hub["id"]!;

// 4. Install a skill from the marketplace catalog.
await api.InstallRuntimeGroupSkillAsync(
    groupId, new InstallRuntimeGroupSkillOptions("skill-weather"));

// 5. Release: roll the runtime and the hub onto a release channel.
await api.ReleaseRuntimeGroupAsync(groupId, new ReleaseOptions { Channel = "stable" });
await api.ReleaseHubAsync(hubId, new ReleaseOptions { Channel = "stable" });
```

Creating a hub is idempotent. `CreateHubAsync` sends a generated
`Idempotency-Key` header, so a retried call after a timeout returns the hub that
was already created instead of making a second one. Set
`CreateHubOptions.IdempotencyKey` to control the key yourself. It is the only
route in this surface that reads the header — runtime-group creates and skill
installs do not.

Updating and deleting a hub use optimistic locking, so `etag` is a **required**
parameter rather than an option. Pass the `etag` from the hub resource you read;
the SDK sends it as `If-Match`, and the API rejects a stale *or missing* value
with HTTP 412 without changing anything:

```csharp
hub = await api.GetHubAsync(hubId);
var etag = (string)hub["etag"]!;

hub = await api.UpdateHubAsync(hubId, new UpdateHubOptions { Active = false }, etag);
await api.DeleteHubAsync(hubId, (string)hub["etag"]!);
```

Deleting a hub also deletes its clients and ACLs. `name`, `namespace`, and
`domain` are immutable on update (HTTP 400), and `UpdateHubOptions.IsLocked` is
admin-only (HTTP 403). Runtime groups have no `If-Match` requirement at all, but
the API refuses to delete the workspace default group or a group that still has
hubs attached (HTTP 409).

Runtime configuration is merged, not replaced, and `personas` is sent only when
you pass it:

```csharp
await api.UpdateRuntimeGroupConfigAsync(groupId, new JsonObject { ["lang"] = "en-us" });
var config = await api.GetRuntimeGroupConfigAsync(groupId);
Console.WriteLine(config["config"]);
```

Rating a public hub needs `hubs:write` but is **not** paid-gated, so a free-plan
token can rate hubs it does not own:

```csharp
await api.SetHubRatingAsync(hubId, 5);
await api.ClearHubRatingAsync(hubId);
```

## Discover Skills

The marketplace catalog is readable with the **`hubs:read`** scope and, unlike
the provisioning routes above, is **not paid-gated** — a free-plan token can
browse the whole catalog before upgrading, and only the install needs a paid
plan. Each entry carries what an install needs (`skill_id`, `source_type`,
`source_ref`, `config_schema`, `secret_schema`) next to presentation fields
(`title`, `tags`, `verified`, `access_tier`).

```csharp
var catalog = await api.ListMarketplaceSkillsAsync(new MarketplaceSkillListOptions
{
    ForceRefresh = true,  // re-syncs the global catalog from source first; slower
});
```

`MarketplaceSkillListOptions.OwnerId` and `IncludeInactive` are honored for admin
tokens only. The API does not reject them for anyone else — it *silently* scopes
a non-admin caller to their own tenant and to active entries, so do not read a
200 as proof they applied. `ForceRefresh` works for every caller.

Two group-scoped reads need the **`hubs:inspect`** scope and are likewise not
paid-gated. The first resolves the catalog against one runtime group, so each
entry reports whether it is already desired, whether it was observed running, and
whether the tenant plan allows installing it:

```csharp
var view = await api.ListRuntimeGroupMarketplaceAsync(groupId);
foreach (var entry in view["data"]!.AsArray())
{
    if ((bool?)entry!["installable"] == true && (bool?)entry["active"] != true)
    {
        Console.WriteLine($"available: {entry["skill_id"]}");
    }
}
```

The second answers what the group is actually running right now, rather than what
could be installed:

```csharp
var inventory = await api.ListRuntimeGroupInventoryAsync(groupId, refresh: true);
Console.WriteLine($"{inventory["source"]} {inventory["data"]!.AsArray().Count}");
```

Both answer from a cached snapshot by default; pass `refreshInventory: true` or
`refresh: true` to force a live read from the runtime operator. Neither fails
when nothing is reporting yet — `ListRuntimeGroupInventoryAsync` returns an empty
`data` list with a pending `source`, and `ListRuntimeGroupMarketplaceAsync` still
returns the catalog. Reading what one *hub* is running is the exception:

```csharp
var capabilities = await api.GetHubRuntimeCapabilitiesAsync(hubId);
Console.WriteLine(capabilities["counts"]!["total_intents"]);
```

`GetHubRuntimeCapabilitiesAsync` needs `hubs:inspect` and is the one read that
answers **HTTP 409** when the hub has no connected client to report inventory.

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

API-token calls are limited per plan. Both limits surface as
`ThalovantApiException` with HTTP 429 in `StatusCode`, a `Retry-After` header,
and a matching `retry_after_seconds` in the body:

- `token_rate_limited` — the plan's per-minute request rate was exceeded (60
  requests per minute on the free plan). Retry once the current minute resets.
- `token_quota_exceeded` — the plan's daily or monthly call quota is exhausted.
  The body names which in `quota` (`daily` or `monthly`) alongside `limit` and
  `used`. Retry after the next UTC day or month starts.

The SDK does not retry automatically. `ThalovantApiException` carries the
status code, the raw `Body`, and the decoded `ErrorCode` — not response
headers — so read `retry_after_seconds` out of the body to decide when to
resend rather than reaching for the `Retry-After` header.

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
