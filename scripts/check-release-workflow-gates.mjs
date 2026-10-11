import { readFileSync } from "node:fs";
import { fileURLToPath, pathToFileURL } from "node:url";
import { dirname, resolve } from "node:path";

import { GATES } from "./ci-passed.mjs";
import { REQUIRED_EVIDENCE } from "./verify-ci-for-release.mjs";

// The release's safety rule: nothing reaches ghcr or the Releases page unless every check passed.
// release.yml runs its checks as parallel jobs, so the rule is expressed as the shape of the workflow:
// two jobs publish, one per thing published, and each needs the shared checks plus its own build's checks.
// `publish-windows` publishes the GitHub Release; `publish-docker` publishes the container image. Neither
// needs the other, so neither can hold the other up, and inside each job the steps come in a safe order.
// CI's side: ci-passed judges every ci.yml job, and the release's evidence (the ci-passed job and step)
// still exists under its name.

const workflowsDir = resolve(dirname(fileURLToPath(import.meta.url)), "..", ".github", "workflows");
const RELEASE = ".github/workflows/release.yml";
const CI = ".github/workflows/ci.yml";
const CI_CONTRACT = ".github/workflows/ci-contract.yml";
const CI_PACKAGING = ".github/workflows/ci-packaging.yml";

const PUBLISH_WINDOWS = "publish-windows";
const PUBLISH_DOCKER = "publish-docker";
const PUBLISHERS = [PUBLISH_WINDOWS, PUBLISH_DOCKER];

const SHARED_GATES = ["ci-passed", "golden-path", "validate"];
const WINDOWS_NEEDS = [...SHARED_GATES, "windows-smoke"];
const DOCKER_NEEDS = [...SHARED_GATES, "docker-candidate", "docker-arm64"];

// What only the job that publishes the GitHub Release may contain, and what only the job that pushes the image may.
const RELEASE_ONLY = ["contents: write", "uses: softprops/action-gh-release@", "gh release"];
const IMAGE_ONLY = [
  "uses: docker/login-action@",
  "push: true",
  "docker push",
  "imagetools create",
  "packages: write",
];
// Provenance needs the OIDC token and the right to record an attestation, in whichever publish job attests.
const ATTESTATION = ["uses: actions/attest-build-provenance@", "attestations: write", "id-token: write"];

export function loadWorkflows() {
  const read = (name) => readFileSync(resolve(workflowsDir, name), "utf8").replace(/\r\n/g, "\n");
  return {
    release: read("release.yml"),
    ci: read("ci.yml"),
    ciContract: read("ci-contract.yml"),
    ciPackaging: read("ci-packaging.yml"),
  };
}

function requireText(source, marker, file) {
  const index = source.indexOf(marker);
  if (index < 0) {
    throw new Error(`${file} is missing required release gate marker: ${marker}`);
  }
  return index;
}

function requireOrder(source, markers, file) {
  let previous = -1;
  for (const marker of markers) {
    const current = requireText(source, marker, file);
    if (current <= previous) {
      throw new Error(`${file} has an unsafe release gate order near: ${marker}`);
    }
    previous = current;
  }
}

function jobIds(source) {
  const jobsStart = source.indexOf("\njobs:\n");
  if (jobsStart < 0) throw new Error("A workflow has no jobs: section.");
  return [...source.slice(jobsStart).matchAll(/\n {2}([a-zA-Z0-9_-]+):\n/g)].map((match) => match[1]);
}

function requireJob(source, jobName, file) {
  const marker = `\n  ${jobName}:\n`;
  const start = source.indexOf(marker);
  if (start < 0) {
    throw new Error(`${file} is missing required job: ${jobName}`);
  }
  const remaining = source.slice(start + marker.length);
  const nextJob = remaining.search(/\n  [a-zA-Z0-9_-]+:\n/);
  return nextJob < 0 ? remaining : remaining.slice(0, nextJob);
}

function rejectText(source, marker, file) {
  if (source.includes(marker)) {
    throw new Error(`${file} contains forbidden workflow text: ${marker}`);
  }
}

function needsOf(body, file) {
  const line = `\n${body}`.match(/\n {4}needs: \[([^\]]*)\]\n/);
  if (!line) throw new Error(`${file} must declare its needs as needs: [job, ...] on one line.`);
  return line[1].split(",").map((name) => name.trim()).filter(Boolean);
}

// A publish job waits for exactly the shared checks plus its own build's: a missing one lets it publish before
// that check passes, and an extra one lets the other publish job hold it up.
function requireExactNeeds(body, expected, file) {
  const actual = needsOf(body, file);
  for (const job of expected) {
    if (!actual.includes(job)) throw new Error(`${file} does not need ${job}; it could publish before ${job} passes.`);
  }
  for (const job of actual) {
    if (!expected.includes(job)) {
      throw new Error(`${file} needs ${job}, which is not one of its gates (${expected.join(", ")}); the two publish jobs must not wait for each other's build.`);
    }
  }
}

function requireNotOverridden(body, job) {
  const jobIf = (body.match(/\n {4}if: (.*)\n/) || [])[1] || "";
  for (const override of ["always()", "failure()", "cancelled()", "success() ||"]) {
    if (jobIf.includes(override)) {
      throw new Error(`${RELEASE} ${job}'s if: must not contain ${override}; it would run past a failed gate.`);
    }
  }
}

function requireAttestationSteps(body, what, job) {
  const steps = body.split(/\n {6}- /).filter((step) => step.includes("uses: actions/attest-build-provenance@"));
  if (steps.length !== 1) {
    throw new Error(`${RELEASE} ${job} must attest ${what}; found ${steps.length} attestation step(s).`);
  }
  requireText(steps[0], "if: ${{ vars.ATTEST_PROVENANCE == 'true' }}", `${RELEASE} ${job} attestation step`);
}

export function checkReleaseGates({ release, ci, ciContract, ciPackaging }) {
  const invalidRunnerTempFixture = "WEIR_LIVE_E2E_FIXTURE_HOST_ROOT: ${{ runner.temp }}";
  rejectText(release, invalidRunnerTempFixture, RELEASE);
  rejectText(ciPackaging, invalidRunnerTempFixture, CI_PACKAGING);

  // --- release.yml ----------------------------------------------------------------------------------

  // No write access for the workflow as a whole; each publish job grants itself what it needs.
  const releaseTop = release
    .slice(0, release.indexOf("\njobs:\n"))
    .split("\n")
    .filter((line) => !line.trimStart().startsWith("#"))
    .join("\n");
  if (/:\s*write\b|write-all/.test(releaseTop)) {
    throw new Error(`${RELEASE} grants write access at workflow level; only the publish jobs may have it.`);
  }

  // Every v* tag runs the release, pre-release tags included: scripts/check-release-version.mjs refuses a tag that
  // is not a SemVer version, and publish-windows marks a tag with a pre-release part as a GitHub pre-release.
  requireText(releaseTop, '    tags:\n      - "v*"\n', `${RELEASE} on.push.tags`);
  rejectText(releaseTop, '"!v*-*"', RELEASE);

  const releaseJobs = jobIds(release);
  const publishWindows = requireJob(release, PUBLISH_WINDOWS, RELEASE);
  const publishDocker = requireJob(release, PUBLISH_DOCKER, RELEASE);

  // Anything that can put something in public is confined to the publish jobs, and each publish job holds only
  // the rights for the thing it publishes: the Release's write access is not the registry's, nor the reverse.
  for (const job of releaseJobs.filter((name) => !PUBLISHERS.includes(name))) {
    const body = requireJob(release, job, RELEASE);
    for (const marker of [...RELEASE_ONLY, ...IMAGE_ONLY, ...ATTESTATION]) {
      rejectText(body, marker, `${RELEASE} job ${job} (only ${PUBLISHERS.join(" and ")} may publish)`);
    }
  }
  for (const marker of IMAGE_ONLY) {
    rejectText(publishWindows, marker, `${RELEASE} job ${PUBLISH_WINDOWS} (only ${PUBLISH_DOCKER} may touch the registry)`);
  }
  for (const marker of RELEASE_ONLY) {
    rejectText(publishDocker, marker, `${RELEASE} job ${PUBLISH_DOCKER} (only ${PUBLISH_WINDOWS} may publish the GitHub Release)`);
  }

  // Each publish job waits for the shared checks and its own build's checks, and only when they all succeeded.
  requireExactNeeds(publishWindows, WINDOWS_NEEDS, `${RELEASE} ${PUBLISH_WINDOWS}`);
  requireExactNeeds(publishDocker, DOCKER_NEEDS, `${RELEASE} ${PUBLISH_DOCKER}`);
  requireNotOverridden(publishWindows, PUBLISH_WINDOWS);
  requireNotOverridden(publishDocker, PUBLISH_DOCKER);

  // The GitHub Release: the Windows files and the web build are attested and the approved notes are in place
  // before the Release is created.
  requireOrder(
    publishWindows,
    [
      "- name: Download web dist",
      "- name: Download Velopack release artifacts",
      "- name: Attest provenance of the release files",
      "- name: Prepare user-facing release notes",
      "- name: Publish GitHub Release",
    ],
    `${RELEASE} ${PUBLISH_WINDOWS} job`,
  );
  // A tag with a pre-release part publishes a GitHub pre-release, never a normal release.
  requireText(publishWindows, "prerelease: ${{ steps.version.outputs.prerelease }}", `${RELEASE} ${PUBLISH_WINDOWS} job`);
  for (const marker of ["id-token: write", "attestations: write"]) {
    requireText(publishWindows, marker, `${RELEASE} ${PUBLISH_WINDOWS} job`);
  }
  requireAttestationSteps(publishWindows, "the release files", PUBLISH_WINDOWS);

  // The version tag is pushed, attested, checked and smoked before any moving tag moves, and the moving tags
  // move last.
  requireOrder(
    publishDocker,
    [
      "uses: docker/setup-qemu-action@",
      "uses: docker/login-action@",
      "- name: Publish release Docker image",
      "platforms: linux/amd64,linux/arm64",
      "push: true",
      "- name: Attest provenance of the Docker image",
      "- name: Verify published Docker manifest",
      "- name: Smoke test published Docker image",
      "- name: Cleanup Docker smoke container",
      "- name: Move the moving image tags",
    ],
    `${RELEASE} ${PUBLISH_DOCKER} job`,
  );
  const movingStep = "- name: Move the moving image tags";
  const movingAt = publishDocker.indexOf(movingStep);
  if (publishDocker.indexOf("imagetools create") < movingAt || publishDocker.includes(":latest")) {
    throw new Error(`${RELEASE} ${PUBLISH_DOCKER} may move a tag (imagetools create) only in its last step, after the image passed its smoke.`);
  }
  const movingBody = publishDocker.slice(movingAt);
  if (/\n {6}- /.test(movingBody)) {
    throw new Error(`${RELEASE} ${PUBLISH_DOCKER} must have no step after the moving-tags step.`);
  }
  // The build pushes the version tag alone. A release candidate (a version with a hyphen) leaves the moving step
  // before it creates anything: only a stable release moves `major.minor` and `latest`.
  requireText(publishDocker, "tags: ${{ steps.image.outputs.name }}:${{ steps.version.outputs.plain }}\n", `${RELEASE} ${PUBLISH_DOCKER} job`);
  const guardAt = movingBody.indexOf('if [ "$PRERELEASE" = "true" ]');
  const exitAt = movingBody.indexOf("exit 0", guardAt);
  if (guardAt < 0 || exitAt < 0 || exitAt > movingBody.indexOf("imagetools create")) {
    throw new Error(`${RELEASE} must leave the moving-tags step for a pre-release before it runs imagetools create.`);
  }
  requireText(movingBody, "PRERELEASE: ${{ steps.version.outputs.prerelease }}", `${RELEASE} moving-tags step`);

  // The image carries its provenance and an SBOM from the build. The pushed image is attested by digest (the build
  // step must expose one), and every signed attestation is switched by the repository variable ATTEST_PROVENANCE: a
  // private repository cannot have them. A release already under way is never cancelled by a second run for the same tag.
  for (const marker of [
    "provenance: mode=max",
    "sbom: true",
    "id: push",
    "subject-digest: ${{ steps.push.outputs.digest }}",
    "push-to-registry: true",
    "id-token: write",
    "attestations: write",
  ]) {
    requireText(publishDocker, marker, `${RELEASE} ${PUBLISH_DOCKER} job`);
  }
  requireAttestationSteps(publishDocker, "the image", PUBLISH_DOCKER);
  requireText(releaseTop, "  cancel-in-progress: false", `${RELEASE} concurrency`);

  // The tagged commit must be one ci.yml already passed on.
  const ciPassed = requireJob(release, "ci-passed", RELEASE);
  for (const marker of ["actions: read", "node scripts/verify-ci-for-release.mjs"]) {
    requireText(ciPassed, marker, `${RELEASE} ci-passed job`);
  }
  rejectText(ciPassed, "--wait", `${RELEASE} ci-passed job (tags are made after CI is green; the gate does not wait)`);

  // ...and one the golden path already passed on: a commit status the person who ran it recorded. The job only
  // reads statuses, so `statuses: read` is all it is granted.
  const goldenPath = requireJob(release, "golden-path", RELEASE);
  for (const marker of ["statuses: read", "node scripts/verify-golden-path-for-release.mjs"]) {
    requireText(goldenPath, marker, `${RELEASE} golden-path job`);
  }
  rejectText(goldenPath, "statuses: write", `${RELEASE} golden-path job (the gate reads the record; it never writes one)`);
  rejectText(goldenPath, "--wait", `${RELEASE} golden-path job (the golden path is run before the tag; the gate does not wait)`);
  // Every tag is held for it, release candidates included (#954): nothing may skip the job or any step of it, and a
  // failure of it may not be waved on.
  for (const skip of [/\n {4}if:/, /\n {6,}(?:- )?if:/, /continue-on-error:/]) {
    if (skip.test(`\n${goldenPath}`)) {
      throw new Error(`${RELEASE} golden-path job must run, and must fail the release, for every tag: it may have no if: and no continue-on-error:, so no tag (a release candidate included) passes it unproven.`);
    }
  }

  const validate = requireJob(release, "validate", RELEASE);
  for (const marker of [
    "- name: Release notes file present",
    "node scripts/check-dotnet-vulnerabilities.mjs",
    "- name: Weir E2E smoke (Playwright + real .NET server)",
    "name: weir-web-dist",
  ]) {
    requireText(validate, marker, `${RELEASE} validate job`);
  }

  // The amd64 candidate: built unpushed, scanned, started, audited end to end, evidence kept.
  const candidate = requireJob(release, "docker-candidate", RELEASE);
  requireOrder(
    candidate,
    [
      "- name: Build unpushed Docker release candidate",
      "platforms: linux/amd64",
      "push: false",
      "- name: Trivy scan of Docker release candidate",
      "- name: Start unpushed Docker release candidate",
      "- name: Full live E2E against unpushed Docker release candidate",
      "- name: Upload Docker release-candidate evidence",
      "- name: Cleanup unpushed Docker release candidate",
    ],
    `${RELEASE} docker-candidate job`,
  );
  for (const marker of [
    "WEIR_LIVE_EXPECTED_VERSION: ${{ steps.version.outputs.plain }}",
    "WEIR_SESSION_COOKIE_SECURE=false",
    "WEIR_LIVE_E2E_FIXTURE_SERVER_ROOT: /e2e-fixture",
    "WEIR_LIVE_E2E_FIXTURE_HOST_ROOT:$WEIR_LIVE_E2E_FIXTURE_SERVER_ROOT",
    "name: weir-docker-release-candidate-audit",
    // The Trivy scan must actually be able to fail the release: a known HIGH/CRITICAL with a fix
    // available blocks it, and ignore-unfixed keeps that from being an unrelated, un-actionable CVE.
    "severity: HIGH,CRITICAL",
    "ignore-unfixed: true",
    'exit-code: "1"',
  ]) {
    requireText(candidate, marker, `${RELEASE} docker-candidate job`);
  }

  // Both published architectures must build before registry credentials exist.
  requireOrder(
    requireJob(release, "docker-arm64", RELEASE),
    [
      "uses: docker/setup-qemu-action@",
      "- name: Build unpushed Docker release candidate (linux/arm64)",
      "platforms: linux/arm64",
      "push: false",
    ],
    `${RELEASE} docker-arm64 job`,
  );

  // Checksums describe the files as published, so they are taken once every file is final and before
  // the upload. (The provenance attestation of these files is publish-windows's, over the bytes it downloads.)
  const windowsSmoke = requireJob(release, "windows-smoke", RELEASE);
  requireOrder(
    windowsSmoke,
    [
      "- name: Validate release version alignment",
      "- name: Verify no other-version release assets",
      "- name: Generate release artifact checksums",
      "- name: Upload Velopack release artifacts",
    ],
    `${RELEASE} windows-smoke job`,
  );

  // --- ci.yml and the workflows it calls ------------------------------------------------------------

  const ciDockerSmoke = requireJob(ciPackaging, "docker-smoke", CI_PACKAGING);
  requireOrder(
    ciDockerSmoke,
    [
      "- name: Build Weir Docker image",
      "- name: Start Weir Docker candidate",
      "- name: Full live E2E against Docker candidate",
      "- name: Upload Docker live-audit evidence",
      "- name: Cleanup Weir Docker smoke",
    ],
    `${CI_PACKAGING} docker-smoke job`,
  );
  for (const marker of [
    "WEIR_SESSION_COOKIE_SECURE=false",
    "WEIR_LIVE_E2E_FIXTURE_SERVER_ROOT: /e2e-fixture",
    "WEIR_LIVE_E2E_FIXTURE_HOST_ROOT:$WEIR_LIVE_E2E_FIXTURE_SERVER_ROOT",
    "name: weir-docker-live-audit",
  ]) {
    requireText(ciDockerSmoke, marker, `${CI_PACKAGING} docker-smoke job`);
  }

  // The contract suite runs one leg per area in the contract project's areas.json, and every leg is part of CI's verdict.
  requireText(requireJob(ci, "contract", CI), "areas: ${{ needs.changes.outputs.contract_areas }}", `${CI} contract job`);
  const contractLeg = requireJob(ciContract, "area", CI_CONTRACT);
  for (const marker of [
    "area: ${{ fromJSON(inputs.areas) }}",
    '--filter "Area=$CONTRACT_AREA"',
    'WEIR_CONTRACT_KEEP_DATA: "1"',
  ]) {
    requireText(contractLeg, marker, `${CI_CONTRACT} area job`);
  }

  // ci-passed judges every other ci.yml job, always runs, and knows each job's flag.
  const ciJobIds = jobIds(ci);
  const verdictJob = requireJob(ci, "ci-passed", CI);
  for (const marker of ["if: ${{ always() }}", "NEEDS: ${{ toJSON(needs) }}", "node scripts/ci-passed.mjs"]) {
    requireText(verdictJob, marker, `${CI} ci-passed job`);
  }
  const verdictNeeds = needsOf(verdictJob, `${CI} ci-passed`);
  for (const job of ciJobIds.filter((name) => name !== "ci-passed")) {
    if (!verdictNeeds.includes(job)) {
      throw new Error(`${CI} ci-passed does not need ${job}, so CI could pass without it.`);
    }
  }
  const judged = ciJobIds.filter((name) => !["changes", "ci-passed"].includes(name)).sort();
  if (JSON.stringify(judged) !== JSON.stringify(Object.keys(GATES).sort())) {
    throw new Error(
      `scripts/ci-passed.mjs GATES (${Object.keys(GATES).sort().join(", ")}) must list exactly the ci.yml jobs it judges (${judged.join(", ")}).`,
    );
  }

  // Every job and step the release gate asks CI for must still exist under that name.
  function ciJobBody(displayName) {
    for (const id of ciJobIds) {
      const body = requireJob(ci, id, CI);
      const named = body.match(/^ {4}name: (.*)$/m);
      if ((named ? named[1] : id) === displayName) return body;
    }
    throw new Error(
      `${CI} has no job named "${displayName}", which scripts/verify-ci-for-release.mjs requires. Update one to match the other.`,
    );
  }
  for (const want of REQUIRED_EVIDENCE) {
    const body = ciJobBody(want.job);
    for (const step of want.steps) {
      requireText(body, `- name: ${step}\n`, `${CI} job "${want.job}"`);
    }
  }
}

export const SUCCESS_MESSAGE =
  "Only publish-windows (the GitHub Release) and publish-docker (the image) can publish, each needs the shared " +
  "checks plus its own build's and neither waits for the other, the moving tags move last and never for a " +
  "release candidate, the Docker candidate's live E2E runs unpushed, every tag (a release candidate too) needs a " +
  "passing golden-path status on the tagged commit with no way to skip it, and ci-passed judges every CI job and still carries the release's evidence.";

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    checkReleaseGates(loadWorkflows());
  } catch (error) {
    console.error(`::error::${error.message}`);
    process.exit(1);
  }
  console.log(SUCCESS_MESSAGE);
}
