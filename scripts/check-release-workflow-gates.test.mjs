// node --test scripts/check-release-workflow-gates.test.mjs
// The release workflow's publish shape, judged on the real workflows and on deliberately broken copies of release.yml.
import assert from "node:assert/strict";
import { test } from "node:test";

import { checkReleaseGates, loadWorkflows } from "./check-release-workflow-gates.mjs";

const real = loadWorkflows();

function replaceOnce(text, from, to) {
  assert.ok(text.includes(from), `the fixture text to replace is not in release.yml: ${from}`);
  return text.replace(from, () => to);
}

const jobBounds = (release, job) => {
  const start = release.indexOf(`\n  ${job}:\n`) + 1;
  assert.ok(start > 0, `release.yml has no job ${job}`);
  const rest = release.slice(start + 1);
  const next = rest.search(/\n  [a-zA-Z0-9_-]+:\n/);
  return [start, next < 0 ? release.length : start + 1 + next + 1];
};

const editJob = (release, job, edit) => {
  const [start, end] = jobBounds(release, job);
  return release.slice(0, start) + edit(release.slice(start, end)) + release.slice(end);
};

const withoutNeed = (body, job) =>
  body.replace(/( {4}needs: \[)([^\]]*)\]/, (_, head, names) => {
    const kept = names.split(",").map((name) => name.trim()).filter((name) => name !== job);
    return `${head}${kept.join(", ")}]`;
  });

const broken = (release) => ({ ...real, release });
const rejects = (release, pattern) => assert.throws(() => checkReleaseGates(broken(release)), pattern);

test("the real workflows have the intended shape", () => {
  assert.doesNotThrow(() => checkReleaseGates(real));
});

test("publish-windows may not wait for a Docker build", () => {
  for (const job of ["docker-candidate", "docker-arm64"]) {
    rejects(
      editJob(real.release, "publish-windows", (body) => body.replace("windows-smoke]", `windows-smoke, ${job}]`)),
      new RegExp(`publish-windows needs ${job}`),
    );
  }
});

test("publish-docker may not wait for the Windows build", () => {
  rejects(
    editJob(real.release, "publish-docker", (body) => body.replace("docker-arm64]", "docker-arm64, windows-smoke]")),
    /publish-docker needs windows-smoke/,
  );
});

test("neither publish job may wait for the other", () => {
  rejects(
    editJob(real.release, "publish-windows", (body) => body.replace("windows-smoke]", "windows-smoke, publish-docker]")),
    /publish-windows needs publish-docker/,
  );
  rejects(
    editJob(real.release, "publish-docker", (body) => body.replace("docker-arm64]", "docker-arm64, publish-windows]")),
    /publish-docker needs publish-windows/,
  );
});

test("each publish job needs every shared check and its own build's checks", () => {
  for (const job of ["ci-passed", "golden-path", "validate", "windows-smoke"]) {
    rejects(
      editJob(real.release, "publish-windows", (body) => withoutNeed(body, job)),
      new RegExp(`publish-windows does not need ${job}`),
    );
  }
  for (const job of ["ci-passed", "golden-path", "validate", "docker-candidate", "docker-arm64"]) {
    rejects(
      editJob(real.release, "publish-docker", (body) => withoutNeed(body, job)),
      new RegExp(`publish-docker does not need ${job}`),
    );
  }
});

test("a publish job may not run past a failed gate", () => {
  for (const job of ["publish-windows", "publish-docker"]) {
    rejects(
      editJob(real.release, job, (body) => body.replace("    needs:", "    if: ${{ always() }}\n    needs:")),
      new RegExp(`${job}'s if: must not contain always\\(\\)`),
    );
  }
});

test("a third job may not publish", () => {
  const third = (permissions) =>
    real.release +
    `\n  publish-extra:\n    runs-on: ubuntu-latest\n    timeout-minutes: 10\n    permissions:\n      ${permissions}\n    steps:\n      - run: echo hi\n`;
  for (const permissions of ["packages: write", "contents: write", "id-token: write", "attestations: write"]) {
    rejects(third(permissions), /only publish-windows and publish-docker may publish/);
  }
});

test("a build job may not log in to the registry, push, release or move a tag", () => {
  for (const [job, marker] of [
    ["docker-arm64", "      - uses: docker/login-action@dbcb813823bdd20940b903addbd779551569679f # v4\n"],
    ["docker-candidate", "      - run: docker push example\n"],
    ["windows-smoke", "      - run: gh release create v1\n"],
    ["validate", "      - run: docker buildx imagetools create --tag a:latest b\n"],
  ]) {
    rejects(
      editJob(real.release, job, (body) => body.replace("    steps:\n", `    steps:\n${marker}`)),
      new RegExp(`job ${job} \\(only publish-windows and publish-docker may publish\\)`),
    );
  }
});

test("publish-windows holds no registry rights and publish-docker no Release rights", () => {
  rejects(editJob(real.release, "publish-windows", (body) => body.replace("      contents: write\n", "      contents: write\n      packages: write\n")), /packages: write/);
  rejects(editJob(real.release, "publish-docker", (body) => body.replace("      contents: read\n", "      contents: write\n")), /contents: write/);
  rejects(replaceOnce(real.release, "  publish-windows:\n", "  publish-windows:\n    # push: true\n"), /push: true/);
});

test("workflow-level write access is refused", () => {
  rejects(replaceOnce(real.release, "permissions:\n  contents: read\n", "permissions:\n  contents: write\n"), /write access at workflow level/);
});

test("publish-docker's steps keep their order", () => {
  const swapped = editJob(real.release, "publish-docker", (body) =>
    body
      .replace("- name: Verify published Docker manifest", "- name: SWAP")
      .replace("- name: Smoke test published Docker image", "- name: Verify published Docker manifest")
      .replace("- name: SWAP", "- name: Smoke test published Docker image"),
  );
  rejects(swapped, /unsafe release gate order/);
});

test("the moving tags move in the last step of publish-docker", () => {
  rejects(
    editJob(real.release, "publish-docker", (body) => `${body.trimEnd()}\n\n      - name: After\n        run: echo done\n`),
    /no step after the moving-tags step/,
  );
  rejects(
    editJob(real.release, "publish-docker", (body) =>
      replaceOnce(body, "      - name: Publish release Docker image", "      - run: docker buildx imagetools create --tag x:latest y\n\n      - name: Publish release Docker image"),
    ),
    /only in its last step/,
  );
});

test("a release candidate moves no moving tag", () => {
  rejects(
    editJob(real.release, "publish-docker", (body) => body.replace('if [ "$PRERELEASE" = "true" ]', 'if [ "$PRERELEASE" = "never" ]')),
    /leave the moving-tags step for a pre-release/,
  );
  rejects(
    editJob(real.release, "publish-docker", (body) => body.replace("            exit 0\n", "")),
    /leave the moving-tags step for a pre-release/,
  );
});

test("publish-docker pushes the version tag alone", () => {
  rejects(
    editJob(real.release, "publish-docker", (body) =>
      body.replace("tags: ${{ steps.image.outputs.name }}:${{ steps.version.outputs.plain }}\n", "tags: ${{ steps.image.outputs.name }}:latest\n"),
    ),
    /missing required release gate marker|may move a tag/,
  );
});

test("publish-windows attests the release files and publishes the notes last", () => {
  rejects(editJob(real.release, "publish-windows", (body) => body.replace("prerelease: ${{ steps.version.outputs.prerelease }}", "prerelease: false")), /prerelease/);
  rejects(editJob(real.release, "publish-windows", (body) => body.replace("if: ${{ vars.ATTEST_PROVENANCE == 'true' }}\n", "")), /ATTEST_PROVENANCE/);
  const swapped = editJob(real.release, "publish-windows", (body) =>
    body
      .replace("- name: Prepare user-facing release notes", "- name: SWAP")
      .replace("- name: Publish GitHub Release", "- name: Prepare user-facing release notes")
      .replace("- name: SWAP", "- name: Publish GitHub Release"),
  );
  rejects(swapped, /unsafe release gate order/);
});

test("publish-docker attests the pushed image only when ATTEST_PROVENANCE is true", () => {
  rejects(editJob(real.release, "publish-docker", (body) => body.replace("if: ${{ vars.ATTEST_PROVENANCE == 'true' }}\n", "")), /ATTEST_PROVENANCE/);
  rejects(editJob(real.release, "publish-docker", (body) => body.replace("subject-digest: ${{ steps.push.outputs.digest }}", "subject-digest: sha256:x")), /subject-digest/);
});

test("the Docker candidate's live E2E stays unpushed and before the evidence upload", () => {
  rejects(editJob(real.release, "docker-candidate", (body) => body.replace("push: false", "push: true")), /push: true/);
  rejects(editJob(real.release, "docker-candidate", (body) => body.replace("- name: Full live E2E against unpushed Docker release candidate", "- name: Skipped")), /Full live E2E/);
});

test("ci-passed must judge every CI job", () => {
  const ci = real.ci.replace("needs: [changes, repo-checks, server-linux, ", "needs: [changes, repo-checks, ");
  assert.throws(() => checkReleaseGates({ ...real, ci }), /ci-passed does not need server-linux/);
});

test("the golden-path job is required and only reads statuses", () => {
  rejects(editJob(real.release, "golden-path", (body) => body.replace("verify-golden-path-for-release.mjs", "something-else.mjs")), /golden-path job/);
  rejects(editJob(real.release, "golden-path", (body) => body.replace("statuses: read", "statuses: write")), /statuses/);
});

test("the golden-path job cannot be skipped for a release candidate or anything else", () => {
  const skipped = /golden-path job must run, and must fail the release, for every tag/;
  // The pass-through #907 added, written as the workflow would: a job that does not run for a pre-release tag.
  rejects(
    editJob(real.release, "golden-path", (body) => body.replace("    runs-on:", "    if: ${{ !contains(github.ref_name, '-') }}\n    runs-on:")),
    skipped,
  );
  rejects(
    editJob(real.release, "golden-path", (body) => body.replace("      - name: The golden path passed on the tagged commit", "      - name: The golden path passed on the tagged commit\n        if: ${{ !contains(github.ref_name, '-rc.') }}")),
    skipped,
  );
  rejects(
    editJob(real.release, "golden-path", (body) => body.replace("    timeout-minutes: 10", "    timeout-minutes: 10\n    continue-on-error: true")),
    skipped,
  );
});
