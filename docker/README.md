# Weir Docker

Weir publishes an all-in-one container image with:

- the Weir server (C# / .NET 10, a self-contained single-file build)
- bundled production web UI, ffmpeg and mkvmerge (MKVToolNix)
- SQLite runtime under `WEIR_HOME`

Images are published for `linux/amd64` and `linux/arm64`:

- `ghcr.io/jampat000/weir:X.Y.Z` (the Git tag is `vX.Y.Z`; the image tag has no `v`), for example `1.0.0-rc.14`
- `ghcr.io/jampat000/weir:X.Y` and `ghcr.io/jampat000/weir:latest`, which follow stable releases only. A release candidate is published under its version tag alone, so these arrive with 1.0.0.

This page is the full reference — every variable Weir reads, plus the recipes for common setups.
For a shorter walkthrough, see the [Quickstart](https://jampat000.github.io/Weir/docs/quickstart) and
[Docker deployment](https://jampat000.github.io/Weir/docs/deployment/docker) pages on the docs site.

## The quickest way

Make a folder, save this as `compose.yaml` inside it:

```yaml
services:
  weir:
    image: ghcr.io/jampat000/weir:1.0.0-rc.14
    container_name: weir
    ports:
      - "9347:9347"
    volumes:
      - ./weir-data:/data/weir
    restart: unless-stopped
```

Then run:

```bash
docker compose up -d
```

Open `http://localhost:9347/` and create your account. If your browser is on another machine, Weir also
asks for a setup code: run `docker logs weir` and look for the line that starts "Weir has no account yet".
That line is logged at Information, so it shows at the default `WEIR_LOG_LEVEL` (`INFO`) and is hidden if you
set the level to `WARNING` or above. The same code is in `weir-data/setup-code`.

`./weir-data` holds Weir's database, settings, logs and backups. Keep it and you keep everything.
No `.env` file or secrets are required to get started — Weir generates its own session secret on
first start and keeps it in that same volume.

## What Weir calls itself

Weir is named after the machine it runs on: the browser tab, the sidebar, System › About and its
alerts say "Weir on my-server". There is nothing to type. In Docker, the machine is the container,
and Docker gives a container a random name unless you set one, so add `hostname:`:

```yaml
services:
  weir:
    hostname: my-server   # what Weir calls itself
```

Without it, System › About shows a reminder to set one.

## With your media folders

Weir can only clean files it can see, so give it your media folders. This is the setup most
people want:

```yaml
services:
  weir:
    image: ghcr.io/jampat000/weir:1.0.0-rc.14
    container_name: weir
    ports:
      - "9347:9347"
    environment:
      - WEIR_PUID=1000   # the user that owns your media folders
      - WEIR_PGID=1000   # that user's group
    volumes:
      - ./weir-data:/data/weir
      - /srv/media:/media
    restart: unless-stopped
```

- `/srv/media` is where your media lives on the host. Change it to your own path.
- `/media` is where Weir sees it. Use `/media/...` paths when you set up folders in Weir.
- `WEIR_PUID` / `WEIR_PGID` make Weir read and write files as that user, so it can move them. Run
  `id your-username` on the host to find the numbers. On Synology it's usually `1026` / `100`, on
  Unraid `99` / `100`.

## Alongside Sonarr, Radarr and a download client

If Weir works next to other apps, **give every container the same folders at the same paths**.
When one app tells another where a file is, that path has to mean the same thing in both.

```yaml
services:
  weir:
    image: ghcr.io/jampat000/weir:1.0.0-rc.14
    container_name: weir
    ports:
      - "9347:9347"
    environment:
      - WEIR_PUID=1000
      - WEIR_PGID=1000
    volumes:
      - ./weir-data:/data/weir
      - /srv/media:/media
    restart: unless-stopped

  sonarr:
    image: lscr.io/linuxserver/sonarr:latest
    environment:
      - PUID=1000
      - PGID=1000
    volumes:
      - ./sonarr-config:/config
      - /srv/media:/media        # same folder, same path as Weir
    ports:
      - "8989:8989"
    restart: unless-stopped

  qbittorrent:
    image: lscr.io/linuxserver/qbittorrent:latest
    environment:
      - PUID=1000
      - PGID=1000
    volumes:
      - ./qbittorrent-config:/config
      - /srv/media:/media        # same folder, same path again
    ports:
      - "8080:8080"
    restart: unless-stopped
```

A folder layout that works well:

```text
/srv/media
├── downloads/complete/movies   ← your download client finishes files here   (Weir's watched folder)
├── downloads/complete/tv
├── weir/movies                 ← Weir puts cleaned files here               (Weir's output folder)
├── weir/tv
├── movies                      ← your library
└── tv
```

## Port

Weir listens on **9347** inside the container — Weir's own default, which spells W-E-I-R on a phone
keypad.

Choose the port on your host with `-p <host port>:9347`, as in the recipes above. To reach Weir at
`http://<host>:8080/` instead, change the left-hand side: `"8080:9347"`.

You rarely need to change the port *inside* the container, but you can: set `PORT`, and publish that
port instead. The server reads `PORT` at startup, and the image's health check follows it.

```bash
docker run --rm \
  -e PORT=9400 \
  -p 9400:9400 \
  -v weir-data:/data/weir \
  ghcr.io/jampat000/weir:1.0.0-rc.14
```

With `network_mode: host` there is no `-p` mapping, so `PORT` is how you move Weir off 9347.

## `docker run` instead of Compose

```bash
docker pull ghcr.io/jampat000/weir:1.0.0-rc.14
docker run --rm \
  -p 9347:9347 \
  -v weir-data:/data/weir \
  ghcr.io/jampat000/weir:1.0.0-rc.14
```

If you want to override defaults with an env file instead of inline `environment:` entries, copy
`docker/.env.example` to `.env.weir` and run `docker compose --env-file .env.weir up -d`.

## Environment variables

### Secrets

| Variable | Purpose |
|---|---|
| `WEIR_SESSION_SECRET` | Signs session cookies and CSRF tokens. Optional — if you don't set it, the container generates a high-entropy secret on first start and keeps it at `$WEIR_HOME/session.secret`, so it survives restarts. Set your own with `openssl rand -hex 32` if you'd rather manage it yourself. Keep it stable across upgrades: changing it signs everyone out. |
| `WEIR_CREDENTIALS_SECRET` | Encrypts saved provider credentials (Sonarr, Radarr, and similar). Optional — if you don't set it, the container generates one on first start and keeps it at `$WEIR_HOME/credentials.secret`. To manage it yourself, set a long random value (`openssl rand -hex 32`), separate from `WEIR_SESSION_SECRET`, **before** you add those connections. If you set it after connections are saved, also list the generated secret from `credentials.secret` in `WEIR_PREVIOUS_CREDENTIALS_SECRETS`, so the keys saved under it still open. |

### Paths

| Variable | Purpose |
|---|---|
| `WEIR_HOME` | Persistent data root inside the container. Defaults to `/data/weir`; the compose recipes above mount a volume here. Holds the SQLite database, settings, logs and backups. |

### Networking and security

| Variable | Purpose |
|---|---|
| `WEIR_ENV` | Defaults to `production` (the image also sets it explicitly), which turns off ASP.NET's developer exception page. There is normally no reason to change this in Docker; `development` is for building Weir from source, not for a container. |
| `WEIR_SESSION_COOKIE_SECURE` | Whether the session cookie is marked HTTPS-only. Defaults to `auto`: the cookie is HTTPS-only when the request arrives over HTTPS (directly, or through a proxy listed in `WEIR_TRUSTED_PROXY_IPS`), so plain `http://` LAN access keeps working. Set `true` to always require HTTPS, or `false` to never require it. |
| `WEIR_TRUSTED_PROXY_IPS` | The IP or CIDR of your immediate reverse proxy. Weir only trusts `X-Forwarded-For` and `X-Forwarded-Proto` from these addresses. Set it when you put Weir behind a proxy. |
| `WEIR_CORS_ORIGINS` | Optional extra hardening. List the origins allowed to make browser requests, for example the address you reach Weir at, or a web app served from another origin; a browser post from any other origin is then refused. Without it Weir does not check where a post came from. Weir refuses to start with `WEIR_CORS_ORIGINS=*` — list real origins instead. |
| `WEIR_ALLOWED_HOSTS` | Extra `Host` header values Weir accepts, beyond IP literals, `localhost`, single-label and local-network names, and the CORS/trusted-browser origins above. Set it to your reverse-proxy domain; a leading `*.` also allows its subdomains. |

### File ownership

The container starts as `root`, remaps the `weir` user, makes sure `WEIR_HOME` belongs to it,
then launches Weir as that unprivileged user. This keeps the app itself non-root while letting
host-mounted media paths match your NAS or Docker user strategy.

| Variable | Purpose |
|---|---|
| `WEIR_PUID` / `PUID` | The UID Weir runs as inside the container. Defaults to `1000`. Set it to the owner of your host media folders so Weir can read and write them. |
| `WEIR_PGID` / `PGID` | The matching GID. Defaults to `1000`. |
| `WEIR_CHOWN_OUTPUT` | When enabled, Weir applies `WEIR_FILE_MODE_OUTPUT` / `WEIR_DIR_MODE_OUTPUT` and the `WEIR_PUID`/`WEIR_PGID` ownership directly to each file or folder it publishes, right after it writes it. Off by default. Applies to files landing in your output folders; it does not touch your watched or work folders — set ownership on those yourself on the host, or choose a `WEIR_PUID`/`WEIR_PGID` that can already write to them. |
| `WEIR_FILE_MODE_OUTPUT` | The file permission mode Weir applies to published files when `WEIR_CHOWN_OUTPUT` is on, as an octal string (e.g. `664`). |
| `WEIR_DIR_MODE_OUTPUT` | The folder permission mode Weir applies to folders it creates for output, as an octal string (e.g. `775`, or `2775` to also set the group-sticky bit). A malformed value refuses to start, with a clear error. |

`WEIR_CHOWN_WATCHED`, `WEIR_CHOWN_TEMP`, `WEIR_DIR_MODE_WATCHED` and `WEIR_DIR_MODE_TEMP` are
still validated at startup (a typo still stops the container) but are not applied to anything —
the watched and work folders are never Weir's own output, so there's nothing to apply them to.
Set ownership and permissions on those host folders directly, or pick a `WEIR_PUID` / `WEIR_PGID`
that can already write to them.

### Image selection

| Variable | Purpose |
|---|---|
| `WEIR_DOCKER_IMAGE` | Overrides the image reference used by helper scripts and env-file workflows. Not read by the container itself. |

## Data and runtime settings

- `WEIR_HOME` defaults to `/data/weir`; mount a volume there if you want the database and runtime
  files to persist (every recipe above already does this).
- if `WEIR_SESSION_SECRET` is not provided, the container generates one automatically and persists
  it to `$WEIR_HOME/session.secret`
- if `WEIR_CREDENTIALS_SECRET` is not provided, the container generates one the same way and persists
  it to `$WEIR_HOME/credentials.secret`
- changing `WEIR_SESSION_SECRET` can require re-entering any credentials that were still encrypted
  with the old session secret

## Work folder placement

A workflow's work folder defaults to a private folder under `WEIR_HOME` (`/data/weir`), which is
usually a different volume from your media in Docker. Weir still finishes files that way, but each
one is copied from the work folder to the output folder rather than moved, roughly doubling the
time the clean's last step takes on a large file. Put the work folder on the same volume as the
output folder — for example both under the `/media` bind mount used elsewhere in this file — and
that copy becomes an instant move instead:

```yaml
volumes:
  - ./weir-data:/data/weir
  - /srv/media:/media   # set the workflow's work_folder and output_folder both under here
```

## Health

The image exposes `GET /health` and includes a Docker `HEALTHCHECK`. Its start period is five minutes: a new image that
has to change the database saves a copy of it in `backups/pre-update` first (it logs "Saving a copy of Weir's data (N MB)
before updating…" when it starts), and the container is not reported unhealthy while that runs. If you set your own
`healthcheck:` in compose, give it a `start_period` as long as the copy of your database takes.

## Hardware acceleration and device passthrough

Weir copies video and audio without decoding them, so a graphics card does nothing for it today and
there is no setting to switch one on. Nothing here is needed to run Processing. The passthrough
below is for when video conversion arrives and uses the card.

`GET /api/v1/processing/hardware` reports what the ffmpeg inside the container was compiled with.
That is not the same as what your host offers — a method being listed does not prove a device is
present — and neither is visible to the container without passthrough.

**Intel QSV / AMD / VAAPI** need the render node:

```yaml
services:
  weir:
    devices:
      - /dev/dri:/dev/dri
```

The container user must be able to read it. On most hosts that means adding the container user to
the `render` group (`group_add: ["render"]`), or matching its gid.

**NVIDIA** needs the NVIDIA Container Toolkit on the host, then:

```yaml
services:
  weir:
    deploy:
      resources:
        reservations:
          devices:
            - capabilities: ["gpu"]
```

## Filesystem events on bind mounts

Processing watches its watched folders so a new file becomes a candidate within seconds, and runs
its periodic scan as a backstop. **Bind mounts frequently deliver no filesystem-change events**,
and neither do most SMB and NFS shares — the events happen on the host, and nothing forwards them
into the container.

This is expected and handled. When the watcher cannot start, Weir:

- falls back to the periodic scan, which finds every file exactly as it did before;
- logs the reason once, not once per tick;
- reports it on `GET /ready` (and `GET /api/v1/system/readiness`) under the `filesystem_watcher` step.

That step stays `ready`. Falling back is slower, not broken, and failing readiness would take a
working instance out of a load balancer over a delay.

If you would rather not be told about it for a given workflow, switch off **Watch this folder for
changes** in that workflow's editor under **Setup › Workflows**. To turn the watcher off for every
workflow, set `WEIR_PROCESSING_WATCHER_ENABLED=0`.

When events *do* work, `WEIR_PROCESSING_WATCHER_DEBOUNCE_SECONDS` (default 3) controls how long
the tree must be quiet before a burst of writes becomes one scan.

## What not to do

- **Do not** run more than one Weir server process against the same database
- **Do not** run multiple containers against the same SQLite database
- **Do not** use `WEIR_CORS_ORIGINS=*` (rejected at startup)

## What Docker does not do

The container starts Weir. It does not:

- install Sonarr, Radarr, Emby, Jellyfin, or Plex
- configure reverse proxies or HTTPS for you
- replace local development docs for source work

## Related files

- `Dockerfile`
- `compose.yaml`
- `docker/.env.example`
- `.github/workflows/release.yml`
