Last Edit: Claude Opus 5.5 - 2026-11-07 - Motive: Record the review decisions on four CodeRabbit review-body items.

# Audit

Known issues, technical debt and review decisions, each tied to the code and the test that shows it.

## Fixed

- **2^63 reached an out-of-range cast in quota parsing** (fixed 2026-11-03). `ThalovantPolicyDeniedException.WholeInRange` in [`src/Thalovant.Sdk/Errors.cs`](src/Thalovant.Sdk/Errors.cs) compared a double with `long.MaxValue`, which rounds to 2^63. Masked by `WholeCount`'s 2^53 - 1 ceiling, so no denial reported a wrong count. Test: `RefusalVectorTests.AWholeDoubleIsReadOnlyWhileItFitsInALong` in [`tests/Thalovant.Sdk.Tests/RefusalVectorTests.cs`](tests/Thalovant.Sdk.Tests/RefusalVectorTests.cs).

## Decisions

- **The conformance digest is the reference's recipe, not RFC 8785.** [`tests/Thalovant.Sdk.Tests/ConformanceRecord.cs`](tests/Thalovant.Sdk.Tests/ConformanceRecord.cs) reproduces `json.dumps(sort_keys=True, separators=(",", ":"), ensure_ascii=False)` with whole floats written as integers: code point key order (`CodePointOrder`), exact whole numbers (`WholeNumber`), and only the reference's string escapes (`QuoteString`). JCS differs on key order above U+FFFF and on number spelling, and the parity gate compares digests with the reference's own.
- **A device verification URL may be plain HTTP on any host** (CodeRabbit on #26, 2026-11-07). `ThalovantControlPlane.DeviceVerificationUri` in [`src/Thalovant.Sdk/ControlPlane.cs`](src/Thalovant.Sdk/ControlPlane.cs) accepts HTTP or HTTPS with a host and no credentials, as the Python reference's `_safe_browser_url` and the Go port's `validateBrowserURL` do. The URL comes from the API the caller chose, and a loopback-only rule would refuse a self-hosted control plane on a LAN. A stricter rule starts in the reference. Test: `ControlPlaneSecurityTests.DeviceBrowserLauncherAcceptsOnlyWebUrlsWithoutUserinfo`.
- **The admission wait rides out a 5xx at the poll interval** (CodeRabbit on #41, 2026-11-07). `WaitForAdmissionAsync` in [`src/Thalovant.Sdk/ControlPlane.cs`](src/Thalovant.Sdk/ControlPlane.cs) follows the reference's `wait_for_operation` and the shared [`connection-admission-vectors.json`](tests/Thalovant.Sdk.Tests/Fixtures/connection-admission-vectors.json), which count the GETs sent; the timeout bounds it. A backoff would be a cross-SDK contract change.
- **An existing noise folder beside the identity is kept even when it cannot be written** (CodeRabbit on #42, 2026-11-07). `HiveMindFileNoiseStore.Prepare` in [`src/Thalovant.Sdk/NoiseStore.cs`](src/Thalovant.Sdk/NoiseStore.cs) falls back to the shared default only when the folder does not exist and cannot be made, as the reference's `default_state_dir` does; `contracts/sdk-parity.json` records it. Test: `KeyFolderTests`.
- **`Fixtures/noise-node.json` keeps its ChaChaPoly exchanges** (CodeRabbit on #12, 2026-11-07). The file is the Node peer's whole transcript, byte-identical to the Kotlin SDK's copy; [`NoiseTests`](tests/Thalovant.Sdk.Tests/NoiseTests.cs) selects the suite this SDK speaks.

## Open

- None recorded.

## Chat apps not covered (2026-11-03)

- **What:** this SDK has no methods for the chat apps routes (`/v1/bridges`), which the Python, Node, Go and Rust SDKs and the MCP server cover. `contracts/sdk-parity.json` records `chat-apps` as not applicable.
- **Status:** a scope decision, not a defect: the API's impact records for chat apps list no obligation here. Revisit if an app built on this SDK needs to manage chat apps.
