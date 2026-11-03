Last Edit: Claude Opus 5.5 - 2026-11-03 - Motive: Record the chat-apps parity acknowledgment (not applicable).

# Maintenance Report

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
