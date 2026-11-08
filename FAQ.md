Last Edit: Claude Opus 5.5 - 2026-11-08 - Motive: Browser sign-in answers: CodeRabbit review on #54 (relay wording, Prompt, mobile rule).

# FAQ: thalovant-dotnet-sdk

**Can I manage chat apps (Telegram, Discord, Matrix, Mattermost, Twitch bridges) with this SDK?**
No. Chat apps are managed from the console, the Python, Node, Go and Rust SDKs, or the MCP server. `contracts/sdk-parity.json` records the `chat-apps` capability as not applicable here.

**Why does this repository change when the Python SDK adds a feature?**
The Python SDK is the parity reference. Each consumer records the reference digest it has reviewed, and answers every capability, in `contracts/sdk-parity.json`; the reference cannot release until every consumer has.

**How do I sign a person in from a desktop app or a command-line tool?**
Call `ThalovantControlPlane.InteractiveLoginAsync()`. On a machine with a browser it signs in through it (`BrowserLoginAsync`: PKCE on a `http://127.0.0.1:<port>/callback` redirect); over SSH, in CI (`CI` set), on Linux without `DISPLAY`/`WAYLAND_DISPLAY`, or against an API without `GET /v1/auth/native/config`, it uses a device login. `InteractiveLoginOptions.Method` forces `LoginMethod.Browser` or `LoginMethod.Device`.

**Why browser sign-in rather than a device code?**
A device code can be read off one screen and approved by somebody else (device-code phishing). With a loopback redirect, the browser that approves delivers the code to a listener on its own computer, so a forwarded sign-in link gives the sender nothing unless the person who approved also sends back the address their browser was redirected to. PKCE binds the code to the verifier the signing-in program holds; it does not stop that whole callback address from being forwarded. Device login stays for machines with no browser.

**The browser did not open. What now?**
Unless you passed `BrowserLoginOptions.Prompt`, the URL is printed as well (`Opening your browser to sign in to Thalovant. If it does not open, visit:`); open it by hand. A `Prompt` receives the URL in place of the print, so showing it is then up to your callback. Pass `Opener` to open it your own way, or `OpenBrowser = false` to only print it.

**What does a browser sign-in throw?**
`ThalovantDeviceAccessDeniedException` when the person denies it, `ThalovantDeviceCodeExpiredException` after the timeout (300 s by default), both with no status; a `ThalovantApiException` with status 400 and detail `invalid_grant` when the API refuses the code (start again; a code is never reused); a 404 `ThalovantApiException` from `BrowserLoginAsync` against an API without browser sign-in. The code, the verifier, the state and the token never appear in an exception.

**Does it work in Unity, on Android or on iOS?**
The code compiles for netstandard2.1. Android and iOS are neither Windows nor macOS, so the browser rule asks them for `DISPLAY` or `WAYLAND_DISPLAY`, as it asks Linux, and a phone sets neither: `LoginMethod.Auto` picks a device login there. The apps sign in through their own flow (`NativeSignIn.Begin` and `CompleteNativeSignInAsync`); forcing the browser there is not supported.

