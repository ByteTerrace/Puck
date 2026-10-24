---
name: verification
description: "Verifies Puck changes: landing bar, builds, stale DLLs, flaky tests, GPU/heavy leases, coverage, source-scan or regex laws versus type/compiler enforcement, and writing tests."
---

# Verification

How to show a change works, which binary to run, and how to read a result.
Puck is pre-release ([AGENTS.md](../../../AGENTS.md#puck-is-pre-release)):
verification is running the change, not defending what landed before it.
[Contributing § Verification](../../../docs/development/contributing.md#verification)
describes the suites, and the [CLI reference](../../../docs/reference/cli.md)
owns each verb's flags. When this file and the code disagree, the code wins.
What the owner says in a session overrides this skill and is written into it in
that session.

## The landing bar

1. A zero-warning, zero-error Release build of what the change touched
   (`dotnet build <project> -c Release -nodeReuse:false`, or `Puck.slnx` when it
   reaches shared projects), on the lane's head with the integration branch's
   current tip merged in. A build or check that passed before the integration
   branch moved says nothing about the merged head: merge the tip, then build
   and run again.
2. The change shown working by running or measuring it: run `Puck.World` or the
   verb, drive a stdin script, take a capture, read a count. The commit body
   says what you ran and what it showed.
3. Tests kept honest: write one only where running cannot show the behavior and
   it will outlive the next rewrite; a test the change breaks is updated or
   deleted in the same change. Run the tests you added or touched.

Gates, parity, canaries, counters, baselines, `puck laws prove` and review
passes are tools to use when they pay for themselves, never preconditions. A
change to simulation state stays deterministic; `puck test --reproduce` shows
two runs agree.

### Coverage

CI checks 100% line coverage of live `src/` from the CPU suites on every push
to `main` (`puck artifacts coverage`, the `coverage` job in `azure.yml`, outside
what deploy waits for). New code lands with a test that reaches it, and code
nothing reaches is deleted, not tested. Code only real hardware reaches (a GPU,
or a session's real mouse and foreground window) goes, as a thin seam of
hardware calls whose decisions are tested apart, in
`build/CoverageHardwareOnly.txt`, the one exclusion list. To see a suite's coverage
locally, run it as
`dotnet <suite>.dll --filter-not-trait Category=Gpu --coverage --coverage-output-format cobertura --coverage-output <dir>/<suite>.cobertura.xml --coverage-settings build/CodeCoverage.config`
and merge with `puck artifacts coverage --results <dir>`. A line counts where a
sequence point starts: a branch body, a lambda body on a line of its own and a
switch-expression arm are lines of their own, and a method ending in a
null-conditional call (`x?.M();`) or returning from inside a `foreach` can
leave its closing brace unhit. Code that keeps its device work in a listed file
reaches it from the CPU side through one expression (a conditional or `&&` over
a helper there), not a guarded statement.

GPU legs require the lead's grant.

An empty shared lease directory can report an unnamed holder indefinitely:
`CliLease.IsStale` cannot reclaim missing metadata. Diagnose it under the
existing exclusive `.guard` file, never by the absence of a World alone.
If that guard is available and the directory is empty while it is held,
removing only the empty directory lets the normal `puck lease` acquire again.
Never unlink the guard file or remove a directory containing holder metadata.

Judge performance by code, disassembly and load-independent counts; only
  counts gate a change. Wall-clock and GPU timing are evidence
  read beside those counts, never alone, and run on a quiet machine: the lead
  keeps every agent's builds, tests and GPU legs off the machine while one
  runs ([the rule](../../../docs/decisions/rendering.md#the-pipeline-foundation)).


## Run verbs from your own CLI copy

Build `src/Puck.Cli` in Release in your worktree, copy
`src/Puck.Cli/bin/Release/net11.0/` to a directory only your lane uses
(`<worktree>/.claude/scratch/<lane>-cli/`), and run `dotnet <copy>/Puck.Cli.dll <verb>` from
the worktree root. Never run the globally installed `puck` on PATH: it is
another build of another head. Refresh the copy after a change the CLI reaches.
A generator verb (`schema`, `registry`, `vocabulary`, `locks`, `baselines`,
`branding`) run without `--check` rewrites the files it owns; `--check` only
reports drift. Restore a fresh worktree once
(`dotnet restore Puck.slnx -nodeReuse:false`) before its first build with
`--no-restore` or `puck references`: both fail on an unrestored tree.

## Boot every World with `--devices none`

A World you boot to verify a change passes `--devices none`: the stdin recipe,
a scratch `.puck` world, and a World you drive over MCP, which you start with
the `puck` server's `puck_world_start` (`checkout`: the worktree's absolute
path; it boots the build of your current sources, building it first when no run
has) instead of a hand-written boot line. That tool passes `--devices none`, its own state directory and
`--control`, holds the GPU lease for a windowed World until it exits, and
returns the `processId` to name on every call; `host.log` reads the World's
stderr lines, and `puck_world_stop` ends it and releases the lease. Without
`--devices none` the World opens the owner's pads and reads the owner's
keyboard and mouse through its window, so the owner's presses drive your run. The ruling, what the
flag does and the verbs that pass it themselves are in
[the `puck-world` skill](../puck-world/SKILL.md#an-agents-world-opens-no-physical-device).
Drive a verification World with scripted input only (`player.signal`, `body.*`,
schedule rows); a stderr naming a `[gamepad]` or `[gameinput]` device means the
flag was missed.

## Build before you test

- A failed build leaves the previous binaries in place, so a test or run after
  it measures old code. Run only after a build that exited 0 following your
  last edit; never pass `--no-build` otherwise.
- The shared Claude and Codex environment sets `MSBUILDDISABLENODEREUSE=1`.
  Keep the hook in all three tools until a probe establishes effective inheritance;
  Antigravity has no project env block. Explicit command overrides still matter.
  Pass `-nodeReuse:false` (or `-nr:false`) to build, msbuild, restore, pack and publish: a reused MSBuild node
  outlives the build and holds memory (`puck host load` counts them as
  `reuseNodes`). Never pass it to `dotnet test`: Microsoft.Testing.Platform
  has rejected it with `error: 1, total: 0` in the observed run; exact text
  and exit status vary by version. Build with the flag,
  then run `dotnet test <project> --no-build`.
- Do not edit the source tree while a canary, parity or test run reads it.
- Read a command's output and exit status: an exit code of 0 is not a verdict.
- MSBuild rebuilds by file time. A file copied back over a mutant keeps its
  older time, so the build keeps the mutated assembly although `git diff` shows
  the source restored. Restore with `git restore` or touch the file, rebuild,
  and confirm the test host loaded the freshly built assembly, using its
  write time/path or a diagnostic in the output; `git diff` and exit 0 alone
  prove neither. `puck laws prove
  <law> --fix <revision>` proves a law against its fix in its own clone and
  never touches your tree.

## Writing a test

Prefer type enforcement when the compiler can hold the contract: a required,
non-null parameter checks every call site, where a law that scans source with a
regular expression misses some. When a scan law misses a real site, replace it
with the type and delete the scan; a law that cannot fail at a real site pins
nothing.

In xUnit v3, `Assert.Throws`, `Record.Exception` and their async forms rethrow
the skip exception, so a test that wraps a call which can skip reports
**Skipped** instead of failing; catch it with `try`/`catch` and assert on it.

## What uses the GPU

Use the private CLI copy's `puck lease gpu --holder <lane> --wait 1200 -- <command...>` for a granted GPU leg, and `puck lease heavy --holder <lane> --wait 1200 -- <command...>` for a heavy suite. The verb owns acquisition, stale recovery and release, including failure and cancellation. A windowed World started with `puck_world_start` takes the same GPU lease itself (`holder`: your lane), waits at most its `timeoutSeconds` for it, and releases it when `puck_world_stop` ends the World or the World exits.
Keep a granted batch within its brief's time box (at most 20 minutes for a GPU lease).
`gate` and `affected` hold the same heavy lease through each heavy suite and
check host headroom after acquisition. When an ancestor already owns it,
validated PID/start-time ancestry admits a serialized child lease, so a run
does not wait on itself and siblings still exclude one another; [lease reference](../../../docs/reference/cli.md#puck-leaseexclusive-machine-work).

In PowerShell, quote forwarded `'-nodeReuse:false'` after `--`; otherwise the
shell can split the colon-bearing switch before the lease receives it
([toolchain caution](../../../docs/development/contributing.md#hardware-and-toolchain-cautions)).


GPU work is `puck parity`, `puck counters`, a canary requiring `gpu`, a windowed
or offscreen `Puck.World` run, and any test run that can select a device law.
Device laws are test classes marked `[Trait("Category", "Gpu")]`, in
`Puck.World.Tests`, `Puck.DirectX.Tests`, `Puck.Vulkan.Tests` and
`Puck.Platform.Windows.Tests`; a full run of one of those suites is GPU work
even though most of it is CPU. Beside another GPU job, run suites with
`--filter-not-trait Category=Gpu`. `puck docs citations` boots `Puck.World`
(headless, then windowed) to read its help vocabulary, so it is GPU work unless
given `--enumeration <file>`; `puck docs links` only reads files and runs at any
time. A World run with effective `host.presentation: none` uses no GPU.

- One GPU job per GPU at a time; two GPU-bound jobs contend for the device and
  time out. CPU work, builds included, runs beside a GPU job.
- Ask the machine with `puck host load`: it names the process holding the GPU
  (`GPU busy (Puck.World 4242)`) or prints `GPU idle`, and `--watch` prints each
  transition once. It never counts itself. A process search of your own
  (`Get-Process`, `pgrep`, `tasklist | findstr`) matches its own command line:
  exclude its PID before concluding another job is running, and never skip or
  postpone a granted leg on that match alone.
- Stop only the processes you started, by the PIDs you recorded.
- A sandboxed process inventory can omit machine-wide jobs, including a lease
  holder visible outside that sandbox. An empty `Get-Process` result there
  does not prove a holder exited; inspect it with machine-wide visibility
  before diagnosing stale metadata. Let `puck lease` own recovery.

## Flake or failure

Re-run a failed leg once, alone, with nothing else running on the GPU and no
heavy build beside it.

- **Flake:** the first failure was a timeout, a bind or listener wait, a
  readiness wait or a temporary-directory teardown error, and the leg passes
  alone. Report it as a flake with the original failure message.
- **Failure:** the leg fails again, or the first failure was a wrong value: a
  pixel or tile verdict, a state hash, a counted work line, a refusal reason, a
  content assertion, a validation-layer message. Never re-run a wrong value
  hoping for green, whatever ran beside it. If the change meant to move that
  value, update the check; otherwise fix the code.

Before stopping a hung test host or World process, capture its managed stacks
with an existing `dotnet-stack report -p <pid>`. The optional installation is
owner-only [machine setup](../../../docs/development/contributing.md#setting-up-a-machine).

## Checking for a leak

A leak shows as memory that climbs across repetitions of one action, read after
a full collection; a long session is not needed and proves less, since its
working set mixes warm-up, caches and garbage. The owner's method:

1. Boot one World (windowed for a GPU leak, under `puck lease gpu`, stopped
   within 10 minutes by its PID) and drive it from a stdin script, so every
   repetition is the same and no round trip spaces the cycles.
2. Warm up (`world.wait bakes <s>`, then some ticks), then `host.memory collect`
   as the baseline: a full blocking collection that compacts the large object
   heap, then process working set, private bytes and the managed heap by
   generation.
3. Repeat the cycle 10 to 20 times and run `host.memory collect` every few
   cycles.
4. Compare checkpoints. The managed heap after collection must stay flat. Read
   native growth as private bytes less the GC's committed bytes, so a native
   leak cannot hide behind a falling managed heap; a page-level probe
   (`VirtualQueryEx` over the process, split by private, write-combined and
   mapped pages) separates upload heaps from native heap.
5. A one-off rise over the first cycles that then holds is warm-up, not a leak.
   A rise every cycle is a leak: bisect the cycle by dropping actions from the
   script and repeating, then fix the action's cause. The managed heap read
   between collections shows each action's garbage, which bisects churn the
   same way.
6. Private bytes lag the GC: after a collection the GC reports its freed
   regions released, but the process holds them for a while, so a single
   private reading can sit several GB above the floor. Compare the floor
   across samples, never one reading.

## Branches

`git log A..B` lists commits reachable from B and not from A. It says nothing
about where B branched or whether A is an ancestor of B: a lane cut from an
older tip and one cut from the current tip can list the same commits. Check a
base with `git merge-base --is-ancestor <base> <head>` and its exit status, and
count how far behind it is with `git rev-list --count <base>..<integration tip>`.

## Tests that create a repository

A test that creates a scratch git repository disables automatic maintenance in
it (`git config maintenance.auto false` and `git config gc.auto 0`). Commands
in the scratch repository start background `git maintenance` or `gc --auto`
that outlives the test, and through alternates or a shared object store it can
prune or repack objects another worktree on the machine still reads.

## Route adjacent work

| Skill | Route there for |
|---|---|
| [`orchestration`](../orchestration/SKILL.md) | Coordinating lanes, landings and machines. |
| [`review-passes`](../review-passes/SKILL.md) | An optional review-and-fix pass and verifying its fixes. |
| [`maths-laws`](../maths-laws/SKILL.md) | The Maths law suite's tiers and structural completeness. |
| [`gaming-bricks`](../gaming-bricks/SKILL.md) | The emulator suites. |
| [`rendering`](../rendering/SKILL.md) | Captures, parity and GPU counters for render work. |
| [`puck-world`](../puck-world/SKILL.md) | Running `Puck.World`, stdin scripts, replays and `puck test` worlds. |
