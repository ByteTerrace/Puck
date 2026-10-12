---
name: verification
description: Says how to show a Puck change works before it lands, in pre-release mode - a zero-warning build of what it touched and the change run or measured - and how to read a result. Covers running verbs from a private copy of the head's own CLI, never testing binaries a failed build left behind, which work uses the GPU and how GPU work shares a machine, flake versus failure, and tests that build a scratch repository. Use before calling a change verified or handing back a lane, when running parity, canaries, GPU tests or docs checks, when deciding whether a test is worth writing, when a check fails, and when judging whether one branch contains another. Owners: maths-laws (Maths suite), gaming-bricks (emulator suites), rendering (GPU, captures), puck-world (World runs).
---

# Verification

How to show a change works, which binary to run, and how to read a result.
Puck is pre-release ([AGENTS.md](../../../AGENTS.md#puck-is-pre-release)):
verification is running the change, not defending what landed before it.
[Contributing § Verification](../../../docs/development/contributing.md#verification)
describes the suites, and the [CLI reference](../../../docs/reference/cli.md)
owns each verb's flags. When this file and the code disagree, the code wins.

## The landing bar

1. A zero-warning, zero-error Release build of what the change touched
   (`dotnet build <project> -c Release -nodeReuse:false`, or `Puck.slnx` when it
   reaches shared projects), on the lane's head with the integration branch's
   current tip merged in.
2. The change shown working by running or measuring it: run `Puck.World` or the
   verb, drive a stdin script, take a capture, read a count. The commit body
   says what you ran and what it showed.
3. Tests kept honest: write one only where running cannot show the behavior and
   it will outlive the next rewrite; a test the change breaks is updated or
   deleted in the same change. Run the tests you added or touched. CI requires
   100% line coverage of live `src/` from the CPU suites (`puck artifacts
   coverage` in `build.yml`), so new code lands with a test that reaches it and
   code nothing reaches is deleted. Code only a GPU reaches goes in
   `build/CoverageGpuOnly.txt`, the one exclusion list. To see a suite's
   coverage locally, run it as
   `dotnet <suite>.dll --filter-not-trait Category=Gpu --coverage --coverage-output-format cobertura --coverage-output <dir>/<suite>.cobertura.xml --coverage-settings build/CodeCoverage.config`
   and merge with `puck artifacts coverage --results <dir>`.

A change to simulation state stays deterministic; `puck test --reproduce` shows
two runs agree. Gates, parity, canaries, counters and `puck laws prove` are
tools to use when they pay for themselves, never preconditions.

## Run verbs from your own CLI copy

Build `src/Puck.Cli` in Release in your worktree, copy
`src/Puck.Cli/bin/Release/net10.0/` to a directory only your lane uses
(`<scratchpad>/<lane>-cli/`), and run `dotnet <copy>/Puck.Cli.dll <verb>` from
the worktree root. Never run the globally installed `puck`: it lags the head.
Refresh the copy after a change the CLI reaches. A generator or ledger verb run
without `--check` rewrites the file it owns; `--check` only reports drift.

## Build before you test

- A failed build leaves the previous binaries in place, so a test or run after
  it measures old code. Run only after a build that exited 0 following your
  last edit; never pass `--no-build` otherwise.
- Pass `-nodeReuse:false` to every build and restore.
- Do not edit the source tree while a canary, parity or test run reads it.
- Read a command's output and exit status: an exit code of 0 is not a verdict.

## Writing a test

Prefer type enforcement when the compiler can hold the contract: a required,
non-null parameter checks every call site, where a regex over source misses
some.

In xUnit v3, `Assert.Throws`, `Record.Exception` and their async forms rethrow
the skip exception, so a test that wraps a call which can skip reports
**Skipped** instead of failing; catch it with `try`/`catch` and assert on it.

## What uses the GPU

GPU work is `puck parity`, `puck counters`, a canary requiring `gpu`, a windowed
or offscreen `Puck.World` run, and any test that opens a device. A full
`Puck.World.Tests` run opens the GPU through its device laws (classes marked
`[Trait("Category", "Gpu")]`); run suites beside other GPU work with
`--filter-not-trait Category=Gpu`. `puck docs citations` boots `Puck.World` to
read its help vocabulary, so it is GPU work unless given `--enumeration <file>`;
`puck docs links` only reads files and runs at any time. A World run with
effective `host.presentation: none` uses no GPU.

- One GPU job per GPU at a time; two GPU-bound jobs contend for the device and
  time out. CPU work, builds included, runs beside a GPU job.
- Wait for an idle GPU with `puck host load` (`--watch` prints `GPU busy` and
  `GPU idle`). A process search of your own matches its own command line;
  exclude it before concluding another job is running.
- Stop only the processes you started, by the PIDs you recorded.

## Flake or failure

Re-run a failed leg once, alone, with nothing else running on the GPU.

- **Flake:** the first failure was a timeout, a bind or listener wait, a
  readiness wait or a temporary-directory teardown error, and the leg passes
  alone. Report it as a flake with the original failure message.
- **Failure:** the leg fails again, or the first failure was a wrong value: a
  pixel or tile verdict, a state hash, a counted work line, a refusal reason, a
  content assertion, a validation-layer message. Never re-run a wrong value
  hoping for green. If the change meant to move that value, update the check;
  otherwise fix the code.

Before stopping a hung test host or World process, capture its managed stacks
(`dotnet-stack report -p <pid>`).

## Branches

A two-dot range lists commits reachable from one side and not the other; it
says nothing about ancestry. Check a base with
`git merge-base --is-ancestor <base> <head>` and its exit status.

## Tests that create a repository

A test that creates a scratch git repository disables automatic maintenance in
it (`maintenance.auto=false` and `gc.auto=0`): background maintenance started by
its commands outlives the test and can touch other repositories that share
objects with it.

## Route adjacent work

| Skill | Route there for |
|---|---|
| [`orchestration`](../orchestration/SKILL.md) | Coordinating lanes, landings and machines. |
| [`maths-laws`](../maths-laws/SKILL.md) | The Maths law suite's tiers and registers. |
| [`gaming-bricks`](../gaming-bricks/SKILL.md) | The emulator suites. |
| [`rendering`](../rendering/SKILL.md) | Captures, parity and GPU counters for render work. |
| [`puck-world`](../puck-world/SKILL.md) | Running `Puck.World`, stdin scripts, replays and `puck test` worlds. |
