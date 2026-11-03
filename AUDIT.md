Last Edit: Claude Opus 5.5 - 2026-11-03 - Motive: Record the 2^63 boundary fix and the conformance digest recipe decision.

# Audit

Known issues, technical debt and review decisions, each tied to the code and the test that shows it.

## Fixed

- **2^63 reached an out-of-range cast in quota parsing** (fixed 2026-11-03). `ThalovantPolicyDeniedException.WholeInRange` in [`src/Thalovant.Sdk/Errors.cs`](src/Thalovant.Sdk/Errors.cs) compared a double with `long.MaxValue`, which rounds to 2^63. Masked by `WholeCount`'s 2^53 - 1 ceiling, so no denial reported a wrong count. Test: `RefusalVectorTests.AWholeDoubleIsReadOnlyWhileItFitsInALong` in [`tests/Thalovant.Sdk.Tests/RefusalVectorTests.cs`](tests/Thalovant.Sdk.Tests/RefusalVectorTests.cs).

## Decisions

- **The conformance digest is the reference's recipe, not RFC 8785.** [`tests/Thalovant.Sdk.Tests/ConformanceRecord.cs`](tests/Thalovant.Sdk.Tests/ConformanceRecord.cs) reproduces `json.dumps(sort_keys=True, separators=(",", ":"), ensure_ascii=False)` with whole floats written as integers: code point key order (`CodePointOrder`), exact whole numbers (`WholeNumber`), and only the reference's string escapes (`QuoteString`). JCS differs on key order above U+FFFF and on number spelling, and the parity gate compares digests with the reference's own.

## Open

- None recorded.
