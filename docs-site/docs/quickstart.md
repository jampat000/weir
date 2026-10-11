---
sidebar_position: 1
title: Quickstart
---

# Quickstart

Get Weir running and clean your first file in a few minutes.

Weir is a self-hosted app: it watches a folder for new movie and TV files, removes the audio
tracks and subtitles you don't want, and puts the result in an output folder. Everything it
needs — including ffmpeg and MKVToolNix — comes bundled with it. There's nothing else to
install.

## 1. Install Weir

Pick whichever fits where you run it.

### Docker (Synology, Unraid, TrueNAS, Raspberry Pi, Linux servers)

Weir is at release-candidate stage, and a release candidate is published under its version tag only, so the example below name `1.0.0-rc.14`. The `latest` tag arrives with 1.0.0.

Make a folder, save this as `compose.yaml` inside it:

```yaml
services:
  weir:
    image: ghcr.io/jampat000/weir:1.0.0-rc.14
    container_name: weir
    hostname: my-server   # what Weir calls itself: "Weir on my-server"
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

This gets you a working Weir you can sign in to. It doesn't yet have your media folders — add
those once you're ready; see [Docker deployment](deployment/docker) for the compose recipe that
mounts them, plus the recipe for running Weir alongside Sonarr, Radarr and a download client.

### Windows

1. Download `Weir-win-Setup.exe` from the newest release on the [releases page](https://github.com/jampat000/Weir/releases). Weir is in pre-release (1.0.0-rc.N), and GitHub's "latest release" link does not show pre-releases.
2. Run it. You don't need admin rights.
3. Weir asks which port to use the first time it starts. Keep **9347** unless something else uses it.
4. A notice from the tray icon says Weir is running. Click it, or the icon, and your browser opens Weir.

See the [Windows installer guide](deployment/windows) for what gets installed, how updates work,
and unattended installs.

## 2. Create your account

The first time you open Weir, it asks for a username and password. This creates the admin
account — there's no separate sign-up step.

On Docker, this form also asks for a **setup code**, since the browser is reaching Weir from
somewhere other than the machine Weir itself runs on. Get it with `docker logs weir` (look for
the line starting "Weir has no account yet"), or open the `setup-code` file in the data folder
you mounted (`./weir-data/setup-code` in the compose recipe above). The Windows installer opens
your browser on the same PC, so it never asks for this.

## 3. Follow the setup wizard

The setup wizard has three parts:

- **Downloads and workflows**: first, how your downloads reach Weir. A workflow is one route a file
  takes: a **Watched folder**, where your downloads finish, and an **Output folder**, where Weir puts
  each cleaned file.
  - **Deluno**, **Sonarr / Radarr** or **a download client** (SABnzbd, NZBGet, qBittorrent, Deluge,
    Transmission): connect it with its address and key, and Weir tests it. Weir then reads where it
    saves finished downloads and offers a Movies and a TV workflow with the folders filled in. Tick
    the ones you want and change any folder before you finish. Nothing is created until you press
    **Finish setup**, and any problem with a folder shows next to it first. Workflows offered from
    Deluno, Sonarr or Radarr are marked as linked to it; a download client only suggests folders.
  - **Neither**: type the folders yourself, a watched and an output folder for Movies and the same for TV.
- **Basics**: your time zone.
- **Automatic backups**: whether Weir keeps a rolling copy of its configuration, and how often.

If Weir cannot reach what you connect, it says so and you can go back or choose **Neither**. You can
skip the wizard and set these later: workflows under **Setup › Workflows**, connections under
**Setup › Connections › Media managers**, backups under **System › Backups**.

## 4. Choose what to keep

Under **Setup › Rules › Profiles**, set the audio and subtitle rules your workflows use. For example, keep
English and Japanese audio, keep English subtitles, and drop commentary tracks.

## 5. Try it with a real file

Put a video file in a watched folder. Weir usually notices within seconds. On network shares and
in Docker it can take up to five minutes, because Weir falls back to checking on a timer instead
of relying on filesystem notifications.

Weir does not touch a new file straight away. It waits until the file has not changed for 60 seconds, by
size or by last-changed time, so a download that is still arriving is left alone. Each workflow has its own
wait under **Setup › Workflows › File readiness**, and its own **Minimum file size** (50 MB for a new
workflow) under **Intake rules**: smaller files, such as samples, are skipped.

- The file shows up on **Processing** while Weir works on it.
- Once it's done, it shows up in **Activity**, and the cleaned copy is in the output folder.

Already have a library you want to clean up? Open **Library** and pick the workflow from the title.
A library with no folders yet shows **Set up this library**: add the folders your files sit in and
choose the rules profile that cleans them (it starts out as the workflow's). Then press **Check
again**. Weir shows you what it would remove and how much space that frees before it changes
anything. **Library setup** in the page header changes those choices later, along with the daily
clean and what happens to the original file after a clean.

## If nothing happens

| Problem | Try this |
| --- | --- |
| Files sit in the watched folder and nothing happens | In Docker, check the path in Weir is the path **inside the container**, not the path on the host. Weir can take up to five minutes to notice a file. |
| "Permission denied" in a file's Activity | Set `WEIR_PUID` / `WEIR_PGID` to the user that owns your media folders. See [Docker deployment](deployment/docker). |
| Can't open Weir | Check the container is running and you're using the right port. Weir's health check is at `http://your-server-ip:9347/health`. |

## Next steps

- [Connecting Deluno, Sonarr and Radarr](guides/media-managers) — hand off files automatically
- [Posters](guides/posters) — what Artwork does, what it sends, and how to turn it off
- [Docker deployment](deployment/docker) — media folders, file ownership, running alongside other apps
- [Windows installer](deployment/windows) — ports, updates, unattended installs
- [Building from source](guides/local-development) — for developers who want to run Weir from a clone
