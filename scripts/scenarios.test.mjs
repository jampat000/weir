// node --test scripts/scenarios.test.mjs
// Weir's VM scenario suite (scripts/scenarios, docs/release.md "Proof before tagging"): every PowerShell file parses in
// Windows PowerShell 5.1, every scenario in the catalog has code and is described in the docs, and the plan modes list the
// scenarios without touching a machine. Windows PowerShell exists on Windows only, so elsewhere these tests are skipped.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, readdirSync, writeFileSync } from "node:fs";
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
    assert.ok(["WithDeluno", "Fresh"].includes(entry.Phase), `${entry.Id} has a phase`);
    assert.ok(entry.Does.length > 40 && entry.Passes.length > 40, `${entry.Id} says what it does and what passes`);
  }
  assert.ok(entries.every((entry) => entry.Required), "every scenario is required: none may be optional");
  const order = entries.map((entry) => entry.Phase);
  assert.equal(order.indexOf("Fresh"), order.lastIndexOf("WithDeluno") + 1, "the WithDeluno scenarios all come first, then the Fresh ones");
});

test("the scenarios the issue asks for are all there", { skip }, () => {
  const ids = plan().map((entry) => entry.Id);
  for (const wanted of [
    "update-over-installed-weir", "workflows-from-deluno", "deluno-film-handoff", "deluno-release-with-extra", "deluno-resend",
    "deluno-outcomes", "logs-clean-deluno", "fresh-install", "first-visit-account", "weir-only-film", "process-again",
    "pause-resume", "deleted-queued-file", "update-requests", "logs-clean",
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
  assert.match(run.out, /An update over the Weir Deluno installed/);
});

test("no secret is a parameter: the Deluno step takes a URL only, defaulting to the Deluno inside the VM", { skip }, () => {
  for (const name of ["Invoke-WeirScenarios.ps1", "Run-WeirScenarios.ps1"]) {
    const source = readFileSync(join(scenarios, name), "utf8");
    assert.doesNotMatch(source, /\$DelunoApiKey/, `${name} takes a Deluno API key`);
    assert.match(source, /\[string\] \$DelunoUrl = 'http:\/\/127\.0\.0\.1:7879'/, `${name} does not default -DelunoUrl to the Deluno in the VM`);
  }
  const run = powershell(["-Command", `(Get-Command '${join(scenarios, "Invoke-WeirScenarios.ps1")}').Parameters.Keys -join ','`]);
  assert.equal(run.status, 0, run.err);
  assert.doesNotMatch(run.out, /DelunoApiKey|Password|Secret/i);
});

test("a secret the run holds is masked wherever it would be written", { skip }, () => {
  const script = [
    `. '${join(scenarios, "Weir.Scenarios.Lib.ps1")}'`,
    "Register-Secret 'throwaway-secret-123'",
    "Protect-Secrets 'key throwaway-secret-123 and deluno_AbC123xyz_-Q9 and plain text'",
    "Protect-Secrets '{\"connection_id\":1,\"webhook_secret\":\"abcDEF123_-xyz\",\"header_name\":\"X-Webhook-Secret\"}'",
  ].join("\n");
  const run = powershell(["-Command", script]);
  assert.deepEqual(run.out.trim().split(/\r?\n/), [
    "key **** and **** and plain text",
    '{"connection_id":1,"webhook_secret":"****","header_name":"X-Webhook-Secret"}',
  ]);
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
  assert.match(run.out, /set the golden-path status on 0123456789abcdef0123456789abcdef01234567/);
  assert.match(run.out, /Pause and resume from the tray/);
  assert.match(run.out, /An update over the Weir Deluno installed/);
  assert.equal(existsSync(join(out, "artifacts")), false, "-WhatIf made the output folder");
});

test("-WhatIf shows the order: WithDeluno first with no restore, then Fresh on the clean checkpoint, and no success without both", { skip }, () => {
  const out = mkdtempSync(join(tmpdir(), "weir-scenarios-order-"));
  const run = powershell([
    "-File", join(scenarios, "Invoke-WeirScenarios.ps1"),
    "-RigHost", "no-such-host", "-InstallerPath", join(out, "x", "Weir-win-Setup.exe"), "-Version", "1.0.0-rc.99",
    "-CommitSha", "0123456789abcdef0123456789abcdef01234567", "-OutDirectory", join(out, "artifacts"), "-WhatIf",
  ]);
  assert.equal(run.status, 0, run.err || run.out);
  const withDeluno = run.out.indexOf("Phase WithDeluno (first, NO restore)");
  const fresh = run.out.indexOf("Phase Fresh (second)");
  assert.ok(withDeluno >= 0 && fresh > withDeluno, "WithDeluno is listed before Fresh");
  assert.match(run.out, /restore nothing/);
  assert.match(run.out, /C:\\golden\\deluno-weir-scenario-key\.txt/);
  assert.equal(run.out.indexOf("restore checkpoint 'clean'") > fresh, true, "the checkpoint is restored only in the Fresh phase");
  assert.match(run.out, /success only if BOTH phases passed every scenario/);
  assert.match(run.out, /pending if none failed but some were not applicable/);
});

test("the Deluno key file is read inside the machine and deleted, and a bad one is refused without showing what it held", { skip }, () => {
  const folder = mkdtempSync(join(tmpdir(), "weir-scenarios-keyfile-"));
  const good = join(folder, "good.txt");
  const bad = join(folder, "bad.txt");
  writeFileSync(good, "deluno_TestKey0123456789abcdef\n");
  writeFileSync(bad, "not-a-key-line-one\nsecret-looking-line-two\n");
  const script = [
    `. '${join(scenarios, "Weir.Scenarios.Lib.ps1")}'`,
    `. '${join(scenarios, "Weir.Scenarios.Context.ps1")}'`,
    `. '${join(scenarios, "Weir.Scenarios.Deluno.ps1")}'`,
    `$script:Ctx.DelunoKeyFile = '${good}'`,
    "'first=' + (Read-DelunoKeyFile)",
    `'goodGone=' + (-not (Test-Path '${good}'))`,
    "'second=' + [bool](Read-DelunoKeyFile)",
    `$script:Ctx.DelunoKeyFile = '${bad}'`,
    "try { Read-DelunoKeyFile | Out-Null; 'bad=accepted' } catch { 'bad=' + (Protect-Secrets $_.Exception.Message) }",
    `'badGone=' + (-not (Test-Path '${bad}'))`,
  ].join("\n");
  const run = powershell(["-Command", script]);
  assert.equal(run.status, 0, run.err);
  const lines = run.out.trim().split(/\r?\n/);
  assert.equal(lines[0], "first=deluno_TestKey0123456789abcdef");
  assert.equal(lines[1], "goodGone=True");
  assert.equal(lines[2], "second=False");
  assert.match(lines[3], /^bad=The key file .* did not hold one line with a Deluno API key/);
  assert.doesNotMatch(lines[3], /secret-looking-line-two|not-a-key-line-one/);
  assert.equal(lines[4], "badGone=True");
});

test("with no Deluno answering and no key file, every Deluno scenario is not-applicable with the reason, never a substitute", { skip }, () => {
  const folder = mkdtempSync(join(tmpdir(), "weir-scenarios-nodeluno-"));
  const script = [
    `. '${join(scenarios, "Weir.Scenarios.Lib.ps1")}'`,
    `. '${join(scenarios, "Weir.Scenarios.Context.ps1")}'`,
    `. '${join(scenarios, "Weir.Scenarios.Deluno.ps1")}'`,
    "$script:Ctx.DelunoUrl = 'http://127.0.0.1:1'",
    `$script:Ctx.DelunoKeyFile = '${join(folder, "missing-key.txt")}'`,
    "foreach ($id in 'workflows_from_deluno', 'deluno_film_handoff', 'deluno_release_with_extra', 'deluno_resend', 'deluno_outcomes') { $r = & (\"Scenario_$id\"); $id + '|' + $r.NotApplicable + '|' + $r.Detail }",
  ].join("\n");
  const run = powershell(["-Command", script]);
  assert.equal(run.status, 0, run.err);
  const lines = run.out.trim().split(/\r?\n/);
  assert.equal(lines.length, 5);
  for (const line of lines) {
    assert.match(line, /\|True\|No Deluno answers at http:\/\/127\.0\.0\.1:1 .*no key file was left in the VM/);
  }
});
