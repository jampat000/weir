// node --test scripts/scenarios.test.mjs
// Weir's VM scenario suite (scripts/scenarios, docs/release.md "Proof before tagging"): every PowerShell file parses in
// Windows PowerShell 5.1, every scenario in the catalog has code and is described in the docs, and the plan modes list the
// scenarios without touching a machine. Windows PowerShell exists on Windows only, so elsewhere these tests are skipped.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, readdirSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const scenarios = join(repoRoot, "scripts", "scenarios");
const hasWindowsPowerShell = process.platform === "win32" && spawnSync("powershell.exe", ["-NoProfile", "-Command", "exit 0"]).status === 0;
const skip = hasWindowsPowerShell ? false : "Windows PowerShell is not available";

function powershell(args) {
  const run = spawnSync("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", ...args], { encoding: "utf8" });
  return { status: run.status, out: run.stdout, err: run.stderr };
}

const plan = () => {
  const run = powershell(["-File", join(scenarios, "Run-WeirScenarios.ps1"), "-Plan", "-Json"]);
  assert.equal(run.status, 0, run.err || run.out);
  return JSON.parse(run.out);
};

test("every scenario file parses in Windows PowerShell 5.1", { skip }, () => {
  const files = readdirSync(scenarios).filter((name) => name.endsWith(".ps1"));
  assert.ok(files.length >= 8, "the suite's scripts are there");
  const script = [
    "$bad = 0",
    `foreach ($f in Get-ChildItem -LiteralPath '${scenarios}' -Filter *.ps1) {`,
    "  $t = $null; $e = $null",
    "  [void][System.Management.Automation.Language.Parser]::ParseFile($f.FullName, [ref]$t, [ref]$e)",
    "  foreach ($x in $e) { $bad++; Write-Output ('{0}:{1}: {2}' -f $f.Name, $x.Extent.StartLineNumber, $x.Message) }",
    "}",
    "exit $bad",
  ].join("\n");
  const run = powershell(["-Command", script]);
  assert.equal(run.status, 0, run.out + run.err);
});

test("every scenario in the catalog has code, a phase, and words for what it does and what passes", { skip }, () => {
  const entries = plan();
  assert.ok(entries.length >= 12);
  assert.equal(new Set(entries.map((entry) => entry.Id)).size, entries.length, "scenario ids are unique");
  for (const entry of entries) {
    assert.ok(entry.Implemented, `${entry.Id} has no Scenario_ function`);
    assert.ok(["Fresh", "Update"].includes(entry.Phase), `${entry.Id} has a phase`);
    assert.ok(entry.Does.length > 40 && entry.Passes.length > 40, `${entry.Id} says what it does and what passes`);
  }
  assert.equal(entries.filter((entry) => !entry.Required).length, 1, "exactly one scenario is optional");
  assert.equal(entries.find((entry) => !entry.Required).Id, "workflows-from-deluno");
});

test("the scenarios the issue asks for are all there", { skip }, () => {
  const ids = plan().map((entry) => entry.Id);
  for (const wanted of [
    "fresh-install", "update-over-previous", "first-visit-account", "workflows-from-deluno", "handoff-film",
    "handoff-release-with-extra", "handoff-resend", "pause-resume", "process-again", "deleted-queued-file",
    "update-requests", "pre-update-copy", "logs-clean",
  ]) {
    assert.ok(ids.includes(wanted), `missing scenario ${wanted}`);
  }
});

test("docs/release.md names every scenario by its title", { skip }, () => {
  const docs = readFileSync(join(repoRoot, "docs", "release.md"), "utf8");
  for (const entry of plan()) assert.ok(docs.includes(entry.Title), `docs/release.md does not name "${entry.Title}"`);
});

test("-Plan prints the plan and touches nothing", { skip }, () => {
  const run = powershell(["-File", join(scenarios, "Run-WeirScenarios.ps1"), "-Plan"]);
  assert.equal(run.status, 0, run.err);
  assert.match(run.out, /Fresh install/);
  assert.match(run.out, /An update over the previous release/);
});

test("-WhatIf lists the run and the scenarios, and makes no folder or file", { skip }, () => {
  const out = mkdtempSync(join(tmpdir(), "weir-scenarios-whatif-"));
  const run = powershell([
    "-File", join(scenarios, "Invoke-WeirScenarios.ps1"),
    "-RigHost", "no-such-host", "-InstallerPath", join(out, "missing", "Weir-win-Setup.exe"), "-Version", "1.0.0-rc.99",
    "-CommitSha", "0123456789abcdef0123456789abcdef01234567", "-OutDirectory", join(out, "artifacts"), "-WhatIf",
  ]);
  assert.equal(run.status, 0, run.err || run.out);
  assert.match(run.out, /restore checkpoint 'clean'/);
  assert.match(run.out, /set the golden-path status to success on 0123456789abcdef0123456789abcdef01234567/);
  assert.match(run.out, /Pause and resume from the tray/);
  assert.match(run.out, /An update over the previous release/);
  assert.equal(existsSync(join(out, "artifacts")), false, "-WhatIf made the output folder");
});
