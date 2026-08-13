# Changelog

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
