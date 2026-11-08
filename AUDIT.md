Last Edit: Claude Opus 5.5 - 2026-11-08 - Motive: Browser sign-in: the CodeRabbit re-review of #54 at b36a198 (11 threads).

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

Evidence, at the commit that ports the final reference (python-sdk #113 at `39e31d8`, digest `6dfb3bb31ac7`):

- `DOTNET_ROLL_FORWARD=Major dotnet test --configuration Release --no-build`: 929 passed, 0 failed, on each of net8.0 (on the 9.0 runtime) and net9.0, after the CodeRabbit re-review (921 at `b36a198`).
- All 119 `browser-login-vectors.json` case digests in `contracts/conformance-results.json` equal the reference's. The 98 cases from the first round record exactly what they did.
- Earlier counts: 846 at `f95ece1` (the CodeRabbit review) and 837 at `5cd79dd` (the first push).

### Decisions

- **Code points are counted by hand, not with `Rune`.** `BrowserSignIn.CutToCodePoints` in [`src/Thalovant.Sdk/BrowserLogin.cs`](src/Thalovant.Sdk/BrowserLogin.cs) walks surrogate pairs, because `System.Text.Rune` is not in netstandard2.1. Test: `authorization_urls/a device name longer than 120 code points ...` in [`BrowserLoginVectorTests`](tests/Thalovant.Sdk.Tests/BrowserLoginVectorTests.cs).
- **Percent-encoding is written out, not `Uri.EscapeDataString`.** `BrowserSignIn.PercentEncode` encodes the UTF-8 bytes with upper-case hex, so the URL is the same on every runtime the netstandard2.1 build can land on. Test: the nine `authorization_urls` vectors.
- **The configuration is read from the string as given.**
  - `NativeSignInConfig.UsableAuthorizationEndpoint` splits the address as Python's `urlsplit` does, never through `Uri.Host`, which can hand an international name back in Unicode.
  - `RealHost` accepts a dotted-quad IPv4 address by hand. `IPAddress.TryParse` takes `127.1` and `0x7f.0.0.1`, which the reference's `ipaddress` refuses.
  - A bracketed host must be IPv6, so IPvFuture is refused too.
  - Tests: the 30 `config` vectors, and `BrowserLoginTests.AnAuthorizationEndpointNoBrowserCanOpenIsUnusable`, `...TheReferenceAcceptsIsUsable`, `AHostnameIsAtMost253CharactersOf63CharacterLabels` and the token-endpoint theories. Every case was checked against the reference's own functions at `39e31d8`.
  - This settles the CodeRabbit thread on `https://%/authorize`: it is now refused, as the reference refuses it.
- **The origin check is a backstop here.**
  - `ThalovantControlPlane.RequireApiOrigin` (ControlPlane.cs) is called by `BuildRequest`, which every control-plane request goes through. It refuses a URL whose scheme, host or port is not the API's, with an error with no status.
  - `BuildRequest` appends the path to `ApiUrl`, which ends in `/`, so a path cannot name another authority the way Python's `urljoin` lets `/https://elsewhere/x` do. The check keeps it so.
  - Tests: `NoRequestLeavesTheApiOrigin`, `ARequestOffTheApiOriginIsRefusedWithNoStatus`, `AnOrdinaryApiPathStillReachesTheApi`, and `sign_ins/a token endpoint that would leave the API is refused before anything is opened`.
- **The exchange is written once.**
  - `SendExchangeOnceAsync` replaces the request body with a `SingleUseContent` that refuses a second write. A pipeline that resends it then fails, with an error with no status, instead of presenting the code twice.
  - The resenders it covers are `SocketsHttpHandler` retrying a request whose reused connection failed, and a retry policy in a caller's handler that writes the body again.
  - A caller's handler that buffers the body itself and then resends the buffer is that caller's own transport.
  - Test: `TheExchangeIsWrittenOnceEvenIfThePipelineResendsIt`; a mutation that disables the guard fails it.
- **The listener stops listening before it tells the waiter.** `LoopbackListener.ServeAsync` in [`src/Thalovant.Sdk/BrowserLoopback.cs`](src/Thalovant.Sdk/BrowserLoopback.cs) calls `StopListening` before completing the accepted task. Without this, a thread-pool continuation could connect before the port closed. Test: `BrowserLoginTests.ASecondAnswerAfterTheFirstIsTurnedAway`.
- **The accepted tab waits as long as the sign-in takes.** It has no deadline of its own: the exchange's HTTP timeout bounds it, and `CloseAsync` always answers it at once. This replaces the first round's 60-second bound and settles the CodeRabbit "pending page" thread. Tests: `TheAcceptedTabWaitsAsLongAsTheSignInTakes`, `ClosingTheListenerAnswersATabThatIsStillWaiting`.
- **Every answer is drained.** `AnswerAsync` (and the accepted tab's path) shuts the write side, then reads and drops what the browser still sends for at most `DrainTimeout` (2 s) and `DrainLimit` (1 MiB). No read goes past the budget (`NextDrainRead`). The accepted tab is drained before closing is told it has its page, so `CloseAsync` waits for it at most `CloseGrace`, as the reference's `close()` waits for the accepted task. On Linux the half-close already delivers the page before any reset, so the test asserts the wait itself (`AnAcceptedTabStillSendingWhenTheListenerClosesGetsItsPage`; a mutation that drains after signalling fails it). Closing with bytes unread resets the connection, and macOS then discards the answer. Tests: `AnEarlyAnswerIsNotLostToAReset` (a 600 KB head and a 600 KB unread body), `DrainingAConnectionIsBounded`, `TheDrainBoundsAreTheBriefs`.
- **A `HEAD` answer carries the headers and no body.** `ResponseBytes(..., headOnly: true)`; its `Content-Length` is the page's. Test: `AHeadRequestGetsTheHeadersAndNoBody`.
- **Only RFC 6749's error codes are kept from a callback.** `LoopbackListener.OAuthErrors` holds the seven codes, and anything else becomes null, so a value the page chose (the state, a code) is never repeated. Tests: the `callbacks` vectors on errors, `sign_ins/an error that repeats the state never reaches the error`, `AnErrorTheTabSendsNeverCarriesTheStateOut`.
- **Only a null device name gets the default.** An empty one keeps the hostname off the consent page and sends no `client_name` to a device login. Tests: `sign_ins/an empty device name keeps the hostname off the consent page`, `chooser/an empty device name is not sent to a device login either`.
- **Redaction happens before the message, in one pass, and on a new error.**
  - `ExchangeBrowserCodeAsync` redacts a failed answer before `ApiError` builds the message, which is cut to 200 characters. A cut would otherwise keep a prefix of a secret.
  - `Redactor` replaces every secret at once, longest first, and never inside an existing `[redacted]`. It covers JSON bodies of any shape, escaped spellings included, and any token value a failed answer carries.
  - `WithoutSecrets` always returns a new error with no inner exception, because a transport's own exception can keep the request. It keeps the kind, so `ThalovantApiUnreachableException` stays unreachable.
  - Tests: `AMessageCutToLengthKeepsNoPartOfASecret`, `ScrubbingIsOnePassLongestFirstAndLeavesItsMarkerAlone`, `ATransportFailureDuringTheExchangeCarriesNothingOfTheRequest`, `AnEchoThatIsNotAnObjectIsCleanedToo`, `ATokenAFailedExchangeCarriesNeverReachesTheError`, `ARedactedErrorIsAlwaysANewOneWithNothingRidingAlong`, `EverySecretIsWrittenTheSameWayPercentEncoded`.
- **Connections are bounded, and the callback is never what makes room.** `LoopbackListener.AcceptLoopAsync` holds at most 32 connections, and read buffers grow only as a head arrives.
  - When full, it closes first the oldest connection that has been answered (a rejection, draining).
  - Next, the oldest that has sent nothing and has no bytes waiting (`TcpClient.Available`).
  - A connection whose first bytes have arrived is never closed to make room; with none left to close, the newcomer is closed.
  - This is not in the reference and changes nothing any vector observes. The `Available` check narrows, but cannot close, the gap between bytes arriving and the handler reading them.
  - Tests: `AFloodOfSilentConnectionsCannotCrowdOutTheCallback`, `AnsweredRejectionsStillDrainingMakeRoomForTheCallback` (a mutation that keeps answered connections unevictable fails it), `ACallbackIsNotEvictedWhileSilentConnectionsKeepArriving`.
- **The result is read before the token is kept.** An answer that cannot be read (a lone surrogate in a scope) is an error with no status and leaves no token. Device login's `AcceptDeviceToken` keeps its own order, as the brief asks. Test: `AnAnswerThatCannotBeReadKeepsNoToken`.
- **A Darwin without `/usr/bin/open` is named `ios`.**
  - `BrowserSignIn.PlatformName` checks Android, iOS, tvOS, watchOS and Mac Catalyst first, and Unity's Mono can report OSX on iOS. Android and iOS then follow the rule for elsewhere (a display variable), as the brief maps them.
  - **Risk accepted:** a stripped macOS without `/usr/bin/open` gets a device login; a forced `LoginMethod.Browser` still works.
  - Tests: `ThisMachineIsNamedAsTheBrowserRuleNamesIt`, `APhoneFollowsTheRuleForEverywhereElse`.
- **A body System.Text.Json cannot read is an error, not a crash.**
  - A lone surrogate escape in a property name or a string makes System.Text.Json throw `InvalidOperationException`. The reference's `json` reads it.
  - The configuration, the token answer and a failed exchange are therefore read through `ReadObject` and `Failure`. An unreadable 2xx is an error with no status, and an unreadable failure keeps its status without its body.
  - `NativeSignInConfig.FromPayload` keeps its `FormatException` contract, so `Auto` still turns to a device login.
  - The rest of the SDK's error parsing (`ThalovantApiException.ParseProblem`, `api-errors`) is unchanged.
  - Tests: `ATokenAnswerWhoseNamesCannotBeReadIsAnErrorWithNoStatus`, `AFailedExchangeWhoseValuesCannotBeReadKeepsItsStatus`, `AConfigurationThatCannotBeReadIsUnusableAndAutoTurnsToADeviceLogin`.
- **A failed exchange's body that is not JSON is withheld.** It cannot be searched for a token it does not name, so `Body` is `WithheldBody`. The api-errors fields come from JSON only, and the reference's error keeps nothing of such a body. Test: `AFailedExchangeBodyThatIsNotJsonIsWithheld`.
- **The code is redacted as a URL or a form would spell it too** (`PercentEncode`, `WebUtility.UrlEncode`). Test: `TheCodeIsRedactedAsAUrlWouldSpellItToo`.
- **Declined, as the cross-SDK contract: a final label like `0x1`.** CodeRabbit asked to refuse `https://example.0x1/a`, as WHATWG host parsing would. The reference's `_real_host` at `39e31d8` accepts it (only an all-decimal last label must be IPv4; even `https://0x7f/a` passes), and no vector pins it. Refusing it here would refuse a configuration every other SDK accepts. Reported to the lead.
- **Declined, as out of reach: a caller's handler that buffers the exchange body and resends the buffer.** `SingleUseContent` refuses a second write of the body. But `HttpContent` buffers on `ReadAs*Async` without calling back, and a later send copies the buffer. A pipeline that does that has its own retry policy for POST, the case the brief leaves with the caller ("A caller's own requests session would resend it only if it mounted a retry policy that allows POST"). The SDK's own stack never buffers or resends the exchange. Reported to the lead.
- **Declined: redirect control inside a caller's own handler.** The exchange goes through `BuildRequest`, as every credential-bearing request does.
  - Automatic redirects are switched off on every handler the SDK can reach, a 3xx is an error, and an injected `HttpClient` is refused.
  - A terminal `HttpMessageHandler` of the caller's own is the caller's transport, and `HttpClient` has no per-request redirect switch.

### Open

- **net8.0 not run on a .NET 8 runtime locally.** The workstation has only the 9.0 runtime, so the net8.0 test build ran with `DOTNET_ROLL_FORWARD=Major`; CI runs net8.0 and net9.0 on ubuntu-latest and windows-latest.
- **Windows makes the vector suite slower.** Each `closed` check connects to the stopped port, and Windows retries a refused loopback connect for about two seconds.
- **`ai_disclosure` is only in `DeviceLoginResult.Raw`.** The porting brief keeps it out of the typed result in this round.
- **docs.thalovant.com.** The .NET SDK page does not yet describe browser sign-in; the README's "Not in the docs yet" section does.

## Open

- Browser sign-in has open items of its own: see [Browser sign-in (2026-11-08, 0.10.0) → Open](#open) above (net8.0 runtime, Windows timing, `ai_disclosure`, docs.thalovant.com). Nothing else is recorded.

## Chat apps not covered (2026-11-03)

- **What:** this SDK has no methods for the chat apps routes (`/v1/bridges`), which the Python, Node, Go and Rust SDKs and the MCP server cover. `contracts/sdk-parity.json` records `chat-apps` as not applicable.
- **Status:** a scope decision, not a defect: the API's impact records for chat apps list no obligation here. Revisit if an app built on this SDK needs to manage chat apps.
