# Repository instructions

This repository owns the published .NET client and agent SDK for supported
Thalovant public API and HiveMind runtime contracts. Read the platform
contracts in `../infra-manifests/docs/thalovant-platform/` when available.

Rules:

- Preserve compatibility with the documented target frameworks: `net8.0` and
  `netstandard2.1` (Unity 2021+). Keep language features usable from both
  targets; do not use runtime APIs missing from netstandard2.1.
- Keep zero external runtime dependencies: HttpClient for HTTP,
  ClientWebSocket for WSS, System.Text.Json for JSON (the System.Text.Json
  NuGet package is the single allowed reference, for the netstandard2.1
  target only), and the in-tree AES-128-GCM implementation for the HiveMind
  wire (16-byte nonces; .NET's AesGcm cannot be used).
- JSON field names on the wire are snake_case exactly as the API pydantic
  schemas define them; map them with explicit `[JsonPropertyName]` attributes
  or custom converters.
- Update types, implementation, examples, tests, changelog, version, and
  public documentation together for observable contract changes.
- Consume additive server behavior only after compatible server support exists.
- Never publish credentials, identity files, or generated secrets.
- Do not create a release for internal platform changes with no .NET SDK
  impact; record `no SDK impact` in the coordinated change instead.
- Update affected `docs.thalovant.com` SDK pages in the same release train.

Validate with `dotnet build` and `dotnet test` (CI runs both on ubuntu-latest
and windows-latest, plus `dotnet pack`). The test suite must stay
network-free.

Releases are automated: `auto-release.yml` tags and creates the GitHub release
for an untagged version on `main`, and `publish.yml` packs, attests, and
publishes `Thalovant.Sdk` to nuget.org. The csproj `<Version>` is the single
source of truth for the version and `CHANGELOG.md` moves with it; the user
agent (`ThalovantDotNetSDK/<version>`) is derived at runtime by
`ThalovantSdkVersion` from the assembly's informational version, so it is
never hand-edited. Never hard-code a version inside a user-agent literal:
`tests/Thalovant.Sdk.Tests/VersionTests.cs` fails the build if one reappears
under `src/`, and tests must assert the derived value, never a literal. See
`RELEASING.md` for the required `NUGET_API_KEY` secret and rollback rules.

Rollback by tagging a corrected patch release; never move or delete an
existing tag that consumers may already resolve.
