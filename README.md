# Weir

**Keep the audio and subtitle tracks you want. Drop the rest.**

Movie and TV files often arrive with a dozen audio tracks and subtitles in languages you will never use. They take up space, and they make every file a little harder to live with. Weir removes them, so each file ends up with just the tracks you chose. It does this for new downloads and for the library you already have, and it never re-encodes, so there is no loss of quality.

It sits beside the tools you already use. Deluno, Radarr or Sonarr finds and downloads a release, Weir cleans it, and the manager imports the cleaned file. Deluno hands files to Weir itself and waits for the result. Radarr and Sonarr import from the folder Weir cleans into. Weir works just as well on its own, on a folder of home videos.

It runs on Windows and in Docker. It is for one person, and your files and settings stay on your machine.

> **Weir is early.** It is in active, hard testing, and it is not finished. Expect some
> rough edges, and please do not point it at the only copy of anything you cannot
> replace. Weir works on a copy of each file and only replaces the original once the
> new one checks out, and everything it does is written to Activity. If something looks
> wrong, confusing or just ugly, [tell us](https://github.com/jampat000/Weir/issues/new/choose).
> That is the most useful thing you can do right now, and every report gets read.

<img src="docs/assets/screenshots/dashboard-live.png" width="49%"> <img src="docs/assets/screenshots/dashboard-system.png" width="49%">

*The Dashboard: every file being worked on right now, and the health of the machine*

<img src="docs/assets/screenshots/activity.png" width="49%"> <img src="docs/assets/screenshots/workflows.png" width="49%">

*Activity: every file Weir handled and what it did, and Workflows: the routes a file takes through Weir*

<img src="docs/assets/screenshots/logs.png" width="67.4%"> <img src="docs/assets/screenshots/file-detail.png" width="30.6%">

*The Logs, and one file's story: which tracks Weir kept, which it removed, and why*

## What it does, and why it is safe

Say you keep English and Japanese audio and English subtitles. A new episode lands in your downloads folder with eleven audio tracks and nineteen subtitle tracks. Weir cleans it into a file with the tracks you chose, and your media manager imports that file. Nobody had to open a tool.

- **You set the rules once.** Keep these audio languages, keep these subtitles, drop commentary tracks. Each workflow can have its own rules.
- **It never re-encodes.** Tracks are copied as they are, so quality is untouched and a file takes seconds, not hours.
- **It works on a copy.** Weir replaces the original only once the new file checks out. If anything goes wrong, you keep the file you started with.
- **It cleans new downloads.** A **workflow** watches a folder, cleans each file that lands there, and puts the result in an output folder.
- **It cleans the library you already have.** Weir scans it, shows what it would remove and how much space that frees, and changes nothing until you confirm.
- **It works with your media manager.** Deluno, Radarr and Sonarr connect in a few steps. See [Working with Deluno](#working-with-deluno).
- **It tells you the truth.** The Dashboard shows live work. Activity records every file: which tracks were kept and removed, and why a file was held or skipped.
- **It comes with everything it needs.** ffmpeg and MKVToolNix are included. There is nothing else to install.

You can also set schedules, so a large library is cleaned overnight, and send alerts to Discord or any webhook when a file finishes or fails.

## Install on Windows

You need 64-bit Windows 10 or 11. You do not need administrator rights to install Weir.

1. Download `Weir-win-Setup.exe` from [Releases](https://github.com/jampat000/Weir/releases). Weir is a pre-release for now, and GitHub's "Latest" button skips pre-releases, so pick the newest release from the list. Beside the installer you will find `SHA256SUMS.txt`, if you want to check the download.
2. Run it. Weir is not code-signed, so Windows SmartScreen may say "Windows protected your PC". Choose **More info**, then **Run anyway**. That warning means the publisher is not verified, not that anything is wrong with the file. Your browser may also say the file "isn't commonly downloaded". Choose **Keep**.
3. Weir installs for your user account and starts. Its icon appears in the system tray, next to the clock.
4. **Weir asks for a port.** Keep **9347** unless something else uses it.
5. **Windows asks for permission once.** Weir answers on this PC only until you let other devices in, and the first start asks whether you want to. Say yes if you want to open Weir from your phone or another computer. Windows asks for administrator approval, and Weir adds one firewall rule named **Weir** that covers every network type. Say no if you only use Weir on this PC. You can change your mind later in **System › About**, by choosing **Devices on my network** or **This PC only**, or from the tray icon's menu.
6. A notice from the tray icon says Weir is running. Click it, or the icon, and your browser opens Weir at [http://localhost:9347](http://localhost:9347). Weir never opens a browser window by itself.

| | |
|--|--|
| Program | `%LocalAppData%\Weir` |
| Your data (database, settings, logs, backups) | `C:\ProgramData\Weir` |
| Start-up log (why Weir did something at start-up) | `C:\ProgramData\Weir\tray-host.log` |

Your data lives outside the program folder on purpose, so updating the program never touches it. Weir runs as you, in your own sign-in session, and not as a Windows service. That way it can reach your mapped network drives.

Click the tray icon to open Weir. Right-click it to pause processing, restart Weir, copy its address, open the data or logs folder, change the port, allow other devices, start Weir with Windows, check for updates or quit. The [Windows guide](docs/install-windows.md) has the details, including how to install Weir from a script.

## Install with Docker

Images are published to GitHub's container registry as `ghcr.io/jampat000/weir`, for 64-bit Intel/AMD (`amd64`) and ARM (`arm64`). That covers Synology, Unraid, TrueNAS, Linux servers and a Raspberry Pi running a 64-bit system.

Use a version tag such as `1.0.0-rc.14`. Release candidates are published under their version tag only, so `latest` does not exist yet. It arrives with 1.0.0 and will follow stable releases. The tag has no `v`: the release `v1.0.0-rc.14` is the image `1.0.0-rc.14`. Newer ones are on the [Releases page](https://github.com/jampat000/Weir/releases).

### Getting the folders right first

This is the part that most often goes wrong, with any media tool, so decide it before you start.

When your download client, Weir and your media manager hand a file to each other, they pass its **path**. That path has to mean the same place to all of them. The easy way is one folder on the host, mounted at the same place in every container:

```text
/srv/media                        <- on the host
├── downloads/complete/movies     <- the client finishes here
├── weir/movies                   <- Weir puts cleaned files here
├── weir/work/movies              <- Weir's private work area
└── movies                        <- your library
```

Each container mounts `/srv/media` as `/media`. Keep Weir's work folder beside its output folder: on the same volume, Weir finishes a file by moving it, which is instant, instead of copying it. Weir's own data goes in a separate `weir-data` folder.

### docker run

Replace `1000:1000` with the user and group that own `/srv/media` on your server (`id` will tell you).

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

Then open `http://<server>:9347`, where `<server>` is the name or address of the machine running Docker.

`--hostname` is the name Weir shows for itself ("Weir on my-server"). Without it, Docker invents twelve random characters. `weir-data` holds Weir's database, settings, logs and backups: keep it and you keep everything. `WEIR_PUID` and `WEIR_PGID` make Weir read and write files as that user. Do not set `--user` as well.

There is no time zone to set on the container. You choose Weir's in the setup wizard.

### docker-compose.yml

This puts a qBittorrent container beside Weir, with the shared folder laid out as above. If you already run your own client, keep only the `weir` part and give your client the same `/srv/media:/media` mount.

```yaml
services:
  weir:
    image: ghcr.io/jampat000/weir:1.0.0-rc.14
    container_name: weir
    hostname: my-server
    ports:
      - "9347:9347"
    environment:
      WEIR_PUID: 1000
      WEIR_PGID: 1000
      # Optional: Weir makes its own. Set one to manage it yourself, before you connect Deluno, Radarr or Sonarr.
      # It encrypts the keys Weir saves. Make one with: openssl rand -hex 32
      # WEIR_CREDENTIALS_SECRET: <paste the value here>
    volumes:
      - ./weir-data:/data/weir
      - /srv/media:/media
    restart: unless-stopped

  qbittorrent:
    image: lscr.io/linuxserver/qbittorrent:latest
    container_name: qbittorrent
    environment:
      PUID: 1000
      PGID: 1000
      TZ: Etc/UTC  # set your own time zone
      WEBUI_PORT: 8081
    ports:
      - "8081:8081"
    volumes:
      - ./qbittorrent-config:/config
      - /srv/media:/media
    restart: unless-stopped
```

Start it:

```bash
docker compose up -d
```

Then set the folders in each app, using the `/media/...` paths, which are the paths inside the containers:

| App | Setting | Value |
| --- | --- | --- |
| qBittorrent | Save path for the `movies` category | `/media/downloads/complete/movies` |
| Weir | Watched folder | `/media/downloads/complete/movies` |
| Weir | Output folder | `/media/weir/movies` |
| Weir | Work folder | `/media/weir/work/movies` |
| Radarr | Root folder | `/media/movies` |

Weir's workflow page checks these for you with **Check again**, and says what to fix. Give Radarr and Sonarr the same `/srv/media:/media` mount as well. [Working with Deluno](#working-with-deluno) and [First run](#first-run) cover connecting them.

The [Docker guide](docs/install-docker.md) goes further, and [docker/README.md](docker/README.md) lists every variable, including GPU access and file ownership on network shares.

## First run

The first time you open Weir it asks you to create your account. This is the admin account. If your browser is on another machine than Weir, Weir also asks for a **setup code**. On Windows it is in the file `C:\ProgramData\Weir\setup-code`. On Docker, run `docker logs weir` and look for the line that starts "Weir has no account yet". The code stops working once an account exists.

Then a short setup wizard asks how your downloads reach Weir:

- **Deluno.** Enter its address and API key. Weir tests the connection and sets up a workflow for each Deluno library that is set to refine before import. You do not type any folders. See [Working with Deluno](#working-with-deluno).
- **Sonarr or Radarr.** Enter the address and API key (each shows its key on the General page of its own settings). Weir reads where your download client saves finished downloads and offers a Movies and a TV workflow with the folders filled in. Tick the ones you want and change any folder. Nothing is created until you press **Finish setup**.
- **A download client on its own**, such as SABnzbd or qBittorrent. It suggests a watched folder.
- **Neither.** Give Weir a watched folder and an output folder for Movies, and the same for TV.

Then choose your time zone, and you are set up. You can skip the wizard and use **Setup › Workflows** and **Setup › Connections** later.

Next, tell Weir what to keep. Under **Setup › Rules › Profiles**, set the audio and subtitle languages a workflow keeps. For example: keep English and Japanese audio, keep English subtitles, drop commentary.

Then try it. Put a video file in a watched folder. Weir usually notices within seconds. It waits until the file has stopped changing for a minute, so a download that is still arriving is left alone. The file appears on the Dashboard while Weir works on it, and in Activity once it is done.

**To clean a library you already have,** open **Library**, pick the library from the title and press **Check again**. Weir shows what it would remove and how much space that frees before it changes anything.

### Sonarr and Radarr

Your download client finishes into Weir's watched folder, Weir writes the cleaned copy to its output folder, and Sonarr or Radarr import from there. You connect them with a **remote path mapping** that says "when the download client reports a file in the downloads folder, look in Weir's output folder instead". Sonarr then only ever sees cleaned files. With the folders from the Docker example, in Radarr under **Settings › Download Clients › Remote Path Mappings**:

| Field | Value |
| --- | --- |
| Host | exactly what is in your download client's Host field |
| Remote Path | `/media/downloads/complete/movies/` |
| Local Path | `/media/weir/movies/` |

Weir's workflow page shows the exact mapping to add, with copy buttons, and **Check again** reads Radarr's settings (it never changes them) to confirm. Weir never deletes the original download for a workflow linked to Sonarr or Radarr, so a torrent keeps seeding. Sonarr works the same way with a `tv` folder. The [media managers guide](https://jampat000.github.io/Weir/docs/guides/media-managers) has all of it.

## Reaching Weir from another computer, safely

Weir listens only on the computer it runs on until you allow more, because a fresh install has no password yet and nothing else on your network should be able to reach it.

- **Windows (the installed app):** answer yes to the firewall question on the first start. If you said no and want to change it, open **System › About** and choose **Devices on my network**, or right-click the tray icon and choose **Allow other devices on your network**. **This PC only** turns it off again.
- **Docker:** `-p 9347:9347` makes Weir available on every network interface of the machine. To keep it to the machine itself, publish it as `-p 127.0.0.1:9347:9347` (in compose, `"127.0.0.1:9347:9347"`) and put a reverse proxy in front.

Weir has one admin account protected by your password, so treat it like the admin page of a router. Choose a long, unique password. Do not forward its port from your router to the internet.

If you want to reach it away from home, the safest options are a VPN you already trust (WireGuard, Tailscale and the like), or a reverse proxy that serves it over HTTPS. The [reverse proxy guide](https://jampat000.github.io/Weir/docs/deployment/reverse-proxy) shows how.

## Working with Deluno

Deluno is a separate, companion project that finds, downloads and files your movies and TV. Weir is the clean-up step before Deluno imports a download. This is how Deluno's *refine before import* works:

1. Your download client finishes a download.
2. Deluno hands the file to Weir and waits.
3. Weir cleans it into its output folder.
4. Deluno imports the cleaned result, through the same checks as any other import.

A few things Weir takes care of, so you do not have to:

- **It sets itself up from Deluno.** Once Deluno is connected under **Setup › Connections**, Weir makes a workflow for each Deluno library that is set to refine before import, fills in the watched and output folders, and keeps them in step when you change them in Deluno.
- **It never touches the original download.** A workflow linked to Deluno does not move or delete the file your download client fetched, which may still be seeding.
- **It acts only on hand-offs.** A linked workflow does not scan its watched folder, so Weir can never pick up a download that is still being fetched.
- **Each file is cleaned once.** If Deluno hands over a file Weir already cleaned, Weir does not clean it again. Activity says **Skipped: already done**, and Deluno gets the same cleaned copy, never an error. A changed file, such as an upgrade, is cleaned as normal.

Deluno and Weir are independent. Each works without the other, and neither contains code for the other. Deluno's Windows installer offers Weir in its list of extras. Otherwise install Weir with the steps above, then connect it in Deluno with **Connect processor** on its processing settings page. If you are happy with files exactly as downloaded, you do not need Weir.

## Updating, backing up and uninstalling

**Windows.** Weir checks GitHub for new versions and tells you through the tray icon. **System › About** has the **Update mode**: **Auto**, **Download only** or **Notify only**. Updates need no administrator rights and keep your data. A release candidate is offered the next release candidate as well as stable releases, while a stable install is only ever offered stable ones. You can also download the new `Weir-win-Setup.exe` and run it: it installs over the old one.

Before a big update, take a backup under **System › Backups**. It keeps a rolling copy of your configuration, and you can copy it somewhere else. To back up everything, quit Weir from the tray icon and copy `C:\ProgramData\Weir`.

To uninstall, use **Settings › Apps › Installed apps** in Windows. That removes the program and its shortcuts, and the **Weir** firewall rule if you run the uninstaller as administrator. Your data in `C:\ProgramData\Weir` stays, and so do the files in your media folders: Weir never deletes those by uninstalling. Delete `C:\ProgramData\Weir` yourself if you want it gone.

**Docker.** There is no updater inside the container. Take a backup, change the version tag in your compose file, and run:

```bash
docker compose pull weir
docker compose up -d
```

Keep the same `weir-data` mount so your settings carry over. To back up everything, stop Weir first so the database is not mid-write, then copy the folder:

```bash
docker compose stop weir
tar -czf weir-backup-$(date +%F).tgz weir-data
docker compose start weir
```

Keep the copy somewhere other than the same disk, together with `WEIR_CREDENTIALS_SECRET` if you set one. Databases only move forward, so to go back to an older version you must also restore a backup taken before the update. To uninstall, run `docker compose down` and delete the image. Your folders stay where they are until you remove them.

## Getting help

- **Something broke, looks wrong, or is confusing:** open an [issue](https://github.com/jampat000/Weir/issues/new/choose) with the bug report form. Say what you did, what you expected and what happened. Lines from **System › Logs**, or `docker logs weir`, help a great deal. Leave out API keys and passwords.
- **A security problem:** please do not post details in a public issue. [SECURITY.md](SECURITY.md) says how to report it privately.
- **What changed in each version:** the [release notes](https://github.com/jampat000/Weir/releases) and the [changelog](CHANGELOG.md).
- **Stuck:** the "If something goes wrong" sections of the [Windows guide](docs/install-windows.md#if-something-goes-wrong) and the [Docker guide](docs/install-docker.md#if-something-goes-wrong) cover the usual first checks, and [SUPPORT.md](SUPPORT.md) lists what else helps.

## More documentation

- [Documentation site](https://jampat000.github.io/Weir/): quickstart, installing, reverse proxies, security and the API
- [Windows guide](docs/install-windows.md) and [Docker guide](docs/install-docker.md): installing, updating and removing, step by step
- [Docker reference](docker/README.md): every variable, GPUs, file ownership, network shares
- [Connecting Deluno, Sonarr and Radarr](https://jampat000.github.io/Weir/docs/guides/media-managers)
- [How Weir keeps your files safe](https://jampat000.github.io/Weir/docs/guides/file-lifecycle)
- [Architecture](ARCHITECTURE.md)
- [Changelog](CHANGELOG.md) and [release notes](docs/release-notes/)

## Privacy

Weir has no analytics and sends no usage reports. It talks only to the media managers and download clients you connect, to GitHub to check for new versions, and to Deluno's metadata service to find posters and each title's original language. That service needs no account. Weir sends it only the title and year it read from a file's name, or the ids your media manager already gave it, never a file name, a folder path, your library or anything about you. Your browser only ever talks to Weir, and posters are kept on your machine. See [Posters](https://jampat000.github.io/Weir/docs/guides/posters) for how it works, including how to switch lookups off. Keys for your connections are kept in Weir's own database, and `WEIR_CREDENTIALS_SECRET` encrypts them.

## Development

For people building Weir from source. You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Node.js 24](https://nodejs.org).

```bash
git clone https://github.com/jampat000/Weir.git
cd Weir/apps/web
npm ci
npm run dev
```

This starts the server and the web app together at `http://localhost:8782/`. See [local development](docs/local-development.md) for the details.

Before sending a change, run the checks for the part you touched:

```bash
dotnet build apps/server/Weir.slnx -warnaserror
dotnet test apps/server/Weir.slnx
cd apps/web && npm run lint && npm run build && npm run test
```

The Windows tray app has its own solution, `apps/tray/Weir.Tray.slnx`, built and tested the same way. [CONTRIBUTING.md](CONTRIBUTING.md) lists the rest, including the contract and end-to-end tests.

| Folder | What it is |
| --- | --- |
| `apps/server` | The .NET 10 server: API, SQLite, the job queue and the cleaning itself |
| `apps/web` | The React and TypeScript web app the server serves |
| `apps/tray` | The Windows tray app that starts and watches the server |
| `docker` | The container entrypoint and the Docker reference |
| `packaging` | The Windows installer build and brand assets |
| `docs`, `docs-site` | Guides and runbooks, and the documentation website |
| `scripts` | Repository tooling and CI checks |

[ARCHITECTURE.md](ARCHITECTURE.md) has the full repository map. Contributors working with an AI assistant should start at [AGENTS.md](AGENTS.md).

<!-- README_LOCKED_SECTION_START: project-note -->
## A note on this project

Weir started as a tool for my own library. I wanted every file to keep only the audio and subtitle tracks I actually use, and nothing I tried did that well, especially for a library I already had.

I'm not a software engineer, so I'll be upfront about it: Weir is built with AI coding assistants. I've tried to make up for that with process. Every change goes in through a pull request that has to pass the full test suite (unit, API contract and end-to-end tests), and a release only goes out once those tests pass on the exact code being shipped. Weir also works on a copy of each file and only replaces the original once the new one checks out, so a bug shouldn't cost you anything in your library.

It's opinionated. Everything in it is there because it fixed a real problem in my own setup first. If it suits the way you manage your library too, use it, improve it, and share what you change under the same license.

— Weir's maintainer

<!-- README_LOCKED_SECTION_END: project-note -->

## Licence

Weir is free software under the [GNU Affero General Public License v3.0 or later](LICENSE) (AGPL-3.0-or-later). You can use it, change it and share it, and if you share a changed version, or run one for other people over a network, you must share your changes under the same licence.

## Security

Found a security problem? Please report it privately, not in a public issue. [SECURITY.md](SECURITY.md) says how.
