Last Edit: Claude Opus 5.5 - 2026-11-08 - Motive: Point the digest follow-up at the final candidate.

# Suggestions: thalovant-dotnet-sdk

## Decide whether this SDK should manage chat apps

- **Problem:** chat apps (`/v1/bridges`) are not covered here; they are recorded as not applicable.
- **Proposal:** if an app or service built on this SDK needs to connect or pause chat apps, record the obligation in the API's impact file first, then port the Python reference's `chat-apps` capability (models, `SecretValue`, the twelve owner routes, `fields` on the API error).
- **Impact:** low today; avoids a silent gap if such an app appears.

## Drop the old reference digest once the Python producer merges

- **Problem:** `contracts/sdk-parity.json` accepts both `57c7e71f6456` and the final browser-sign-in candidate `6dfb3bb31ac7` during the rollout.
- **Proposal:** once thalovant-python-sdk `main` carries the candidate (python-sdk #113 merged), remove `57c7e71f6456` in a follow-up, re-running `check-sdk-parity.py --consumer thalovant-dotnet-sdk`.
- **Impact:** low; keeps the acceptance honest about what was reviewed.

## Surface `ai_disclosure` when the reference types it

- **Problem:** a browser sign-in's token answer carries `ai_disclosure`; this SDK keeps it only in `DeviceLoginResult.Raw`, as the porting brief asks for this round.
- **Proposal:** add a typed property when the Python reference adds it to `ApiToken`.
- **Impact:** low; a convenience for apps that show the disclosure.

## Share one loopback test server

- **Problem:** `tests/Thalovant.Sdk.Tests/LoopbackApi.cs` and the `TinyHttpServer` in `BrowserLoginVectorTests.cs` both read HTTP/1.1 off a `TcpListener`. They stay separate because `LoopbackApi.cs` is evidence for three capabilities, and the browser vectors have to rewrite the exchange body (the generated verifier) before comparing it.
- **Proposal:** give `LoopbackApi` a body-normalising hook and move the browser tests onto it in a change that re-reviews those three capabilities.
- **Impact:** low; less test code.

## Describe browser sign-in on docs.thalovant.com

- **Problem:** the .NET SDK guide still describes only password and device sign-in.
- **Proposal:** move the README's "Sign in through the browser" section into the guide in the 0.10.0 release train.
- **Impact:** medium; it is the sign-in most desktop callers should use.

