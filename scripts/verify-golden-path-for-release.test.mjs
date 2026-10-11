// node --test scripts/verify-golden-path-for-release.test.mjs
// The release gate's judgement of a commit's statuses, on hand-built status objects.
import assert from "node:assert/strict";
import { test } from "node:test";

import { NO_PASSING_RUN, evaluateStatuses, latestStatus, releaseVersion, runGate } from "./verify-golden-path-for-release.mjs";

let nextId = 1;
const status = (overrides = {}) => ({
  id: nextId++,
  context: "golden-path",
  state: "success",
  description: "Passed on the clean VM",
  created_at: "2026-10-07T10:00:00Z",
  ...overrides,
});

test("a success for the golden-path context passes", () => {
  assert.deepEqual(evaluateStatuses([status()]), []);
});

test("a commit with no statuses, or none for the context, does not pass", () => {
  assert.match(evaluateStatuses([]).join("\n"), /no status with the context "golden-path"/);
  assert.match(evaluateStatuses([status({ context: "ci-passed" })]).join("\n"), /no status with the context "golden-path"/);
});

test("a success under another context does not count", () => {
  assert.notDeepEqual(evaluateStatuses([status({ context: "golden-path-draft" }), status({ context: "ci-passed" })]), []);
});

test("failure, error and pending do not pass", () => {
  for (const state of ["failure", "error", "pending"]) {
    assert.match(evaluateStatuses([status({ state })]).join("\n"), new RegExp(`latest "golden-path" status is ${state}`));
  }
});

test("the failure's description is part of the reason", () => {
  assert.match(evaluateStatuses([status({ state: "failure", description: "Pause did not hold" })]).join("\n"), /Pause did not hold/);
});

test("the newest status for the context counts, whatever order the API lists them in", () => {
  const passed = status({ created_at: "2026-10-07T10:00:00Z" });
  const failedLater = status({ state: "failure", created_at: "2026-10-07T11:00:00Z" });
  assert.notDeepEqual(evaluateStatuses([failedLater, passed]), []);
  assert.notDeepEqual(evaluateStatuses([passed, failedLater]), []);

  const failedFirst = status({ state: "failure", created_at: "2026-10-07T09:00:00Z" });
  const passedLater = status({ created_at: "2026-10-07T12:00:00Z" });
  assert.deepEqual(evaluateStatuses([passedLater, failedFirst]), []);
  assert.deepEqual(evaluateStatuses([failedFirst, passedLater]), []);
});

test("two statuses made in the same second are told apart by id", () => {
  const first = status({ state: "failure" });
  const second = status();
  assert.equal(latestStatus([second, first]).id, second.id);
  assert.equal(latestStatus([first, second]).id, second.id);
  assert.deepEqual(evaluateStatuses([first, second]), []);
});

test("other contexts never hide the golden-path status", () => {
  const passed = status();
  const newerOther = status({ context: "ci-passed", state: "failure", created_at: "2026-10-07T13:00:00Z" });
  assert.deepEqual(evaluateStatuses([newerOther, passed]), []);
});

test("the refusal tells the operator what to do", () => {
  assert.equal(
    NO_PASSING_RUN,
    "This commit has no passing golden-path run. Run Weir's scenario suite on this exact build (see docs/release.md), then re-run the release.",
  );
});

test("something that is not a release tag is refused rather than waved through", () => {
  assert.throws(() => releaseVersion("main"), /Not a release tag/);
  assert.throws(() => releaseVersion(undefined), /Not a release tag/);
  assert.equal(releaseVersion("refs/tags/v1.0.0-rc.4"), "1.0.0-rc.4");
  assert.equal(releaseVersion("v2.3.4"), "2.3.4");
});

// The gate as release.yml runs it: a tag, a commit and what GitHub lists for that commit.
const SHA = "a".repeat(40);
function gate(tag, statuses, overrides = {}) {
  const out = [];
  const err = [];
  const passed = runGate({
    tag,
    repository: "jampat000/Weir",
    sha: SHA,
    statusesOf: (repository, sha) => {
      assert.equal(repository, "jampat000/Weir");
      assert.equal(sha, SHA);
      return statuses;
    },
    log: (line) => out.push(line),
    error: (line) => err.push(line),
    ...overrides,
  });
  return { passed, out: out.join("\n"), err: err.join("\n") };
}

test("a release candidate with no record is refused", () => {
  for (const tag of ["v1.0.0-rc.14", "v1.0.0-beta.1", "v2.0.0-rc.1+abc1234"]) {
    const verdict = gate(tag, []);
    assert.equal(verdict.passed, false, tag);
    assert.match(verdict.err, /no passing golden-path run/);
    assert.match(verdict.err, /no status with the context "golden-path"/);
  }
});

test("a release candidate whose only record is another context or a failure is refused", () => {
  assert.equal(gate("v1.0.0-rc.14", [status({ context: "ci-passed" })]).passed, false);
  assert.equal(gate("v1.0.0-rc.14", [status({ state: "failure", description: "Pause did not hold" })]).passed, false);
  const withdrawn = gate("v1.0.0-rc.14", [status(), status({ state: "failure", created_at: "2026-10-07T11:00:00Z" })]);
  assert.equal(withdrawn.passed, false);
  assert.match(withdrawn.err, /latest "golden-path" status is failure/);
});

test("a release candidate with a passing record goes ahead, and the verdict names the evidence", () => {
  const verdict = gate("v1.0.0-rc.14", [
    status({ creator: "jampat000", description: "Weir scenarios passed 11 of 11", target_url: "https://example.test/run" }),
  ]);
  assert.equal(verdict.passed, true);
  assert.equal(verdict.err, "");
  assert.match(verdict.out, /PASS: "golden-path" is success on a{40}, recorded by jampat000/);
  assert.match(verdict.out, /Weir scenarios passed 11 of 11/);
  assert.match(verdict.out, /Evidence: https:\/\/example\.test\/run/);
});

test("a stable release is judged the same way as a release candidate", () => {
  assert.equal(gate("v1.0.0", []).passed, false);
  assert.equal(gate("v1.0.0", [status()]).passed, true);
});

test("the gate says nothing is waived: it names the tag's version and the commit it judges", () => {
  const verdict = gate("v1.0.0-rc.14", [status()]);
  assert.match(verdict.out, /scenario suite must already have passed on a{40} before 1\.0\.0-rc\.14 ships/);
});

test("a tag that is not a release tag, a missing repository or a short SHA stops the gate", () => {
  assert.throws(() => gate("main", [status()]), /Not a release tag/);
  assert.throws(() => gate("v1.0.0-rc.1", [status()], { repository: "" }), /GITHUB_REPOSITORY/);
  assert.throws(() => gate("v1.0.0-rc.1", [status()], { sha: "abc1234" }), /Not a full commit SHA/);
});
