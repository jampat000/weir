# Docker

Full Docker instructions live in [`docker/README.md`](../docker/README.md).

Short summary:

- one container: the .NET server, SQLite, ffmpeg, mkvmerge and the web app on port `9347`
- images for `linux/amd64` and `linux/arm64`, running as the `weir` user (UID/GID 1000, remapped with `WEIR_PUID` / `WEIR_PGID`)
- data volume `/data/weir`; the server creates or updates its database on start, and a generated session secret is kept at `$WEIR_HOME/session.secret`
- `WEIR_CHOWN_*` / `WEIR_DIR_MODE_*` are validated but not applied
- one server process per database: do not run multiple containers against the same SQLite file
- same-origin API under `/api/v1`
- stable tags are published by `.github/workflows/release.yml`
- root `compose.yaml` pulls `ghcr.io/jampat000/weir:1.0.0-rc.14` (a release candidate is published under its version tag only; `latest` arrives with 1.0.0)
- release smoke validation is defined in [`smoke-checklists.md`](smoke-checklists.md)

Upgrade continuity requirements:

- persist `WEIR_HOME` on a durable volume so SQLite data survives container replacement
- keep `WEIR_SESSION_SECRET` stable across upgrades so browser sessions remain valid
- if either value changes unexpectedly, users can be forced back through sign-in/setup flows
