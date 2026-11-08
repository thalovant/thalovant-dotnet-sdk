Last Edit: Claude Opus 5.5 - 2026-11-08 - Motive: Add the browser sign-in questions.

# FAQ: thalovant-dotnet-sdk

**Can I manage chat apps (Telegram, Discord, Matrix, Mattermost, Twitch bridges) with this SDK?**
No. Chat apps are managed from the console, the Python, Node, Go and Rust SDKs, or the MCP server. `contracts/sdk-parity.json` records the `chat-apps` capability as not applicable here.

**Why does this repository change when the Python SDK adds a feature?**
The Python SDK is the parity reference. Each consumer records the reference digest it has reviewed, and answers every capability, in `contracts/sdk-parity.json`; the reference cannot release until every consumer has.

**How do I sign a person in from a desktop app or a command-line tool?**
Call `ThalovantControlPlane.InteractiveLoginAsync()`. On a machine with a browser it signs in through it (`BrowserLoginAsync`: PKCE on a `http://127.0.0.1:<port>/callback` redirect); over SSH, in CI (`CI` set), on Linux without `DISPLAY`/`WAYLAND_DISPLAY`, or against an API without `GET /v1/auth/native/config`, it uses a device login. `InteractiveLoginOptions.Method` forces `LoginMethod.Browser` or `LoginMethod.Device`.

**Why browser sign-in rather than a device code?**
A device code can be read off one screen and approved by somebody else (device-code phishing). A loopback redirect cannot be relayed: the browser that approves delivers the code to its own computer. Device login stays for machines with no browser.

**The browser did not open. What now?**
The URL is always printed as well (`Opening your browser to sign in to Thalovant. If it does not open, visit:`); open it by hand. Pass `BrowserLoginOptions.Prompt` to show it yourself, `Opener` to open it your own way, or `OpenBrowser = false` to only print it.

**What does a browser sign-in throw?**
`ThalovantDeviceAccessDeniedException` when the person denies it, `ThalovantDeviceCodeExpiredException` after the timeout (300 s by default), both with no status; a `ThalovantApiException` with status 400 and detail `invalid_grant` when the API refuses the code (start again; a code is never reused); a 404 `ThalovantApiException` from `BrowserLoginAsync` against an API without browser sign-in. The code, the verifier, the state and the token never appear in an exception.

**Does it work in Unity, on Android or on iOS?**
The code compiles for netstandard2.1, but `LoginMethod.Auto` picks a device login on Android and iOS, where the apps sign in through their own flow (`NativeSignIn.Begin` and `CompleteNativeSignInAsync`). Forcing the browser there is not supported.

