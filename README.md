# api

Backend API for CampusPulse, shared by the [`mobile`](https://github.com/CampusPulse-ro/mobile)
and [`web`](https://github.com/CampusPulse-ro/web) clients.

- **Stack:** .NET 10 (ASP.NET Core) · PostgreSQL 18 · EF Core 10 (Npgsql)
- **Contract:** the OpenAPI document at **`/openapi/v1.json`**. The clients generate their
  typed API client from it, so it is served in every environment, not only Development.

## Layout

```
src/CampusPulse.Domain          entities + pure logic. No dependencies, no EF, no web.
src/CampusPulse.Infrastructure  EF Core, DbContext, (later) migrations.
src/CampusPulse.Api             ASP.NET Core host, DI wiring, endpoints.
tests/CampusPulse.Domain.Tests  unit tests for the pure logic.
tests/CampusPulse.Api.Tests     in-process tests of the HTTP surface (WebApplicationFactory).
```

## Configuration

Environment variables only. The one setting so far:

| Variable | What |
|---|---|
| `ConnectionStrings__Postgres` | Npgsql connection string. Required: startup fails fast without it. The database does not have to be reachable for the API to start. |

## Running it

```bash
docker compose up -d                       # Postgres on 127.0.0.1:5434
dotnet run --project src/CampusPulse.Api   # http://localhost:5280 (connection string from launchSettings.json)
```

- `GET /health` → `{"status":"ok"}` (liveness; does not touch the database)
- `GET /health/ready` → `{"status":"ok"}`, or **503** `{"status":"unavailable"}` when the database
  can't be reached within 3 s (readiness; the deploy checks this one)
- `GET /openapi/v1.json` → the API contract

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

There is no Dockerfile. .NET runs the same build on any OS, so:

- **Raspberry Pi (production):** a systemd service runs the framework-dependent publish
  output (`dotnet CampusPulse.Api.dll`) with its environment from an `EnvironmentFile`. CI
  builds it on GitHub's runners; nothing is built on the Pi.
- **Hosts that only run images** (Cloud Run, Azure Container Apps, Fly.io): the SDK builds
  the image from the csproj, with no Dockerfile and no Docker daemon:

  ```bash
  dotnet publish src/CampusPulse.Api -c Release -t:PublishContainer \
    -p ContainerRegistry=ghcr.io -p ContainerRepository=campuspulse-ro/campuspulse-api
  ```

  One command gives a multi-arch image (`linux-x64` + `linux-arm64`, set by
  `ContainerRuntimeIdentifiers` in the csproj).
