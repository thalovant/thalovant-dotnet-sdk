# Releasing the Thalovant .NET SDK

The SDK is published to nuget.org as `Thalovant.Sdk` (with a `snupkg` symbol
package). The csproj `<Version>` in
`src/Thalovant.Sdk/Thalovant.Sdk.csproj` is the **single source of truth** for
the version: `ThalovantSdkVersion` reads it back off the built assembly's
`AssemblyInformationalVersionAttribute`, and `ThalovantDefaults.UserAgent`
(`ThalovantDotNetSDK/<version>`) derives from it, on both target frameworks.
Never hand-edit a version into a user-agent literal — `VersionTests` fails the
build if one reappears under `src/`. Only `<Version>` and `CHANGELOG.md` move
in a release. The README install snippet (`dotnet add package Thalovant.Sdk`)
does not name a version and needs no update.

## Prerequisites (one-time)

**NuGet Trusted Publishing policy.** No repository secret is stored. The
publish workflow authenticates with GitHub Actions OIDC through the
`NuGet/login` action, which exchanges the job's OIDC token for a
short-lived API key. Create the policy once on nuget.org (account owning
`Thalovant.Sdk` → Trusted Publishing → Create):

| Field | Value |
| --- | --- |
| Package Owner | `thalovant` |
| CI/CD Provider | GitHub Actions |
| Repository Owner | `thalovant` |
| Repository | `thalovant-dotnet-sdk` |
| Workflow File | `publish.yml` |
| Environment | `nuget` |

The Environment must stay in sync with the `environment:` declared in
`publish.yml`. Local builds and CI test runs need no credentials.

**Repository variable.** Set the Actions variable
`NUGET_TRUSTED_PUBLISHING_USER` to the nuget.org username of the person
who created the trusted-publishing policy (the policy creator, not the
`thalovant` package-owner organization — the token exchange rejects the
owner name). `gh variable set NUGET_TRUSTED_PUBLISHING_USER --body "<username>"`.

## Publish

1. Update the `<Version>` in `src/Thalovant.Sdk/Thalovant.Sdk.csproj`, then
   `CHANGELOG.md` and any affected docs. The user agent follows automatically;
   there is nothing else to hand-edit.
2. Run `dotnet build`, `dotnet test`, and
   `dotnet pack src/Thalovant.Sdk/Thalovant.Sdk.csproj --configuration Release`,
   and inspect the staged `.nupkg`/`.snupkg`.
3. Merge to `main`. The **Auto Release** workflow detects that the version has
   no matching `v<version>` tag, re-runs the build, creates the tag and GitHub
   release, and dispatches the **Publish NuGet Package** workflow. (If the
   current version is already tagged but release-relevant files changed, it
   auto-bumps a patch version first, rewriting only the csproj `<Version>` and
   prepending a `CHANGELOG.md` entry.)
4. The publish workflow builds and tests the tagged commit, packs the
   `.nupkg` and `.snupkg`, generates a CycloneDX SBOM, attests provenance and
   SBOM, pushes to nuget.org with `--skip-duplicate`, then polls
   `https://api.nuget.org/v3-flatcontainer/thalovant.sdk/index.json` until the
   version appears. nuget.org validation and indexing usually takes a few
   minutes but can take longer; a verification timeout usually means indexing
   is slow, not that the publish failed — check
   https://www.nuget.org/packages/Thalovant.Sdk before re-running.
5. Validate a clean-project `dotnet add package Thalovant.Sdk` restore of the
   new version on `net8.0` (and a `netstandard2.1`/Unity consumer where
   practical) before declaring the release complete.

A publish can also be run manually: **Actions → Publish NuGet Package → Run
workflow** with the immutable `release_tag` (for example `v0.1.0`).

## Idempotence

The publish workflow is safe to re-run for a tag: it skips the `dotnet nuget
push` when the version is already in the NuGet index, and pushes with
`--skip-duplicate` so a concurrent or partially indexed upload turns the
registry's 409 conflict into a warning rather than a failure. Release assets
already attached to the GitHub release are preserved, never replaced.

## Rollback

Published nuget.org package versions are immutable: a version can never be
overwritten or re-uploaded, and even deleting is unsupported — packages can
only be **unlisted**, which hides them from search but keeps them restorable
for existing consumers.

1. Do not attempt to remove or overwrite a broken version.
2. Publish a corrected patch release: bump the csproj `<Version>` and add a
   changelog entry.
3. Deprecate the broken version on nuget.org (Manage package → Deprecation,
   pointing at the corrected version) and unlist it if it should not be
   discovered by new consumers.
4. Update `docs.thalovant.com` and compatibility notes to name the
   replacement version.
