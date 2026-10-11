# Changelog

One line per released version, newest first. Each line links the full release notes in
[`docs/release-notes/`](docs/release-notes/) and the GitHub release with its downloads.

Versions follow `MAJOR.MINOR.PATCH`, with `-rc.N` for release candidates. A major version can contain
breaking changes, which its notes list first.

Earlier version numbers were retired when Weir restarted at 1.0.0-rc.1. Their history stays in git.

## 1.x

- **1.0.0-rc.14** (2026-10-11). Downloads sent twice get the right answer, a file that comes back is picked up, imported films stay imported, and every release is proven on a clean machine before it is tagged. [notes](docs/release-notes/v1.0.0-rc.14.md) · [release](https://github.com/jampat000/Weir/releases/tag/v1.0.0-rc.14)
- **1.0.0-rc.13** (2026-10-10). Check, download and apply an update from System › About without waiting for the tray, a checked copy of your data before every update, a frozen ffmpeg stopped after 10 minutes without progress, and files under a workflow's minimum size left out of a hand-off. [notes](docs/release-notes/v1.0.0-rc.13.md) · [release](https://github.com/jampat000/Weir/releases/tag/v1.0.0-rc.13)
- **1.0.0-rc.12** (2026-10-09). The tray's dot shows Weir's health only, an extra skipped for being too small no longer needs you, files that leave early settle at once, and the first-run port window opens in front. [notes](docs/release-notes/v1.0.0-rc.12.md) · [release](https://github.com/jampat000/Weir/releases/tag/v1.0.0-rc.12)
- **1.0.0-rc.11** (2026-10-09). The update check works when GitHub limits your network, the tray matches Deluno's, Weir stays quick while it processes, and files deleted or finished by hand-off settle instead of waiting. [notes](docs/release-notes/v1.0.0-rc.11.md) · [release](https://github.com/jampat000/Weir/releases/tag/v1.0.0-rc.11)
- **1.0.0-rc.10** (2026-10-09). Logs speak plainly, no warning at every start, Docker folders readable again, and a newer FFmpeg build. [notes](docs/release-notes/v1.0.0-rc.10.md) · [release](https://github.com/jampat000/Weir/releases/tag/v1.0.0-rc.10)
- **1.0.0-rc.9** (2026-10-09, tagged but not published; its changes are in rc.10). Logs speak plainly, and no warning at every start. [notes](docs/release-notes/v1.0.0-rc.9.md)
- **1.0.0-rc.8** (2026-10-08). No guessed folders, unreadable files are never deleted or passed on, and plain words throughout. [notes](docs/release-notes/v1.0.0-rc.8.md) · [release](https://github.com/jampat000/Weir/releases/tag/v1.0.0-rc.8)
- **1.0.0-rc.7** (2026-10-08). Every screen is live, with nothing to refresh. [notes](docs/release-notes/v1.0.0-rc.7.md) · [release](https://github.com/jampat000/Weir/releases/tag/v1.0.0-rc.7)
- **1.0.0-rc.6** (2026-10-08). A media manager can name its connection to Weir on its own. [notes](docs/release-notes/v1.0.0-rc.6.md) · [release](https://github.com/jampat000/Weir/releases/tag/v1.0.0-rc.6)
- **1.0.0-rc.5** (2026-10-08). Deluno can check the folder each of Weir's workflows watches. [notes](docs/release-notes/v1.0.0-rc.5.md) · [release](https://github.com/jampat000/Weir/releases/tag/v1.0.0-rc.5)
- **1.0.0-rc.4** (2026-10-07, tagged but not published; its changes are in rc.5). Pausing is dependable, each downloaded file is cleaned once, Activity shows what every cleaned file saved, and workflows linked to Deluno by hand are kept in step too. [notes](docs/release-notes/v1.0.0-rc.4.md)
- **1.0.0-rc.3** (2026-10-07). Linked workflows never touch the original download, Deluno-linked ones act only on hand-offs, and Weir sets up its workflows from Deluno. [notes](docs/release-notes/v1.0.0-rc.3.md) · [release](https://github.com/jampat000/Weir/releases/tag/v1.0.0-rc.3)
- **1.0.0-rc.2** (2026-10-07). Network access works on every network type, Public included, and "waiting for a free slot" no longer counts library scans. [notes](docs/release-notes/v1.0.0-rc.2.md) · [release](https://github.com/jampat000/Weir/releases/tag/v1.0.0-rc.2)
- **1.0.0-rc.1** (2026-10-06). The first release of Weir, a pre-release: an early build that is in active testing, so expect rough edges. [notes](docs/release-notes/v1.0.0-rc.1.md) · [release](https://github.com/jampat000/Weir/releases/tag/v1.0.0-rc.1)
