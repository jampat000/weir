---
sidebar_position: 1
title: Docker
---

# Docker Deployment

Weir runs on any machine with Docker, including Synology, Unraid, TrueNAS and Raspberry Pi. Both
64-bit Intel/AMD and ARM are supported.

## The quickest way

Weir is at release-candidate stage, and a release candidate is published under its version tag only, so the examples below name `1.0.0-rc.14`. The `latest` tag arrives with 1.0.0.

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

Open **http://your-server-ip:9347** and create your account. The form also asks for a **setup
code** — get it with `docker logs weir`, or from the `setup-code` file in `./weir-data`. That's it.

`./weir-data` holds Weir's database, settings, logs and backups. Keep it and you keep everything.

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

- **`/srv/media`** is where your media lives on the host. Change it to your own path.
- **`/media`** is where Weir sees it. Use `/media/...` paths when you set up folders in Weir.
- **`WEIR_PUID` / `WEIR_PGID`** make Weir read and write files as that user, so it can move them.
  Run `id your-username` on the host to find the numbers. On Synology it's usually `1026` /
  `100`, on Unraid `99` / `100`.

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

## Work folder placement

By default a workflow's work folder is a private folder inside `/data/weir`, which in Docker is
usually a different volume from your media. Weir can still finish files that way, but each one is
*copied* from the work folder to the output folder rather than moved, which on a large file takes
about as long again as the clean itself. Put the work folder on the same volume as the output
folder and that last step becomes an instant move:

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
      - /srv/media:/media   # work_folder and output_folder both live under here
    restart: unless-stopped
```

Then, in the workflow's settings, set **Work folder** to something like `/media/weir/movies-work` —
alongside `/media/weir/movies` (the output folder) rather than under `/data/weir`.

## What's in the image

- Images are published for **linux/amd64** and **linux/arm64**, under their version tag (`ghcr.io/jampat000/weir:X.Y.Z`); `latest` and `X.Y` follow stable releases only, so they arrive with 1.0.0
- A self-contained .NET server, with the bundled web UI
- ffmpeg, mkvmerge (MKVToolNix), curl and gosu on an Ubuntu base (.NET runtime-deps, noble)
- Runs as the `weir` user (UID/GID 1000 by default, or whatever `WEIR_PUID`/`WEIR_PGID` you set)
- Data volume `/data/weir`, port `9347`
- A built-in health check on `/health`

## Persisting data

Persist `/data/weir` on a durable volume — that's what `./weir-data` or `weir-data:` does in the
recipes above — so the database, settings, logs and backups survive a container replacement. Keep
`WEIR_SESSION_SECRET` stable across upgrades so browser sessions remain valid; if you don't set
one, Weir generates one and keeps it in that same volume.

That folder is protected from other local accounts by whichever install method you use: in Docker
the entrypoint writes secrets with `umask 077` and runs the server as a non-root user; on Windows
the tray locks the folder to its owner; a bare source install should restrict `WEIR_HOME` itself.

## Common changes

| I want to… | Do this |
| --- | --- |
| Use a different port | Change the left number: `"8080:9347"` puts Weir at `http://your-server-ip:8080` |
| Use a different version | `image: ghcr.io/jampat000/weir:X.Y.Z`, with a version from [the releases page](https://github.com/jampat000/Weir/releases) |
| Use HTTPS through a reverse proxy | Set `WEIR_TRUSTED_PROXY_IPS=<your proxy's IP>`. The sign-in cookie becomes HTTPS-only on its own once requests arrive over HTTPS; set `WEIR_SESSION_COOKIE_SECURE=true` only to force it. See [Reverse proxy](reverse-proxy) |
| Reach Weir by a domain name through a reverse proxy | Add it to `WEIR_ALLOWED_HOSTS` — Weir refuses a `Host` header it doesn't recognise. See [Host header allow-list](reverse-proxy#host-header-allow-list) |
| Manage the secret that protects saved API keys yourself | Weir makes its own on first start. To use your own, set `WEIR_CREDENTIALS_SECRET` to a long random value (`openssl rand -hex 32`) **before** you add Sonarr or Radarr |
| Use a GPU | See [hardware acceleration](https://github.com/jampat000/Weir/blob/main/docker/README.md#hardware-acceleration-and-device-passthrough) in the Docker reference. It's optional; Weir doesn't re-encode, so you usually don't need it |

Every variable Weir reads is documented in the [Docker reference](https://github.com/jampat000/Weir/blob/main/docker/README.md).

## What Docker does not do

The container starts Weir. It does not:

- install Sonarr, Radarr, Emby, Jellyfin, or Plex
- configure reverse proxies or HTTPS for you
- run more than one Weir server process against the same database — never point two containers at the same SQLite data
