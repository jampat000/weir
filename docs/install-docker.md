# Install Weir with Docker

Weir runs as one container: the server, the web app, ffmpeg and mkvmerge are all inside it. It works on any
64-bit Linux machine or NAS with Docker (Synology, Unraid, TrueNAS, a Raspberry Pi running a 64-bit system), on Intel/AMD
(`amd64`) or ARM (`arm64`).

This guide gets you from nothing to a running Weir, then lines its folders up with your download client and
Radarr, Sonarr or Deluno. Every variable Weir reads is listed in the [Docker reference](../docker/README.md).

## Which image

The image is `ghcr.io/jampat000/weir`. The tag after the colon picks the version.

| Tag | What it is | Use it when |
| --- | --- | --- |
| `1.0.0-rc.14` | Exactly that release, forever | You want to choose when Weir changes. **Use this while Weir is a release candidate.** Newer ones are listed on the [Releases page](https://github.com/jampat000/Weir/releases). |
| `latest` | The newest stable release | You are happy for `docker compose pull` to move you to whatever is newest. It does not exist yet: it arrives with 1.0.0. |

Release candidates are published under their version tag only. They never move `latest`, so until 1.0.0 you
name the version, as every example below does.

The image tag has no `v`: the release `v1.0.0-rc.14` is the image `1.0.0-rc.14`.

## 1. The quickest start

Make a folder for Weir, and save this as `compose.yaml` inside it:

```yaml
services:
  weir:
    image: ghcr.io/jampat000/weir:1.0.0-rc.14
    container_name: weir
    hostname: my-server        # what Weir calls itself: "Weir on my-server"
    ports:
      - "9347:9347"
    volumes:
      - ./weir-data:/data/weir
    restart: unless-stopped
```

Start it:

```bash
docker compose up -d
```

Open `http://<your-server>:9347/`. That is it. The next section adds your media folders.

Two things to know:

- **`hostname`** is the name Weir shows for itself (the browser tab, alerts, **System › About**). Set it to your
  server's name. Without it, Docker invents twelve random characters.
- **`./weir-data`** holds Weir's database, settings, logs and backups. Keep it and you keep everything.

## 2. A complete setup

This is the version most people want: your media folders, the right file owner, and nothing left to guess.

```yaml
services:
  weir:
    image: ghcr.io/jampat000/weir:1.0.0-rc.14
    container_name: weir
    hostname: my-server
    ports:
      - "9347:9347"
    environment:
      - WEIR_PUID=1000             # the user that owns your media folders
      - WEIR_PGID=1000             # that user's group
      # Optional: encrypts the Radarr, Sonarr and Deluno keys Weir saves. Weir makes its own if you leave this out.
      # Make one with:  openssl rand -hex 32   and keep it with your backups.
      # - WEIR_CREDENTIALS_SECRET=<paste the value here>
    volumes:
      - ./weir-data:/data/weir     # Weir's own data
      - /srv/media:/media          # your media, downloads included (see step 4)
    restart: unless-stopped
```

The same thing as a single `docker run`:

```bash
docker run -d \
  --name weir \
  --hostname my-server \
  -p 9347:9347 \
  -e WEIR_PUID=1000 \
  -e WEIR_PGID=1000 \
  -v "$(pwd)/weir-data:/data/weir" \
  -v /srv/media:/media \
  --restart unless-stopped \
  ghcr.io/jampat000/weir:1.0.0-rc.14
```

What each part means:

| Setting | What it does |
| --- | --- |
| `9347:9347` | Weir's port. The left number is the port on your server. Change it to `8080:9347` to reach Weir on port 8080. The right number stays 9347. |
| `/data/weir` | Where Weir keeps its data inside the container. Always mount something here, or your data disappears with the container. |
| `/srv/media:/media` | Left is your media folder on the server, right is where Weir sees it. Change the left side. Use `/media/...` paths when you set up folders in Weir. |
| `WEIR_PUID` / `WEIR_PGID` | The user and group Weir runs as, so it can read and write your files. Run `id <your-username>` on the server to find them. Synology is usually `1026` / `100`, Unraid `99` / `100`. `PUID` and `PGID` work too. Neither may be `0`: Weir refuses to run as root. |
| `WEIR_CREDENTIALS_SECRET` | Optional. Weir makes one on first start and keeps it in `weir-data`. Set your own before you add Radarr, Sonarr or Deluno if you want to manage it yourself. If you lose it later, you re-enter those keys. |

**Time zone.** There is nothing to set on the container. Weir keeps its own time zone, which you choose in the
setup wizard and can change under **System › About**. Schedules and dates follow that.

**Session secret.** You do not need one. Weir makes its own on first start and keeps it in `weir-data`. Only set
`WEIR_SESSION_SECRET` if you want to manage it yourself, and then keep it the same between updates, or everyone is
signed out.

Leave `user:` out of the compose file. The container starts as root only to match `WEIR_PUID` and `WEIR_PGID`, then
runs Weir as that user. If you set `user:` yourself, `WEIR_PUID` is ignored and `weir-data` must already be writable by that
user.

## 3. Create your account

Open `http://<your-server>:9347/`. Weir asks for a username and a password. This creates the admin account.

It also asks for a **setup code**, because your browser is not on the server itself. Get it with:

```bash
docker logs weir
```

Look for the line that starts "Weir has no account yet". The same code is in `weir-data/setup-code`. It stops
working once an account exists.

Then the setup wizard asks how your downloads reach Weir and which folders to watch. The
[Quickstart](../docs-site/docs/quickstart.md#3-follow-the-setup-wizard) walks through it.

## 4. Line the folders up

This is the part that matters most. When Radarr, Sonarr, Deluno, your download client and Weir hand a file to
each other, they pass its **path**. That path has to mean the same place to all of them.

The easy way is to give every container the **same folder at the same path**. The example below uses one folder
on the server, `/srv/media`, mounted as `/media` in every container.

On the server:

```text
/srv/media
├── downloads/complete/movies    the download client finishes files here
├── weir/movies                  Weir puts the cleaned files here
├── weir/work/movies             Weir's private work area
└── movies                       your Radarr library
```

Each container mounts it the same way:

```yaml
  weir:
    volumes:
      - ./weir-data:/data/weir
      - /srv/media:/media

  qbittorrent:                       # your download client
    volumes:
      - ./qbittorrent-config:/config
      - /srv/media:/media

  radarr:
    volumes:
      - ./radarr-config:/config
      - /srv/media:/media
```

Those are the volume lines to copy into your own compose file for those apps. Their image, port and user
settings stay as you already have them. Use the user and group IDs you gave Weir, so all three can read and
write the same files.

Then set the folders in each app:

| App | Setting | Value |
| --- | --- | --- |
| qBittorrent | Default save path for the `movies` category | `/media/downloads/complete/movies` |
| Weir (**Setup › Workflows**, edit the workflow) | **Watched folder** | `/media/downloads/complete/movies` |
| Weir | **Output folder** | `/media/weir/movies` |
| Weir | **Work folder** | `/media/weir/work/movies` |
| Radarr | Root folder | `/media/movies` |
| Radarr (**Settings › Download Clients › Remote Path Mappings**) | Host | the download client's address, exactly as in its Host field |
| | Remote Path | `/media/downloads/complete/movies/` |
| | Local Path | `/media/weir/movies/` |

Radarr then imports from Weir's output folder and only ever sees cleaned files. Weir's workflow page has a
**What your media manager needs** section that shows the exact mapping with copy buttons, and **Check again**
reads Radarr's settings (it never changes them) and tells you if something does not line up.

Why the work folder sits on the same volume as the output folder: when both are on one volume, Weir finishes
a file by moving it, which is instant. If the work folder is on a different volume (by default it is inside
`/data/weir`), Weir copies it instead, which takes about as long again as the cleaning.

Weir never deletes the original download for a workflow linked to Sonarr or Radarr, so a torrent keeps
seeding.

**Sonarr** works the same way with a `tv` folder. **Deluno** needs no remote path mapping when it sees the same
paths: Deluno hands Weir each file, Weir cleans it, and Deluno imports the result. If Deluno sees a different path,
add a path mapping in Deluno. The [media managers guide](../docs-site/docs/guides/media-managers.md) covers
Deluno, Sonarr and Radarr in full.

## Reach Weir from another computer

Docker publishes port 9347 on every network interface of your server, so any computer on your network can open
`http://<your-server>:9347/`. That is all you need at home.

Do not forward port 9347 on your router. To reach Weir from outside, put it behind a reverse proxy with HTTPS.
The [reverse proxy guide](../docs-site/docs/deployment/reverse-proxy.md) shows how. To keep Weir off the network
and reach it only through a proxy on the same machine, publish it on the loopback address instead:
`"127.0.0.1:9347:9347"`.

## Update

Weir updates its own database when it starts, so updating is just replacing the container.

With a pinned tag, edit the tag in `compose.yaml` first (for example `1.0.0-rc.14`), then:

```bash
docker compose pull
docker compose up -d
```

Once 1.0.0 is out and you use `latest`, only the two commands are needed. With `docker run`, pull the new image, then stop and
remove the old container and run the same `docker run` command again:

```bash
docker pull ghcr.io/jampat000/weir:1.0.0-rc.14
docker stop weir && docker rm weir
```

Your data is in `weir-data`, so it carries over. Take a backup first (below). Going back to an older version
after an upgrade is only safe from a backup made before it. When the new version has to change the database, it first
saves a copy of the database, your settings files and the two secrets in `weir-data/backups/pre-update` (the newest five
are kept), and does not start if it cannot, saying why in the container log. It logs "Saving a copy of Weir's data (N MB)
before updating…" first; the image's healthcheck allows five minutes for that. If you set your own `healthcheck:`, give it a
`start_period` as long as the copy takes. **System › About** says where the latest copy is, inside the `weir-data` volume.

## Back up

Weir has two kinds of backup.

- **Settings backup, inside Weir.** **System › Backups** keeps a rolling copy of your configuration (your workflows,
  rules and connections) and has **Back up now** and **Restore from file**. Copies are saved in `weir-data/backups`.
- **Everything, from Docker.** The whole `weir-data` folder is Weir's database, logs, secrets and backups. To copy
  it safely, stop Weir first so the database is not in the middle of a write:

  ```bash
  docker compose stop weir
  tar -czf weir-backup-$(date +%F).tgz weir-data
  docker compose start weir
  ```

  Keep the file somewhere other than the same disk. If you set `WEIR_CREDENTIALS_SECRET` yourself, store it with the
  backup, because without it the saved connection keys cannot be read. The one Weir makes is inside `weir-data`.

To restore: stop Weir, put the folder back, start it.

Never run two Weir containers against the same `weir-data`. SQLite is for one process at a time.

## If something goes wrong

| Problem | Try this |
| --- | --- |
| Cannot open Weir | `docker ps` shows whether it is running. Weir's health check is at `http://<your-server>:9347/health`. |
| Files sit in the watched folder and nothing happens | The path in Weir must be the path **inside the container** (`/media/...`), not the host path (`/srv/media/...`). In Docker, Weir may take up to five minutes to notice a new file. |
| "Permission denied" on a file | Set `WEIR_PUID` and `WEIR_PGID` to the owner of your media folders. |
| Signed out after every restart | `WEIR_SESSION_SECRET` is set to something that changes. Remove it and let Weir manage it. |
| Weir shows a random name | Add `hostname:`. |
| Anything else | `docker logs weir` and **System › Logs**, then [open an issue](https://github.com/jampat000/Weir/issues) with what they say. |
