# api

Backend API for CampusPulse, shared by the [`mobile`](https://github.com/CampusPulse-ro/mobile)
and [`web`](https://github.com/CampusPulse-ro/web) clients.

- **Stack:** .NET 10 (ASP.NET Core) · PostgreSQL 18 · EF Core 10 (Npgsql)
- **Contract:** [`openapi/v1.json`](openapi/v1.json), committed. The clients generate their typed
  API client from it (see [API contract](#api-contract)); the running API also serves it at
  `/openapi/v1.json` in every environment.

## Layout

```
src/CampusPulse.Domain          entities + pure logic. No dependencies, no EF, no web.
src/CampusPulse.Infrastructure  EF Core, DbContext, (later) migrations.
src/CampusPulse.Api             ASP.NET Core host, DI wiring, endpoints.
tests/CampusPulse.Domain.Tests  unit tests for the pure logic.
tests/CampusPulse.Api.Tests     in-process tests of the HTTP surface (WebApplicationFactory).
```

## Configuration

Environment variables only:

| Variable | What |
|---|---|
| `ConnectionStrings__Postgres` | Npgsql connection string. Required: startup fails fast without it. The database does not have to be reachable for the API to start. |
| `Cors__AllowedOrigins__<n>` | browser origins allowed to call the API; `https://*.example.com` allows any subdomain (Cloudflare PR previews). None set = no browser origin allowed |
| `Logging__File__Path` | also write the logs to this file (see [Logs](#logs)). Unset = stdout only. If set and the file can't be written, startup fails |
| `Logging__File__FileSizeLimitBytes` | roll the file at this size; default 10485760 (10 MB) |
| `Logging__File__RetainedFileCountLimit` | files kept, the live one included; default 10 |
| `Logging__LogLevel__<category>` | the usual .NET log levels, for stdout and the file alike; defaults in `appsettings.json` (Information, `Microsoft.AspNetCore` at Warning, `Microsoft.AspNetCore.DataProtection` at Error: sign-in registers Data Protection, which nothing uses (bearer tokens only), and with no home directory, as for the Pi's service users, it warns at every start that its keys are in memory. Lower it again once a feature protects data across restarts) |
| `Auth__TenantId` | UPT's Entra tenant id (`6bb41fe4-40a3-4a10-b6cd-38278e78b21a`). With `Auth__ClientId` unset too, sign-in is off and every endpoint that needs it answers 401 (see Sign-in) |
| `Auth__ClientId` | the app registration's client id (one registration for web, mobile and the API) |
| `HTTPS_PROXY`, `NO_PROXY` | on the Pi, the egress proxy the API reaches Microsoft and Expo through (see Sign-in, Push notifications) |
| `Push__Expo__AccessToken` | optional: an Expo access token, sent as `Authorization: Bearer` to Expo's push API. Needed only once "enhanced security for push notifications" is turned on for the Expo project |

## Sign-in

Students sign in with their UPT Microsoft account. The clients (MSAL in web, expo-auth-session in
mobile) get an access token for this API from UPT's tenant and send it as
`Authorization: Bearer …`. Web, mobile and the API are **one app registration**, so one client id.

**Every endpoint needs a token** except `/`, `/health*` and `/openapi/v1.json`: a fallback policy
protects anything not marked `.AllowAnonymous()`, so a new endpoint is protected by default, and the
contract marks every other operation as needing the bearer token. The API accepts a token only if it:

- is signed with RS256 by Microsoft (keys from the tenant's metadata);
- has the issuer `https://login.microsoftonline.com/<tenant>/v2.0` exactly (v2 tokens; see below);
- has an audience of the client id or `api://<client-id>`, and was issued to this app (`azp` = the
  client id);
- has `tid` equal to the tenant, and no `idp` other than the tenant's own: **guests are refused**;
- has `access_as_user` in `scp`, so app-only tokens and ID tokens are refused;
- has an `oid` and hasn't expired.

Anything else is 401, logged at Warning with the reason (never the token).

- **Who gets in:** every member of the tenant, students and staff alike. Students only would need a
  further rule (for example on the account's domain or a group); that's a product decision.
- `POST /me` creates the signed-in student's account (keyed by `oid`), or returns the one they
  have: **201** when it was created, **200** when it existed, with the body `{ id, email,
  displayName }` either way.
- `GET /me` returns that same body, and **never creates an account**: no account answers **404**
  (ProblemDetails). Both refresh email and name when the token carries them.
- `DELETE /me` deletes the signed-in student's account and everything stored about them (see
  [Data](#data)) and answers **204**, also when there was nothing to delete, so retrying is safe.
- **What clients must do:**
  - Call `POST /me` once, right after an **interactive** sign-in. Never on app start or after a
    silent token refresh. Otherwise a second device that still holds tokens would silently re-create
    an account the student deleted on the first.
  - Treat a **404 from `GET /me`** as "this account no longer exists" (deleted, maybe from another
    device): sign out locally, drop cached data and show sign-in.
  - After a **204 from `DELETE /me`**, sign out locally right away: drop the tokens and the cached
    data. The access token stays valid until it expires.
  - Send a random `X-Request-Id` per request, or none, never one fixed per install: a stable id
    would tie all of a student's log lines together, a deleted student's included.
- **Unconfigured** (no `Auth__TenantId` / `Auth__ClientId`): the API still starts and logs a
  warning, and every endpoint that needs sign-in answers 401. It never lets a request through
  anonymously.
- **At startup** the API loads the tenant's metadata in the background: one Information line if it
  can, an Error if it can't reach Microsoft. It still starts, and requests with a token answer 500
  (a server fault, so clients don't send the student back to sign in) until the metadata loads.
- **Clients** use the authority `https://login.microsoftonline.com/<tenant>` (not `/common`, which a
  single-tenant app refuses with AADSTS50194) and request the scopes
  `openid profile offline_access api://<client-id>/access_as_user`. Without `profile` the token may
  lack the name and email; `offline_access` gives the refresh token.

**Registering the app (once, by Sebastian, signed in with his `@student.upt.ro` account):** a test
on 2026-10-14 showed UPT lets students register apps and consent to their own organisation's apps.

1. entra.microsoft.com → App registrations → New registration: name `CampusPulse`, **single tenant**
   (UPT accounts only).
2. **Authentication:**
   - Single-page application redirect URIs: `http://localhost:5173`,
     `https://staging.campuspulse-web.pages.dev` and `https://campuspulse.thorsp.net`.
   - Mobile and desktop applications: `campuspulse://auth`. Any app on a phone can claim a custom
     scheme; PKCE (expo-auth-session's default) is what keeps a code caught there useless.
   - **Allow public client flows: No.** The SPA and mobile flows don't need it; it only enables
     device-code and password flows.
   - PR previews (`pr-<n>.campuspulse-web.pages.dev`) can't be registered one by one, so they can't
     sign in.
3. **Expose an API:** set the Application ID URI to the default `api://<client-id>`, and add the
   scope `access_as_user` (who can consent: admins and users).
4. **Manifest:** set `"requestedAccessTokenVersion": 2` (in `api`). Without it Entra issues v1
   tokens, whose issuer is `https://sts.windows.net/<tenant>/`, and the API refuses them.
5. **On the Pi:** add `Auth__TenantId=6bb41fe4-40a3-4a10-b6cd-38278e78b21a` and
   `Auth__ClientId=<client-id>` to both env files
   (`/srv/campuspulse/.config/campuspulse/api-{stage,prod}.env`), then restart the services. The
   client id isn't a secret; record it in the Pi's credential registry when it's created. The Pi's
   network guard lets the API out only through the egress proxy, so the env files also need
   `HTTPS_PROXY=http://127.0.0.1:8899` and `NO_PROXY=127.0.0.1,localhost` (see
   [deploy/pi/README.md](deploy/pi/README.md)), and the proxy's allow list must hold
   `login.microsoftonline.com`. Without them the startup check logs an Error and every signed-in
   request fails.

## Running it

```bash
docker compose up -d                       # Postgres on 127.0.0.1:5434
dotnet run --project src/CampusPulse.Api   # http://localhost:5280 (connection string from launchSettings.json)
```

- `GET /health` → `{"status":"ok"}` (liveness; does not touch the database)
- `GET /health/ready` → `{"status":"ok"}`, or **503** `{"status":"unavailable"}` when the database
  can't be reached within 3 s (readiness; the deploy checks this one)
- `GET /openapi/v1.json` → the API contract

## API contract

`openapi/v1.json` is written by the build (`Microsoft.Extensions.ApiDescription.Server`) and
committed. **Regenerating it is just `dotnet build`**: commit the changed file with the change that
caused it. If the file didn't change after an API change, run `dotnet build --no-incremental`. CI
deletes the committed copy, rebuilds it and fails when the result differs ("run dotnet build and
commit openapi/v1.json"); that run also uploads the regenerated file as the `openapi-v1` artifact,
so the fix can be "download and commit". Never edit it by hand. Only per-architecture builds (for
example the linux-arm64 migrations bundle) skip writing it.

The SDK is pinned by `global.json` (10.0.4xx band, latest patch), so every machine and CI generate
the same JSON.

The clients read it straight from GitHub, so they need no token:

```bash
bunx openapi-typescript https://raw.githubusercontent.com/CampusPulse-ro/api/main/openapi/v1.json \
  -o src/api/schema.d.ts
```

and call the API through [`openapi-fetch`](https://openapi-ts.dev/openapi-fetch/) with those types.
`main` in that URL can be a release tag (`…/api/v0.2.0/openapi/v1.json`) to pin a client to a
released contract. List every endpoint change in the PR's "API contract" section, since the
clients follow `main`.

**Writing endpoints so the generated types are right:**

- Write response DTOs as positional records (`record HealthResponse(string Status)`) or with
  `required` members; otherwise their fields come out optional (`?`) in TypeScript.
- Integers are serialized as numbers only (`JsonNumberHandling.Strict`) and enums by their
  camelCase name (`JsonStringEnumConverter` with `JsonNamingPolicy.CamelCase`: `PushPlatform.Ios` is
  `"ios"`), so they become `number` and string unions, not `number | string`.
- Request bodies are read as strictly as the contract describes them: every constructor parameter of
  a body record must be present (`RespectRequiredConstructorParameters`) and an enum is accepted only
  by name, never as a number. A body that can't be read is a **400** ProblemDetails (an empty 400 gets
  one from `UseStatusCodePages`), in every environment.
- Declare every response (`.Produces<T>()`, `.Produces<T>(503)`) so clients get a type for it.

## Data

- `users`: one row per student who has signed in (`oid`, email, name, first and last seen).
- **Everything about a student refers to their `users` row with ON DELETE CASCADE.** `DELETE /me`
  deletes that row, and the database deletes the rest with it: settings, device push tokens,
  Campus Virtual tokens, any later table. In EF that's `.OnDelete(DeleteBehavior.Cascade)`, the
  default for a required relationship; `ClientCascade` is not enough, because the delete doesn't
  go through EF's change tracker. The rule covers the whole tree under `users`: a table that
  refers to a per-user table (device tokens under devices) cascades from it too.
  `PerUserDataTests` fails the test run if any key on a path from `users` doesn't cascade, both in
  the EF model and in the migrated database (`pg_constraint`), so a key written by hand in a
  migration is checked too.
- **Per-user data must refer to `users` by foreign key.** A column that only stores the oid or the
  email, with no foreign key, isn't covered: nothing deletes it. Data that can't have a key (a file,
  another service) needs its own step in `AccountDeletion`, added in the same PR as the data.
- `push_devices`: a student's phones, by Expo push token (unique), with platform and first and last
  registration (see [Push notifications](#push-notifications)). `push_receipts_pending`: tickets of
  sent notifications whose receipt hasn't been read yet, under their device. Both are in the cascade
  tree: `users` → `push_devices` → `push_receipts_pending`.
- `account_deletion_counts`: the number of accounts deleted per UTC day, added to in the same
  transaction as the delete. It holds no identifier. The deletion's log line (`Account deleted`)
  names no one either, but like every line of the request it carries the request's scope: request
  id, trace and span ids, connection id and path.
- **Backups:** the production backups dump this database nightly and keep 7 daily, 5 weekly and 12
  monthly copies, plus a weekly copy on a second machine. A deleted account's row therefore
  survives in backups for up to about a year after the delete.
  - Open: whether to shorten that retention for this database (the owner's decision).

## Push notifications

Sent through [Expo's push service](https://docs.expo.dev/push-notifications/sending-notifications/)
(`IPushSender`, implemented by `ExpoPushSender`; FCM/APNs directly would be another implementation).
Every endpoint needs sign-in, and the first and last need an account (404 like `GET /me` otherwise).

- `POST /me/devices` `{ token, platform }` (`platform` is `"android"` or `"ios"`): registers the
  app's Expo push token for the student. **201** when it's new, **200** when it was known (its
  last-registered time is refreshed). **400** ProblemDetails unless `token` looks like
  `ExponentPushToken[…]` or `ExpoPushToken[…]` (letters, digits, `_`, `-`), and **400** too when
  `platform` is missing or isn't one of the two names (a number isn't accepted). A token registered by
  another student moves to this one (another student signed in on the same phone). A student
  keeps at most **10** devices: registering an 11th drops the least recently registered.
- `DELETE /me/devices` `{ token }`: forgets that device. **204** for any token, also one that isn't
  the student's (nothing happens), so the answer doesn't tell whose a token is; **400** only when the
  body isn't `{ token }`. The token goes in the
  body, not the path, because paths are logged here and by the reverse proxy, and push tokens never
  are.
- `POST /me/notifications/test`: sends "CampusPulse test notification" to the student's devices and
  answers `{ sent }`, the number the push service accepted it for. Once a minute per student:
  **429** with `Retry-After` otherwise. Only a send that reached the push service counts, so after
  `sent: 0` with no devices, or a 502, the next tap sends right away. The limit is kept **in
  memory**: a restart forgets it, each API instance would count on its own, and an entry is dropped
  as soon as its minute is over. **502** when the push service can't be used at all.

**What the clients do:**
- Register (`POST /me/devices`) on **every app launch** (Expo's advice: it keeps the token fresh),
  after `POST /me`, and whenever the Expo push token changes (`addPushTokenListener`).
- Delete it (`DELETE /me/devices`) on sign-out, before dropping the access token.
- `DELETE /me` removes every device with the account.

**Sending, tickets and receipts:**
- Messages go to Expo in batches of 100, through a typed HttpClient with a 15 s timeout.
- Expo answers each message with a ticket. A `DeviceNotRegistered` ticket (the app was uninstalled)
  deletes that device at once. An accepted one is kept in `push_receipts_pending`.
- A background service (`PushReceiptChecker`) runs every 15 minutes. It reads the receipts of the
  tickets that are at least 15 minutes old, in batches of 300, each applied on its own in one
  transaction: first the devices whose receipt is `DeviceNotRegistered`, then the answered rows. A
  batch Expo can't answer keeps its rows for the next pass. Rows older than 24 hours are dropped
  unread. With nothing pending, a pass is one query.
- **A device that registered again after a message was sent is never deleted for that message.** A
  reinstall can bring the same Expo token back, so a late `DeviceNotRegistered` (ticket or receipt)
  deletes the device only if its last registration is older than the send.

**Logs** carry counts and Expo's error codes (`DeviceNotRegistered`, `MessageTooBig`, a failed
request's `PUSH_TOO_MANY_EXPERIENCE_IDS`…) only, never a push token, user id or email. Expo's own
error texts and details quote tokens, so they're never kept, and a code that isn't ASCII letters and
`_` is logged as `Unknown`.

**Network:** the push client sets no proxy of its own. It uses
[`HttpClient.DefaultProxy`](https://learn.microsoft.com/dotnet/api/system.net.http.httpclient.defaultproxy),
which on Linux comes from `HTTPS_PROXY` / `NO_PROXY`; a test checks the handler keeps that
default. So on the Pi it goes through the egress proxy, whose allow list has `exp.host`
(`deploy/pi/egress-allow`; installing it on the Pi is a manual step, see
[deploy/pi/README.md](deploy/pi/README.md#egress-proxy)).

**Android needs an FCM key at Expo (not part of this API):** Expo delivers to Android through
Firebase Cloud Messaging, so an FCM V1 service account key has to be uploaded to EAS for the app's
Android credentials (`eas credentials`). Sebastian does that. Until then Expo can't deliver to
Android: expect an `InvalidCredentials` error on the ticket or on its receipt, in the logs.

## Database migrations

The schema is versioned with EF Core migrations in `src/CampusPulse.Infrastructure/Migrations/`
(ORM: EF Core 10 + Npgsql, snake_case names). A model change is committed together with its
migration.

```bash
dotnet tool restore     # dotnet-ef, pinned in dotnet-tools.json
dotnet ef migrations add <Name> --project src/CampusPulse.Infrastructure --startup-project src/CampusPulse.Api
```

- Make migrations on a development machine (ThorSPB or ThorSPC), never on the Pi.
- **A migration must keep working with the previous release:** expand first (add the new
  column or table), contract in a later release (drop the old one). A rollback restores the
  code, not the schema.
- `dotnet ef` creates the context with `DesignTimeDbContextFactory`, which takes the connection
  string from `ConnectionStrings__Postgres` in the environment. `migrations add`,
  `has-pending-model-changes` and building a bundle don't connect and need no value;
  `database update` and running a bundle do.
- To migrate the local Compose database:

  ```bash
  ConnectionStrings__Postgres="Host=localhost;Port=5434;Database=campuspulse;Username=campuspulse;Password=campuspulse_dev" \
    dotnet ef database update --project src/CampusPulse.Infrastructure --startup-project src/CampusPulse.Api
  ```
- CI runs `dotnet ef migrations has-pending-model-changes`: a model change without its
  migration fails the build.
- CI also builds a **migrations bundle** (`efbundle`, self-contained linux-arm64, artifact
  `campuspulse-efbundle`). Each deploy runs it against its environment's database, with the
  connection string from the environment (never in argv), before the new release goes live,
  so the Pi needs neither the SDK nor `dotnet-ef`.

## Logs

- **Stdout:** .NET's JSON console lines (journald on the Pi): UTC `Timestamp`, `LogLevel`,
  `Category`, `Message`, and `Scopes`, where every line logged during a request has its `RequestId`.
- **File:** when `Logging__File__Path` is set (the Pi's units set
  `/srv/campuspulse/logs/<env>/api.log`, the only folder the service can write), the same events
  also go to a file, at the same levels. It rolls at 10 MB and keeps 10 files: `api.log` first, then
  `api_001.log`, `api_002.log`… (the newest is the highest number; the oldest is deleted). The Pi's
  journald keeps logs in RAM only, so the file is what survives a reboot. Locally it's off unless
  you set it. Lines are Serilog's rendered compact JSON: `@t` (UTC time), `@m` (the message, with
  string values in quotes: `HTTP "GET" "/" responded 200 in 3.1 ms`), `@l`
  (the level, left out at Information), `@x` (an exception with its stack), `@i` (an id of the
  message template), then the event's properties (`RequestId`, `SourceContext` = the category,
  `StatusCode`…).
- **One line per request:** method, path (never the query string), status and duration. A passing
  `/health*` check (status below 400) is logged at Debug, since deploys and monitoring call them
  constantly; a failing one (a 503 readiness) at Information like any other request.
- **Request id:** a caller's `X-Request-Id` is kept if it's 1–64 characters of letters, digits,
  `.`, `_`, `-` or `:`; otherwise a new one is made. It is the request's `TraceIdentifier`, returned
  in `X-Request-Id` on every response (exposed to browser scripts through CORS), so a client's bug
  report can be matched to its log lines.
- **Errors:** an unhandled exception returns a ProblemDetails 500 with no exception details, whose
  `requestId` is the request id (and `traceId` the W3C trace id, for tracing tools); the exception
  and its stack trace are logged at Error.
- **Reading them on the Pi:** `journalctl -u campuspulse-api-stage -o cat | jq`, or the file, which
  follows the newest one:
  `sudo sh -c 'tail -F "$(ls -t /srv/campuspulse/logs/stage/api*.log | head -1)"' | jq -c '[."@t", ."@l", ."@m", .RequestId]'`.
  Look one request up with `sudo grep -h '"RequestId":"<id>"' /srv/campuspulse/logs/stage/api*.log | jq`.
- **Error reporting (Sentry):** not enabled. `Program.cs` marks where it would go; it would also need
  an allow rule in the Pi's network guard.

## Tests

```bash
dotnet test
```

Tests marked `[PostgresFact]` need a real Postgres. Point `CAMPUSPULSE_TEST_DB` at a server you
can create databases on (no database name in it). Their fixture, `PostgresAppFactory`, creates a
database of its own, migrates it, runs the app on it and drops it afterwards. For the Compose database:

```bash
docker compose up -d
CAMPUSPULSE_TEST_DB="Host=localhost;Port=5434;Username=campuspulse;Password=campuspulse_dev" dotnet test
```

Without the variable those tests show as **skipped**, with the reason. On CI (`CI=true`) they are
never skipped: CI runs a Postgres 18 service and sets the variable.

Formatting follows `.editorconfig`: run `dotnet format` before committing; CI fails on unformatted code.

CI (`.github/workflows/ci.yml`) checks formatting, builds with warnings as errors, checks for
pending model changes, runs the tests against Postgres, publishes the app and the migrations
bundle as artifacts, and builds the container image for `linux/amd64` and `linux/arm64`
without pushing it.

## Deploying

| | stage | prod |
|---|---|---|
| URL | https://campuspulse-api-stage.thorsp.net | https://campuspulse-api.thorsp.net |
| Deployed by | every merge to `main` (a green `CI` run) | a published GitHub Release `vX.Y.Z` |
| Used by | web PR previews and staging, mobile dev and preview builds | web production, mobile production builds |

- **Releasing:** publish a GitHub Release with a `vX.Y.Z` tag on a commit that is on `main` (only
  admins can create `v*` tags). The `Release` workflow builds it, attests the build and attaches
  `campuspulse-api.tar.gz` and `efbundle` (the migrations bundle); the Pi verifies the attestation and
  deploys exactly that version to prod. Pre-releases and drafts are never deployed.
  Cut one with `gh release create vX.Y.Z --target main --generate-notes`: the notes group merged PRs
  by label (`enhancement`/`feat`, `bug`, `dependencies`, the rest), configured in
  [`.github/release.yml`](.github/release.yml). Label PRs so they land in the right group.
- **Rolling back:** on the Pi, `campuspulse-deploy rollback prod` (or `stage`) switches to the
  healthy release before the live one. It restores code, not schema. To roll forward again:
  `campuspulse-deploy prod <run-id> vX.Y.Z --force`.
- How the Pi side works and how it was set up: [`deploy/pi/README.md`](deploy/pi/README.md).
- There is no Dockerfile. Hosts that only run images get one from the SDK
  (`dotnet publish -t:PublishContainer`), multi-arch (`linux-x64` + `linux-arm64`) from the csproj:

  ```bash
  dotnet publish src/CampusPulse.Api -c Release -t:PublishContainer \
    -p ContainerRegistry=ghcr.io -p ContainerRepository=campuspulse-ro/campuspulse-api
  ```
