# Weir documentation index

This directory holds the repository's own documentation: how Weir is built, released and kept safe.
Guides for people running Weir are on the [documentation site](https://jampat000.github.io/Weir/),
whose source is in [`../docs-site/docs`](../docs-site/docs).

## User

- [`../README.md`](../README.md) - what Weir does, installing on Docker and Windows, first steps.
- [`../docker/README.md`](../docker/README.md) - the full Docker reference: every variable, GPUs, file ownership, network shares.
- [`install-windows.md`](install-windows.md) - installing, updating and removing Weir on Windows, step by step.
- [`install-docker.md`](install-docker.md) - installing Weir with Docker, lining up folders with your download client and media manager, updating and backing up.
- [`docker.md`](docker.md) - short summary of the Docker runtime.
- [`ports.md`](ports.md) - the ports Weir and its development servers use.
- [`deployment-model.md`](deployment-model.md) - the supported deployment model: one process, one database.
- [`../CHANGELOG.md`](../CHANGELOG.md) - one line per released version.
- [`release-notes/`](release-notes/) - the full notes for each release.
- [`../SUPPORT.md`](../SUPPORT.md) - how to get help and what to include in an issue.
- [`../SECURITY.md`](../SECURITY.md) - how to report a vulnerability.

## Maintainer

- [`../CONTRIBUTING.md`](../CONTRIBUTING.md) - how changes are made and validated.
- [`agent-harness.md`](agent-harness.md) - working model for coding agents: where things live and which checks to run.
- [`local-development.md`](local-development.md) - local setup and the development workflow, including how to run the real server with its web app on a fresh data folder.
- [`triage.md`](triage.md) - issue labels and triage rules.
- [`exec-plans/README.md`](exec-plans/README.md) - where execution plans live.
- [`release.md`](release.md) - the release procedure and what each release produces.
- [`release-governance.md`](release-governance.md) - repository controls and the checks before and after a release.
- [`ci-standard.md`](ci-standard.md) - the CI and release standard Weir shares, as an identical separate copy, with Deluno.
- [`tray-standard.md`](tray-standard.md) - what the tray icon shows, its menu and its behaviour, the same in Weir and Deluno (a word-for-word copy).
- [`golden-path.md`](golden-path.md) - the shared checklist for Deluno's golden path, the same in Weir and Deluno: install on a clean VM, use it like a person, record the pass. Weir's own gate is the scenario suite in `scripts/scenarios` (see `release.md`, "Proof before tagging").
- [`release-notes/TEMPLATE.md`](release-notes/TEMPLATE.md) - the template for release notes.
- [`smoke-checklists.md`](smoke-checklists.md) - manual Windows and Docker smoke checks.
- [`security-hardening.md`](security-hardening.md) - security baseline and credential handling.
- [`operator-messaging-standard.md`](operator-messaging-standard.md) - how operator-facing messages are worded.
- [`settings-truthfulness-audit.md`](settings-truthfulness-audit.md) - what each setting actually does when saved.
- [`visual-identity.md`](visual-identity.md) - palette, logo and brand assets.
- [`ux-polish.md`](ux-polish.md) - the UI review baseline.
- [`design/content-language.md`](design/content-language.md) - how the content of each page is laid out.
- [`engineering/503-mkvmerge-vs-ffmpeg.md`](engineering/503-mkvmerge-vs-ffmpeg.md) - the trial that chose mkvmerge for Matroska output.
- [`../apps/server/README.md`](../apps/server/README.md) - the .NET server: projects, build, schema and publishing.
- [`../apps/web/README.md`](../apps/web/README.md) - the web app.
- [`../apps/server/tests/Weir.Contract.Tests/README.md`](../apps/server/tests/Weir.Contract.Tests/README.md) - the API contract suite.

## Architecture

- [`../ARCHITECTURE.md`](../ARCHITECTURE.md) - top-level architecture map.
- [`adr/README.md`](adr/README.md) - architecture decision records.
- [`file-lifecycle-contract.md`](file-lifecycle-contract.md) - how Weir moves, replaces and deletes media files safely.
- [`diagnostics-contract.md`](diagnostics-contract.md) - diagnostics and failure reporting.
- [`processing-manager-capabilities.md`](processing-manager-capabilities.md) - what Processing does on its own and what needs a media manager.
- [`artwork.md`](artwork.md) - how posters are found, kept and limited.

## Archive

Historical records, kept for context. They don't describe the current product.

- [`archive/server-port-notes.md`](archive/server-port-notes.md) - notes from porting the server from Python to .NET.
- [`archive/processing-library-model.md`](archive/processing-library-model.md) - the plan that replaced fixed Movies and TV scopes with libraries.
- [`archive/live-and-library.md`](archive/live-and-library.md) - decisions behind the 3.2 layout.
- [`archive/fileflows-parity-audit.md`](archive/fileflows-parity-audit.md) - a capability comparison with FileFlows.
- [`archive/redesign-docs-impact.md`](archive/redesign-docs-impact.md) - docs affected by the content-language redesign.
- [`archive/site-qa-findings.md`](archive/site-qa-findings.md) - a QA pass over every screen after the redesign.

## Maintenance rule

When a code change alters a documented invariant, update the doc in the same pull request. If a doc
can't be updated confidently, open a follow-up issue with the missing context.

`node scripts/check-agent-docs.mjs` checks every relative link in these docs, the docs site and
`docker/`, and fails when a current doc names a retired screen as if it still existed. A line that
mentions a retired name on purpose, to say it is gone, ends with `<!-- retired-ui: history -->`.
