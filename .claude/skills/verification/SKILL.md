---
name: verification
description: Routes the verification of a Puck change and defines what a finished lane proves. Covers the gate route (`puck gate`), running gates from a private copy of the head's own CLI, ledgers and generated files checked with `--check`, `puck affected` against the lane's merge base, never testing binaries a failed build left behind, red legs proved by withholding the fix (`puck laws prove`), GPU legs one at a time per GPU under a grant, and flake versus failure. Use before calling any change verified, before handing back or merging a lane, when writing a law or canary, when running parity, canaries or GPU tests, and when a gate fails. Subsystem commands belong to their owners: maths-laws for the Maths law suite, gaming-bricks for the emulator batteries, rendering for GPU, parity and capture specifics, puck-world for World runs and stdin scripts. review-passes owns briefing and verifying a cross-family review-and-fix pass.
---

# Verification

This skill is the agent procedure for proving a change: which commands to run,
from which binary, in what order, and what a lane must show before it is handed
back or merged. It does not list what each suite or canary covers;
[Contributing § Verification](../../../docs/development/contributing.md#verification)
is the human owner of that, the [CLI reference](../../../docs/reference/cli.md)
owns each verb's flags, and the subsystem skills own their own gates. When this
skill and the code disagree, the code wins and this file is corrected in the
same change. The user's current instruction outranks it.

## The route

- **`puck gate`** builds the solution and runs the affected selection and
  repository checks against the merge base. Run it on the lane's final head
  with `--merge-base origin/<integration-branch>`, naming the branch in the
  brief, from a CLI copy outside the checkout. Report its verdict and do not
  repeat by hand a step it ran; its `--help` lists the steps. Its `--gpu` adds
  the GPU legs and runs only under a GPU grant.
- **`puck laws prove`** proves red legs: use it for every new or changed law,
  with `--fix <commit>` or with `--file-list` for an uncommitted fix. It
  withholds the fix in a proof tree of its own (a persistent clone it builds
  incrementally, never the shared tree), and refuses a proof in which a
  selected test was skipped.

Run covered steps by hand only where a brief rules a verb out, for example a
machine that must not build the solution. Complete checks the gate omits:
`puck baselines <artifact> --check` for affected committed baselines, explicit
`puck docs links <document>...` for changed documents outside its default set,
and `puck docs citations` when required below, under the GPU rules. The law
prover supplies build and run evidence; inspect the withheld change and the
failure messages before accepting a red leg.

## Run gates from your own CLI copy

- Build `src/Puck.Cli` in Release in your worktree, then copy
  `src/Puck.Cli/bin/Release/net10.0/` into a directory only your lane uses,
  named for the lane (`<scratchpad>/<lane>-cli/`). Run every verb as
  `dotnet <copy>/Puck.Cli.dll <verb>` from your worktree root.
- Never run the globally installed `puck` tool: it lags the head and silently
  skips manifest members it does not know. Never run from a copy another agent
  can overwrite, or from the `bin` directory while a build may rewrite it.
- Refresh the copy after a change under `src/Puck.Cli` or any project it
  references, before the gate that judges that change.
- Name the CLI that produced each result in your report.

## Build before you test

- A failed build leaves the previous binaries in place, so a test run after it
  measures old code. Run tests only after a build that exited 0 following your
  last edit. Never pass `--no-build`, or run a test assembly directly, unless
  that build succeeded.
- Send every build and test run's full output to a lane-named log
  (`<scratchpad>/<lane>-gate.log`) and stop at the first build error. A flake
  needs its failure message to be judged.
- Do not edit the source tree while a canary, parity or test run is going:
  canaries rebuild from source, and tests read baselines, schemas and generated
  tables from it. Apply the
  [review launch restriction](../review-passes/SKILL.md#before-launching) when
  scheduling a pass alongside a run.
- Verify the operation itself: read its output and its exit status, since an
  exit code of 0 is not a verdict. After moving a tool or document, run its
  real consumer at the new location.
- Build the projects you touched while iterating. Build the whole solution
  (`dotnet build Puck.slnx -c Release`) once at the lane's end; it must finish
  with zero warnings and zero errors.

## Check, never record, while verifying

A verb run without `--check` rewrites the file it owns. While verifying, run the
check form:

| Owns | Check form |
|---|---|
| `FileLengths.json` | `puck lengths --check` |
| `CommentSmells.json` | `puck comment-smells --check` |
| `FormatVersions.json` | `puck formats --check` |
| `CanaryCeilings.json` | `puck canary-ceilings --check` |
| Formatting of touched files | `puck format --check --file-list <scratchpad>/<lane>-files.json` |
| The project-map layering block | `puck architecture --check` |
| `docs/world-name-registry.md` | `puck registry --check` |
| Generated schemas and model shape | `puck schema --check` |
| A committed test baseline | `puck baselines <artifact> --check` |

Run the recording form only to apply a deliberate change: `puck lengths` after
shrinking a recorded file, `puck format --file-list` over your own files,
`puck formats` after bumping a format token, `puck canary-ceilings` after
changing canary cost, a baseline whose movement the change explains. Review
the rewritten file's diff and commit it in the same change. A ledger rewritten
during verification hides the drift the check exists to report.

## Choose what to run

- Use the lane's real base: `puck affected --merge-base
  origin/<integration-branch>` reads the change against the merge base of HEAD
  and that branch, so landings on it after the lane started are not counted.
  `--since <branch>` compares against the branch's moving tip and folds those
  landings in. The integration branch is the one the lead's brief names.
- `puck affected` without `--run` prints the plan. `--run` builds and runs the
  chosen suites, `test` worlds and catalog check; `--gpu` adds the chosen
  canaries and parity, so add it only under a GPU grant and otherwise list the
  plan's `canary` and `parity` lines as GPU legs owed.
- While iterating, run only the test classes your edits add or touch and the
  canaries that exercise the change. Run the affected selection once at the
  lane's end. Run full sets (`puck canary --merge`, every suite) only when the
  owner or the lead asks.
- A changed fixture or canary world reaches every law that boots it. Find its
  other readers with `puck search` on its path and run them.

## Prove each law's red leg

Prefer type enforcement over a source-scan law when the compiler can enforce
the contract. Make a required dependency a required, non-null parameter; let
the compiler check every call site. Do not substitute a regex over constructor
calls: it misses target-typed `new(...)` and gives false assurance.

A law or canary that passes proves nothing until it has been seen to fail when
the behavior it pins is wrong. A law that passes with the fix withheld pins
nothing, however plausible it reads.

Before `puck laws prove`, inspect the change its selected paths will withhold.
Afterwards, check its reported failures against the intended assertion and
message. It rejects failed builds, skipped tests and inconsistent runs, but
does not judge whether a failure is the one the law was written for. When a
brief requires a manual proof, use these steps:

1. Withhold the fix and keep the law, in a scratch worktree of your own, never
   by reverting or stashing files in a shared tree.
   In a probe or script, point Git at the scratch tree with `git -C <tree>`
   rather than a directory change. Where a script must change directory, make
   a failed change stop it (`cd <tree> || exit 1`, or `set -e`): after
   `cd <tree>; git ...`, a `cd` line followed by Git lines, or
   `cd <tree> && git a; git b`, a failed `cd` leaves the later Git commands
   running against the real repository.
2. Rebuild, and confirm the build exited 0 after the withholding: a failed
   build leaves the previous binaries, and a run against them tests the fix.
3. Run the law. It must fail at the assertion it was written for, with the
   intended message. A failure anywhere else (a compile error, a setup throw, a
   different assertion) is not a red leg. The outcome must be **Failed**; a
   **Skipped** run, or one that selected no test, is not a red leg: read the
   counts.
4. Confirm the withheld tree differs from the fixed one exactly where you
   meant it to (diff it): a mutation tool can produce a different mutant, and
   a red leg against it proves nothing about the fix.
5. Restore the fix, then touch the restored files before rebuilding and running
   the law again. It must pass. Restoring an older timestamp can let MSBuild
   keep the mutated assembly, so a run without this rebuild can test the
   mutation instead of the restored fix.
6. Record in the commit message which laws were proved red and how.

In xUnit v3, `Assert.Throws`, `Assert.ThrowsAny`, `Assert.ThrowsAsync`,
`Record.Exception` and `Record.ExceptionAsync` all rethrow the skip exception
(verified on xUnit 3.2.2). A law that wraps a call which can skip, such as a
device or capability probe, can therefore report **Skipped** with its fix
withheld and pin nothing. In such laws, catch the exception directly with a
`try`/`catch` and assert on it.

A canary's discriminating leg is its red leg and must fail for the reason its
manifest names. For `tests/Puck.Maths.Tests`, the `maths-laws` mutation probe is
the procedure. Report an existing law you find that cannot fail; fixing it is
owed when your change relies on it.

## GPU legs

GPU work is `puck parity`, `puck counters`, any canary requiring `gpu`
(including `--merge`), a windowed or offscreen `Puck.World` run, any verb that
boots one in those modes, and any test that opens a device. This includes a
full `Puck.World.Tests` run: its device-law classes open the GPU. `puck docs
citations` without `--enumeration` builds `Puck.World` and boots it headless
and windowed to read its help vocabulary, so it waits for the GPU like any
other GPU leg. A World run with effective `host.presentation: none` uses no
GPU; the `puck-world` skill owns the presentation modes and deployment
overrides.

- A GPU runs one GPU leg at a time. Legs compete for the device, the ports and
  the frame budget, and a contended leg times out.
- While another GPU leg runs, filter World tests with
  `--filter "FullyQualifiedName!~DeviceLaw"` and list the skipped device-law
  classes as owed. Keep the CPU-heavy work restriction below.
- In delegated work, run GPU legs only under a grant the lead issues in your
  brief. Without one, run none: list each leg you need (verb, canaries,
  backend) in your hand-back report.
- With a grant, run the granted legs serially, nothing else GPU-bound beside
  them, and keep CPU-heavy work (solution builds, large suites) off the machine
  while they run.
- Qualify the merged head, never a batch on its own. Before a batch's GPU run,
  merge the current integration head into it. A stale batch can fail or pass
  because it lacks changes already on the integration branch.
  [`orchestration`](../orchestration/SKILL.md#assemble-and-refresh-batches)
  owns batch assembly and merge sequencing.
- A per-change GPU check is one to four canaries on one backend: the backend
  the change touches, or Vulkan when it is backend-neutral. Parity runs when
  the change means to move pixels or simulation state, and otherwise once at
  the lane's end. The `rendering` skill owns which canaries a render change
  owes.
- Treat `puck counters --check` on a non-recording device as partial ledger
  evidence under the
  [rules](../../../docs/reference/cli.md#puck-counterswork-counter-collector).
  It judges `deterministic` ceilings, including required zeros, but skips
  `per-backend-deterministic` values, including zero ceilings. Require each
  backend's recorded device identity to match before claiming the whole ledger
  was judged; use the recording device (the floor GPU) for a re-record.
- Stop a counters re-record when a `deterministic` count disagrees between
  backends. Find and fix the cause before recording again or committing the
  ledger. Inspect the differences and exit status even when `--record` writes
  a file: it writes the ceilings before returning a backend-mismatch failure.

### GPU process checks and script runs

A detector that waits for the GPU to go idle by matching process command lines
excludes the matching shell (`powershell`, `pwsh` or `bash`). The query's own
command line contains the strings it searches for; without this exclusion it
waits on itself forever. Run such a detector once by hand on an idle machine
before trusting it.

On Windows, stopping a background task can kill a wrapper shell and leave the
script's own bash running. Before relaunching a GPU script, find every instance
by command line, stop each by process id, and confirm none remain. Bash reads
a script as it runs, so never edit its file while an instance is running. Copy
the script under a new name per run.

### Flake or failure

Re-run a failed leg once, alone, with nothing else running on the machine.

- **Flake:** the first failure was a timeout, a bind, listener or port wait, a
  readiness wait, or a temporary-directory teardown error, and the leg passes
  alone. Report it with the original message and the passing re-run.
- **Failure:** the leg fails again, or the first failure was a wrong value: a
  pixel or tile verdict, a state hash, a counted work line, a refusal reason, a
  content assertion, or a validation-layer message. Fix it. Never re-run a
  content failure hoping for green.

The same rule applies to load-sensitive CPU tests.

## What a finished lane proves

A lane is finished when its final head, with the integration branch's current
tip merged in, shows all of the following. `puck gate` is the one
command for the parts it covers.

1. `dotnet build Puck.slnx -c Release` exits 0 with zero warnings.
2. The affected selection against the merge base passes: suites, `test`
   worlds, the catalog check, and the GPU legs run under grant or listed as
   owed.
3. Every new or changed law has a proved red leg.
4. The check forms above pass for everything the change reaches, and
   `puck docs links` passes on every changed document (`puck docs citations`
   when a document or XML comment cites a verb or document field).
5. Persisted replays, baselines and ceilings a deliberate correction moved are
   re-recorded in the same change, with the reason in the commit (`AGENTS.md`
   rule 4).
6. The commits follow `AGENTS.md`: explicit staging, `area: sentence` subjects,
   the evidence in the message.
7. The hand-back report names, under Needs you, Changed and Found: the CLI
   that produced each result, GPU legs still owed, each flake with its message,
   and what was not verified.

## Route adjacent work

| Skill | Route there for |
|---|---|
| [`orchestration`](../orchestration/SKILL.md) | Coordinating lanes, integration batches, machines and GPU grants. |
| [`review-passes`](../review-passes/SKILL.md) | Briefing a cross-family review-and-fix pass, and verifying and landing its fixes. |
| [`maths-laws`](../maths-laws/SKILL.md) | The Maths law suite's tiers, mutation probe and recorded registers. |
| [`gaming-bricks`](../gaming-bricks/SKILL.md) | The Humble and Advanced Post batteries. |
| [`rendering`](../rendering/SKILL.md) | Which canaries, parity stations and captures a render change owes; GPU counters and ceilings. |
| [`puck-world`](../puck-world/SKILL.md) | Running `Puck.World`, stdin scripts, replays and `puck test` worlds. |
| [`documentation`](../documentation/SKILL.md) | Verifying a documentation change. |
