# Changelog

## 0.9.1 — 2026-09-28

Brings the SDK to the Python reference's 0.9.1 (`d33dc2be8b00`). Every addition is new API or a subclass of an existing exception; no signature changed.

- **Sign in as a registered app.** A new `BeginDeviceLoginAsync(scopes, clientName, clientId)` overload, and `DeviceLoginOptions.ClientId` for `LoginWithBrowserAsync`, send `client_id`; without one nothing is sent. `ThalovantHome.HomeAssistantClientId` is `thalovant-home-assistant`. The approval page then shows the platform's name for the app as verified, and approving it again replaces the token it already holds. An id the API does not know is a 400 `unknown_client`. The existing overload is unchanged, since an optional parameter added to it would have broken every compiled caller.
- **Read a pending code as its approver sees it.** `DescribeDeviceLoginAsync(userCode)` (`GET /v1/auth/device/codes/{user_code}`, signed in) returns a `DeviceLoginRequest`: `Scopes`, `ClientName`, `ClientId`, `DeviceName`, `ExpiresAt`, and `ClientVerified`, which is true only when a registered app asked. An unknown, expired or answered code is a 404.
- **A hub that has spoken has accepted the key.** A close with 1000, 1005 or 1008 inside the 750 ms after the handshake is a refusal only while the hub has sent nothing that decrypts under the new session's keys. Any decrypted frame counts: JSON, WIRE-1 binary, a chunk of a larger message, or the hub's encrypted HELLO. After one, the close is a drop, as it is after the window.
- **`ThalovantClientKeyRejectedException`.** A refusal as an XX handshake ends, with nothing from the hub in between, means the hub pinned another key for this client. It is a `ThalovantHubRefusedException`, so existing catches still take it, and it names the folder this client's key is in (`KeyFolder`) and the likely other one (`OtherKeyFolder`). Its message says to re-pair or to share the key folder. `HubSession.RunAsync` gives up on it at once rather than retrying refusals for ten minutes, since no handshake can change which key the hub pinned. After KK the same close is still a plain refusal.
- **The key lives beside the identity file.** `ThalovantIdentity.FromFile` records the file on the new `SourcePath`. On .NET 8, a client given no Noise store keeps that identity's key in a `noise` folder beside the file, so every program reading the same file presents the same key. The first time the folder is used, it copies the key and hub pins from the old shared default (`LocalApplicationData/Thalovant/noise`), but only when that key has met this identity's hub. They are copied, never moved: another program may still read the old folder. The key is copied only with this hub's pin, never without it, and another hub's pin that cannot be read is left behind without stopping the copy. An identity from anywhere else uses the shared default as before, and so does one whose `noise` folder is missing and cannot be created there; a `noise` folder that already exists is kept. netstandard2.1 is unchanged.
- **Compressed WIRE-1 parts inflate to 32 MiB**, up from 1 MiB, which refused legitimate large frames, and is what the reference allows. A payload that would inflate further, or whose zlib stream is truncated or fails its checksum, refuses the frame. .NET's inflaters return what they have when the input runs out, and read a stream with its checksum cut off as whole, so the header and the Adler-32 trailer are now checked. The same holds for compressed metadata: a block that will not inflate now refuses the frame, as it does in the reference. Before, it read as absent, and a clip arrived with no language and no name.
- **`ThalovantContext.StripSsml` uses the same tag rule as the home link's speech.** It feeds `DisplayText` and the reply text. It dropped everything between any `<` and the next `>`, which mangled "5 < 6 and 7 > 3", and it did not handle comments, processing instructions or a `>` inside a quoted attribute. White space is left as it was.
- Runs the reference's new cases: `client_id` and `describe` in `device-login-vectors.json`, and the echoed-input cases in `api-error-vectors.json`. From `home-link-vectors.json` it runs the White_Space trim edges and the `queued` cases, where a reply waits behind a frame held on a real link. From `link-keeping-vectors.json` it runs the `after_authenticated_frame` closes, the `client_key_changed`, `client_key_changed_pinned_here` and `closed_after_first_frame` handshakes, and the `client_key_rejected` supervisor. The handshake cases now connect as a kept link does: the handshake, then the settle window. The loopback hub pins a client's first key as hivemind-core does. Declares `link-carriers` not applicable, since this SDK offers only the WebSocket carrier.

## 0.9.0 — 2026-09-28

Everything a Home Assistant link needs, in the four capabilities the Python reference added for it: sign in on a device, create a connection of a named kind, wait for the hub to admit it, and answer the hub's requests on the data plane -- keeping that link up included. Each is run against the reference's shared vectors: the control-plane cases through a loopback API over the SDK's own HttpClient, the home link through the real WSS transport, and the handshakes against a loopback hub that does the WebSocket upgrade and the Noise handshake for real. The results match the reference's case for case.

- **Device sign-in one step at a time.** `BeginDeviceLoginAsync` returns the code to show and the interval; `PollDeviceLoginAsync` asks once and either returns the token -- kept on `AccessToken`, with its id on the new `TokenId` -- or throws `ThalovantDeviceLoginPendingException` with the `Interval` to wait. A `slow_down` adds five seconds for good, across however many calls the caller's loop makes. An empty scope list is left out of the request, exactly as none is: the API asks for at least one and answers `[]` with a 422 (`LoginWithBrowserAsync` too). `RevokeApiTokenAsync` revokes the token this client signed in with and forgets it; revoking the token in use is idempotent, since one already revoked cannot authenticate its own revoke and the API's 401 counts as revoked, and revoking again sends nothing until the next sign-in. Revoking another token by id still throws what the API said, and a revoke forgets the token only if it is still the one revoked: a sign-in that finished meanwhile installed another. A device-token answer that is not a JSON object has no status, the same as a 2xx with no token. `ThalovantHome.HomeAssistantScopes` is what a Home Assistant link asks for, and all a Free plan can approve.
- `ThalovantDeviceAccessDeniedException` and `ThalovantDeviceCodeExpiredException` are now `ThalovantApiException`, carrying the API's HTTP 400. A handler that catches `ThalovantApiException` ahead of them now takes them first. Every sign-in -- device, password, native -- sets `TokenId` from its own token, so a default revoke never reaches for an id left over from an earlier one.
- **Connections of a named kind.** `CreateClientIdentityOptions.ConnectionType` (one of `ThalovantConnectionTypes`) is sent as `spec.connection_type`, and the API must echo it: a connection made without it is deleted before `ThalovantUnsupportedConnectionTypeException` is thrown. So is a 422 whose `detail` or `code`, or a validation error's `loc` or `msg` (under `errors`, or under a `detail` that is a list), names `connection_type` -- never an entry's `input`, which echoes the spec the SDK sent, `connection_type` and all. `BootstrapIdentityResult` gains `ClientId`, `ConnectionType` and the `Operation` that admits it. `GetClientAsync` and `DeleteClientAsync` are new; the delete reads the etag when none is given, retries once on 412, and counts 404 as deleted.
- **Refusals a caller can branch on.** Every failed control-plane call now throws the kind of refusal it is: `ThalovantAuthenticationException` for 401, 423 and 403 `Insufficient scopes`; `ThalovantPlanException` for 402 and 403 `plan_limit`; `ThalovantAlreadyLinkedException`, with the `ClientId` holding the link, for 409 `home_assistant_already_linked`; and `ThalovantApiUnreachableException` when the API could not be reached at all (it was a plain `ThalovantApiException` with no status before). `ThalovantApiException` is no longer sealed so these can derive from it, each keeps `StatusCode`, `ErrorCode`, `Detail` and `Problem`, and a catch for `ThalovantApiException` still catches every one. The new `RetryAfter` is the wait a 429 names: its body's `retry_after_seconds` (inside `detail`, where the API puts it, or at the top), else `Retry-After`, else `RateLimit-Reset`, which is all the API's own rate limiter sends with its plain-text 429.
- **Admission.** `WaitForAdmissionAsync` follows the operation a create returned: `ready` is admitted; `failed` and `timed_out` throw `ThalovantAdmissionFailedException` with the operation's `ErrorCode`; a 404, or no operation at all, returns at once. A 5xx is ridden out, and so is a 429 -- the wait shares the token's rate limit with the caller's other calls -- for its `RetryAfter` when that is longer than the poll interval, or as an immediate timeout when it is longer than the time left. A 401 or 403 is thrown as the API's own authentication error and an unreachable API as `ThalovantApiUnreachableException`, since neither says anything about the connection; any other refusal of the wait keeps what the API said on `ThalovantAdmissionFailedException.ApiError`. Every read is bounded by the deadline, every wait lasts at least what it should by the monotonic clock (Windows' timer woke a millisecond early on a one-second wait), and a link to another origin -- scheme, host and port -- is never fetched. Running out of time throws `ThalovantAdmissionTimeoutException`, whose message ends "it may still admit it later": a `ThalovantConnectionException` that is also an `IThalovantTimeout`, the new marker `ThalovantTimeoutException` implements too. `ThalovantConnectionException` is no longer sealed.
- **The home link.** `ThalovantClient.AnswerHomeRequests` and `HubSession.AnswerHomeRequests` answer every `thalovant.home.request` with at most one `thalovant.home.response`, sent as a reply, and never after the hub's ten seconds counted from the request's arrival. The handler (`HomeRequestHandler`, returning a `HomeAnswer`) runs on its own: one that throws is answered `failed_to_handle`; one still busy after `ThalovantHome.DefaultHandlerTimeout` (nine seconds), or after what is left of the ten, is answered `timeout` at that moment and cancelled, even when it ignores the token or blocks; one outside the contract is answered `unknown` -- each with empty speech, so the hub speaks its own sentence in the device's language. A reply that could only go out after the bound is withdrawn rather than sent late. `ThalovantHome.AnswerAsync(request, handler, reply, …)` applies the same bound around a transport of the caller's own. Speech goes out as plain text by the portable rules, in linear time whatever the text holds: tags (`<` or `</` and an ASCII letter, up to the next `>` outside a quoted value), comments and processing instructions removed, so "5 < 6 and 7 > 3" and an unclosed tag survive; numeric references, the five XML entities and `&nbsp;` decoded, nothing else; Unicode White_Space collapsed, and only White_Space trimmed. `ThalovantClient.ReplyAsync` and `ThalovantContext.ReplyContext` are the reply itself (OVOS-MSG-1 §5.2): a deep copy of the request's context with `source` and `destination` swapped, and no destination at all for a request that named a destination and no source.
- **A link that stays up.** `HubSession.ConnectAsync` makes one attempt and `RunAsync` keeps the link until the session closes, as `link-keeping-vectors.json` says: a drop dials again at once, a failure waits 10 s doubling to 120 s, refusals wait the same until they have lasted `HubSessionPolicy.RefusalGraceSeconds` (600) and then throw `ThalovantHubRefusedException`, and a changed hub key throws `ThalovantHubKeyChangedException` at once. A close with 1000, 1005 or 1008 during the handshake -- any step of it -- or within 750 ms after it is a refusal, as are a Noise answer that does not authenticate (a wrong password) and a 401 or 403 on the WebSocket upgrade, now on netstandard2.1 too. A pinned KK handshake that fails is followed at once by one XX attempt in the same connect: only XX tells a changed password from a changed hub key, and the pin is still checked. `OnStateChange`, `Connected`, `SettleWindow`, `ReplyAsync` and `HubSession.ForIdentity` are new; the SDK logs nothing.
- The conformance recorder digests a vendored vector file's fractional numbers as written, sorts keys by code point, writes the results file as the reference writes it (byte for byte the same file), and spells strings exactly as the reference's `json.dumps(ensure_ascii=False)` does: System.Text.Json also escaped U+2028, U+007F and the C1 controls, so `home-link-vectors.json` digested differently although every case matched. The first cut of `connection-admission-vectors.json` held fractional seconds, and the recorder's refusal, thrown at process exit, had stopped the record being written at all. The records written before any of this were complete: main's own recorder, run over main's suite, writes exactly the committed `api-errors`, `binary` and `conversation` records, and they equal the reference's on every case.
- Declares the parity contract's `device-login`, `connection-kinds`, `connection-admission` and `home-link` (with `link-keeping-vectors.json`) capabilities against reference `2460eb90bd56`, and brings `DeviceLogin.cs`, `Operations.cs` and `Provisioning.cs` under the `control` evidence they were always part of.

## 0.8.6 — 2026-09-26

- `ThalovantApiException` carries what the API said, not only the line built from it. `Problem` is the whole error body parsed, as a `JsonObject`, when it is a JSON object; `ErrorCode` is its machine-readable code; the new `Detail` is its sentence whole, exactly as sent. The message was the only place the sentence reached a caller, and it is cut at 200 characters: a `platform_image_required` refusal names every image each refused key may be instead, which is longer than that, so the list a caller needed was the part cut off -- and `refused_images`, `allowed_images` and `allowed_repositories` reached nobody who did not parse `Body` by hand. The same held for every structured refusal, `plan_limit`'s `resource`, `limit` and `used` included. The message itself is unchanged, and still never repeats a value the body echoed back from the request. Each read of `Problem` returns a new copy, so one caller cannot change what the next one reads.
- `ErrorCode` follows the rule every SDK now shares: a `code` that is not a string, is empty or is only whitespace is no code, and the code inside a `detail` that is itself an object is read instead. It used to return an empty top-level `code` as the code. `Detail` is read the same way. The constructor is unchanged, and an error code passed to it still wins.
- Response bodies are read as UTF-8 whatever the Content-Type says. The API labels its errors `application/problem+json` with no charset, and a charset added on the way would have re-decoded an accented sentence as something else. An error body that repeats a name keeps its last value, as the other SDKs' decoders do, instead of throwing `ArgumentException` out of the call.
- Declares the parity contract's new `api-errors` capability, run against the Python reference's `api-error-vectors.json`: thirteen responses served through the control plane's own request path and read back through `GetHubAsync`, with the results recorded in `contracts/conformance-results.json`.

## 0.8.5

- Automated patch release of the unreleased changes on `main` since v0.8.4.

## 0.8.4

- Automated patch release of the unreleased changes on `main` since v0.8.3.

## 0.8.3

- Automated patch release of the unreleased changes on `main` since v0.8.2.

## 0.8.2 — 2026-09-18

- A refusal ends an `AskAsync` at once instead of letting it run to the deadline. The hub sends `hive.policy.denied` the instant it refuses, with no request id, and the collector's correlation gate dropped it: the ask waited out its whole budget while a caller told somebody their hub "did not answer in time" about a question it had refused and explained. A denial with no request id is taken when it names the type this ask sent and this ask is the only utterance the client has out; a second ask, a query, or a fire-and-forget utterance still inside the shared 10-second grace window makes it ambiguous, so neither takes it.
- `AskAsync` throws `ThalovantPolicyDeniedException` with `Quota` -- `Period`, `Limit`, `Used`, `ResetAfter` -- for a spent `intent_quota_exceeded`, and a message that fits the refusal rather than offering allow-list advice for a spent day or for `backend_unavailable`.
- An unmatched intent throws the new `ThalovantUnansweredException`. Both remain `ThalovantRuntimeException`, so a caller catching that still catches these.
- `ThalovantUnansweredException.Said` carries what the person said. Both event names put the input in the event's text; the old read of `reason`/`error` left it empty.
- A fire-and-forget utterance whose publish never happened is dropped again, rather than suppressing a real refusal for the rest of the grace window.
- A refusal on a quota the hub sent no numbers for says a quota has run out, rather than claiming "all questions used".
- Quota counts read every numeric shape a `JsonValue` holds -- `TryGetValue<T>` coerces nothing, so `int`, `uint`, `ulong`, `byte`, `short`, `decimal`, `double`, `float` and numeric strings each answer only their own -- and are whole, non-negative and inside a signed 64-bit integer. Reading only `long` made a quota assembled in memory come back as zeros, which a new test caught.
- Declares the parity contract's new `refusal` capability, run against the Python reference's `refusal-vectors.json`.

## 0.8.1

- Automated patch release of the unreleased changes on `main` since v0.8.0.

## 0.8.0

- Carry the conversation between the turns of a named session. A hub keeps nothing for a named session -- OVOS-SESSION-2 §2.2 makes the orchestrator stateless for those -- so whatever a turn activated is discarded the moment it ends, and every follow-up fell past the converse pipeline to the fallback. `ThalovantContext.CarryConversation` and `ConversationSessionFields` carry conversation state only, by allow-list: never `lang`, which would pin a bilingual conversation to whichever language it opened in. The state is remembered under the session id the request used *and* the one the hub answered with, because `ThalovantReply.SessionId` hands the caller the latter.
- Speak the rest of the HiveMind protocol. `OnHive(kind, handler)` listens to the five hive kinds -- `broadcast`, `propagate`, `escalate`, `intercom`, `rendezvous` -- and `PropagateAsync`, `EscalateAsync` and `BroadcastAsync` send. A refusal is a disconnection rather than an error: a hub's HELLO says nothing about what a client may do, so nothing can check first.
- Receive binary frames. This is how a hub answers `speak:synth`: it renders the utterance and sends the audio back, so a client with no synthesiser of its own can still speak, and it is how a file arrives. The Noise framing marks each frame JSON or not, and a frame marked binary was refused outright -- "Binary HiveMind payloads were not negotiated" -- so every one of them was unreachable. `HiveWire.DecodeBinaryFrame` reads WIRE-1 properly, including the four payload-type bits and the clip after them, and `OnBinary(handler)` delivers it.
- Read the metadata of a frame the hub chose to compress. The encoder picks per frame whichever of the two is shorter, so compressed metadata is not an edge case; the clip itself is never inflated whatever the flag says.

## 0.7.2

- Automated patch release of the unreleased changes on `main` since v0.7.1.

## 0.7.1 — 2026-09-13

- Expose advisory reply claim status and first-seen pipeline/skill identifiers, with shared conformance for fallback, mixed stages, legacy hubs and malformed stamps. Existing reply construction remains compatible.

## 0.7.0 — 2026-09-13

- Add managed hub sessions with persistent subscriptions, bounded background retry backoff, terminal close, and no automatic replay of admitted requests. Preferred-origin selection delegates address binding and failed-attempt cleanup to the transport builder.
- Add presentable skill/intent inventories, regional example selection, tri-state catalogue locale support, and private best-effort inventory caches. Explicit language order survives JSON serialization across SDKs; invalid cache records become misses.
- Match Python question detection, including unnamed-locale patterns and Unicode question marks, with shared executable conformance vectors.
- Require reviewed Python reference and consumer evidence in PR and publishing parity checks, with scheduled fresh-dependency conformance checks.

## 0.6.1 — 2026-09-12

- Compare and trim sentence punctuation as full Unicode scalars, preserving supplementary letters that share a surrogate with a punctuation mark.

- Refresh bundled listing rules to thalovant-languages 0.2.1, matching Python 0.6.8 across 270 languages. Preserve regional inheritance and the corrected French/Spanish trailing-word behavior.
- Regenerate public-reference cases for every shipped locale, including Spanish questions and French complete phrases.

## 0.6.0 — 2026-09-12

- Add locale-aware sentence listings, canonical slot examples, fuller phrase ranking and OVOS-compatible regional language selection.
- Snapshot custom language data, preserve selected locale and count unique rendered examples toward limits.
- Bound question regex evaluation and leave failed rules unpunctuated. No new runtime package dependency.

## 0.5.0 — 2026-09-12

- Match Python 0.6.3 request hints, location construction, ordered embedded audio replies, strict bounded hex decoding, and speakable intent examples with original phrase priority.
- Default runtime configuration updates to revision-guarded deep merges. Retry only HTTP 412 (three attempts maximum); fail before writing against older servers. Explicit replacement remains available. Merging now requires both hubs:read and hubs:write scopes, plus a paid plan.
- Add regression coverage for conflict preservation, retry limits, unsupported revisions, audio bounds, caller context preservation, and example ranking.

## 0.4.0 — 2026-09-12

- Reject malformed operation fields with SDK errors and prevent a rounded timer from admitting an extra status request.

- Add hub-addressed skill listing, history, install, update and removal.
- Add optional bounded polling and explicit operation resumption without repeating accepted writes.
- Document shared-runtime scope, authorization and cancellation behavior.

## 0.3.3

- Preserve a description timeout when earlier replies contain only empty or refused definitions; fully answered empty inventories still succeed.

- Redact recognized credential fields recursively in default bootstrap and identity metadata displays, including case, underscore, and hyphen variants; preserve explicit secret serialization and reference fields.

- Preserve successful engine-manifest fallback replies when another engine is policy-denied or silent; propagate caller cancellation and fail when every engine is unavailable.
- Reject duplicate active Ask request IDs and Query IDs on the same client before subscribing or dispatching; preserve separate namespaces and remove reservations on collector cleanup.
- Correct marketplace desired-state examples and fallback permission documentation; restore the missing 0.1.8 and 0.1.9 historical release notes.

## 0.3.2

- Keep admitted encrypted frame sequences owned through caller cancellation, under an independent 20-second physical send budget. Physical timeout or a real write error retires only the captured socket generation.
- Report the actual invalid reply-window parameter and run the full target-framework test suites sequentially to avoid competing cold Noise handshakes.

- Collect terminal query replies while admitted writes retain transport ownership; surface write failures throughout Ask reply phases while preserving terminal/deadline precedence.
- Apply one Ask timeout across connection admission, authentication, sending and
  reply collection. Clip fixed empty-reply and settling windows to that deadline.
- Freeze Ask collection on policy denial or query timeout, retaining only speech
  received before the hard failure, and interrupt optional waits immediately.
- Return the first correlated runtime session ID from Ask and Query while preserving strict request
  correlation and the original request ID.
- Add regressions for blocked connection/send, clipped reply phases, cancellation
  cleanup and explicit event-stream buffer overflow.
- Retire a cancelled or expired event-stream subscription even while its consumer
  pauses between reads; preserve caller cancellation in all Ask gate waits.

## 0.3.1

- Validate both device authorization URLs before displaying a prompt, invoking
  a browser callback, or polling. Accept only HTTP(S) URLs with a host and no
  userinfo, raw whitespace, or control characters; launch with a single URL argument.

- Reject automatic control-plane redirects so 307/308 responses cannot replay
  login credentials to another endpoint.
- Require HTTPS for authorization and request bodies except explicit loopback
  development hosts, and reject URL userinfo without disclosing its contents.
- Enforce redirect policy for injected HTTP transports and add real loopback
  redirect regressions plus validation before I/O.

## 0.3.0

- Add scoped conversations, direct HiveMind query/cascade replies, bounded event
  streams and waits, action/code input helpers, and local connection/health diagnostics.
- Fall back to engine intent names when the detailed listing is silent as well
  as denied. Discover fallback handlers with a bounded optional probe and expose
  known/unknown discovery plus conservative language answerability.
- Keep query collection open after soft intent misses, recover on later speech,
  and retain partial speech when a policy denial or query timeout terminates it.
- Ignore foreign correlated denials and describe replies; retain content-based
  describe matching only when a reply carries no request id.
- Bound queued connect admission by the caller's deadline without cancelling
  another caller's socket. Recheck transport readiness before reusing the client.
- Run the full unit suite on .NET 8 and .NET 9 (Ubuntu and Windows), compile the
  Unity-compatible netstandard2.1 target, and add independent pinned Node
  XX-to-KK loopback interop with encrypted messages and scoped queries.

## 0.2.1

- Publish Noise static keys and hub pins only after a private same-directory temporary file is completely written and flushed. Interrupted writes cannot leave a truncated trusted key; existing keys and pins are never replaced automatically.
- Preserve cross-process locking and validate write/flush failures, process termination during publication, and competing pin creation.
- Correct the packaged README to describe the current 0.2.x WSS-only transport scope.

## 0.2.0

- Bind callbacks, queued sends, handshake completion, and failure cleanup to their owning socket under the same lifecycle lock. Delayed activity cannot reset a replacement connection.

- Implement HiveMind v3 Noise WSS (XXpsk2 and pinned KKpsk0, AESGCM/SHA256), Argon2id PSK derivation, persistent client static keys and server pins, and bounded authenticated binary JSON framing.
- Reject legacy downgrade, unauthenticated application messages, changed pins, replay and malformed chunk sequences; clear ephemeral state on reconnect and failure.
- Add `IHiveMindNoiseStore`/`HiveMindFileNoiseStore` for app-private persistence. Unity/netstandard2.1 requires explicit secure storage; .NET 8 provides private filesystem defaults.
- Preserve net8.0/netstandard2.1 and zero additional runtime packages using the documented Bouncy Castle source subset. HTTP/MQTT runtime implementations remain unsupported.
- Validate independent Node/noble transcript vectors, upstream Argon2/AEAD values and in-memory WebSocket peers, including encrypted ask/reply, reconnect and negative readiness cases.

## 0.1.13

- `ListIntentsAsync` throws `ThalovantRuntimeException` when the hub answers
  `ovos.intent.list` with `ok: false`, instead of reading the missing `intents`
  key as an empty list. A refused listing is not an empty hub, and reporting it
  as no intents showed a person a device that can do nothing; the exception
  carries the hub's `error` text. `DescribeIntentAsync` keeps returning an empty
  list for `ok: false`, which there is a real answer: the hub does not know that
  registration, so the intent simply has no sentences. Reported by the Kotlin
  port's review, fixed in the Python reference as 0.4.40.
- `ThalovantPolicyDeniedException.Allowed` keeps the hub's `allowed` list as the
  platform contract words it: non-empty string entries, trimmed. A number or a
  null there is not a message type, and stringifying one put `"3"` or `"true"`
  in front of an operator reading which types to allow; a blank entry names
  nothing at all. Reported by the Kotlin port's review; the reference settled on
  the same rule in 0.4.41.

## 0.1.12

- Add the intent inventory: `ThalovantClient.IntentsAsync(languages)` reads the
  hub runtime's intent manifest (OVOS-INTENT-4 §10) over the client's own
  session and returns a `HubIntentInventory` — every intent each skill
  registered, per language, with the sentences a person says to reach it as the
  skill's locale files wrote them, `{slot}` placeholders included. No
  control-plane credential is involved. `ListIntentsAsync(lang)` and
  `DescribeIntentAsync(skillId, intentName, lang)` expose the two underlying
  queries (`ovos.intent.list` / `ovos.intent.describe`) as `IntentRegistration`
  rows and `IntentDefinition`s; `IntentInventoryOptions`, `IntentListOptions`,
  and `IntentDescribeOptions` carry the timeout and switches. The models
  (`HubIntentInventory`, `HubSkillIntents`, `HubIntent`) offer `PhrasesFor`,
  `Examples`, and `ToJsonObject()`.
- Queries are correlated by `context.request_id` like every other request, and
  a reply delivered more than once is taken once. Describes are sent together
  and matched by request id, or by the definition's own
  `skill_id`/`intent_name`/`lang` for a hub that does not echo the id; a
  describe that never comes leaves that intent without sentences rather than
  failing the inventory. Language tags compare case-insensitively with `_`/`-`
  folded (`ThalovantContext.SameLanguage`).
- Add `ThalovantPolicyDeniedException` (a `ThalovantRuntimeException`, which is
  no longer `sealed`), thrown at once from the hub's `hive.policy.denied` with
  `DeniedType`, `Code`, `Reason` and the `Allowed` list, instead of waiting for
  a timeout. `IntentInventoryOptions.Fallback` (on by default) falls back to the
  engines' own manifests (`intent.service.adapt.manifest.get` /
  `intent.service.padatious.manifest.get`) when `ovos.intent.list` is refused;
  the result then carries names only, `Source` `engine-manifests`, and `Denied`
  naming the refused query.
- A runtime that attaches each row's `definition` to `ovos.intent.list` when
  asked with `include_definitions` is used as such; one that does not is
  described row by row.
- A describe window that receives no reply contributes nothing instead of
  discarding the other windows' definitions; the call fails only when no window
  produced one, so a hub silent from the start still fails at the first window.
  Windows are contiguous slices, so without this an unresponsive skill with more
  than one window's worth of intents turned the whole inventory into a timeout
  while the same skill with fewer intents only lost its sentences. Reported by
  the Rust port's review.
- Send describes in batches of at most 32, each batch its own subscription
  window, instead of putting every request in flight at once. A hub with 69
  intents in two languages is 138 requests and, with every reply delivered
  twice, 276 inbound events; an SDK whose reply queue is bounded drops replies
  past its capacity and returns an inventory missing sentences. Reported by the
  Rust port's review. The per-batch deadline also means a hub that answers
  nothing fails after one batch rather than holding every request open.
- The four points the ports settled with the reference (Python SDK 0.4.37):
  `HasPhrases` is true only when at least one intent carries at least one
  sentence; the languages given to `IntentsAsync` are trimmed and folded before
  asking, so `en-us`, `en-US` and `en_us` are one language asked once and
  `Languages` keeps the first spelling; an intent registered under both engines
  keeps the template row's sentences whichever order the rows arrive in, and
  the first row seen names its `Engine`; on the names-only fallback the first
  engine to name an intent decides its `Engine` (`adapt` is asked first).
- `ThalovantEvents` gains the constants `IntentList`, `IntentListResponse`,
  `IntentDescribe`, `IntentDescribeResponse`, `AdaptManifestGet`,
  `AdaptManifest`, `PadatiousManifestGet`, and `PadatiousManifest`.
- Internal: the data-plane client now talks to its transport through an
  `IHiveMindBus` seam so the test suite can drive `IntentsAsync` (and
  `AskAsync`) against a fake hub, network-free. No public constructor changed.

## 0.1.11

- Automated patch release of the unreleased changes on `main` since v0.1.10.

## 0.1.10

- Automated patch release of the unreleased changes on `main` since v0.1.9.

## 0.1.9

- Treat intent misses as soft failures and allow a correlated fallback speech
  reply during the empty-reply wait before surfacing an unrecovered miss.

## 0.1.8

- Recognize `ovos.intent.unmatched` alongside `complete_intent_failure` in Ask
  replies. Version 0.1.9 subsequently added the fallback grace period.

## 0.1.7

- Automated patch release of the unreleased changes on `main` since v0.1.6.

## 0.1.6

- Automated patch release of the unreleased changes on `main` since v0.1.5.

## Unreleased

- **Security (F1):** `BootstrapIdentityResult.ToJsonObject()` now redacts the
  secrets of the passed-through `hub`/`client` resources in its default
  (non-secrets) form, the same way the identity is already redacted. Previously
  the default form returned the raw `client` from `POST /v1/clients`, leaking
  the `initial_identify` credentials (`access_key`, `password`, `crypto_key`,
  `mqtt.password`, and the broker `username`/`broker_username`, which can equal
  the access key), the `initial_identify_token`, the echoed `spec` (`apiKey`,
  `password`, `cryptoKey`), any secret-named keys in arbitrary `metadata`, and
  `user:pass@` credentials embedded in endpoint URLs. Identity `metadata` is
  scrubbed the same way. The `includeSecrets: true` path is unchanged and still
  returns the raw credentials.
- **Security (F9):** `ThalovantApiException` messages for failed control-plane
  requests (including `auth/token`, `auth/device/token`, and `POST /v1/clients`)
  now carry only the HTTP status plus, when present, a known human-readable field
  of a JSON error envelope (`detail` string, `detail.message`/`code`, the `msg`
  strings of a FastAPI validation-error array, or `message`/`error`/`title`/
  `code`) — never arbitrary or reflected response-body text. A 4xx that echoes
  the request (for example a 422 whose `input` reflects the `apiKey`/`password`/
  `cryptoKey` the SDK generated) can no longer launder those secrets into the
  message. The full body stays on `ThalovantApiException.Body` and still feeds
  `ErrorCode`.
- **BREAKING:** removed the admin analytics surface. `AnalyticsOverviewOptions`
  no longer exposes `Admin` or `OwnerId`, and `AnalyticsOverviewAsync` no longer
  calls `GET /v1/admin/analytics/overview` — it always uses the workspace
  `GET /v1/analytics/overview`. Code that set `Admin`/`OwnerId` will no longer
  compile.
- The secret-bearing types (`ThalovantIdentity`, `MqttBrokerCredentials`,
  `BootstrapIdentityResult`) are documented and tested as plain `sealed` classes
  with no `ToString()` override, pinning that behavior so a future refactor to
  `record` (whose synthesized `ToString()` would leak secrets) fails the tests.
- Docs: clarified that `BootstrapIdentityResult.ToJsonObject()` (default) is the
  redacted, safe-to-log form while only `includeSecrets: true` returns
  credentials, and made the netstandard2.1 identity-file permission-check skip
  explicit in source (no portable file-mode API before net7.0).

## 0.1.5

- Hub provisioning on `ThalovantControlPlane`: `CreateHubAsync`,
  `UpdateHubAsync`, `DeleteHubAsync`, `ReleaseHubAsync`, `SetHubRatingAsync`,
  `ClearHubRatingAsync`, and `GetHubRuntimeCapabilitiesAsync`, with the
  `CreateHubOptions`, `UpdateHubOptions`, and `ReleaseOptions` bodies.
- Runtime groups: `ListRuntimeGroupsAsync`, `GetRuntimeGroupAsync`,
  `CreateRuntimeGroupAsync`, `UpdateRuntimeGroupAsync`,
  `GetRuntimeGroupConfigAsync`, `UpdateRuntimeGroupConfigAsync`,
  `ReleaseRuntimeGroupAsync`, `DeleteRuntimeGroupAsync`,
  `InstallRuntimeGroupSkillAsync`, and `UninstallRuntimeGroupSkillAsync`, with
  `CreateRuntimeGroupOptions`, `UpdateRuntimeGroupOptions`, and
  `InstallRuntimeGroupSkillOptions`.
- Skill discovery: `ListMarketplaceSkillsAsync` (with
  `MarketplaceSkillListOptions`), `ListRuntimeGroupMarketplaceAsync`, and
  `ListRuntimeGroupInventoryAsync`.
- `UpdateHubAsync` and `DeleteHubAsync` take `etag` as a **required**
  parameter, not an option: the API compares it as `If-Match` against the hub's
  current etag and answers HTTP 412 `ETag mismatch` when it is stale *or
  absent*. No runtime-group route reads `If-Match`, so none of them take an
  etag.
- `CreateHubAsync` sends a generated `Idempotency-Key` unless
  `CreateHubOptions.IdempotencyKey` supplies one, so a retried create after a
  timeout returns the first hub instead of making a second. It is the only
  route in this surface that reads the header; runtime-group creates and skill
  installs do not, and the SDK does not send one there.
- The provisioning writes are paid-gated (`hubs:write` plus a paid plan) and
  answer HTTP 402 `API access requires a paid plan.` on the free tier; a
  missing scope answers HTTP 403 `Insufficient scopes` first. The hub rating
  routes need `hubs:write` but are **not** paid-gated, and the discovery reads
  need only `hubs:read` (marketplace catalog) or `hubs:inspect` (group-scoped
  reads and hub runtime capabilities) and are likewise not paid-gated.
- `GetHubRuntimeCapabilitiesAsync` is the only read here that answers HTTP 409
  when no client is connected. `ListRuntimeGroupInventoryAsync` returns an
  empty `data` list with a pending `source` instead, and
  `ListRuntimeGroupMarketplaceAsync` still returns the catalog.
- `MarketplaceSkillListOptions.OwnerId` and `IncludeInactive` are admin-only
  and are *silently* ignored for other callers rather than rejected, so a 200
  is not proof they applied.

## 0.1.4

- Derive the user agent from the assembly version so the csproj `<Version>` is
  the single place in the repository that names it; `ThalovantDefaults.UserAgent`
  became `static readonly` rather than a compile-time `const`.

## 0.1.3

- Correct the 429 guidance: `ThalovantApiException` exposes the status code, raw body, and decoded error code but not response headers, so read `retry_after_seconds` from the body instead of the `Retry-After` header.

## 0.1.2

- Fix the CI token example in the README to read `THALOVANT_API_TOKEN`, the
  environment variable name used by the other Thalovant SDKs and the MCP
  server. The previous `THALOVANT_TOKEN` matched nothing else in the family.
  The SDK reads no environment variable itself, so this is a documentation
  fix only.
- Document the two per-plan API token 429s: `token_rate_limited` for the
  per-minute rate and `token_quota_exceeded` for the daily or monthly call
  quota (the body names the `quota`, `limit`, and `used`). Both carry a
  `Retry-After` header and `retry_after_seconds`. The SDK does not retry
  automatically.

## 0.1.1

- `ThalovantControlPlane.LoginWithBrowserAsync`: browser device-flow sign-in
  for accounts without a password (for example Google sign-in). Requests a
  device authorization (`POST /v1/auth/device/authorize`), prompts with the
  plain `verification_uri` and `user_code` (or calls a custom
  `DeviceLoginOptions.Prompt`), best-effort opens the browser at
  `verification_uri_complete`, and polls `POST /v1/auth/device/token`
  honoring the server `interval` and `slow_down` back-off (+5s) until
  approval, denial, expiry, or the `Timeout` (default 15 minutes). On
  approval the durable scoped API token is stored on `AccessToken` exactly
  like `LoginAsync`, and a typed `DeviceLoginResult` (`AccessToken`,
  `TokenType`, `Scopes`, `ExpiresAt`, `TokenId`, `Raw`) is returned.
- New exception types `ThalovantDeviceAccessDeniedException` and
  `ThalovantDeviceCodeExpiredException`; polling past the timeout throws
  `ThalovantTimeoutException`, and cancellation surfaces as
  `OperationCanceledException`.
- Documented pre-provisioned token auth for CI:
  `new ThalovantControlPlane(accessToken: ...)` or setting the `AccessToken`
  property directly (already supported since 0.1.0).

## 0.1.0

Initial release of the Thalovant .NET SDK for enterprise .NET (`net8.0`) and
Unity (`netstandard2.1`). Single NuGet package (`Thalovant.Sdk`) with zero
external runtime dependencies (the netstandard2.1 target references only the
System.Text.Json package).

- `ThalovantControlPlane`: `LoginAsync` with optional `scope`/`otpCode`/`recoveryCode`
  (MFA fields are sent as `otp_code`/`recovery_code` only when provided;
  MFA-enabled accounts receive HTTP 401 `mfa_required` without one), hubs and
  public hubs (public discovery is unauthenticated), typed `GetOperationAsync`,
  memory list/summary/create/get/update/delete with all documented filters,
  `AnalyticsOverviewAsync` with the 13 filters and the admin endpoint switch
  (`owner_id` admin-only), and `CreateClientIdentityAsync` with an
  `Idempotency-Key` header, `Active` option, and `initial_identify` parsing.
- `ThalovantIdentity` and `MqttBrokerCredentials` matching the API client
  identify schema, with JSON and secure-file loading (POSIX 600 enforcement
  on the net8.0 target; skipped on Windows) and secret-redacting
  serialization.
- Hub protocol settings (`spec.protocols.{wss,http,mqtt}.enabled`, WSS enabled
  by default) and `data_plane_endpoints` selection with the `wss`, `https`,
  `mqtt` preference order.
- `ThalovantClient` data plane v0.1 over WSS (`ClientWebSocket`):
  authorization query credential, preshared-key handshake with plaintext
  `hello` reply, AES-128-GCM encrypted HiveMessage frames (pure managed
  cipher supporting the 16-byte HiveMind nonce, byte-compatible with the
  Node, Go, and Swift SDKs), `AskAsync` with request-id correlated reply
  aggregation, event handler registration, and `CloseAsync`. HTTPS and MQTT
  data-plane transports throw `ThalovantUnsupportedProtocolException`.
- `ThalovantApiException` with HTTP status code, raw body, and decoded error
  code, plus connection/timeout/runtime/identity/protocol exception types.
