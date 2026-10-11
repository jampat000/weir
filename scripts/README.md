# Repository scripts

## Which language

- **Node** (`.mjs`) for repository tooling and CI gates. Node is on every CI runner and every
  workstation that builds the web app, and needs no install step.
- **.NET** for anything that drives a browser or judges a running server: the contract suite, the E2E tests and
  the packaged live audit are projects under `apps/server`, not scripts.
- **No Python.** `check-no-python.mjs` fails CI if a `.py` file is tracked.
- **PowerShell** only for Windows packaging and checks that exercise the Windows package, which includes the VM scenario suite (`scenarios/`): it installs the package on a clean Windows machine and drives it, and runs in Windows PowerShell 5.1 because that is all the VM has.

A new script follows these rules. A script in the wrong language is replaced when it next needs real
work, not rewritten for its own sake. Scripts are named in kebab-case.

## CI and release gates

| Script | What it does |
| --- | --- |
| `ci-passed.mjs` | CI's single verdict: every job due for the change passed, every other job was skipped (`ci-passed.test.mjs`). |
| `check-agent-docs.mjs` | Checks that the agent documentation map (`AGENTS.md` and its links) is valid. |
| `check-github-action-pins.mjs` | Fails when a workflow uses an action that is not pinned to a full commit SHA. |
| `check-workflow-hygiene.mjs` | Fails when a workflow has no top-level `permissions` block (or grants write there), or a job has no `timeout-minutes`. |
| `summarize-test-results.mjs` | Writes TRX and Playwright results to a CI job's summary page. |
| `retry-failed-tests.mjs` | Runs the browser tests that failed in the E2E smoke's first run once more and lists those that passed only then under "Flaky (passed on retry)"; backend tests are never retried (`retry-failed-tests.test.mjs`). |
| `docker-entrypoint.test.mjs` | Runs `docker/entrypoint.sh` against a stand-in server and checks that the owner-only mode for the secret files does not pass to the server, whose output folders and files would otherwise be private to the `weir` user. |
| `check-no-python.mjs` | Fails when a Python file is tracked: Weir's tooling and tests are Node, PowerShell or .NET (`check-no-python.test.mjs`). |
| `check-public-privacy.mjs` | Fails when a private-looking value (a private IP address, a Windows machine name, a UNC or NAS path, an email address, an API key value, or a known private word held only as a SHA-256 hash in `public-privacy-hashes.json`) is in the README, the docs, the release notes, the changelog or the built web app. It never prints the match. `--audit` lists the same matches across every tracked file (`check-public-privacy.test.mjs`). |
| `check-contract-areas.mjs` | Fails when the contract suite's `areas.json` and its `[ContractArea]` test classes disagree, so a CI leg can never run zero tests (`check-contract-areas.test.mjs`). |
| `check-event-titles.mjs` | Fails when the server defines an Activity event type that `apps/web/src/lib/activity/event-labels.ts` has no title for, so System > Logs never shows a raw event name (`check-event-titles.test.mjs`). |
| `check-test-console-programs.mjs` | Fails when a test source under `apps/` starts a system console program (`ping`, `timeout`, ...) as a stand-in, because that can pop a console window on a desktop (#806, #821) (`check-test-console-programs.test.mjs`). |
| `check-node-docker-version.mjs` | Fails when the Dockerfile's node image major does not match the root `.node-version`. |
| `check-release-workflow-gates.mjs` | Checks the shape of `release.yml` and `ci.yml`: only `publish-windows` and `publish-docker` publish and neither waits for the other, the moving image tags move last and never for a release candidate, `ci-passed` judges every job (`check-release-workflow-gates.test.mjs`). |
| `check-release-version.mjs` | Fails a release whose tag is not a well-formed `X.Y.Z` or `X.Y.Z-rc.N` SemVer version (`check-release-version.test.mjs`). No file carries the release version; every build that ships takes it from the tag instead (#804). |
| `semver.mjs` | SemVer parsing and precedence for the release scripts (`semver.test.mjs`). |
| `find-previous-release.mjs` | The delta base for the Windows package: the newest published release older than the one being released, by SemVer precedence (`find-previous-release.test.mjs`). |
| `verify-ci-for-release.mjs` | Makes a release prove `ci.yml`'s `ci-passed` passed on the tagged commit, without waiting for a run still going (`verify-ci-for-release.test.mjs`). |
| `verify-golden-path-for-release.mjs` | Makes every release, release candidates included, prove Weir's scenario suite passed on the tagged commit: the newest `golden-path` commit status on it must be `success` (`verify-golden-path-for-release.test.mjs`). |
| `prune-release-feed.mjs` | Removes the previous release's full nupkg (fetched only as the Windows package's delta base) and its feed entries from a `vpk pack` output directory, keeping just the version being released (`prune-release-feed.test.mjs`). |
| `check-release-assets-single-version.mjs` | Release gate: fails if the Windows package output still names any version other than the one being released, as a backstop for `prune-release-feed.mjs` (`check-release-assets-single-version.test.mjs`). |
| `check-dead-code.mjs` | Dead-code guard for the web app: unreferenced files, exports and types, found by Knip (allowlist in `dead-code-allowlist.json`) and unstyled class names. |
| `check-dotnet-vulnerabilities.mjs` | Fails on High or Critical NuGet advisories in a .NET solution. |
| `npm-audit-retry.mjs` | Runs `npm audit`, retrying only a registry-side failure; fails on a real High/Critical finding (`npm-audit-retry.test.mjs`). Used by apps/web's audit step and imported by `docs-site/scripts/audit-dependencies.mjs`. |
| `wait-for-health.mjs` | Waits for a server's `/health`, printing a container's log if it never answers. |
| `ffmpeg-pin.mjs` | Reads the FFmpeg pin (release tag, archive names, SHA-256) from `packaging/windows/build-velopack-vendored-media-tools.ps1`, for the two scripts below (`ffmpeg-pin.test.mjs`). |
| `ffmpeg-cache-key.mjs` | Prints the cache key for the pinned FFmpeg, the Windows package's by default or the Linux CI jobs' with `linux`: it changes exactly when the pin does. |
| `install-ffmpeg-linux.mjs` | Downloads the pinned static Linux FFmpeg with retries, checks its SHA-256 before unpacking and installs `ffmpeg` and `ffprobe` into a folder. Run through `.github/actions/setup-ffmpeg-linux`, so no Linux job installs FFmpeg from a package mirror. |
| `pinned-download.mjs` | Downloads a file pinned by SHA-256 from its upstream URL, or from the same-named asset of the newest Weir release when upstream no longer has it (found from the release feed, not the API), and refuses bytes that do not match the pin (`pinned-download.test.mjs`). The Node counterpart of `packaging/windows/pinned-download.ps1`. |

## Local development

| Script | What it does |
| --- | --- |
| `scenarios/` | Weir's VM scenario suite, PowerShell because it drives the installed Windows build (#954): `Invoke-WeirScenarios.ps1` is the orchestrator for the golden VM (same shape as Deluno's `Invoke-GoldenPath.ps1`), `Run-WeirScenarios.ps1` runs inside the VM, `Weir.Scenarios.*.ps1` hold the catalog and the scenarios. `scenarios.test.mjs` checks that every file parses, every scenario has code and is in the docs, and `-WhatIf` touches nothing. |
| `pre-push.mjs` | The pre-push checks `.githooks/pre-push` runs: no Python, the contract areas, prettier, the dead-code guard and API types drift. |
| `build-brand-icons.mjs` | Renders every raster icon (favicon, apple touch icon, Windows tray and installer icon) from the SVGs in `packaging/brand`; `--check` compares them with the committed files without changing anything (`build-brand-icons.test.mjs`). Needs `npm ci` in `apps/web`. |
| `stop-dev-api-port.mjs` | Stops the dev API that this worktree's `npm run dev` started, and nothing else. |
| `stop-dev-web-port.mjs` | Stops the dev Vite server that this worktree's `npm run dev` started, and nothing else. |
| `dev-reset-auth.mjs` | Clears a development database's users and sessions so `/setup` works again. |
| `dev-ports.json` | The development and production ports every launcher and the Vite config read. |
| `dev.ps1`, `dev-backend.ps1`, `dev-web.ps1`, `weir-env.ps1`, `dev-reset-auth.ps1`, `verify-local.ps1` | PowerShell dev launchers and checks. `npm run dev` in `apps/web` always starts the API and Vite together and cannot run the server alone; this trio stays for the API-only case (`verify-local.ps1`, manual API testing) and for two separate windows with separate logs. See `docs/local-development.md`. |

## Windows packaging and live audits

| Script | What it does |
| --- | --- |
| `smoke-windows-package.ps1` | Starts the assembled Windows package's server and checks it end to end. |
| `verify-docker-remote.ps1` | Runs the Docker validation workflow on GitHub-hosted runners from a machine without Docker. |

The full browser and API audit of a packaged server (Docker or Windows) is not a script: it is
`apps/server/tools/Weir.LiveAudit` (`dotnet run --project apps/server/tools/Weir.LiveAudit -c Release` with
`WEIR_LIVE_BASE_URL` set; see its README). The README screenshots come from a real Weir (#890).
