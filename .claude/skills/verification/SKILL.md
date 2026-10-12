---
name: verification
description: Says how to show a Puck change works before it lands, in pre-release mode - a zero-warning build of what it touched and the change run or measured - and how to use the optional tools (`puck gate`, `puck affected`, parity, canaries, `puck counters`, `puck laws prove`, ledger `--check` forms) when they pay for themselves. Covers running verbs from a private copy of the head's own CLI, never testing binaries a failed build left behind, GPU legs one at a time per GPU under a grant, flake versus failure, and tests that build a scratch repository. Use before calling a change verified or handing back a lane, when running parity, canaries, GPU tests, docs checks or a GPU-idle process check, when deciding whether a test is worth writing, when a check fails, and when judging whether one branch contains another. Owners: maths-laws (Maths suite), gaming-bricks (emulator suites), rendering (GPU, captures), puck-world (World runs), review-passes (optional review passes).
---

# Verification

This skill is the agent procedure for showing a change works: what a lane
lands on, which binary to run, and how to read a result. Puck is pre-release
([AGENTS.md](../../../AGENTS.md#puck-is-pre-release)): most of what lands now
will be rebuilt, so verification is running the change, not defending what
landed before it. [Contributing § Verification](../../../docs/development/contributing.md#verification)
describes the suites and canaries, and the [CLI reference](../../../docs/reference/cli.md)
owns each verb's flags. When this skill and the code disagree, the code wins and
this file is corrected in the same change.

## The landing bar

A lane lands on:

1. A zero-warning, zero-error Release build of what it touched
   (`dotnet build <project> -c Release -nodeReuse:false`, or `Puck.slnx` when
   the change reaches shared projects).
2. The change shown working by running or measuring it: run `Puck.World` or the
   verb, drive a stdin script, take a capture, read a count. Say in the commit
   body what you ran and what it showed.
3. Tests kept honest. Write a test only where running cannot show the behavior
   and the test will outlive the next rewrite. A test the change breaks is
   updated or deleted in the same change, never kept as a constraint. Run the
   tests you added or touched.

A change to simulation state stays deterministic (`AGENTS.md` rule 4);
`puck test --reproduce` on a world the change reaches is the quick way to see
two runs agree.

Nothing else is a precondition. A red gate or CI run is information to act on,
not a stop. While the debug world is the one live world, the canary, parity,
determinism and qualification fixtures and most counters workloads sit in
`experimental/` ([quarantined gates](../../../docs/plans/open-items.md#quarantined-gates)).

## Optional checks

Use these when they pay for themselves, for example when the owner asks for a
qualification or a change reaches more than running it can show. Run each from
your CLI copy (below).

- **`puck gate --merge-base origin/<integration-branch>`** runs the affected
  suites, `test` worlds, catalog check, committed baseline checks and ledger
  check forms against the merge base. `--gpu` adds the affected canaries,
  parity, device suites, counters workloads and docs citations, serially, and
  is a GPU leg.
- **`puck affected --merge-base origin/<integration-branch>`** prints the
  selection the change reaches; `--run` runs it and `--gpu` adds its canaries
  and parity. `--since <branch>` folds in landings after the lane started.
- **`puck parity`**, **`puck canary <names>`** and **`puck counters --check`**
  compare backends, launch the real World against a manifest, and judge counted
  GPU work against recorded ceilings. All are GPU legs.
- **`puck laws prove`** runs a law with its fix withheld (`--fix <commit>` or
  `--file-list`) in a proof tree of its own and with it restored, and reports
  both legs. Use it when you want to know a new test can fail.

A verb run without `--check` rewrites the file it owns; the check form only
reports drift. When a change moves one of these files, rerun the recording form
and commit the result with the change:

| Owns | Check form |
|---|---|
| `FormatVersions.json` | `puck formats --check` |
| `CanaryCeilings.json` | `puck canary-ceilings --check` |
| Formatting of touched files | `puck format --check --file-list <scratchpad>/<lane>-files.json` |
| The project-map layering block | `puck architecture --check` |
| `docs/world-name-registry.md` | `puck registry --check` |
| Generated schemas and model shape | `puck schema --check` |
| A committed test baseline | `puck baselines <artifact> --check` |
| Each project's `packages.lock.json` | `puck locks --check` |

A recording verb that exits nonzero has not recorded, whatever file it wrote:
`puck counters --record` exits 1 and writes nothing when the backends disagree.

## Run verbs from your own CLI copy

- Build `src/Puck.Cli` in Release in your worktree, then copy
  `src/Puck.Cli/bin/Release/net10.0/` into a directory only your lane uses,
  named for the lane (`<scratchpad>/<lane>-cli/`). Run every verb as
  `dotnet <copy>/Puck.Cli.dll <verb>` from your worktree root.
- Never run the globally installed `puck` tool: it lags the head and silently
  skips manifest members it does not know. Never run from a copy another agent
  can overwrite, or from the `bin` directory while a build may rewrite it.
- Refresh the copy after a change under `src/Puck.Cli` or a project it
  references.

## Build before you test

- A failed build leaves the previous binaries in place, so a test run after it
  measures old code. Run tests only after a build that exited 0 following your
  last edit. Never pass `--no-build`, or run a test assembly directly, unless
  that build succeeded.
- Send build and test output to a lane-named log
  (`<scratchpad>/<lane>-build.log`) and stop at the first build error.
- Build and test in Release (`-c Release`). The allocation laws' ceilings are
  calibrated against Release code, so a Debug run's allocation failure is not a
  finding.
- Pass `-nodeReuse:false` to every build, and to `dotnet restore` as well:
  restore otherwise leaves MSBuild reuse nodes that hold memory after it exits.
- A build interrupted under memory pressure can leave a corrupt `ref` assembly
  under `obj/`, and every dependent compile then fails with `CS0009` pointing at
  it. Clean only that reference directory and rebuild once.
- Do not edit the source tree while a canary, parity or test run is going:
  canaries rebuild from source, and tests read baselines, schemas and generated
  tables from it.
- Read a command's output and exit status: an exit code of 0 is not a verdict.
  After moving a tool or document, run its real consumer at the new location.

## Writing a test

Prefer type enforcement when the compiler can hold the contract: a required,
non-null parameter checks every call site, where a regex over source misses
target-typed `new(...)`.

In xUnit v3, `Assert.Throws`, `Assert.ThrowsAny`, `Assert.ThrowsAsync`,
`Record.Exception` and `Record.ExceptionAsync` all rethrow the skip exception.
A test that wraps a call which can skip, such as a device or capability probe,
reports **Skipped** instead of failing; catch the exception with a
`try`/`catch` and assert on it.

## GPU legs

GPU work is `puck parity`, `puck counters`, any canary requiring `gpu`
(including `--merge`), a windowed or offscreen `Puck.World` run, any verb that
boots one in those modes, and any test that opens a device, including a full
`Puck.World.Tests` run. A suite runs its own `Gpu` classes one law at a time,
after its CPU laws (`tests/Shared/GpuDeviceCollection.cs`), so one run of a
suite is one GPU leg. `puck docs citations` boots `Puck.World` to read its help
vocabulary, so it is a GPU leg unless given `--enumeration <file>`.
`puck docs links` only reads files and runs at any time. A World run with
effective `host.presentation: none` uses no GPU; the `puck-world` skill owns
the presentation modes.

- A GPU runs one GPU leg at a time. Legs compete for the device, the ports and
  the frame budget, and a contended leg times out. One `puck canary` run (or
  `puck affected --gpu`, `puck gate --gpu`) is one GPU leg: it schedules its
  own canary legs side by side up to its `--gpu-jobs` bound.
- While another GPU leg runs, run test suites with
  `--filter-not-trait Category=Gpu`. Every class that opens a hardware GPU
  device carries `[Trait("Category", "Gpu")]`, and the build refuses a class
  that reaches a way onto the GPU marked `[OpensGpuDevice]` without it (GPU001).
- CPU work runs beside a GPU leg: a correctness verdict does not depend on
  load. Two GPU-bound jobs never overlap. Only timing work (`puck bench`,
  `world.gpu-timing`, `world.fps`, a timed build or test pass) needs a quiet
  machine, and a timing is reported beside the counts of the same run.
- In delegated work, run GPU legs only under a grant the lead issues in your
  brief. Without one, list the legs you would run in your hand-back report.
- `puck counters --check` fails on a device the ledger has no record for with
  `no ceilings recorded for <device>`; `puck counters --record` adds it, and
  `--report <file>` judges a saved report with no GPU
  ([rules](../../../docs/reference/cli.md#counted-cost-ceilings)).

### GPU process checks and script runs

Wait for an idle GPU with `puck host load` (`--watch` prints `GPU busy (<holder>)`
and `GPU idle` transitions); its probe already excludes shells and builds. A
detector of your own that matches command lines must exclude its own shell,
since the query's command line contains the strings it searches for.

On Windows, stopping a background task can kill a wrapper shell and leave the
script's own bash running. Before relaunching a GPU script, find every instance
by command line, stop each by process id, and confirm none remain. Bash reads
a script as it runs, so never edit its file while an instance is running. Copy
the script under a new name per run.

### Flake or failure

Re-run a failed leg once, alone.

- **Flake:** the first failure was a timeout, a bind, listener or port wait, a
  readiness wait, or a temporary-directory teardown error, and the leg passes
  alone. Report it with the original message.
- **Failure:** the leg fails again, or the first failure was a wrong value: a
  pixel or tile verdict, a state hash, a counted work line, a refusal reason, a
  content assertion, or a validation-layer message. Never re-run a content
  failure hoping for green. If the change meant to move that value, update the
  check; otherwise fix the code.

Before stopping a hung test host or World process, capture its managed stacks
(`dotnet-stack report -p <pid>`) and keep the report with the failure.

## Tests that create a repository

A test that creates a scratch git repository under the temporary directory
disables automatic maintenance in it (`maintenance.auto=false` and `gc.auto=0`
in its configuration). Background maintenance started by a scratch repository's
own commands outlives the test and can touch other repositories on the machine.

## Route adjacent work

| Skill | Route there for |
|---|---|
| [`orchestration`](../orchestration/SKILL.md) | Coordinating lanes, landings, machines and GPU grants. |
| [`review-passes`](../review-passes/SKILL.md) | An optional review-and-fix pass, and landing its fixes. |
| [`maths-laws`](../maths-laws/SKILL.md) | The Maths law suite's tiers, mutation probe and recorded registers. |
| [`gaming-bricks`](../gaming-bricks/SKILL.md) | The emulator suites. |
| [`rendering`](../rendering/SKILL.md) | Captures, canaries, parity stations and GPU counters for render work. |
| [`puck-world`](../puck-world/SKILL.md) | Running `Puck.World`, stdin scripts, replays and `puck test` worlds. |
| [`documentation`](../documentation/SKILL.md) | Verifying a documentation change. |
