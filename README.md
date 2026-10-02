# Thalovant .NET SDK

[![CI](https://github.com/thalovant/thalovant-dotnet-sdk/actions/workflows/ci.yml/badge.svg)](https://github.com/thalovant/thalovant-dotnet-sdk/actions/workflows/ci.yml)
[![Licence](https://img.shields.io/github/license/thalovant/thalovant-dotnet-sdk)](LICENSE)
[![Docs](https://img.shields.io/badge/docs-docs.thalovant.com-5c6bc0)](https://docs.thalovant.com/developers/sdks/dotnet/)

.NET SDK for connecting enterprise .NET and Unity apps to Thalovant hubs.

The control API is used to discover hubs and provision a client identity. After
that, the SDK talks directly to the hub data plane over WSS. (HTTPS and MQTTS
data-plane transports are available in the Node and Go SDKs and are not part of
this .NET SDK yet.)

The full guide lives at <https://docs.thalovant.com/developers/sdks/dotnet/>.

## Requirements

- .NET 8 (`net8.0`), or `netstandard2.1` for Unity 2021+. The `net8.0` build
  has zero external dependencies; the `netstandard2.1` target only depends on
  `System.Text.Json`.
- A Thalovant account with API access for authenticated control-plane actions.
- A hub id or slug, and a client identity for that hub.

## Install

```bash
dotnet add package Thalovant.Sdk --version 0.7.1
```

## Quick start

```csharp
using Thalovant;

var api = new ThalovantControlPlane();
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

Keep `result.Identity` secret: it holds the client credentials the hub trusts,
and the raw hub and client resources carry bootstrap credentials too.
`result.ToJsonObject()` redacts all of them and is safe to log; only
`result.ToJsonObject(includeSecrets: true)` returns the credentials in the
clear, so never log or print that form.

## Documentation

| Topic | Where |
| :--- | :--- |
| Sign in, identities, noise key storage, supported protocol | [.NET SDK guide](https://docs.thalovant.com/developers/sdks/dotnet/) |
| Events, runtime helpers, deadlines, listing what a hub can be asked | [.NET SDK guide](https://docs.thalovant.com/developers/sdks/dotnet/) |
| Provisioning hubs, managing skills, safe configuration updates | [.NET SDK guide](https://docs.thalovant.com/developers/sdks/dotnet/) |
| Control-plane HTTP security, managed sessions, reply claims | [.NET SDK guide](https://docs.thalovant.com/developers/sdks/dotnet/) |
| Product documentation | <https://docs.thalovant.com> |

## Not in the docs yet

### Durable memory

```csharp
var memory = await api.CreateMemoryItemAsync(new MemoryCreatePayload("Prefer America/Toronto for scheduling.")
{
    Scope = MemoryScope.Workspace,
    Kind = MemoryKind.Preference,
    Tags = new[] { "timezone" },
});
var items = await api.ListMemoryItemsAsync(new MemoryListOptions { Scope = MemoryScope.Workspace, Query = "timezone" });
var summary = await api.GetMemorySummaryAsync();
await api.DeleteMemoryItemAsync(memory.Id);
```

### Custom listing rules and data refresh

`ListingRules.Default` uses embedded thalovant-languages 0.2.1 data.
`new ListingRules(data)` snapshots a complete JSON tree; pass it as `listing`.
`new ListingRules(null)` selects bare rendering. Invalid patterns fail
construction; `Asks` throws `RegexMatchTimeoutException` when a rule exceeds
100ms. Regenerate the data with `python scripts/sync-listing-data.py --data-dir
src/Thalovant.Sdk/ListingData --test-dir tests/Thalovant.Sdk.Tests/Fixtures`.

### Marketplace catalog and workspace analytics

`api.ListMarketplaceSkillsAsync(...)` needs `hubs:read` and is not paid-gated;
`api.AnalyticsOverviewAsync(new AnalyticsOverviewOptions { Range = "7d" })`
reads the dashboard overview.

## Development

```bash
dotnet build
dotnet test
```

The test suite is fully offline.

## Security

Report vulnerabilities as described in the
[security policy](https://github.com/thalovant/.github/blob/main/SECURITY.md).

## Licence

MIT. See [LICENSE](LICENSE). Bundled language data notices are in
`LICENSE-languages`, `LICENSE-langcodes` and `THIRD-PARTY-NOTICES.md`.
