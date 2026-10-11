# Weir Agent Map

This file is the short entry point for coding agents. Keep detailed rules in linked docs so this stays readable.

## Repository Boundaries

- Do not use old duplicate checkouts or copied source trees.
- Treat `main` as protected. Use short-lived branches and pull requests.
- Never revert user changes unless the user explicitly asks for that exact revert.

## Start Here

- Project overview: [`README.md`](README.md)
- Architecture map: [`ARCHITECTURE.md`](ARCHITECTURE.md)
- Local development: [`docs/local-development.md`](docs/local-development.md)
- Documentation index: [`docs/README.md`](docs/README.md)
- Repository scripts and which language each uses: [`scripts/README.md`](scripts/README.md)
- Agent operating model: [`docs/agent-harness.md`](docs/agent-harness.md)
- Issue triage: [`docs/triage.md`](docs/triage.md)
- Release governance: [`docs/release-governance.md`](docs/release-governance.md)
- CI and release standard (shared with Deluno as an identical separate copy): [`docs/ci-standard.md`](docs/ci-standard.md)

## High-Risk Invariants

- Media mutation safety lives in [`docs/file-lifecycle-contract.md`](docs/file-lifecycle-contract.md).
- Windows installer and upgrade behavior must follow [`docs/release.md`](docs/release.md) and [`docs/smoke-checklists.md`](docs/smoke-checklists.md).
- Secrets and credential rotation rules live in [`docs/security-hardening.md`](docs/security-hardening.md).
- Operator-facing messages should follow [`docs/operator-messaging-standard.md`](docs/operator-messaging-standard.md).

## Validation Defaults

- Server: `dotnet build apps/server/Weir.slnx -warnaserror` and `dotnet test apps/server/Weir.slnx`.
- Frontend: from `apps/web`, run `npm run lint`, `npm run build` and `npm run test`.
- Tray (Windows): `dotnet build apps/tray/Weir.Tray.slnx` and `dotnet test apps/tray/Weir.Tray.slnx`.
- Contract suite (judges a running server over HTTP): `dotnet test apps/server/tests/Weir.Contract.Tests --filter "Area=<area>"` per area, listed in `areas.json`; see [`Weir.Contract.Tests/README.md`](apps/server/tests/Weir.Contract.Tests/README.md). The server test run above leaves it and E2E out (`--filter "Category!=Stress&Category!=Contract&Category!=E2E"` is what CI uses).
- E2E (Playwright for .NET): `WEIR_E2E=1 dotnet test apps/server/tests/Weir.E2E.Tests`, after `npm run build` in `apps/web` and the browser install in [`Weir.E2E.Tests/README.md`](apps/server/tests/Weir.E2E.Tests/README.md). The packaged live audit is `apps/server/tools/Weir.LiveAudit`.
- Weir has no Python: `node scripts/check-no-python.mjs` fails if a `.py` file is tracked. Tooling is Node, PowerShell or .NET.
- Packaging smoke checks are documented in [`CONTRIBUTING.md`](CONTRIBUTING.md).
- Docs map validation: `node scripts/check-agent-docs.mjs`.

## Working Style

- Encode recurring lessons in docs, scripts, tests, or CI instead of relying on chat memory.
- Prefer small PRs with focused validation.
- No user-facing issue closes without live proof on the rig, and every tag, release candidates included, needs a passing scenario-suite record on its exact commit before it is made (no waivers): [`docs/release-governance.md`](docs/release-governance.md), [`docs/release.md`](docs/release.md).
- If a task exposes missing tooling or missing repository knowledge, add that capability as part of the fix or open a backlog issue.
