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
dotnet add package Thalovant.Sdk --version 0.5.0
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

Default bootstrap and identity JSON displays also remove recognized credential
fields recursively from metadata (`authorization`, `client_secret`,
`private_key`, `api_secret`, `secret_key`, `credentials`, token fields, and
`initial_identify`), ignoring case,
underscores, and hyphens. Reference fields such as `apiKeyRef` remain intact.
This does not sanitize arbitrary text or alter the explicit `includeSecrets`
serialization used for persistence.

Intent descriptions may return partial results after a timeout only when at
least one reply supplied parsed definitions. Empty or refused descriptions
alone do not hide a missing reply, including in a later batch. When every
requested description is answered explicitly, an empty inventory is valid.

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

Runtime configuration is deep-merged using a revision precondition, and `personas` is sent only when
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
(`title`, `category`, `tags`, `verified`, `access_tier`).

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
    if ((bool?)entry!["installable"] == true && (bool?)entry["desired"] != true)
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

## Runtime helpers

```csharp
var conversation = client.Conversation(lang: "fr-fr");
var reply = await conversation.QueryAsync("bonjour");
await conversation.SendActionAsync("show-details", title: "Details");
await conversation.SendCodeAsync("001-09", label: "Ticket");
var health = await client.HealthcheckAsync();
Console.WriteLine(health.Ok);
```

Conversations keep a stable session and merge nested context without changing
caller input. Each request receives a fresh request id. `QueryAsync` exchanges
HiveMind query/cascade frames, accepts only matching query ids, and waits for
query completion. Requests are not automatically replayed after disconnects.

`WaitForEventAsync` accepts correlation filters and a predicate; `ListenAsync`
returns a bounded `IAsyncEnumerable<ThalovantEvent>` with optional deadline and
maximum count. Cancellation, completion and timeout remove subscriptions;
transport loss fails promptly. The 64-event stream buffer reports overflow
explicitly. Pass a `CancellationToken` to stop an outstanding operation.

`ConnectWithInfoAsync`, `ConnectionInfo`, `HealthcheckAsync` and `DoctorAsync`
report local authenticated transport state, not health of every skill or hub
dependency. Each queued connection caller's deadline includes admission wait;
its cancellation does not cancel another caller's active socket.

## Events

Handlers can observe hub bus events directly:

```csharp
var subscription = client.On("speak", e => Console.WriteLine(e.DisplayText));
// later:
subscription.Close();
```

## What the Hub Can Be Asked

A connected client can ask its hub for the intent inventory: every intent each
skill registered, per language, with the sentences a person says to reach it
as the skill's locale files wrote them, `{slot}` placeholders included. It is
read from the hub runtime's intent manifest over the client's own session, so
no control-plane credential is involved — a satellite, an installer, or an
agent can show a person what they can say.

```csharp
var inventory = await client.IntentsAsync(new[] { "en-us", "fr-fr" });
foreach (var skill in inventory.Skills)
{
    Console.WriteLine($"{skill.SkillId} ({string.Join(", ", skill.Languages)})");
    foreach (var intent in skill.Intents)
    {
        // "what is the weather", "how is it outside", ...
        Console.WriteLine($"  {intent.Name} [{intent.Engine}]: {string.Join(" | ", intent.Examples("en-us"))}");
    }
}
Console.WriteLine(inventory.ToJsonObject().ToJsonString());
```

`languages` defaults to `en-us`. Tags are trimmed and folded before asking:
`en-us`, `en-US`, and `en_us` are one language, asked once, and
`inventory.Languages` keeps the first spelling given. Each `HubIntent` carries
`Id` (`skill_id:name`), `Engine` (`padatious` for sample sentences, `adapt` for
keyword sets), `Enabled`, `Languages`, and `Phrases` keyed by language;
`PhrasesFor("fr-FR")` finds `fr-fr` too, and `Examples(lang, limit: 2)` prefers
whole sentences over ones with a `{slot}`, shorter first. An intent registered
under both engines has two rows per language: the template row carries the
sentences, the keyword row never erases them, and the first row seen names
`Engine`. `inventory.Source` is `intent-manifest` when the sentences came from
the manifest, and `inventory.HasPhrases` is true only when at least one intent
carries at least one sentence.

The two underlying queries are exposed as well: `ListIntentsAsync(lang)`
returns the manifest rows (`IntentRegistration`) and
`DescribeIntentAsync(skillId, intentName, lang)` the registrations behind one
intent (`IntentDefinition`, with `Samples`). `IntentInventoryOptions`,
`IntentListOptions`, and `IntentDescribeOptions` carry the timeout (5 seconds
by default) and the switches:

```csharp
var rows = await client.ListIntentsAsync("fr-fr", new IntentListOptions { IncludeDefinitions = true });
var definitions = await client.DescribeIntentAsync("thalovant-skill-weather.thalovant", "current.weather", "fr-fr");
var namesOnly = await client.IntentsAsync(new[] { "en-us" }, new IntentInventoryOptions { Describe = false });
```

Queries are correlated by `context.request_id` like every other request; a
reply delivered more than once is taken once, and a describe the hub never
answers leaves that intent without sentences rather than failing the whole
inventory. Describes go out in batches of at most 32, each batch its own
subscription window, so a hub with many intents is never sent every request at
once; a window the hub does not answer costs only its own sentences, and the
call fails only when no window answered at all. A reply that carries no request id is taken for the request in flight (a
hub that echoes ids gets strict matching), so do not run two single-reply
intent queries concurrently on one client against a hub that does not echo
request ids.

A hub whose connection may not publish `ovos.intent.list` answers
`hive.policy.denied` at once, which surfaces as `ThalovantPolicyDeniedException`
(`DeniedType`, `Code`, `Reason`, `Allowed`) rather than a timeout. With
`IntentInventoryOptions.Fallback` on (the default) the SDK then asks the
engines' own manifests instead and returns names only: `inventory.Source` is
`engine-manifests`, `inventory.Denied` names the query that triggered fallback, and
`inventory.HasPhrases` is false. The first engine to name an intent decides its
`Engine` there (`adapt` is asked before `padatious`). Each successful engine
reply is kept even if the other engine is denied or silent; if both engines
are unavailable, the first failure is thrown. Caller cancellation still
propagates. Manifest-backed discovery requires `ovos.intent.list`, plus
`ovos.intent.describe` when sentences need a separate description query.
`IntentInventoryOptions.Describe` is on by default; disabling it requires only
the listing. Names-only engine fallback needs permission for at least one
engine manifest instead of these detailed queries. Connections the control plane provisions for SDK
clients allow these read-only queries by default; the exception's message names
what to add to the connection's allow-list otherwise.

A refusal is not the only negative answer. A hub that answers
`ovos.intent.list` with `ok: false` has failed the query rather than reported an
empty hub, so `ListIntentsAsync` (and the inventory behind it) throws
`ThalovantRuntimeException` carrying the hub's `error` text — showing a person a
device that can do nothing would be worse than saying the query failed. The
same `ok: false` from `ovos.intent.describe` is a real answer, meaning the hub
does not know that registration: `DescribeIntentAsync` returns an empty list and
the intent is listed without sentences.

A silent detailed listing now takes the same default engine-manifest fallback
as an explicit policy denial. Disable `Fallback` to keep strict listing timeout
behavior. `Denied` records which query triggered fallback; the marker alone is
not proof of a policy denial. Engine-query errors still propagate.

`ListFallbacksAsync` discovers registered fallback handlers. Every inventory
also makes an optional probe, capped at 1.5 seconds across connection, send and
reply wait. `FallbacksKnown` distinguishes known empty from unknown (denied,
silent or explicitly failed discovery). `inventory.MayAnswer(lang)` remains
true when an enabled intent has phrases, a fallback handler exists, or discovery
is unknown. Missing phrases alone do not rule out an answer in that language.

## HiveMind v3 Noise

WSS requires HiveMind v3 Noise. The first authenticated connection uses
`XXpsk2`; subsequent connections can use `KKpsk0` when both peers have pinned
static keys. The SDK negotiates `25519_AESGCM_SHA256` only, derives the PSK
using Argon2id (64 MiB, 3 iterations, one lane), and binds the complete server
HELLO and offer into the handshake transcript. A ChaChaPoly-only offer fails
with an unsupported-suite error. Both patterns use encrypted binary JSON
frames with ordered cipher counters and bounded chunk reassembly.

`ConnectAsync` completes after key exchange and the encrypted client HELLO.
Legacy crypto-key handshakes and plaintext application frames are rejected.
Standalone legacy wire helpers remain available, but WSS no longer uses them.
Reconnects preserve static identity and pins while clearing ephemeral keys,
transcript, reassembly and cipher counters.

On .NET 8, the default persistent state is under the current user's local
application data directory (`Thalovant/noise`). POSIX directories require 0700
and files 0600; Windows relies on the user's profile ACLs. Never share or
check in this state. A changed hub key fails authentication; verify intentional
key rotation before replacing its saved pin.

Unity/netstandard2.1 lacks portable permission APIs, so it must supply an
**existing app-private directory**, or an `IHiveMindNoiseStore` implementation
backed by platform secure storage. The default file store refuses to create
unprotected state on that target:

```csharp
var store = new HiveMindFileNoiseStore(existingAppPrivateDirectory);
using var client = new ThalovantClient(identity, noiseStore: store);
```

The library keeps zero additional runtime package dependencies. X25519,
Argon2id and BLAKE2b use a pinned, licensed Bouncy Castle source subset;
AES-256 uses the platform provider with the existing in-tree GCM arithmetic.
See [third-party notices](THIRD-PARTY-NOTICES.md) for source revision and
adaptations. HTTPS/MQTT runtime transports remain explicitly unsupported.

## Ask deadlines and correlation

Each logical Ask or Query operation needs a fresh correlation ID. Defaults
already generate one. If you supply an ID, simultaneous Ask calls on one client
must use distinct request IDs, and simultaneous Query calls must use distinct
query IDs. A duplicate active ID raises a runtime error before dispatch. Ask
and Query have separate namespaces, and separate clients are independent.
The reservation ends when its collector unsubscribes, including on cancellation;
it does not cancel or release an admitted physical write. Never reuse an ID for
a later logical operation while a delayed reply from an earlier operation may
still arrive.

Ask uses one total timeout across connection, authentication, send, and replies.
After a WSS write is admitted, caller cancellation stops waiting while the complete encrypted frame sequence retains transport ownership under an independent 20-second physical send budget. A physical timeout or write error retires the captured connection; queued cancellation never interrupts another owner.

A terminal query reply can complete while its admitted write is still retiring. The transport retains write ownership and never replays the request. Ask surfaces a write failure during any active reply phase; a hard terminal reply or an already elapsed reply window takes precedence over later write errors.

The first nonempty speech starts a fixed settling window (250ms by default).
The first handled or soft-miss event without speech starts a fixed empty-reply
window (5s by default); subsequent speech switches to settling. Both windows
are clipped to the original deadline, and an empty window does not add settling.
Hard policy denial or query timeout freezes collection immediately: prior speech
is returned as a failed partial reply; otherwise the call raises a runtime error.
Caller cancellation removes owned subscriptions and preserves a different caller's
connection attempt. Ask requires a matching request ID and returns the first
nonblank correlated runtime session ID, falling back to the requested session.
Query replies use the same session selection from accepted query events.
Event-stream cancellation or expiry also retires the subscription while a consumer
is paused between reads.

## Control-Plane HTTP Security

Device login validates both verification URLs before displaying a prompt,
invoking a browser callback, or polling. Each URL must use HTTP(S), include a
host, and contain no userinfo, raw whitespace, or control characters. Invalid
grants fail with a generic API error; their URLs are not displayed or launched.

Control-plane requests never follow redirects automatically. Credentials and
request bodies require HTTPS, except explicit `localhost`, `127.0.0.1`, and
`[::1]` HTTP development endpoints. Anonymous body-free reads may use HTTP.
URLs containing userinfo are rejected before I/O. Configure the intended API
endpoint directly instead of relying on a redirect.

Authenticated custom transports must use `httpMessageHandler:` instead of
`httpClient:`. A supplied `HttpClient` cannot expose or enforce its redirect
policy, so credential-bearing calls fail before I/O with migration guidance.
The SDK disables redirects on `HttpClientHandler`, `SocketsHttpHandler`, and
recognized inner handlers. Custom handler implementations remain trusted
application code and must not follow redirects or forward credentials elsewhere.

## Protocol Selection

Hubs advertise enabled protocols (`spec.protocols.{wss,http,mqtt}.enabled`,
WSS enabled by default) and concrete `data_plane_endpoints`. The SDK prefers
`wss`, then `https`, then `mqtt`:

```csharp
var selected = HubEndpoints.SelectDataPlaneEndpoint(
    HubDataPlaneEndpoints.FromHub(hub),
    HubProtocolSettings.From(hub));
```

`ThalovantClient` itself is WSS-only in 0.2.x; constructing it with
`HubProtocol.Https` or `HubProtocol.Mqtt` throws
`ThalovantUnsupportedProtocolException`.

## Errors

- `ThalovantApiException` — control API failures, with `StatusCode`, raw
  `Body`, and the decoded `ErrorCode` where the API provides one.
- `ThalovantConnectionException` / `ThalovantTimeoutException` /
  `ThalovantRuntimeException` — data-plane connection, deadline, and hub
  failures.
- `ThalovantPolicyDeniedException` (a `ThalovantRuntimeException`) — the hub
  refused a message type this connection may not publish (`hive.policy.denied`),
  with `DeniedType`, `Code`, `Reason`, and the `Allowed` list.
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
`HttpMessageHandler`, the WSS wire protocol is tested through its pure
encode/decode functions, and the data-plane client is driven against a fake
hub bus that reproduces the observed reply shapes. The in-tree AES-128-GCM
implementation is validated against NIST vectors plus known-answer vectors
generated with Node.js `crypto` (the exact configuration the Node SDK uses on
the HiveMind wire).

## License

MIT — see [LICENSE](LICENSE).


### Shared-runtime skill management

Hub-addressed skill methods select the runtime group attached to the hub UUID.
Every hub sharing that group sees the same skill changes and history. The API
requires a restricted token to cover all served hubs. Reads need `hubs:inspect`
(`hubs:read` implies it); writes need `hubs:write`, an eligible paid plan and ownership.

The history response contains newest-first `event` and `operation` entries,
including nullable actor/version fields. Its limit is 1–200 (50 where omitted).
An accepted mutation is not proof the skill is ready. Optional waiting polls the
operation, with a 120-second default timeout and two-second interval. Polling
never repeats an accepted mutation and starts no new read after its deadline;
an already-running HTTP request retains its normal request timeout.

Methods: `ListHubSkillsAsync / ListHubSkillHistoryAsync / InstallHubSkillAsync / UpdateHubSkillAsync / RemoveHubSkillAsync / WaitForHubSkillOperationAsync`. Responses preserve API JSON fields. Use
`HubSkillWaitOptions` to opt into waiting. For cancellation-sensitive work, submit
without waiting, retain the complete accepted response (including `operation_id`
and `state`), then pass that response to the wait helper separately. Cancelling waiting does not undo the server operation. After a polling
failure, inspect/resume that operation instead of submitting the write again.

## Request helpers and safe configuration updates (0.5.0)

Request hints carry a recognized language, ordered intent pipeline, and caller
location without changing the caller's context. Empty hints are omitted. The
location helper requires a city and omits invalid or zero/zero coordinates.
The hub validates language hints against its configured languages.

Replies expose their reported language, ordered speech/audio events, and a
count of dropped media. Embedded skill clips are limited to 4 MiB each and
16 MiB per reply, checked before retention and decoding. Audio does not extend
the reply settlement window. Decoding accepts hexadecimal bytes with ASCII
whitespace between bytes; it never fetches a skill-supplied URL or file path.
The application owns playback (the `play`/`Play` function in this example).

```csharp
var location = ThalovantContext.BuildLocation("Montréal", country: "CA");
var reply = await client.AskWithHintsAsync("Quel temps fait-il ?", sttLang: "fr-ca", location: location);
foreach (var e in reply.MediaEvents) if (e.IsAudio) Play(e.AudioBytes());
var examples = intent.ExamplesWithOptions("en-us", speakable: true);
await api.UpdateRuntimeGroupConfigAsync(groupId, delta);
// Explicit full replacement:
await api.ReplaceRuntimeGroupConfigAsync(groupId, fullConfig);
```

Safe merging requires an API whose configuration GET returns a valid `revision`
and whose configuration PUT checks `expected_revision`. The SDK rereads and
reapplies the original delta only after HTTP 412, with at most three attempts.
Arrays and scalar values replace; objects merge recursively. Personas replace
only when explicitly supplied. Connection failures, redirects, other statuses,
and ambiguous write results are never retried. No unsafe PATCH fallback is used.
Unconditional replacements must still be coordinated with other writers.

Use the explicit replacement operation shown above when a complete replacement
is intended, including when working with an older API. Existing code relying on
replacement must opt into it when upgrading. Raw intent patterns remain the
default; speakable examples remove optional parts, choose alternatives, and
substitute caller-supplied slots while retaining complete-phrase priority.
