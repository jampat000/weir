# Release Governance

This is the canonical governance checklist for keeping Weir releases controlled and repeatable.

## GitHub repository controls

- `main` is protected by the active `Owner-Only Main Protection` ruleset.
- Direct deletion and force-pushes to `main` are blocked.
- Pull requests into `main` require conversation resolution.
- Code-owner review is required by the ruleset. `.github/CODEOWNERS` owns the full tree.
- The one required status check for `main` is `ci-passed`, CI's verdict (`.github/workflows/ci.yml`,
  `scripts/ci-passed.mjs`): every job the change made due passed, and every other job was skipped by path
  filtering. The individual jobs (`server-linux`, `server-windows`, `web`, `web-dist`, `e2e-smoke`, the
  `contract (<area>)` legs, `tray`, `docker-smoke`, `windows-package-smoke`, `repo-checks`) are judged by
  it, so none of them is a required check of its own.
- The repo Wiki is disabled. Public docs live in the repository.
- Issues are enabled and use structured templates.
- Releases are tag-driven from `v*` tags. A tag with a pre-release part (`v1.0.0-rc.1`) runs the same release and publishes a GitHub pre-release (`docs/release.md`).

## Proof before closing and before tagging

Decided by the owner, 7 Oct 2026 (#903), after bugs kept turning up in things that seemed to work.

1. **No user-facing issue closes without live proof.** The fix is clicked or watched working on the rig, and the
   closing note says what was seen (a screenshot, a log line, the Activity entry). Green tests and a code read are
   never enough.
2. **Every fix gets a test that does what the user does:** a real HTTP request against a running Weir, or a
   Playwright click. A check of an attribute or a label is not that test.
3. **Weir's scenario suite gates every tag, release candidates included.** Before the tag, the exact commit's build is
   installed on the golden VM (first as Deluno's suite leaves it, then on the clean checkpoint) and Weir's scenario suite is run against it (`scripts/scenarios`; `docs/release.md`,
   "Proof before tagging"). The run is recorded as the commit status `golden-path`, and `release.yml` refuses to publish any
   tag whose commit has no passing one. There is no release-candidate pass-through and no waiver: a release is proven before its
   tag, never after it (the owner, 11 Oct 2026, #954; the pass-through added in #907 is gone). An intermittent failure is a bug
   until its root cause is found.
4. **Broad changes need a full click-through before they ship:** class rewrites, dependency bumps and refactors are
   checked by walking the whole product, not by the tests that cover the lines they touched.

## Before every release

1. Confirm the working tree is clean.
2. Confirm `main` is up to date with `origin/main`.
3. Confirm `.github/workflows/ci.yml` still has the `ci-passed` job the ruleset requires (`node scripts/check-release-workflow-gates.mjs` checks it).
4. Confirm `.github/dependabot.yml` has no `ignore` hold that conflicts with the workflow pins (version-update pull requests are off; the holds are kept as the record of why a major is not taken).
5. Confirm open issues tagged `priority: critical` or `priority: high` are either fixed, intentionally deferred, or not release-blocking.
6. Create `docs/release-notes/vX.Y.Z.md` (`vX.Y.Z-rc.N.md` for a release candidate) from `docs/release-notes/TEMPLATE.md` with plain-language user-facing notes.
7. Run Weir's scenario suite on the exact commit being tagged (`scripts/scenarios/Invoke-WeirScenarios.ps1`), for a release candidate too, and keep its record. A pass sets the `golden-path` commit status; `release.yml` reads it (`docs/release.md`).
8. Run the release path from `docs/release.md`.

## After every release

1. Confirm the GitHub Release exists for the pushed tag.
2. Confirm `Weir-win-Setup.exe` is attached to the release, alongside exactly one full and (when a
   previous release existed) one delta nupkg for this version only — no earlier version's full nupkg
   (`scripts/check-release-assets-single-version.mjs` gates this in `windows-smoke`; #804).
3. Confirm the published release body is plain-language and matches the approved `docs/release-notes/vX.Y.Z.md` content.
4. Confirm the release notes/install guidance names the attached `Weir-win-Setup.exe` installer and explains any one-time upgrade requirement for older installs.
5. Confirm the GHCR image exists under its version tag (`X.Y.Z` or `X.Y.Z-rc.N`) and can be pulled by it. For a stable release, `X.Y` and `latest` name the same image; they move only after the version's image passed its smoke, and a release candidate moves neither.
6. Confirm the release workflow completed `ci-passed`, `golden-path`, `validate`, `windows-smoke`, `docker-candidate`, `docker-arm64`, `publish-windows` and `publish-docker`. The two publish jobs do not wait for each other; if one failed, re-run that job alone.
7. Download `weir-docker-release-candidate-audit` and confirm its summary has
   no console warnings, console errors, page errors, failed requests, or bad responses;
   confirm `pass-through-proof.json` reports a completed job, byte-identical output,
   and successful watched-source cleanup.
8. Open a follow-up issue for any manual smoke-test failure.
