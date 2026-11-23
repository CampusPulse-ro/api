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
| `Logging__LogLevel__<category>` | the usual .NET log levels, for stdout and the file alike; defaults in `appsettings.json` (Information, `Microsoft.AspNetCore` at Warning, `Microsoft.AspNetCore.DataProtection` at Error: sign-in registers Data Protection, which nothing uses (bearer tokens only), and with no home directory, as for the Pi's service users, it warns at every start that its keys are in memory. Lower it again once a feature protects data across restarts; `Microsoft.EntityFrameworkCore` at Warning, so SQL commands aren't logged: `Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Information` shows them while debugging) |
| `Auth__TenantId` | UPT's Entra tenant id (`6bb41fe4-40a3-4a10-b6cd-38278e78b21a`). With `Auth__ClientId` unset too, sign-in is off and every endpoint that needs it answers 401 (see Sign-in) |
| `Auth__ClientId` | the app registration's client id (one registration for web, mobile and the API) |
| `Auth__SigningKey` | the key our own tokens are signed with (HS256): base64 of at least 32 random bytes (`openssl rand -base64 32`), one per environment, a secret. Unset: no sessions are issued (`/auth/*` answers 503; see [Sign-in](#sign-in)), except in Development, where each start makes one up. Not base64 or too short: the start fails. Changing it signs every access token out (refresh tokens keep working) |
| `Email__ResendApiKey` | the [Resend](https://resend.com) API key sign-in codes are sent with (see [Email sender](#email-sender)), a secret. Unset: email sign-in answers 503, except in Development (codes go to the log) |
| `Email__From` | the sender, default `CampusPulse <no-reply@campuspulse.thorsp.net>`; its domain must be verified at Resend |
| `HTTPS_PROXY`, `NO_PROXY` | on the Pi, the egress proxy the API reaches Microsoft, Expo and Resend through (see Sign-in, Push notifications, Email sender) |
| `Push__Expo__AccessToken` | optional: an Expo access token, sent as `Authorization: Bearer` to Expo's push API. Needed only once "enhanced security for push notifications" is turned on for the Expo project |
| `Reminders__Enabled` | `false` stops sending class reminders (see [Class reminders](#class-reminders)); default `true`. The tests turn it off |
| `RateLimits__Enabled` | `false` turns the rate limits off (load tests, an emergency); default `true`. See [Rate limits](#rate-limits) |
| `RateLimits__<bucket>__Burst`, `RateLimits__<bucket>__PerMinute` | the token buckets: `SignedIn` (per student) 100 / 100, `Anonymous` (per client IP) 300 / 300, `Writes` 30 / 30, `Auth` (`/auth/*`, per client IP) 60 / 60, `Account` (`POST`/`DELETE /me`, linking UPT) 10 / 1. A value below 1 fails the start |
| `RateLimits__EmailStart__Limit`, `RateLimits__EmailStart__WindowMinutes` | `POST /auth/email/start` per client IP: 20 an hour by default. Raise it for an event where many students share one network (a lab, the dorms); the per-address limits stay |
| `TimetableCache__VersionCheckSeconds` | how often, at most, the cached timetable asks the database whether an import ran; default `10` (see [Timetable cache](#timetable-cache)) |
| `Limits__MaxRequestBodyBytes` | the largest request body Kestrel takes; default `16384` (16 KiB). Larger is a 413 ProblemDetails |
| `Limits__RequestTimeoutSeconds` | a request running longer is cancelled and answered 504 ProblemDetails; default `30` |
| `Database__MaxPoolSize`, `Database__MinPoolSize` | the Npgsql connection pool of one process. Set, they win over the connection string; unset, a size in the connection string is kept, else `20` and `0` |
| `Metrics__Port` | serve [metrics](#metrics) on `127.0.0.1:<port>`. Unset (the default): no metrics. A port the API already listens on fails the start |

## Sign-in

CampusPulse has its own accounts (CampusPulse-ro/api#63). A student signs in with **an email and a one-time code**, or
with **UPT Microsoft** ("Continue with UPT"); UPT can also be linked to an email account later. Either way the API
issues **its own tokens**: a short access token and a refresh token. The app works without UPT (timetable, rooms,
reminders, manual deadlines); linking UPT adds Microsoft-verified student status and, on the phone, Campus Virtual.

**Every endpoint needs a bearer token** except `/`, `/health*`, `/openapi/v1.json` and `/auth/*`: a fallback policy
protects anything not marked `.AllowAnonymous()`, so a new endpoint is protected by default, and the contract marks
every other operation as needing the bearer token. Two kinds of bearer token are accepted, chosen by the token's issuer:

- **Ours** (issuer `campuspulse`), what every client sends from now on: a JWT signed with `Auth__SigningKey` (HS256),
  audience `campuspulse-api`, `sub` = the account id, `amr` = how the student signed in (`email` or `upt-microsoft`),
  valid **15 minutes**. Checked without the database.
- **A UPT Entra access token** (today's scheme), so installed app versions keep working: checked as below, and the
  account found through its `upt-microsoft` identity. Clients otherwise send Entra tokens only to `/auth/microsoft` and
  to link UPT.

Anything else is 401, logged at Warning with the reason (never the token).

### Endpoints

All JSON; errors are ProblemDetails whose **`type`** is stable and says which (the title and detail are for people). On
every endpoint, a request that doesn't match the contract (a body that is missing, isn't JSON or lacks a required field,
a parameter of the wrong type) is **400** `invalid-request`, and a body over the size limit **413** `body-too-large`.

| Method + path | Auth | Request → answer |
|---|---|---|
| `POST /auth/email/start` | none | `{ "email": "ana@example.com", "language": "en" }` (`"en"` or `"ro"`, the email's language) → **202** `{ "expiresInSeconds": 600, "resendAfterSeconds": 60 }`, **always**, whether or not an account has the address (signing in creates one, so nothing is revealed). Sends "Your CampusPulse code: 123456". **400** `invalid-email`; **429** `too-many-codes` (with `Retry-After`) or the per-IP limit; **503** `email-unavailable` |
| `POST /auth/email/verify` | none | `{ "email": "ana@example.com", "code": "123456" }` → **200** `TokenResponse`, `isNew: true` when the account was made now. **400** `invalid-code` (wrong, expired or used; the attempt counts), **400** `code-burned` after 5 wrong attempts: ask for a new code |
| `POST /auth/microsoft` | none | `{ "accessToken": "<Entra token for api://<client>/access_as_user>" }` → **200** `TokenResponse`. **401** `microsoft-token-invalid`, **503** `microsoft-unavailable` |
| `POST /auth/refresh` | none | `{ "refreshToken": "…" }` → **200** `TokenResponse` with **both tokens new**; the old refresh token stops working. **401** `refresh-invalid`: expired, revoked, or already used (then its whole session is revoked): sign out |
| `POST /auth/sign-out` | none | `{ "refreshToken": "…" }` → **204**, also for an unknown or revoked token. Ends that session |
| `GET /me` | bearer | → **200** `Me`; **404** when the account is gone |
| `POST /me` | bearer | installed apps only (Entra token): creates the account (**201**) or returns it (**200**). With our token: **200**, or **404** once deleted |
| `DELETE /me` | bearer | → **204**, also when there was nothing to delete (see [Data](#data)) |
| `POST /me/identities/upt-microsoft` | bearer | `{ "accessToken": "<Entra token>" }` → **200** `Me`. **409** `identity-in-use` (that UPT account belongs to another CampusPulse account; accounts aren't merged), **409** `already-linked` (this account has another UPT account), **401** `microsoft-token-invalid`. When another account takes the UPT address at the same moment, UPT is linked without it |
| `DELETE /me/identities/upt-microsoft` | bearer | → **204**, also when none is linked. **409** `last-sign-in-method` when the account has no verified email |
| `GET /me/deadlines?from=&to=` | bearer | → **200** `[Deadline]` by `dueAt`; `from`/`to` optional instants, both included |
| `POST /me/deadlines` | bearer | `{ "title": "Lab 3 report", "course": "MSA", "dueAt": "2026-11-30T21:59:00Z", "notes": "PDF" }` (`course`, `notes` optional; `title`, `dueAt` required) → **201** `Deadline`. **400** `invalid-deadline` (title 1-200, course ≤ 200, notes ≤ 2000, `dueAt` from 2000 to before 2100), **400** `invalid-request` without `dueAt`, **409** `deadline-limit` (500 per student) |
| `PATCH /me/deadlines/{id}` | bearer | any of `{ "title", "course", "dueAt", "notes", "done" }`; a missing field is unchanged; `title`, `dueAt` and `done` can't be null (**400** `invalid-deadline` / `invalid-request`); a null `course`/`notes` is unchanged and `""` clears it → **200** `Deadline`; **404** for an id that isn't the student's |
| `DELETE /me/deadlines/{id}` | bearer | → **204** |

```jsonc
// TokenResponse
{ "accessToken": "eyJ…", "accessTokenExpiresAt": "2026-11-23T12:15:00+00:00",
  "refreshToken": "q3V…43 chars", "refreshTokenExpiresAt": "2027-01-22T12:00:00+00:00",
  "isNew": true, "user": /* Me */ }
// Me (GET /me, and user above)
{ "id": "01a1…", "email": "ana@example.com", "displayName": "Ana Pop",
  "identities": [{ "provider": "upt-microsoft", "email": "ana.pop@student.upt.ro", "linkedAt": "2026-11-23T12:00:00+00:00" }],
  "uptLinked": true }
// Deadline
{ "id": "01a1…", "title": "Lab 3 report", "course": "MSA", "dueAt": "2026-11-30T21:59:00+00:00", "notes": "PDF",
  "done": false, "createdAt": "…", "updatedAt": "…" }
```

### Accounts, codes and sessions

- **An account's email is verified**: confirmed with a code, or a UPT account's address when it is one of UPT's own
  (`upt.ro` or a subdomain such as `student.upt.ro`). Microsoft doesn't vouch for a token's `email` claim, and a tenant
  can put any address there, so a UPT token's other addresses are only shown on the identity: they link nothing and
  open nothing. It is unique and stored normalized (trimmed, lowercase). An email account's first name is the
  address's local part; linking UPT sets the UPT name.
- **Codes:** 6 digits, valid **10 minutes**, **5 wrong attempts** then burned; a new code replaces the last; only a
  salted SHA-256 is stored. An address gets **3 codes per 15 minutes and 10 a day**; `/auth/email/start` is also
  limited per client IP (20 an hour), and every `/auth/*` call (60 a minute per IP): see [Rate limits](#rate-limits).
- **Email sign-in finds** the account with that verified email, else one whose linked UPT account has it (a UPT
  address), else makes a new one.
- **UPT sign-in finds** the account of that UPT account; else **links automatically** to an account whose verified email
  is the UPT account's (both addresses are verified, so it is the same person) and that has no UPT account yet; else
  makes a new one, with the UPT address unless another account has it or it isn't a UPT one (then the account has no
  email, and can only sign in with UPT until it gets one).
- **Refresh tokens:** 256 random bits (base64url), stored as their SHA-256, **60 days** from each refresh, replaced on
  every refresh. One sign-in on one device is one *family*; a token that was already replaced, shown again, means it was
  copied, and revokes the family. Except for **one minute** after its replacement while the replacement is unused: then
  the client never got that answer (a dropped connection) or sent two refreshes at once, and it gets new tokens again;
  the unused replacement is revoked and chained to them, so a family never has two live tokens, and showing that one
  later is a reuse too. Clients still refresh one request at a time: a retry after that minute, or after the new token was used, signs the
  device out.
- Logged as counts only (`Email sign-in code sent`, `Signed in with UPT (new account: True)`, `A refresh token was used
  again: its session was revoked`): never a code, a token or an address. An hourly job deletes expired refresh tokens
  and an address's code row a day after its last code.
- **Unconfigured:** without `Auth__SigningKey` (outside Development, where a key per process is made up) no session is
  issued: `/auth/*` answers 503 `sign-in-unavailable` and only UPT Entra tokens work. Without `Auth__TenantId` /
  `Auth__ClientId`, UPT tokens are refused and `/auth/microsoft` answers 503 `microsoft-unavailable`. Either way nothing
  gets in anonymously; the start logs a warning.

### UPT Microsoft (Entra) tokens

A UPT token, as a bearer or in `/auth/microsoft` and linking (checked the same way in both), is accepted only if it:

- is signed with RS256 by Microsoft (keys from the tenant's metadata);
- has the issuer `https://login.microsoftonline.com/<tenant>/v2.0` exactly (v2 tokens; see below);
- has an audience of the client id or `api://<client-id>`, and was issued to this app (`azp` = the
  client id);
- has `tid` equal to the tenant, and no `idp` other than the tenant's own: **guests are refused**;
- has `access_as_user` in `scp`, so app-only tokens and ID tokens are refused;
- has an `oid` and hasn't expired.

Its email is `email`, else the UPN in `preferred_username`. **Who gets in with UPT:** every member of the tenant,
students and staff alike. **At startup** the API loads the tenant's metadata in the background: one Information line if
it can, an Error if it can't reach Microsoft; until it loads, requests with a UPT bearer answer 500 (a server fault, so
clients don't send the student back to sign in) and `/auth/microsoft` answers 503.

### What clients must do

- **Sign-in screen:** "Continue with UPT" (the Microsoft flow, then `POST /auth/microsoft` with its access token), or
  email → `POST /auth/email/start` → the 6-digit code → `POST /auth/email/verify`. Offer "resend" after
  `resendAfterSeconds`; show `invalid-code` and `code-burned`.
- **Keep our refresh token** (mobile: SecureStore; web: localStorage), send our access token as the bearer, refresh
  before `accessTokenExpiresAt` and retry once on a 401. **One refresh at a time** (a retry of a lost refresh within a
  minute is fine). When refresh answers 401, sign out
  locally. On sign-out call `POST /auth/sign-out` with the refresh token, then drop both.
- **Link UPT:** run the Microsoft flow, then `POST /me/identities/upt-microsoft` with its access token. Unlink with
  `DELETE`, after a confirmation; the server refuses unlinking the last sign-in method.
- Treat a **404 from `GET /me`** as "this account no longer exists" (deleted, maybe from another device): sign out
  locally, drop cached data and show sign-in. After a **204 from `DELETE /me`**, sign out locally right away.
- **Installed versions** that still send Entra tokens keep working: `POST /me` once right after an interactive sign-in
  (never on app start, or a second device would re-create an account deleted on the first), then the Entra token as the
  bearer.
- Send a random `X-Request-Id` per request, or none, never one fixed per install: a stable id
  would tie all of a student's log lines together, a deleted student's included.
- **Microsoft's authority** is `https://login.microsoftonline.com/<tenant>` (not `/common`, which a single-tenant app
  refuses with AADSTS50194), with the scopes `openid profile offline_access api://<client-id>/access_as_user`. Without
  `profile` the token may lack the name and email.

### Registering the UPT app

Once, by Sebastian, signed in with his `@student.upt.ro` account: a test
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
- Declare every response (`.Produces<T>()`, `.Produces<T>(503)`) so clients get a type for it. Two are added for
  you: a **429** ProblemDetails on every operation but `GET /health` (the rate limits; endpoints mapped through the
  `api` group in `Program.cs` get it), and a
  **304** on the GETs that opt into conditional requests (`.WithHttpCaching(...)`, see [HTTP caching](#http-caching)).

## Data

- `users`: one row per account (CampusPulse-ro/api#63): `id`, `verified_email` (normalized, unique; null for an
  account made with UPT whose address another account had or isn't a UPT one), name, first and last seen.
  - Two columns are the previous release's, kept so it still works on this schema after a rollback (a rollback
    restores code, not schema): `entra_object_id`, the UPT account's `oid` while one is linked (now nullable, still
    unique), and `email` (non-null, not unique; this release keeps it equal to `verified_email`, `''` for none, and never
    reads it). Accounts made by email have no `entra_object_id`, so the previous release doesn't see them. Both columns go
    in a later release (contract).
- `user_identities`: the sign-in methods linked to an account other than its email: one `upt-microsoft` row (`subject`
  = the `oid`, unique per provider; the UPT account's email; when it was linked). The migration made one for every
  existing account.
- `email_sign_in_codes`: per address (normalized; not a key to `users`, since an address that has no account can ask
  for a code): the live code's salted SHA-256, its expiry and failed attempts, and when the last codes were sent (the
  per-address limit). Deleted by the hourly cleanup a day after the last code, and by `DELETE /me` for the account's
  addresses.
- `refresh_tokens`: SHA-256 of each refresh token, its family (one sign-in on one device), the sign-in method,
  expiry, when it was revoked and what replaced it. Expired rows are deleted hourly.
- `deadlines`: a student's manual deadlines (title, course, due, notes, done). At most 500 per student.
- **Everything about a student refers to their `users` row with ON DELETE CASCADE.** `DELETE /me`
  deletes that row, and the database deletes the rest with it: identities, refresh tokens (so every session ends),
  deadlines, settings, device push tokens, any later table. The account's sign-in code rows (keyed by address, not by
  account) are deleted in the same transaction. In EF that's `.OnDelete(DeleteBehavior.Cascade)`, the
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
- The timetable (see [Timetable](#timetable)): `semesters` (with `teaching_weeks` and
  `non_teaching_days`), `programmes`, `study_groups`, `rooms`, `subjects`, `timetable_slots` (with
  `timetable_slot_groups`), `offered_slots`, `timetable_notes` and `timetable_import_runs`. Written by
  the import only. A student's own rows are `student_settings` (their study group) and
  `student_subjects` (optional subjects and picked offers), both in the cascade tree under `users`.
  - The import never deletes a study group or a subject. One a newer workbook no longer has is
    *retired* (`retired_at`), so the student's row keeps pointing at it, shown as "no longer offered".
  - It does delete offered slots that changed or went: `student_subjects.offered_slot_id` is `SET NULL`,
    so that clears the pick, not the choice.
  - `student_settings.study_group_id` is `SET NULL` and `student_subjects.subject_id` is `CASCADE`. Those
    only matter if a group or subject row is ever deleted by hand.
- Class reminders (see [Class reminders](#class-reminders)): `reminder_settings` (a student's lead; no row = off)
  and `class_reminders_sent` (the reminders that went out, kept 2 days), both in the cascade tree under `users`.
- `account_deletion_counts`: the number of accounts deleted per UTC day, added to in the same
  transaction as the delete. It holds no identifier. The deletion's log line (`Account deleted`)
  names no one either, but like every line of the request it carries the request's scope: request
  id, trace and span ids, connection id and path.
- **Backups:** the production backups dump this database nightly and keep 7 daily, 5 weekly and 12
  monthly copies, plus a weekly copy on a second machine. A deleted account's row therefore
  survives in backups for up to about a year after the delete.
  - Decided (2026-10-14): retention stays as it is. Where users are told (the deletion page, Play's data
    safety form): deleted data stays only in the system-wide backups, is never used for anything, and is
    gone after at most about a year.

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
  answers `{ sent }`, the number the push service accepted it for. Once a minute per student (on top of the
  general [rate limits](#rate-limits)):
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

### Class reminders

A push notification before each of a student's classes (CampusPulse-ro/api#53), at the lead they pick. Both
endpoints need sign-in and an account (404 like `GET /me` otherwise).

- `GET /me/reminders` → `{ minutesBefore }`: the lead in minutes, or `null` when reminders are off (the default).
- `PUT /me/reminders` `{ minutesBefore }`: `5`, `10`, `15`, `30` or `60`, or `null` to turn them off; answers the
  same body. **400** ProblemDetails for any other value, a missing field or a non-number.

**What is sent:** one notification per class, to each of the student's devices.
- Title `MSA in 15 min`: the subject's short name (else its name) and the minutes left, which is the lead unless
  the reminder went out late.
- Body `Lab · 303CP`: the kinds and the room, whichever there are (the subject's name when neither).
- Data `{ type: "classReminder", date: "2030-11-25", startsAt: "08:00:00", subjectId }`: the date and start as
  `GET /me/timetable` writes them, so the app can open that class.
- High priority, since Android may hold a normal one back on a sleeping phone, and a time to live of the
  minutes until the class starts, so a phone that comes online later gets no reminder for a class that began.
- The text is English: the API doesn't know the app's language.

**When:** `ClassReminderSender` ticks once a minute. For every student with a lead and at least one device it
takes their classes of today and tomorrow, computed exactly as `GET /me/timetable` computes them
(`TimetableSnapshot.ForStudent`, from the same [timetable cache](#timetable-cache), so nothing on a non-teaching day
or in a break). A class's reminder is due from its start
(Romanian time) minus the lead until the class starts, and goes out at the first tick in that span. A class that
has started gets none. Whether a reminder went out is recorded in the database, not in the process, so a skipped
tick, a restart or a deploy sends the ones still before their class, late, with the minutes actually left. The
same goes for a student who turns reminders on, picks a longer lead or registers a first device after a class's
due time: they get that reminder before the class starts.

**Never twice:** each reminder is recorded in `class_reminders_sent` (student, date, start, subject), in one
statement per tick, just before it goes out, and a recorded one is never sent again.
- Changing the lead after a class's reminder went out sends no second one for that class.
- A grid class and a picked offer of the same subject at the same time are one reminder.
- When the push service certainly didn't take it (unreachable, or it refused the whole request: `RequestFailed`),
  the record is dropped and the next tick tries again while the class hasn't started. A timeout (`TimedOut`) or an
  answer that can't be read (`MalformedResponse`) may hide an accepted message, so those stay recorded: a missed
  reminder rather than a doubled one.
- From the record to the send, a tick runs to its end even when the API is stopping (a deploy). A crash in
  between loses those reminders rather than sending them twice.
- Records are deleted after 2 days, and with the account.

Tickets, `DeviceNotRegistered` and receipts are handled as for any other send (above). A tick is two statements
when nobody has reminders on, and a handful of small queries otherwise (only the slots of today's and tomorrow's
weekdays), however many students.

**Logs:** one Information line per tick that had reminders due (`Class reminders: 3 due, 3 sent, 0 already
sent, 0 to retry`), counts only, plus the push lines above. With `Reminders__Enabled=false`, one line at startup
says reminders are off.

**What the clients do:** show a picker (off, 5, 10, 15, 30, 60 minutes) filled from `GET /me/reminders`, and save
it with `PUT /me/reminders`. Open the class when a notification with `data.type == "classReminder"` is tapped.
Reminders reach only registered devices (`POST /me/devices`).

## Email sender

Sign-in codes are the only email the API sends (`IEmailSender`, chosen at startup by `AddCampusPulseEmail`):

- **Resend** when `Email__ResendApiKey` is set: `POST https://api.resend.com/emails` with the key as the bearer, from
  `Email__From`, a 15 s timeout. Like Entra and Expo it goes out through the Pi's egress proxy, so `api.resend.com` is
  on [`deploy/pi/egress-allow`](deploy/pi/egress-allow) (installing that line on the Pi is a manual step: see
  [deploy/pi/README.md](deploy/pi/README.md#egress-proxy)). The sender's domain (`campuspulse.thorsp.net` by default)
  must be verified at Resend (its SPF and DKIM records in the DNS), or Resend refuses every email.
- **The log**, in Development without a key: the email's text, code included, is logged at Warning, so a local run
  can sign in without an email account. Never anywhere else.
- **None** otherwise: `POST /auth/email/start` answers 503 `email-unavailable` (the start logs a warning), and UPT
  sign-in still works.

The email is plain text with a minimal HTML copy, in English or Romanian (`language` in the request): the subject
carries the code ("Your CampusPulse code: 123456", "Codul tău CampusPulse: 123456") so it shows in a notification, and
there are **no links** in it, so a phished copy has nothing to click. When Resend refuses or can't be reached, or the
sender fails in any other way, the request answers 503 `email-unavailable` and the new code is withdrawn: it doesn't
count against the address's limit and can't be used, and the code it replaced (an earlier email's, still within its 10
minutes) works again. The send isn't tied to the request, so a client that disconnects can't leave a code issued and
counted behind. Logged: `Resend refused an email: HTTP 422`, `Resend couldn't be reached: <exception type>` or `The
email sender failed: <exception type>`, never the address, the code or Resend's answer (which may quote them).

## Timetable

The AC faculty's timetable for a semester (CampusPulse-ro/api#5), loaded by a one-off import:
[docs/timetable-import.md](docs/timetable-import.md). Every endpoint needs sign-in: the faculty shares
these files with UPT accounts only. Dates are Romanian dates. The API expands the weekly slots into
dates itself (teaching weeks, the break, non-teaching days, each slot's weeks), so no client handles
week parity. What is the same for everyone comes from memory and is reloaded after an import (see
[Timetable cache](#timetable-cache)); answers carry an `ETag` (see [HTTP caching](#http-caching)).

- `GET /semesters/current`: the teaching weeks with their dates, non-teaching days (`reason`), the breaks
  (`from`, `to`, `name`, null when unnamed), today and its teaching week. Calendar names come as `{ ro, en }`:
  show the one of the UI language (`en` repeats `ro` when the timetable gives no English name).
- `GET /timetable/programmes`: programmes → years → study groups, to pick a group from.
- `GET /me/settings` / `PUT /me/settings` `{ studyGroupId }` (null clears it): the student's group.
  400 for a group that doesn't exist, 404 without an account.
- `GET /timetable/groups/{id}/choices`: the subjects of that group's year a student chooses, each
  with the slots it's offered at:
  - `optional` (opt in, then pick a slot);
  - `pickSlot` (everyone has it, at a time they pick: most master labs).
- `GET /me/subjects`, `PUT /me/subjects/{subjectId}` `{ offeredSlotId }` (null: taking it, no slot
  picked yet), `DELETE /me/subjects/{subjectId}`. 400 for a subject with nothing to choose or an
  offer of another subject.
- `GET /me/timetable?from=&to=`: the student's classes, both dates included, at most 31 days,
  defaulting to today. That's their group's classes, minus optional subjects they didn't choose,
  plus the offered slots they picked; plus the notes of their year (sport, tutoring hours, rotas
  written out).
- `GET /timetable/groups/{id}?from=&to=`: a group's classes, optional subjects included (`optional`).
- `GET /rooms`, `GET /rooms/{code}/timetable?date=`: everything in a room on a date, grid slots and
  offered slots.

Clients: read the student's group from `GET /me/settings` after `POST /me`. With no group, send them to
the picker. With `noLongerOffered` (a newer timetable doesn't have it) or `fromAnotherSemester` (a new
semester was imported), ask them to pick again. A chosen subject with `noLongerOffered` is shown as such.
Every class, subject and room entry carries `campusVirtualUrl`: the subject's course page on Campus Virtual
(cv.upt.ro), or null when it has none we know of. Link to it as given; don't build the URL.

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

## Scaling and limits

What keeps the API fast and hard to abuse (CampusPulse-ro/api#58). Each part has tests; the numbers at the end were
measured.

### Behind nginx

nginx terminates TLS and proxies to the API on 127.0.0.1, so every connection comes from nginx. The API takes the
client's address and scheme from nginx's `X-Forwarded-For` and `X-Forwarded-Proto` (`ForwardedHeaders`), only from a
loopback proxy and only nginx's own hop: a client can't choose the address it is counted under, and the header from
anywhere else is ignored (`ProxyHeaders`).

### Rate limits

ASP.NET Core's rate limiter with token buckets in memory (`RateLimitSetup`). A bucket holds *Burst* requests and gets
*PerMinute* back a minute, one at a time:

| Bucket | Counted per | Burst / per minute | Takes from |
|---|---|---|---|
| `SignedIn` | student (the account, or for a UPT Entra bearer its `oid`) | 100 / 100 | every request with a valid token |
| `Anonymous` | client IP (an IPv6 address by its /64) | 300 / 300 | every request without one: readiness checks, the contract, refused tokens, `/auth/*` |
| `Writes` | student, else IP | 30 / 30 | every POST, PUT, PATCH, DELETE outside `/auth`, first |
| `Auth` | client IP | 60 / 60 | every `/auth/*` request, first (instead of `Writes`: signing in is anonymous, and a shared network shouldn't share 30 a minute) |
| `Account` | student | 10 / 1 | `POST /me`, `DELETE /me`, `POST`/`DELETE /me/identities/upt-microsoft`, as well |

`POST /auth/email/start` is also limited to **20 an hour per client IP** (a fixed window, `RateLimits:EmailStart`), and
each address to **3 codes per 15 minutes and 10 a day**: that limit is in the database (an address's row in
`email_sign_in_codes`), so it holds across restarts and instances, and answers 429 `too-many-codes` with `Retry-After`
until the oldest counted code leaves the window. The per-IP numbers are a guess for one campus behind one NAT: for a
lab, raise `RateLimits__EmailStart__Limit`.

- A refused request is a **429** ProblemDetails with `Retry-After` (seconds), which browsers can read (CORS exposes it).
  Clients wait that long before retrying.
- An invalid token counts as anonymous, so made-up `oid`s can't open buckets of their own. Writes are limited by
  method, so a new endpoint is covered without anyone remembering to, and checked first, so a refused write costs no
  read.
- `Anonymous` is larger because a campus or dorm network puts many students behind one address, and an expired
  token's 401 should reach the app (it signs the student out) rather than a 429.
- `GET /health` is exempt: answering it costs less than refusing it, and the web's status line must never show an
  outage that isn't one. `/health/ready` (it touches the database) is limited like any anonymous call.
- Logged as counts only, once a minute when there were any: `Rate limits refused 37 requests in the last minute (30
  signed in, 7 anonymous)`. Per route and per bucket they are in the [metrics](#metrics).
- The test notification keeps its own once-a-minute limit (see [Push notifications](#push-notifications)).
- **Limits:** the buckets live in one process. A restart empties them, and two instances would each count on their
  own: several instances need a shared store (Redis).

### Timetable cache

The timetable is the same for every student and changes only when `import-timetable` runs. `TimetableCache` keeps it
in memory as one read-only `TimetableSnapshot`: semesters and their calendars, programmes and groups, **each group's
weekly schedule** (its slots with subject, room and semester resolved), the choices of each year and each room's
classes. A request only turns weeks into dates.

- **Invalidation:** the snapshot is labelled with the newest `timetable_import_runs` id. An import is a separate
  process, but it writes its runs in the transaction that writes the timetable, so a new id means new data. The cache
  asks for the id at most every `TimetableCache__VersionCheckSeconds` (10 s; one indexed query) and loads the snapshot
  again when it changed: nine queries in one REPEATABLE READ transaction, so the data and its label come from the same
  database snapshot.
- After an import every instance serves it within 10 s. A read that looks for something the snapshot lacks makes the
  cache check sooner: at once for ids from the database (the group a student has, or just picked and the database
  confirmed), at most once a second for ids a client sent, so made-up ids can't make every request query the
  database. Changed data can be up to 10 s old.
- **One check at a time, and readers don't wait for it:** once a snapshot is in memory, only the request that starts a
  check waits for it (one small query); every other request is answered from memory meanwhile, so a slow database
  never stalls timetable reads. With nothing loaded yet, requests wait for the one load (no stampede).
- The check runs on the cache's own token with a 15 s timeout, so a client that goes away doesn't cancel it for the
  others.
- **Database down:** a check that can't reach the database keeps serving the snapshot in memory (one Warning) and
  tries again an interval later, so timetable reads keep working. With nothing loaded yet, a failed load fails the
  next second's requests at once instead of each trying again in turn.
- `GET /me/timetable` is **one query** (the student's account, group and choices, joined) plus the cached group
  schedule. The class reminders use the same snapshot. **Per-student data is never cached on the server**: a change
  shows on the next request.
- It holds every imported semester (today one: 129 groups, 743 slots).

### HTTP caching

- Every timetable and `/me` GET answers a weak `ETag` (a hash of its JSON) and **304 Not Modified**, with no body, to a
  matching `If-None-Match` (`HttpCaching`).
- Shared data not tied to today (a group's timetable for given dates, the choices, `/rooms`, a room on a given date):
  `Cache-Control: private, max-age=60`. It is the same for everyone but needs sign-in, so it is never `public`: no
  shared cache (a CDN, a proxy) may keep it.
- Shared data that depends on today's date (`/semesters/current`, `/timetable/programmes`, a timetable without dates):
  `private, no-cache`, so the browser asks each time (a 304 when nothing changed) and never shows yesterday after
  midnight.
- A student's own data (`/me…`): `private, no-store`, so a browser never writes it to its disk cache (a shared lab PC
  keeps nothing after sign-out). It still carries its `ETag`: a client that keeps its own copy can send
  `If-None-Match` and get a 304.
- Everything else (writes, errors, health): `no-store`.

### Compression

Brotli or gzip, at the fastest level, when the client accepts it. nginx's `gzip` covers only HTML (its default
`gzip_types`), so the API compresses its own JSON. On for HTTPS: BREACH needs a page that makes the browser send
credentialed requests mixing attacker input with a secret, and this API takes bearer tokens only, never cookies.

### Security headers and request limits

- On every response, errors included (`SecurityHeaders`): `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
  `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'`, `Referrer-Policy: no-referrer`, and
  `Cache-Control: no-store` unless the endpoint sets its own. HSTS (one year) on HTTPS. No `Server` header.
- Request bodies up to `Limits__MaxRequestBodyBytes` (16 KiB; the largest real body is a push token, ~100 bytes),
  enforced by Kestrel: larger is a **413** ProblemDetails.
- A request running longer than `Limits__RequestTimeoutSeconds` (30 s) is cancelled and answered **504**
  ProblemDetails, so a request stuck on the database frees its connection. 30 s leaves room for the first request
  after a start (EF Core compiles its queries then) and for the test notification's 15 s push timeout.
- CORS allows only `Cors__AllowedOrigins__<n>`. Checked on the Pi (2026-11-23): prod `https://campuspulse.thorsp.net`
  and `https://campuspulse-web.pages.dev`; stage the staging and preview Pages sites and the local dev servers
  (`localhost:5173`, `:8081`). Browsers may reuse a preflight for 2 hours (`Access-Control-Max-Age`): every call carries
  `Authorization`, so every call would otherwise need an `OPTIONS` first.

### Database

- **Indexes:** every query behind `/me/timetable`, the settings, the reminder sender and the rooms has an index path,
  checked with `EXPLAIN` on the stage database (with sequential scans off, since its tables are a few pages and the
  planner rightly reads them whole). The one gap was the cache's own version query, which now has
  `ix_timetable_import_runs_imported_at_id` (read backwards: one entry).
- Read paths use `AsNoTracking` (no change-tracking snapshots for rows that are only read).
- **Pool:** at most `Database__MaxPoolSize` (20; or the connection string's size when that setting is unset)
  connections per process instead of Npgsql's 100. Stage and prod share
  one Postgres with `max_connections = 100`, so a spike in both still leaves room for migrations, backups and psql. A
  request that finds all 20 busy waits for one (up to the connection string's `Timeout`).

### Metrics

OpenTelemetry metrics in Prometheus text format at `/metrics`, served **only** on a separate listener the API binds to
`127.0.0.1:<Metrics__Port>` itself, and only to loopback clients (`MetricsSetup`). nginx proxies the API's own port,
which has no `/metrics`; every other path on the metrics port is a 404. Off unless `Metrics__Port` is set.

- `http_server_request_duration_seconds` (request rate, latency and status per route), `aspnetcore_rate_limiting_requests_total`
  (429s by `aspnetcore_rate_limiting_result`), `campuspulse_timetable_cache_lookups_total{result="hit"|"miss"}` and
  `campuspulse_timetable_cache_load_duration_seconds`, `db_client_connection_count{db_client_connection_state="used"|"idle"}`
  and `db_client_connection_max` (the Npgsql pool; labelled with the connection string without its password),
  `db_client_operation_duration_seconds` (query time), `kestrel_*`, `http_client_*` (Microsoft sign-in, Expo),
  `dotnet_*` (GC, memory, thread pool).
- Read them on the Pi: `curl -s http://127.0.0.1:9093/metrics` (stage, once its env file sets `Metrics__Port=9093`).

### Measured

On ThorSPB (laptop, x64), API in Release on Kestrel against Postgres 18 in Docker with stage's timetable (743 slots,
129 groups, 78 rooms), signed in as one student whose group has 17 classes that week. Sign-in as in the tests (a local
signing key), rate limits off for throughput, logging at Warning. `oha`, 16 connections, 5 s warm-up, then 20 s. Before:
`main` at fc5ba5f. These are relative numbers; the Pi is slower.

| Endpoint | Before: requests/s | p95 | After: requests/s | p95 |
|---|---|---|---|---|
| `GET /timetable/programmes` | 835 | 23.8 ms | 13,142 | 2.0 ms |
| `GET /timetable/groups/{id}` (a week) | 411 | 46.9 ms | 13,596 | 2.1 ms |
| `GET /me/timetable` (a week) | 333 | 60.5 ms | 2,162 | 11.2 ms |
| `GET /rooms` | 3,829 | 6.5 ms | 19,505 | 1.5 ms |
| `GET /health` (no database) | 37,442 | 0.8 ms | 29,823 | 1.0 ms |

- **Cold vs cached:** loading the snapshot takes 370 ms in a fresh process (query compilation included) and 74 ms
  after an import in a warm one; the first request after an import took 80 ms, the next ones ~2 ms.
- **Compression** (Brotli): `/timetable/programmes` 21.2 KB → 3.2 KB, `/me/timetable` a week 7.3 KB → 1.4 KB and 31
  days 29.6 KB → 2.2 KB, `/rooms` 4.8 KB → 0.5 KB.
- **Limits** (defaults): one student calling `/me/timetable` as fast as 4 connections allow for 10 s got 115 answers
  (the burst of 100 plus the refill) and 74,571 429s; refusing costs no database work (p95 0.6 ms).
- Measured on the first version of this change; the review fixes after it don't touch the path of a cached read.
- `/health` is ~0.1 ms slower per request: the new middleware (forwarded headers, security headers, compression).

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
