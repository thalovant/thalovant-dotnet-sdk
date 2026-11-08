Last Edit: Claude Opus 5.5 - 2026-11-08 - Motive: Record the browser sign-in decisions and open items (0.10.0).

# Audit

Known issues, technical debt and review decisions, each tied to the code and the test that shows it.

## Fixed

- **2^63 reached an out-of-range cast in quota parsing** (fixed 2026-11-03). `ThalovantPolicyDeniedException.WholeInRange` in [`src/Thalovant.Sdk/Errors.cs`](src/Thalovant.Sdk/Errors.cs) compared a double with `long.MaxValue`, which rounds to 2^63. Masked by `WholeCount`'s 2^53 - 1 ceiling, so no denial reported a wrong count. Test: `RefusalVectorTests.AWholeDoubleIsReadOnlyWhileItFitsInALong` in [`tests/Thalovant.Sdk.Tests/RefusalVectorTests.cs`](tests/Thalovant.Sdk.Tests/RefusalVectorTests.cs).

## Decisions

- **The conformance digest is the reference's recipe, not RFC 8785.** [`tests/Thalovant.Sdk.Tests/ConformanceRecord.cs`](tests/Thalovant.Sdk.Tests/ConformanceRecord.cs) reproduces `json.dumps(sort_keys=True, separators=(",", ":"), ensure_ascii=False)` with whole floats written as integers: code point key order (`CodePointOrder`), exact whole numbers (`WholeNumber`), and only the reference's string escapes (`QuoteString`). JCS differs on key order above U+FFFF and on number spelling, and the parity gate compares digests with the reference's own.
- **A device verification URL may be plain HTTP on any host** (CodeRabbit on #26, 2026-11-07). `ThalovantControlPlane.DeviceVerificationUri` in [`src/Thalovant.Sdk/ControlPlane.cs`](src/Thalovant.Sdk/ControlPlane.cs) accepts HTTP or HTTPS with a host and no credentials, as the Python reference's `_safe_browser_url` and the Go port's `validateBrowserURL` do. The URL comes from the API the caller chose, and a loopback-only rule would refuse a self-hosted control plane on a LAN. **Risk accepted:** when that API hands out a remote plain-HTTP page, an on-path attacker can replace the page and ask for the person's credentials even though the API itself is trustworthy. The hosted API answers HTTPS, so this arises only for a control plane deliberately served over plain HTTP. A stricter rule starts in the reference. Tests: `ControlPlaneSecurityTests.DeviceBrowserLauncherAcceptsOnlyWebUrlsWithoutUserinfo` and `DeviceVerificationUrlMayBePlainHttpAsInTheReference`.
- **The admission wait rides out a 5xx at the poll interval** (CodeRabbit on #41, 2026-11-07). `WaitForAdmissionAsync` in [`src/Thalovant.Sdk/ControlPlane.cs`](src/Thalovant.Sdk/ControlPlane.cs) follows the reference's `wait_for_operation` and the shared [`connection-admission-vectors.json`](tests/Thalovant.Sdk.Tests/Fixtures/connection-admission-vectors.json), which count the GETs sent; the timeout bounds it. A backoff would be a cross-SDK contract change.
- **An existing noise folder beside the identity is kept even when it cannot be written** (CodeRabbit on #42, 2026-11-07). `HiveMindFileNoiseStore.Prepare` in [`src/Thalovant.Sdk/NoiseStore.cs`](src/Thalovant.Sdk/NoiseStore.cs) falls back to the shared default only when the folder does not exist and cannot be made, as the reference's `default_state_dir` does; `contracts/sdk-parity.json` records it. Test: `KeyFolderTests`.
- **`Fixtures/noise-node.json` keeps its ChaChaPoly exchanges** (CodeRabbit on #12, 2026-11-07). The file is the Node peer's whole transcript, byte-identical to the Kotlin SDK's copy; [`NoiseTests`](tests/Thalovant.Sdk.Tests/NoiseTests.cs) selects the suite this SDK speaks.

## Browser sign-in (2026-11-08, 0.10.0)

Evidence: `dotnet test -c Release -f net9.0` 836 passed, 0 failed; the net8.0 test build 836 passed on the 9.0 runtime (`DOTNET_ROLL_FORWARD=Major`); all 98 `browser-login-vectors.json` case digests in `contracts/conformance-results.json` equal the reference's.

### Decisions

- **Code points are counted by hand, not with `Rune`.** `BrowserSignIn.CutToCodePoints` in [`src/Thalovant.Sdk/BrowserLogin.cs`](src/Thalovant.Sdk/BrowserLogin.cs) walks surrogate pairs because `System.Text.Rune` is not in netstandard2.1. Test: `authorization_urls/a device name longer than 120 code points ...` in [`BrowserLoginVectorTests`](tests/Thalovant.Sdk.Tests/BrowserLoginVectorTests.cs) and `BrowserLoginTests.TheDeviceNameDefaultsToThisSdkOnThisHost`.
- **Percent-encoding is written out, not `Uri.EscapeDataString`.** `BrowserSignIn.PercentEncode` encodes the UTF-8 bytes with upper-case hex, so the URL is the same on every runtime the netstandard2.1 build can land on (Mono's escaping has differed). Test: the nine `authorization_urls` vectors.
- **The listener stops listening before it tells the waiter.** `LoopbackListener.ServeAsync` in [`src/Thalovant.Sdk/BrowserLoopback.cs`](src/Thalovant.Sdk/BrowserLoopback.cs) calls `StopListening` before completing the accepted task: unlike the reference's single event loop, a thread-pool continuation could otherwise connect before the port closed. Test: `BrowserLoginTests.ASecondAnswerAfterTheFirstIsTurnedAway`.
- **Every answer half-closes and drains briefly.** `AnswerAsync` shuts the send side, then reads what the tab still sends for at most `CloseGrace` (1 s): closing a socket with unread input resets it, and the reset can discard the 400 an oversized head gets before the tab reads it. Test: `BrowserLoginTests.ARequestThatIsNotOneIs400AndTheWaitGoesOn`.
- **Query values decode as a form does, as the reference's `parse_qsl`.** `LoopbackListener.FormDecode` turns `+` into a space, so a code containing `+` must arrive as `%2B` (the dashboard encodes it). `#` is not stripped (browsers never send a fragment). Test: `BrowserLoginTests.QueryValuesAreDecodedOnceAsAFormDecodesThem`.
- **The authorization endpoint is split as Python's `urlsplit` splits it.** `NativeSignInConfig.UsableAuthorizationEndpoint` mirrors the reference, including accepting `https://x\y/a` (a backslash is not a delimiter to `urlsplit`). The vectors do not pin that edge; a stricter rule starts in the reference. Tests: `BrowserLoginTests.AnAuthorizationEndpointNoBrowserCanOpenIsUnusable` and `...TheReferenceAcceptsIsUsable`.
- **A Darwin without `/usr/bin/open` is named `ios`.** `BrowserSignIn.PlatformName` checks Android, iOS, tvOS, watchOS and Mac Catalyst first, then treats an OSX report without `/usr/bin/open` as iOS, because Unity's Mono can report OSX on iOS. `LoginMethod.Auto` then picks a device login, as the porting brief asks. **Risk accepted:** a stripped macOS without `/usr/bin/open` also gets a device login (a forced `LoginMethod.Browser` still works). Test: `BrowserLoginTests.ThisMachineIsNamedAsTheBrowserRuleNamesIt`.
- **An HTTP timeout on the configuration request is a fallback, not an error, in `Auto`.** `InteractiveLoginAsync` treats an `OperationCanceledException` that the caller's token did not cause as "the configuration fails". The caller's own cancellation still propagates.
- **Secrets are redacted on a copy with no inner exception.** `ThalovantControlPlane.WithoutSecrets` rebuilds the error through `ThalovantApiException.FromResponse`, so the kind (for example `ThalovantAuthenticationException`), `ErrorCode`, `Detail` and `RetryAfter` survive, and writes `Body` back from the clean problem so an escaped spelling cannot survive. Tests: `BrowserLoginTests.AnEchoedCodeAndVerifierNeverReachTheError`, `ARedactedErrorKeepsItsKindAndItsFields`; a mutation that skips redaction fails both.

### Open

- **net8.0 not run on a .NET 8 runtime locally.** The workstation has only the 9.0 runtime, so the net8.0 test build ran with `DOTNET_ROLL_FORWARD=Major`; CI runs net8.0 and net9.0 on ubuntu-latest and windows-latest.
- **Windows makes the vector suite slower.** Each `closed` check connects to the stopped port, and Windows retries a refused loopback connect for about two seconds (as `LoopbackApi.Unreachable` notes), so `BrowserLoginVectorTests` takes about a minute per framework on windows-latest. Expected, not a defect.
- **`ai_disclosure` is only in `DeviceLoginResult.Raw`.** The porting brief keeps it out of the typed result in this round.
- **docs.thalovant.com.** The .NET SDK page does not yet describe browser sign-in; the README's "Not in the docs yet" section does.

## Open

- None recorded.

## Chat apps not covered (2026-11-03)

- **What:** this SDK has no methods for the chat apps routes (`/v1/bridges`), which the Python, Node, Go and Rust SDKs and the MCP server cover. `contracts/sdk-parity.json` records `chat-apps` as not applicable.
- **Status:** a scope decision, not a defect: the API's impact records for chat apps list no obligation here. Revisit if an app built on this SDK needs to manage chat apps.
