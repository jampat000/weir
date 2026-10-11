#!/usr/bin/env node
// Release gate: refuse to publish any tag, a release candidate included, unless Weir's scenario suite has passed on
// the exact commit the tag points at. There is no pass-through for a release candidate and no waiver: a release is
// proven before its tag, never after it (the owner, 11 Oct 2026, #954; the pass-through added in #907 is gone).
//
// The proof (docs/release.md, "Proof before tagging") is a run of the real product on a clean machine, installed from
// that commit's own build: scripts/scenarios/Invoke-WeirScenarios.ps1 on the golden VM. A passing run records its
// result as a GitHub commit status on the commit, and a failing run records a failure:
//
//   gh api repos/<owner>/<repo>/statuses/<sha> -f state=success -f context=golden-path \
//     -f description="..." -f target_url="<link to the evidence>"
//
// The gate passes only when the newest status with the context `golden-path` on this commit is `success`. A later
// `failure`, `error` or `pending` for the same context withdraws an earlier `success`, so the record always says
// how the latest run went.
//
// Usage (in release.yml):  node scripts/verify-golden-path-for-release.mjs
//   env GH_TOKEN (statuses: read), GITHUB_REPOSITORY, GITHUB_REF_NAME (the tag), GITHUB_SHA (else `git rev-parse HEAD`)
// Options: --sha <sha>, --tag <tag>
import { execFileSync } from "node:child_process";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

export const GOLDEN_PATH_CONTEXT = "golden-path";

export const NO_PASSING_RUN =
  "This commit has no passing golden-path run. Run Weir's scenario suite on this exact build (see docs/release.md), then re-run the release.";

// The version a release tag names. Every `v*` tag is gated, so this refuses only what is not a release tag at all,
// rather than letting it through unjudged.
export function releaseVersion(tag) {
  const version = String(tag ?? "").replace(/^refs\/tags\//, "").replace(/^v/, "").split("+")[0];
  if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(version)) throw new Error(`Not a release tag: ${tag}`);
  return version;
}

// The newest status for a context. `statuses` are commit statuses as the API returns them (id, context, state,
// created_at, ...). Ids rise with time, so they settle two statuses created in the same second.
export function latestStatus(statuses, context = GOLDEN_PATH_CONTEXT) {
  let latest = null;
  for (const status of statuses) {
    if (status.context !== context) continue;
    if (!latest || Date.parse(status.created_at) > Date.parse(latest.created_at)
      || (status.created_at === latest.created_at && status.id > latest.id)) {
      latest = status;
    }
  }
  return latest;
}

// Judges a commit's statuses. Returns the reasons the golden path does not count; empty means it passed.
export function evaluateStatuses(statuses, context = GOLDEN_PATH_CONTEXT) {
  const latest = latestStatus(statuses, context);
  if (!latest) return [`no status with the context "${context}" is recorded`];
  if (latest.state !== "success") {
    return [`the latest "${context}" status is ${latest.state}${latest.description ? ` (${latest.description})` : ""}`];
  }
  return [];
}

function listStatuses(repository, sha) {
  // One compact object per line, so a paginated listing needs no JSON array stitching.
  const output = execFileSync(
    "gh",
    [
      "api", "--paginate", `repos/${repository}/commits/${sha}/statuses?per_page=100`,
      "--jq", ".[] | {id, context, state, description, target_url, created_at, creator: .creator.login}",
    ],
    { encoding: "utf8", maxBuffer: 64 * 1024 * 1024 },
  );
  return output.split("\n").filter((line) => line.trim()).map((line) => JSON.parse(line));
}

// Judges one tag on one commit and says why. `statusesOf(repository, sha)` lists the commit's statuses; `log` and
// `error` are where the verdict goes. Returns true when the release may go ahead. Every tag is judged, whatever its
// pre-release part: a release candidate needs the same record a stable release does.
export function runGate({ tag, repository, sha, statusesOf = listStatuses, log = console.log, error = console.error }) {
  const version = releaseVersion(tag);
  if (!repository) throw new Error("GITHUB_REPOSITORY must be set.");
  if (!/^[0-9a-f]{40}$/.test(sha ?? "")) throw new Error(`Not a full commit SHA: ${sha}`);
  log(`Release gate: Weir's scenario suite must already have passed on ${sha} before ${version} ships.`);

  const statuses = statusesOf(repository, sha);
  const problems = evaluateStatuses(statuses);
  if (problems.length === 0) {
    const passed = latestStatus(statuses);
    log(`PASS: "${GOLDEN_PATH_CONTEXT}" is success on ${sha}, recorded by ${passed.creator} at ${passed.created_at}.`);
    if (passed.description) log(`  ${passed.description}`);
    if (passed.target_url) log(`  Evidence: ${passed.target_url}`);
    return true;
  }

  error(`::error::${NO_PASSING_RUN}`);
  for (const problem of problems) error(`  - ${problem}`);
  return false;
}

function main() {
  const argv = process.argv.slice(2);
  const option = (flag) => (argv.includes(flag) ? argv[argv.indexOf(flag) + 1] : undefined);
  const sha = option("--sha") || process.env.GITHUB_SHA || execFileSync("git", ["rev-parse", "HEAD^{commit}"], { encoding: "utf8" }).trim();
  const passed = runGate({ tag: option("--tag") || process.env.GITHUB_REF_NAME, repository: process.env.GITHUB_REPOSITORY, sha });
  if (!passed) process.exit(1);
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  try {
    main();
  } catch (error) {
    console.error(`::error::${error.message}`);
    process.exit(1);
  }
}
