Last Edit: Claude Opus 5.5 - 2026-11-07 - Motive: Record the CodeRabbit review-body items on #10, #11, #12, #26, #37, #41 and #42 as checked against main.

# Maintenance Report

## 2026-11-07 — CodeRabbit review-body items (#10, #11, #12, #26, #37, #41, #42)

CodeRabbit left eight items in review bodies (nitpicks and outside-diff comments) on merged pull requests, which GitHub does not show as threads. Each was checked against `main` (ef3a0c5) before acting.

| PR | Item | Outcome |
| :--- | :--- | :--- |
| #10 | `Intents.cs`: one engine's denial or timeout in the engine-manifest fallback discards the other engine's names | Fixed since 81dd907 (#17): `IntentNamesAsync` keeps each engine that answers and rethrows the first failure only when none did. Test: `IntentInventoryTests.EngineFallbackRetainsTheOtherEngineWhenOneIsUnavailable` |
| #11 | `Intents.cs` and README: say `Fallback` covers a denial, and `ok: false` throws regardless | Fixed since 81dd907 (#17): the `IntentInventoryOptions.Fallback` doc says a negative `ok: false` listing still throws and does not trigger fallback; the README said which queries each path needs until bad56ba (#44) moved that text to the docs site |
| #12 | README: "WSS-only in 0.1.x" was a stale version label in the packaged README | The label was corrected in c69bda5 (#13) and the passage left the README in bad56ba (#44). The same staleness was back in the install line (`--version 0.7.1`, packed into the NuGet package); fixed here by dropping the pin |
| #12 | `Fixtures/noise-node.json`: the two `25519_ChaChaPoly_SHA256` exchanges are unused | Not changed: the file is the Node peer's whole transcript and is byte-identical to the Kotlin SDK's copy; trimming one copy would make the shared vectors diverge. `NoiseTests` selects the suite this SDK speaks |
| #26 | `ControlPlane.DeviceVerificationUri`: allow plain HTTP only for loopback | Not changed: the Python reference (`_safe_browser_url`) and the Go port (`validateBrowserURL`) accept HTTP or HTTPS without credentials, and so does this port. The URL comes from the API the caller chose; an endpoint able to inject one can inject an HTTPS phishing page as easily, and a loopback-only rule would refuse a self-hosted control plane on a LAN. A stricter rule belongs in the reference first |
| #37 | `HubIntentInventory.ToJsonObject()` drops `ListedIn` | Fixed since b22d188 (#48): it writes `listed_in`. Test: `IntentInventoryTests.TheSerialisedInventorySaysWhichLanguageItListedIn` |
| #41 | Admission wait: back off, or honour `Retry-After`, on repeated 5xx | Not changed: the reference's `wait_for_operation` and the shared `connection-admission-vectors.json` ("A 5xx is ridden out", and each vector counts the GETs sent) poll at the interval, and the Go port does the same. The wait is bounded by its timeout (90 s at a 2 s interval by default). Changing it is a cross-SDK contract change that starts in the reference vectors |
| #42 | `NoiseStore`: an existing but unwritable folder beside the identity does not fall back to the shared default | Not changed: deliberate and documented in bc57222 (#42) itself, where `contracts/sdk-parity.json` says a noise folder that exists is kept even when it cannot be written, as in the reference (`default_state_dir` returns the folder when `beside.is_dir()`) |

### Verification

- `dotnet test tests/Thalovant.Sdk.Tests -c Release -f net9.0` on this branch: 650 passed, 0 failed (net8.0 runs in CI; no net8 runtime on the workstation). No source or test file changes, so `auto-release.yml` has nothing to publish.
- No file hashed by `contracts/sdk-parity.json` changed.

### Transparency Report

- **AI Model:** Claude Opus 5.5 (claude-opus-5-5), as a Claude Code agent.
- **Actions Taken:** read each review body and the current code, traced each fix to its commit with `git log -S`, compared the declined items with the Python reference and the Go port, dropped the README's stale install pin, and wrote this entry, the changelog line and the audit decisions.
- **Oversight:** hosted CI gates the merge; the platform owner's coordinating agent merges.

## 2026-11-03 — Chat apps parity acknowledgment

**What changed:** `contracts/sdk-parity.json` records the Python reference digest `1e56099da5db` (thalovant-python-sdk#108) and answers its new `chat-apps` capability as not applicable, with the reason the reference gives:

> The API's impact records for chat apps record no .NET obligation: the owner routes are offered in the Python, Node, Go and Rust SDKs and the MCP server. A .NET port is a separate decision to record in the API's impact file first, not a silent gap.

No source, test or version file changes, so `auto-release.yml` publishes nothing.

**Why:** the reference's release gate refuses to publish while any consumer has not answered a capability. Chat apps (the `/v1/bridges` owner routes) ship in the Python, Node, Go and Rust SDKs and the MCP server.

**Verification:** `check-sdk-parity.py --workspace <checkouts> --consumer thalovant-dotnet-sdk` verifies this acceptance against the reference branch, and against `main` (where the new digest is only a candidate). The language suite runs in hosted CI on this PR.

### Transparency Report

- **AI Model:** Claude Opus 5.5 (claude-opus-5-5), as a Claude Code agent.
- **Actions Taken:** added the digest and the `chat-apps` entry; wrote this entry and the quick facts, FAQ and suggestions files.
- **Oversight:** hosted CI and the CodeRabbit review gate the merge; the platform owner's coordinating agent merges.

## 2026-11-03 — CodeRabbit follow-ups (#35, #36)

Two review threads were still open on merged pull requests. Each was checked against `main` (b22d188) before acting.

| Thread | Finding | Outcome |
| :--- | :--- | :--- |
| #36 `src/Thalovant.Sdk/Errors.cs` | `WholeInRange` compared a double with `long.MaxValue`, which rounds to 2^63, so 2^63 reached the cast | Fixed: exclusive `PastLong` (2^63) bound in `ThalovantPolicyDeniedException.WholeInRange`; 2^63 added to `NumericShapes` |
| #35 `tests/Thalovant.Sdk.Tests/ConformanceRecord.cs` | Use RFC 8785 (JCS) for the conformance digest | Not applied: the digest must match the Python reference's `json.dumps(sort_keys=True, separators=(",", ":"), ensure_ascii=False)`; JCS orders keys by UTF-16 unit and spells numbers as doubles, so it would disagree. Answered on the thread |

The 2^63 value never produced a wrong quota: `WholeCount` caps counts at `MaxCount` (2^53 - 1), and the cast's result at 2^63 (`long.MinValue` on x64 before .NET 9, `long.MaxValue` where it saturates) falls outside that range either way. The helper is now correct on its own.

### Verification

- `dotnet test tests/Thalovant.Sdk.Tests -f net9.0`: 650 passed, 0 failed (net8.0 runs in CI; no net8 runtime on the workstation).
- With the old bound restored, `RefusalVectorTests.AWholeDoubleIsReadOnlyWhileItFitsInALong(9.2233720368547758E+18, 0)` fails with `Actual: 9223372036854775807`; with the fix it passes.
- `THALOVANT_CONFORMANCE_OUT` run: the produced record is identical to `contracts/conformance-results.json`.
- The reference's `scripts/check-sdk-parity.py --consumer thalovant-dotnet-sdk` passes after refreshing the evidence hashes of `Errors.cs` and `RefusalVectorTests.cs`, and fails without the refresh.

## Transparency Report

- **AI Model**: Claude Opus 5.5.
- **Actions Taken**: Read both threads and the current code, changed the bound in `WholeInRange` and made it `internal` for a direct test, added the boundary tests, refreshed the two evidence hashes in `contracts/sdk-parity.json`, wrote the CHANGELOG entry, this report and `AUDIT.md`, and replied on the threads.
- **Oversight**: Run by an agent on the repository owner's request to handle every open CodeRabbit thread; merged through the normal CI gates. No release was cut by hand; `auto-release.yml` patches the version on merge as it does for every change under `src/`.
