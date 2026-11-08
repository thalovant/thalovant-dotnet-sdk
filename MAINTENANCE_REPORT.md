Last Edit: Claude Opus 5.5 - 2026-11-08 - Motive: Browser sign-in (PKCE on a loopback redirect), the browser-login parity binding, 0.10.0.

# Maintenance Report

## 2026-11-08 — Browser sign-in (`browser-login`, 0.10.0)

**What changed:** a port of the Python reference's browser sign-in (thalovant-python-sdk#113, `e3999cb`, porting brief `docs/browser-login-porting.md`).

| Area | Change |
| :--- | :--- |
| `src/Thalovant.Sdk/BrowserLogin.cs` (new) | `ThalovantControlPlane.BrowserLoginAsync` and `InteractiveLoginAsync` (a partial of the class), `BrowserLoginOptions`, `InteractiveLoginOptions`, `LoginMethod`, `NativeSignInConfig.FromPayload`, and `BrowserSignIn` (URL, redirect, state, verifier, challenge, browser rule, device name, opener) |
| `src/Thalovant.Sdk/BrowserLoopback.cs` (new) | the internal `LoopbackListener`: `TcpListener` on `127.0.0.1` port 0 (exclusive on Windows), `Classify` without a socket, the page and its headers, the accepted tab answered after the sign-in ends |
| `src/Thalovant.Sdk/Thalovant.Sdk.csproj` | `<Version>` 0.10.0 (next minor) |
| Unchanged | `ControlPlane.cs`, `DeviceLogin.cs`, `Errors.cs`, `NativeSignIn.cs`: the new code reuses `AcceptDeviceToken`/`KeepToken`, `SendAsync`, `ApiError`, `LoginWithBrowserAsync`, the device-login exception types and `RequireSecureTokenExchange` from the partial, so no evidence another capability binds was touched |
| Tests (new) | `BrowserLoginVectorTests.cs` (126 test cases: all 98 vectors, the 24 callbacks through a real listener as well, and 4 checks of the file itself) and `BrowserLoginTests.cs` (60 test cases beyond the vectors); `Fixtures/browser-login-vectors.json` vendored byte for byte (sha256 `9058adf2ad30`) |
| Contracts | `sdk-parity.json`: `reference_digests` keeps `57c7e71f6456` and adds the candidate `49744ee385bf`; `capabilities["browser-login"]` is `required` with implementation, test and vector evidence. `conformance-results.json`: the browser-login results (98 cases), every other file's results unchanged |
| Docs | README "Sign in through the browser", CHANGELOG 0.10.0, AGENTS.md rule, QUICK_FACTS, FAQ, AUDIT, SUGGESTIONS |

**Why:** a device code can be relayed to somebody else (device-code phishing); a loopback redirect cannot. On a machine with a browser the SDK now signs in through it, and keeps device login for machines with none.

**How each porting-brief decision is met:** listener on `127.0.0.1:0` before the URL, `ExclusiveAddressUse` on Windows; only `GET /callback`, a 400 that keeps waiting for a missing, repeated or wrong state (`CryptographicOperations.FixedTimeEquals` over UTF-8), a 400 for a head with no request line or over 16 KiB; error before code, form decoding once; the port stops on the first accepted callback; the tab is answered after the sign-in ends (60 s cap, 1 s grace on close), idle connections closed without waiting, the five page headers on every answer, nothing logged. URL parameters in the contract's order with RFC 3986 encoding (upper-case hex, `%20`), scope left out when empty, device name cut to 120 code points; the URL always printed, the browser opened on a background thread (`ShellExecute`, `open`, `xdg-open`, never `cmd /c start`), `Prompt` and `Opener` injectable. The exchange sends exactly four fields and is never retried; results are `DeviceLoginResult`; denied and expired reuse the device-login types with no status; the code, verifier and state are redacted from any error. The chooser follows the rule (CI, SSH, display; Android and iOS get a device login), falls back only before anything is opened, and a null timeout is each method's own default. The timeout is measured with `Stopwatch`, and every waiting call takes a `CancellationToken` (a cancelled sign-in closes its port).

### Verification

- `dotnet build --configuration Release`: 0 warnings, 0 errors (net8.0 and netstandard2.1 library; net8.0 and net9.0 tests).
- `DOTNET_ROLL_FORWARD=Major THALOVANT_CONFORMANCE_OUT=$PWD/contracts/conformance-results.json dotnet test --configuration Release --no-build`: net8.0 837 passed, 0 failed; net9.0 837 passed, 0 failed (651 before this change). The workstation has only the .NET 9 runtime, so the net8.0 build ran on it by roll-forward; CI runs both on ubuntu-latest and windows-latest.
- The 186 browser-login tests passed 10 runs in a row on net9.0. A mutation that skips the secret redaction fails `AnEchoedCodeAndVerifierNeverReachTheError` and `ARedactedErrorKeepsItsKindAndItsFields`.
- Every recorded `browser-login-vectors.json` case digest (98) and the file digest (`b17cb43466c0`) equal the Python reference's `contracts/conformance-results.json` on `feat/browser-sign-in`.
- `dotnet pack src/Thalovant.Sdk/Thalovant.Sdk.csproj --configuration Release --no-build`: `Thalovant.Sdk.0.10.0.nupkg` and `.snupkg`; the packed README carries the new section.
- `check-sdk-parity.py --workspace <checkouts> --consumer thalovant-dotnet-sdk`: verified against the Python PR head (`e3999cb`, candidate reference) and against Python `main` (`44b6e2b`, where `browser-login` is reported as not known yet, the expected rollout state).
- Not run locally: the `interop` CI job (Node Noise peer; no transport code changed) and a real .NET 8 runtime. No test opened a real browser.

### Transparency Report

- **AI Model:** Claude Opus 5.5 (claude-opus-5-5), as a Claude Code agent.
- **Actions Taken:** read the porting brief, the Python reference (`native_auth.py`, `_loopback.py`, `control.py`, both test files, the vectors and the parity checker) and this SDK's device login; probed the reference's parsing edges (`urlsplit`, `parse_qsl`) in Python to mirror them; wrote `BrowserLogin.cs`, `BrowserLoopback.cs`, both test files and the docs; vendored the vectors; recorded the conformance results by running the suite; computed the evidence hashes the checker's way; ran the build, tests, pack and parity checker.
- **Oversight:** hosted CI and the CodeRabbit review gate the merge; the platform owner's coordinating agent merges. Nothing was merged, tagged or released.

## 2026-11-07 — CodeRabbit review-body items (#10, #11, #12, #26, #37, #41, #42)

CodeRabbit left eight items in review bodies (nitpicks and outside-diff comments) on merged pull requests, which GitHub does not show as threads. Each was checked against `main` (ef3a0c5) before acting.

| PR | Item | Outcome |
| :--- | :--- | :--- |
| #10 | `Intents.cs`: one engine's denial or timeout in the engine-manifest fallback discards the other engine's names | Fixed since 81dd907 (#17): `IntentNamesAsync` keeps each engine that answers and rethrows the first failure only when none did. Test: `IntentInventoryTests.EngineFallbackRetainsTheOtherEngineWhenOneIsUnavailable` |
| #11 | `Intents.cs` and README: say `Fallback` covers a denial, and `ok: false` throws regardless | Fixed since 81dd907 (#17): the `IntentInventoryOptions.Fallback` doc says a negative `ok: false` listing still throws and does not trigger fallback; the README said which queries each path needs until bad56ba (#44) moved that text to the docs site |
| #12 | README: "WSS-only in 0.1.x" was a stale version label in the packaged README | The label was corrected in c69bda5 (#13) and the passage left the README in bad56ba (#44). The same staleness was back in the install line (`--version 0.7.1`, packed into the NuGet package); fixed here by dropping the pin |
| #12 | `Fixtures/noise-node.json`: the two `25519_ChaChaPoly_SHA256` exchanges are unused | Not changed: the file is the Node peer's whole transcript and is byte-identical to the Kotlin SDK's copy; trimming one copy would make the shared vectors diverge. `NoiseTests` selects the suite this SDK speaks |
| #26 | `ControlPlane.DeviceVerificationUri`: allow plain HTTP only for loopback | Not changed: the Python reference (`_safe_browser_url`) and the Go port (`validateBrowserURL`) accept HTTP or HTTPS without credentials, and so does this port. The URL comes from the API the caller chose; an endpoint able to inject one can inject an HTTPS phishing page as easily, and a loopback-only rule would refuse a self-hosted control plane on a LAN. The risk that remains, an on-path attacker replacing a remote plain-HTTP page, is recorded in `AUDIT.md`, and `ControlPlaneSecurityTests.DeviceVerificationUrlMayBePlainHttpAsInTheReference` now pins the behaviour. A stricter rule belongs in the reference first |
| #37 | `HubIntentInventory.ToJsonObject()` drops `ListedIn` | Fixed since b22d188 (#48): it writes `listed_in`. Test: `IntentInventoryTests.TheSerialisedInventorySaysWhichLanguageItListedIn` |
| #41 | Admission wait: back off, or honour `Retry-After`, on repeated 5xx | Not changed: the reference's `wait_for_operation` and the shared `connection-admission-vectors.json` ("A 5xx is ridden out", and each vector counts the GETs sent) poll at the interval, and the Go port does the same. The wait is bounded by its timeout (90 s at a 2 s interval by default). Changing it is a cross-SDK contract change that starts in the reference vectors |
| #42 | `NoiseStore`: an existing but unwritable folder beside the identity does not fall back to the shared default | Not changed: deliberate and documented in bc57222 (#42) itself, where `contracts/sdk-parity.json` says a noise folder that exists is kept even when it cannot be written, as in the reference (`default_state_dir` returns the folder when `beside.is_dir()`) |

### Verification

- `dotnet test tests/Thalovant.Sdk.Tests -c Release -f net9.0` on this branch: 651 passed, 0 failed (net8.0 runs in CI; no net8 runtime on the workstation). No source file changes; the one new test pins existing behaviour, and it fails when `DeviceVerificationUri` is made to refuse remote HTTP.
- `contracts/sdk-parity.json`: the evidence hash of `ControlPlaneSecurityTests.cs` is refreshed, and the reference's `check-sdk-parity.py --consumer thalovant-dotnet-sdk` passes.

### Transparency Report

- **AI Model:** Claude Opus 5.5 (claude-opus-5-5), as a Claude Code agent.
- **Actions Taken:** read each review body and the current code, traced each fix to its commit with `git log -S`, compared the declined items with the Python reference and the Go port, dropped the README's stale install pin, added a test pinning the plain-HTTP verification URL behaviour (from CodeRabbit's review of this follow-up), and wrote this entry, the changelog line and the audit decisions.
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
