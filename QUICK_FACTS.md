Last Edit: Claude Opus 5.5 - 2026-11-08 - Motive: Final browser-login reference (python-sdk 39e31d8, digest 6dfb3bb31ac7, 119 vectors).

# Quick facts: thalovant-dotnet-sdk

| Fact | Value |
| :--- | :--- |
| Package | Thalovant.Sdk (NuGet) |
| Version | 0.10.0 (src/Thalovant.Sdk/Thalovant.Sdk.csproj) |
| Targets | net8.0, netstandard2.1 (Unity 2021+); tests on net8.0 and net9.0 |
| Public surface | ThalovantClient, ThalovantControlPlane (src/Thalovant.Sdk) |
| Sign-in | `BrowserLoginAsync` (PKCE on a loopback redirect), `InteractiveLoginAsync` (`LoginMethod.Auto`/`Browser`/`Device`), `LoginWithBrowserAsync` (device login), `LoginAsync` (password) |
| Browser sign-in pieces | `BrowserSignIn` (URL, redirect, state, browser rule, device name), `NativeSignInConfig` (BrowserLogin.cs); `LoopbackListener` (internal, BrowserLoopback.cs) |
| Browser sign-in API | `GET /v1/auth/native/config`, `POST {token_endpoint}` (thalovant-api #501, live); a config 404 falls back to device login only in `InteractiveLoginAsync` with `LoginMethod.Auto`; `BrowserLoginAsync` and `LoginMethod.Browser` throw it |
| Parity | consumer of the Python reference; acknowledges `57c7e71f6456` and the final candidate `6dfb3bb31ac7` (python-sdk #113 at `39e31d8`) in `contracts/sdk-parity.json` |
| Conformance | `contracts/conformance-results.json`, written by `THALOVANT_CONFORMANCE_OUT=... dotnet test`; browser-login: 119 cases |
| Request origin | every control-plane request is refused off the API's origin (`ThalovantControlPlane.RequireApiOrigin`) |
| Chat apps (`/v1/bridges`) | not covered (`chat-apps` not applicable); see the Python, Node, Go or Rust SDK or the MCP server |
| Release | merge to `main` with release-relevant changes -> `auto-release.yml` |
