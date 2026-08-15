# Changelog

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
