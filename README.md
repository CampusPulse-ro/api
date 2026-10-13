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
- `GET /openapi/v1.json` → the API contract

## Tests

```bash
dotnet test
```

CI (`.github/workflows/ci.yml`) builds with warnings as errors, runs the tests, publishes the
app as an artifact, and builds the container image for `linux/amd64` and `linux/arm64`
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
