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
dotnet add package Thalovant.Sdk --version 0.7.1
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

### One step at a time

A caller that runs its own loop, such as a setup screen that shows the code and
polls on its own schedule, takes the flow in steps:

```csharp
var grant = await api.BeginDeviceLoginAsync(
    ThalovantHome.HomeAssistantScopes, "Home Assistant (kitchen)", ThalovantHome.HomeAssistantClientId);
Show(grant.VerificationUriComplete ?? grant.VerificationUri, grant.UserCode);

while (true)
{
    try
    {
        var token = await api.PollDeviceLoginAsync(grant);   // kept on api.AccessToken and api.TokenId
        break;
    }
    catch (ThalovantDeviceLoginPendingException pending)
    {
        await Task.Delay(pending.Interval);                 // already longer after a slow_down
    }
}
```

`PollDeviceLoginAsync` makes one request. Besides the pending answer it throws
`ThalovantDeviceCodeExpiredException` or `ThalovantDeviceAccessDeniedException`;
all three are `ThalovantApiException` and carry the API's HTTP 400. A
`slow_down` adds five seconds to the interval for good, however many calls your
loop makes. An empty scope list is left out of the request, exactly as none is:
the API asks for at least one and answers `[]` with a 422. A verification URL
that is not http(s), has no host, or carries credentials is refused before you
could open it, and neither the device code nor the token ever appears in an
exception message.

The `clientId` overload signs in as a registered app. With
`ThalovantHome.HomeAssistantClientId` (`thalovant-home-assistant`), the approval
page shows the platform's own name for the app as verified, with the client name
as the device's label beside it, and approving the app again replaces the token
it already holds instead of counting a second one against the plan. An id the
API does not know is refused with a 400 `unknown_client`. `DeviceLoginOptions.ClientId`
does the same for `LoginWithBrowserAsync`; without one, nothing is sent.

An app that approves codes reads one first, signed in as the person approving
it: `DescribeDeviceLoginAsync(userCode)` returns the `Scopes`, `ClientName`,
`ClientId`, `DeviceName` and `ClientVerified`, which is true only when a
registered app asked. Otherwise the client name is whatever the device claimed.
A code that is unknown, expired or already answered is a 404.

`RevokeApiTokenAsync()` revokes the token this client signed in with (a token
may always revoke itself) and forgets it locally; pass a token id to revoke
another one. Revoking the token in use is idempotent: one already revoked
cannot authenticate its own revoke, so the API's 401 counts as revoked, and
revoking again sends nothing until the next sign-in.

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
`ThalovantApiException` with the status code on `StatusCode` (and `ErrorCode`,
`Detail` and `Problem`; see [Reading An API Error](#reading-an-api-error)).

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

A hub pins the first static key a connection shows it, so every program that
uses one identity must present the same key. On .NET 8, a client given no store
keeps the key of an identity read from a file (`ThalovantIdentity.FromFile`,
which sets `SourcePath`) in a `noise` folder beside that file, so two programs
reading the same file share it. The first time that folder is used, the key and
hub pins the identity had in the shared default folder are copied into it --
never moved -- when that key has already met this identity's hub. Any other
identity, or one in a folder the user cannot write to, keeps its state under the
current user's local application data directory (`Thalovant/noise`), as before
0.9.1. POSIX directories require 0700 and files 0600; Windows relies on the
user's profile ACLs. Never share or
check in this state. A changed hub key fails authentication; verify intentional
key rotation before replacing its saved pin. A hub that pinned another key for
this client throws `ThalovantClientKeyRejectedException`, naming the folder this
client's key is in (`KeyFolder`) and the likely other one (`OtherKeyFolder`):
pair again, or give every program that uses the identity the folder holding the
key the hub trusts.

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

## Link Home Assistant

A Home Assistant link is a connection of its own kind: the hub's home skill
sends it `thalovant.home.request`, and it answers every one with
`thalovant.home.response`. Setting one up takes four steps.

1. **Sign in** with the device flow, one step at a time (see above), asking for
   `ThalovantHome.HomeAssistantScopes`: `hubs:read`, `clients:read` and
   `clients:write`, which is all a Free plan can approve.
2. **Create the connection** with its kind:

   ```csharp
   var link = await api.CreateClientIdentityAsync(hub, new CreateClientIdentityOptions("Home Assistant")
   {
       ConnectionType = ThalovantConnectionTypes.HomeAssistant,
   });
   ```

   The API must say the connection is of that kind. When it answers with an
   ordinary connection instead, the SDK deletes it and throws
   `ThalovantUnsupportedConnectionTypeException`, as it does for a 422 about the
   field. A hub takes one Home Assistant link, so a second one throws
   `ThalovantAlreadyLinkedException` naming the connection that holds it.
   `DeleteClientAsync(clientId)` removes a connection: it reads the etag when
   you have none, retries once if the connection changed underneath, and treats
   one already gone as deleted.
3. **Wait for the hub to admit it**, about ninety seconds:

   ```csharp
   await api.WaitForAdmissionAsync(link);   // 180 s by default
   ```

   An operation the platform failed throws `ThalovantAdmissionFailedException`
   with its `ErrorCode`; a refusal of the wait itself throws the same exception
   with the API's own error on `ApiError` (status, code, detail). A 401 or 403
   is thrown as the API's authentication error, and an API out of reach as
   `ThalovantApiUnreachableException`: neither says anything about the
   connection. Running out of time throws `ThalovantAdmissionTimeoutException`,
   which is both a connection error and a timeout, because the connection may
   still be admitted later. No read runs past the deadline. A 5xx while polling
   is ridden out, and so is a 429 (your plan's rate limit): the next poll waits
   `RetryAfter`, read from the body's `retry_after_seconds`, else the
   `Retry-After` header, else `RateLimit-Reset`, and when that is longer than
   the time left the wait ends at once as a timeout. An operation link to
   another origin (scheme, host and port) is never followed.
4. **Answer requests** on a link that stays up:

   ```csharp
   await using var session = HubSession.ForIdentity(link.Identity);
   using var answering = session.AnswerHomeRequests(async (request, cancellationToken) =>
   {
       var speech = await agent.ProcessAsync(request.Utterance, request.Lang, cancellationToken);
       return HomeAnswer.ActionDone(speech);
   });
   await session.RunAsync(stopping);
   ```

The answer is a reply: it carries the request's context, with `source` and
`destination` swapped, so it goes back to the skill that asked. A request with a
destination and no source is answered with no destination at all, rather than
back to itself. `response_type` is `action_done`, `query_answer` or `error`, and
an error names one of `HomeErrorCodes`.

Speech is sent as plain text, in linear time whatever the text holds. A tag (`<`
or `</` and an ASCII letter, up to the next `>` outside a quoted value), a
comment and a processing instruction are removed; any other `<` is text, so
"5 < 6 and 7 > 3" and an unclosed tag survive whole. Numeric character references,
the five XML entities and `&nbsp;` are decoded, and nothing else (`&eacute;`
stays as written). Every run of Unicode White_Space becomes one space.

Every request gets at most one answer, and never after the hub's ten seconds,
counted from its arrival:

- A handler that throws is answered `failed_to_handle`.
- One still busy after nine seconds, or after what is left of the ten, is
  answered `timeout` at that moment and its token is cancelled. It runs on its
  own, so ignoring the token cannot hold the answer back.
- One that answers outside the contract is answered `unknown`.
- A reply that could only go out after the bound is withdrawn, not sent late.

When the SDK answers for a handler, the speech is empty and the hub speaks its
own sentence for the code, in the device's language. A `ThalovantClient` answers
the same way through `client.AnswerHomeRequests(handler)`, and
`ThalovantHome.AnswerAsync(request, handler, reply)` applies the same bound
around a transport of your own.

## Errors

- `ThalovantApiException` — control API failures, with `StatusCode`, raw
  `Body`, the decoded `ErrorCode`, the API's whole `Detail` sentence, and the
  error body as a `JsonObject` in `Problem`; see
  [Reading An API Error](#reading-an-api-error).
- `ThalovantConnectionException` / `ThalovantTimeoutException` /
  `ThalovantRuntimeException` — data-plane connection, deadline, and hub
  failures.
- `ThalovantPolicyDeniedException` (a `ThalovantRuntimeException`) — the hub
  refused a message type this connection may not publish (`hive.policy.denied`),
  with `DeniedType`, `Code`, `Reason`, and the `Allowed` list.
- `ThalovantAuthenticationException`, `ThalovantPlanException`,
  `ThalovantAlreadyLinkedException` (with `ClientId`) and
  `ThalovantUnsupportedConnectionTypeException` — control API refusals a caller
  can act on: sign in again (401, 423, 403 `Insufficient scopes`), the plan
  does not allow it (402, 403 `plan_limit`), the hub already has its Home
  Assistant link (409), or the API cannot make that kind of connection. All of
  them are `ThalovantApiException`.
- `ThalovantDeviceLoginPendingException` (with `Interval`),
  `ThalovantDeviceAccessDeniedException` / `ThalovantDeviceCodeExpiredException`
  — a device sign-in not decided yet, denied, or expired. All three are
  `ThalovantApiException` since 0.9.0.
- `ThalovantApiUnreachableException` (a `ThalovantApiException`) — the control
  API could not be reached at all, so there is no status; trying later can
  succeed. A 429's wait, when the API named one, is on every
  `ThalovantApiException` as `RetryAfter`.
- `ThalovantHubRefusedException`, `ThalovantHubKeyChangedException`,
  `ThalovantAdmissionFailedException` (with `ErrorCode`, or the API's refusal
  on `ApiError`) and `ThalovantAdmissionTimeoutException` — all
  `ThalovantConnectionException`. In order: the hub turned the credentials away;
  its Noise key is not the one pinned for it; the platform could not admit a new
  connection; or it has not admitted it yet. `ThalovantClientKeyRejectedException`
  (with `KeyFolder` and `OtherKeyFolder`) is the refusal of this client's own
  Noise key, and a `ThalovantHubRefusedException`. The admission timeout is also an
  `IThalovantTimeout`, like `ThalovantTimeoutException`.
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

The SDK does not retry automatically, except inside `WaitForAdmissionAsync`.
To decide when to resend, read `RetryAfter` on the exception. It is the body's
`retry_after_seconds` when the body names one (inside `detail` or at the top),
else the `Retry-After` header in seconds, else `RateLimit-Reset`. The last is
all the API's own rate limiter sends with its plain-text 429, which has no body
and so no `Problem`.

## Reading An API Error

A refused control-plane request throws `ThalovantApiException`. Its message is
one line for display and can be shortened, so read what the API said from the
exception itself:

- `StatusCode`: the HTTP status.
- `ErrorCode`: the machine-readable code, such as `platform_image_required` or
  `plan_limit`, or `null`.
- `Detail`: the API's whole sentence, exactly as sent, or `null`.
- `Problem`: the whole error body as a `JsonObject` when it is a JSON object,
  or `null`. Every structured field the API sends is here, including ones added
  after this SDK was released. Each read returns a new copy, so keep it in a
  local rather than reading the property once per field.

```csharp
using System.Collections.Generic;
using Thalovant;

try
{
    await api.ReleaseRuntimeGroupAsync(groupId, new ReleaseOptions
    {
        Images = new Dictionary<string, string> { ["core"] = "docker.io/me/ovos-core:dev" },
    });
}
catch (ThalovantApiException error) when (error.ErrorCode == "platform_image_required")
{
    var problem = error.Problem!;
    Console.WriteLine(error.Detail);
    Console.WriteLine(problem["allowed_images"]);        // per image key
    Console.WriteLine(problem["allowed_repositories"]);  // any tag or digest of these
}
catch (ThalovantApiException error) when (error.ErrorCode == "plan_limit")
{
    var problem = error.Problem!;
    Console.WriteLine($"{problem["resource"]}: {problem["used"]} of {problem["limit"]}");
}
```

A value the body echoes back from your request (a validation error repeats
what it was sent) is only ever in `Problem` and `Body`, never in the message.

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

## Request helpers and safe configuration updates (0.6.0)

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

Guarded merging requires the `hubs:read` and `hubs:write` scopes and a paid plan.
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

The audio limits use encoded-length upper bounds before decoding, so formatting
whitespace consumes budget too. Like Python's `bytes.fromhex`, ASCII whitespace
alone decodes to zero bytes. Bounded malformed clips remain available as event
metadata and fail when decoded; they are never fetched or played automatically.
Distinct audio events may intentionally repeat identical sound content. Only
repeated delivery of the same event object is suppressed where object identity
is available, without counting it as a dropped clip. Rendered example ranking
uses the original pattern's slot presence even when sample values are supplied.

## Locale-aware intent listings

`intent.ExamplesWithListing("fr-CA", sentence: true)` renders sentences from
the closest registered locale. `ThalovantContext.AsSentence("quelle heure est-il",
"fr-CA")` returns `"Quelle heure est-il?"`. `SpeakableWithLanguage(pattern, slots,
lang)` fills canonical locale examples before explicit slot overrides. Existing
`Speakable` and `ExamplesWithOptions` signatures remain available.

Complete phrases rank before prefixes and slot patterns, then fuller wording up
to eight words. Empty and duplicate rendered examples do not consume limits.
Raw unlimited examples retain registration order. Omitted languages preserve the
selected registration's locale. OVOS-compatible CLDR matching uses langcodes
3.5.1 data; distances above ten do not match and ties preserve candidate order.

`ListingRules.Default` uses embedded thalovant-languages 0.2.1 data.
`new ListingRules(data)` snapshots a complete JSON tree; pass it as `listing` or
call its methods. `new ListingRules(null)` selects bare rendering with slot
names. Unknown languages also remain bare. Invalid patterns fail construction;
`Asks` throws `RegexMatchTimeoutException` when a rule exceeds 100ms. Sentence
rendering leaves such rules unpunctuated. The rules are safe for concurrent
readers and use embedded assembly resources without new runtime packages.

The package includes `LICENSE-languages` and `LICENSE-langcodes`. Regenerate
data with `python scripts/sync-listing-data.py --data-dir
src/Thalovant.Sdk/ListingData --test-dir tests/Thalovant.Sdk.Tests/Fixtures` in
the pinned public Python environment specified in that script.

The SDK code, CLDR matching tables and bundled `thalovant-languages` data
retain their upstream MIT license notices. Both data notices ship with the SDK.

### Language data refresh

The bundled listing data follows `thalovant-languages` 0.2.1: 270 languages
(290 base and regional entries), with regional rules resolved through the
public package loader. Sentence marks and trailing words now match Python 0.6.8;
for example Spanish `qué hora es` becomes `Qué hora es?`, while French
`coupe le son` remains a complete sentence. Undescribed languages such as
`tlh` still render bare. The reference fixtures cover 4,652 listing cases and
990 OVOS language-selection cases.

## Managed sessions and inventory caches

`HubSession` owns one reusable hub connection. Supply a factory that returns a
connected client and cleans up a failed or cancelled connection attempt, or use
`HubSession.ForIdentity(identity)`. Event
subscriptions survive client replacement. Go and Rust expose a persistent event
stream; the other managed SDKs expose subscription handles. Close the session
when its owner shuts down; close waits for admitted operations and is terminal.

Background connection attempts back off for 10, 20, 40, 80, then 120 seconds.
Foreground calls can try immediately. Your application owns probe scheduling:
use the reported probe delay (60 seconds while held, 5 seconds while down).
The SDK never replays an admitted Ask or Emit after a lost response, because an
Ask can trigger an action. A request timeout applies to the underlying operation;
waiting for session admission and your connection factory are separate budgets.

```csharp
await using var session = new HubSession(connectClient, warm: false);
var reply = await session.AskAsync("What is the weather?");
```

A link the hub sends requests down has to stay up. `ConnectAsync` makes one
attempt, and `RunAsync` keeps the link until the session closes:

- It dials a dropped link again at once.
- After a failed attempt it waits 10 seconds, doubling to 120.
- It looks at a held link every probe interval, and the moment it drops.

A close with code 1000, 1005 or 1008 is a refusal rather than a drop when it
comes during the handshake, or within `SettleWindow` (0.75 seconds) after it
while the hub has sent nothing that decrypts under the new session's keys. That
is how a hub says it does not know the connection's key; a hub that has spoken
has accepted it, so a close after its first frame is a drop. So are a Noise
answer that does not authenticate (a wrong password) and a 401 or 403 on the
WebSocket upgrade. A refusal that comes as an XX handshake ends is the hub
refusing this client's own key -- it pinned another one for the connection --
and throws `ThalovantClientKeyRejectedException`; after KK the same close is a
plain refusal.

A pinned KK handshake that fails is followed at once by one XX attempt, inside
the same connect. Only XX tells a changed password from a changed hub key, and
it is not a downgrade: the pin is still checked. A hub whose key is not the
pinned one throws `ThalovantHubKeyChangedException`, and `RunAsync` stops at
once, since retrying cannot change it; so does a refused client key. Because a new connection is refused until
its hub admits it, `RunAsync` retries refusals for `RefusalGraceSeconds` (600 by
default, set through the five-argument `HubSessionPolicy` constructor) before it
throws `ThalovantHubRefusedException`.

`OnStateChange` reports each time the link comes up or goes down; the SDK itself
logs nothing. `ReplyAsync` answers a message back along the route it came.

`Inventory`, `Skill`, and `Intent` provide a presentable view separate from the
runtime's native intent inventory. Unknown catalogue locales remain unknown;
phrases observed for a language do not prove catalogue support. Examples choose
the closest supported locale. A nonpositive limit returns the raw phrase pool
(Rust uses zero for its unsigned limit). Cache JSON includes explicit intent
language order so serialization cannot change the default example language.

`InventoryCache` is optional, defaults to a one-hour TTL, and returns a miss for
invalid, expired, or unreadable data. Writes use private, unique scratch files
and atomic replacement. POSIX cache files are owner-readable/writable; Windows
uses the user's directory ACLs. Cache keys separate mode, identity path, and the full normalized hub hostname.
Never use inventory caches to store credentials.

`OriginPreference` gives a preferred address its own short handshake budget and
cools it down after a failure. In non-Python SDKs the factory must implement the
address binding on its own transport, retain the public host for TLS/SNI, and
finish failed-attempt cleanup before returning. Transport/platform restrictions
still apply. This helper does not change global DNS or disable TLS validation.

### Reply claims

Replies expose advisory claim status and first-seen, unique pipeline and skill
IDs. A failed or unhandled reply is not claimed; a successful fallback-only
reply is not claimed. Successful replies without stage stamps retain legacy
behavior and are claimed. Only nonempty string stamps are used; malformed
metadata is ignored. Claim status does not authenticate a peer or suppress
reply text. See the public SDK guide for native member names.
