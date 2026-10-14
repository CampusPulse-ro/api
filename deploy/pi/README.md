# Deploying to the Raspberry Pi

Two environments run on the Pi, each a systemd service behind nginx with its own user and database:

| | stage | prod |
|---|---|---|
| URL | https://campuspulse-api-stage.thorsp.net | https://campuspulse-api.thorsp.net |
| Deployed by | a green `ci.yml` run of a push to `main`, if it's still main's tip | a green `release.yml` run for a `vX.Y.Z` release on `main`, newer than the live one |
| Service / user | `campuspulse-api-stage` as `campuspulse-stage` (127.0.0.1:5093) | `campuspulse-api` as `campuspulse-prod` (127.0.0.1:5092) |
| Database | `campuspulse_stage` | `campuspulse_prod` |

## How a deploy happens

There is **no self-hosted runner**: the repo is public, and a Pi runner that public repos can use
would let a fork PR run code on the Pi. Instead the Pi pulls:

1. `ci.yml` finishes on `main`, or `release.yml` finishes after a release is published.
2. GitHub sends a signed `workflow_run` webhook. pi-control checks the signature (its own secret),
   accepts only a successful run of one of those two workflow **files** in this repo, and starts
   `campuspulse-deploy`.
3. [`campuspulse-deploy`](campuspulse-deploy) trusts nothing it was given. It re-reads the run from
   GitHub (repo, workflow file, event, ref, conclusion, commit), deploys stage only when the commit is
   still main's tip, and prod only for a release that isn't a pre-release or draft, whose commit is on
   `main`, and that is newer (in version order) than the live one. For prod it also verifies both
   files' **build attestation**: signed by GitHub for exactly `release.yml` at that tag and commit, so a
   file swapped into the release afterwards is refused.
4. It installs the build into a staging folder (root-owned, read-only for the app), runs the
   migrations as a sandboxed one-off unit under the environment's user, moves the release into
   `releases/<version>`, switches `current`, restarts the service and checks `/health/ready` (which
   touches the database) and that the process really runs from the new folder. If that fails it goes
   back to the release that was live before.
5. Every failure, rollback and prod deploy is posted to Discord.

The script is installed **by hand** and never by a webhook, so a bad build can't change how deploys
run. After changing it, install it again:

```bash
sudo install -m 755 deploy/pi/campuspulse-deploy /usr/local/bin/campuspulse-deploy
```

## Operating

```bash
campuspulse-deploy rollback stage                    # back to the healthy release before the live one
campuspulse-deploy stage <ci-run-id> <sha> --force   # deploy a green main run that isn't the tip
campuspulse-deploy prod <release-run-id> vX.Y.Z --force  # redeploy an older release
journalctl -u campuspulse-api-stage -f               # the service's own output
```

- **A rollback restores code, not schema.** Migrations must keep working with the previous release:
  expand first (add), contract in a later release (remove). See the main README.
- Each environment keeps a history (`/srv/campuspulse/apps/api-<env>/history`) of the releases that
  passed their checks. Rollback walks it backwards, never picks a release that failed, and skips one
  a rollback already left (until it's deployed again). A failed rollback goes back to where it
  started. The newest five healthy releases are kept.
- Every run, by webhook or by hand, appends to `~/.local/state/campuspulse-deploy/deploy.log` (with
  the lock and scratch space) of the user running it; pi-control's `logs/campuspulse_deploy.log` also
  has the webhook-started ones.

## Isolation

- Each environment has its own Unix user, so stage code can't read prod's process or files.
- Releases are owned by root and read-only for the app. The app can write only its log folder.
- The env files `/srv/campuspulse/.config/campuspulse/api-{stage,prod}.env` are root-only (0600).
  systemd reads them before dropping privileges, both for the service and for the migration unit, so
  the database password never appears in a command line.
- The units carry systemd's sandboxing (`ProtectSystem=strict`, `SystemCallFilter=@system-service`,
  no capabilities, and so on); `MemoryDenyWriteExecute` is left out because .NET's JIT needs it off.
  `/srv`, `/mnt` and `/nas` are hidden except the environment's own releases (read-only) and log
  folder. The migration unit gets the same sandbox.
- **Network guard** ([`netguard.nft`](netguard.nft); loaded by
  [`campuspulse-netguard.service`](campuspulse-netguard.service), again by each unit before it starts,
  and by the deploy right before migrations): **deny by default**. The two app users may only answer
  connections made to them (nginx), reach their Postgres on 127.0.0.1:5435, and use the egress proxy on
  127.0.0.1:8899 (see [Egress proxy](#egress-proxy)). No other local service,
  no LAN or tailnet, and no public internet either: the Pi's own public IP comes back through the
  router's hairpin NAT looking like LAN traffic, so "public only" isn't safe. A feature that needs an
  outside service (push notifications, error reporting) adds its host to the proxy's allow-list, never a
  rule here. The
  units also drop `AF_UNIX`, so world-writable local sockets are out of reach.

## Egress proxy

The guard lets the app users reach nothing outside the Pi. For the few services the API does need, it
goes through **tinyproxy** on `127.0.0.1:8899` ([`tinyproxy.conf`](tinyproxy.conf)): HTTPS (CONNECT to
port 443) only, default-deny, to hosts listed in [`egress-allow`](egress-allow) as `^host:443$`
(`FilterURLs On`: plain-HTTP requests never match, so they're refused too). Today that's only
`login.microsoftonline.com` (sign-in metadata and keys). The env files set
`HTTPS_PROXY=http://127.0.0.1:8899` and `NO_PROXY=127.0.0.1,localhost`; .NET's HttpClient honours them.

To allow a new host (push notifications, error reporting): add one `^host\.name:443$` line to `egress-allow` in
the same PR as the feature, then on the Pi `sudo install -m 644 deploy/pi/egress-allow
/etc/tinyproxy/campuspulse-allow && sudo systemctl restart tinyproxy`. Don't widen the guard instead.

The proxy itself is confined by the guard too (user `tinyproxy`: DNS on Tailscale's resolver and HTTPS to
public addresses only), and logs every tunnel as `CONNECT host:443`. Known limits: stage and prod share
one proxy (20 connections), so a misbehaving stage could slow prod's key refresh; and the guard can't
tell the Pi's own public IP from any other public address, so the allow-list is what keeps the proxy
off the hairpin path.

## Request ids

nginx gives every request its own id and passes it to the app as `X-Request-Id`, ignoring any id the
client sent. The app returns it and puts it on every log line; nginx logs it as `rid=` in
`/var/log/nginx/campuspulse.access.log` ([`nginx-campuspulse-request-id.conf`](nginx-campuspulse-request-id.conf)),
so a request can be followed from nginx into the app's logs.

## One-time setup (already done; for a new machine)

- Users `campuspulse-stage` and `campuspulse-prod` (system, no shell, no home).
- `/srv/campuspulse/apps/api-{stage,prod}/` (root), `logs/{stage,prod}` (owned by each user, 0750),
  `.config/campuspulse/` (root, 0700).
- Postgres: [`postgres.compose.yml`](postgres.compose.yml) at `/srv/campuspulse/postgres/compose.yml`,
  bound to `127.0.0.1:5435`, with a role and database per environment. Dumped nightly with the Pi's
  other databases.
- Env files with `ConnectionStrings__Postgres` (quoted: it contains semicolons) and the CORS origins
  (`Cors__AllowedOrigins__0=…`).
- `/etc/campuspulse/netguard.nft` and `campuspulse-netguard.service` (enabled), then the units
  [`campuspulse-api.service`](campuspulse-api.service) and
  [`campuspulse-api-stage.service`](campuspulse-api-stage.service) in `/etc/systemd/system/`. Each
  stays disabled until its first successful deploy enables it.
- **The units are installed by hand, like the deploy script; a deploy never touches them.** After
  changing one, install it again and reload (the new settings apply at the next restart or deploy):
  `sudo install -m 644 deploy/pi/campuspulse-api{,-stage}.service /etc/systemd/system/ && sudo systemctl daemon-reload`.
  Their non-secret settings live in the unit (`ASPNETCORE_URLS`, `Logging__File__Path`, which must
  stay inside the unit's writable `BindPaths` folder or the API refuses to start); secrets stay in the
  env files.
- nginx: [`nginx-campuspulse.conf`](nginx-campuspulse.conf) (both names; DNS: CNAMEs to `thorsp.net`; TLS by
  certbot, cert `campuspulse`) and [`nginx-campuspulse-request-id.conf`](nginx-campuspulse-request-id.conf) in
  `/etc/nginx/conf.d/`.
- tinyproxy: write [`tinyproxy.conf`](tinyproxy.conf) to `/etc/tinyproxy/` and [`egress-allow`](egress-allow) to
  `/etc/tinyproxy/campuspulse-allow` BEFORE `apt install tinyproxy -o Dpkg::Options::=--force-confold` (its
  default config listens on every interface), plus [`tinyproxy.service.d-campuspulse.conf`](tinyproxy.service.d-campuspulse.conf)
  as `/etc/systemd/system/tinyproxy.service.d/campuspulse.conf` (it starts only after the guard); the env
  files get `HTTPS_PROXY` / `NO_PROXY`.
- The repo webhook (event `workflow_run`) pointing at pi-control's `/gh/campuspulse-deploy`, with its
  own secret.
