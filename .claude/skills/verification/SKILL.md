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

During the refactoring campaign the owner has lifted every test requirement
([AGENTS.md](../../../AGENTS.md#puck-is-pre-release)): the landing bar below
reduces to building what a change touched and running what it changed. Suites,
coverage, red legs and focused tests are optional tools, and a red one holds
nothing back.

For settled mesh comparisons, `world.wait bakes` finishes CPU bake/contour work;
`world.wait ready` finishes renderer startup. Neither proves GPU mesh landing.
After route warmup has produced its actual program, pause authority and use
`world.wait residency 20` before the measured interval (the deadline in seconds is required). Pin counter endpoints to command drains at the first measured tick and
lastTick+1, with opening reads before authored commands; direct paused reads can
include extra presentation work before resume. The wait finishes the required geometry
upload's fence, without converging lighting or draining changing pose uploads.
Arm the presentation observer only for measured ticks so paused settling frames
do not create duplicate samples. Canopy coverage also needs actual position and
image inspection; a matching replay can faithfully walk outside its intended load.
A sweep warms and measures frame signals with ordinary display-paced presentation;
its capture tour enables lockstep separately. Do not enable still-capture lighting
convergence before a normal frame window. It can relight the complete probe set
every frame and exhaust the call before any signal is read.

For per-tick headless counter comparisons, check the counter's scope first.
`FixedStaticContactWork.Process` sums every solver in the process, whereas the
registered rule counters belong to the boot server. `world.rate pause` pauses
only the boot instance; neighboring worlds keep ticking. List them with
`world.instance.status` and pause each through `world.rate pause instance:<name>`
before the measured interval when comparing only the boot world's work. Use
`world.at` for identical counter endpoints in both builds and compare deltas,
not totals that include neighboring worlds' earlier work.
## The landing bar

A queued World build can outlive a cancelled start. Do not change a checkout's
sources until its build has finished. The artifact resolver checks the source
key after build admission and after compilation, and discards a build if either
check changed; a capture run must still confirm its implementation through live
counters before accepting measurements.

Retain each prepared `WorldArtifact` lease until its last comparison leg exits.
Stopping a preparation World releases that lease, and other lanes' normal cache
retention can evict the build while a GPU batch queues. Keep builds outside the
GPU hold, and give capture tours a separate bounded leg when frame-budget
sampling and timing would consume their deadline. Preflight console recipes
after integration: memory compaction is `host.memory collect`; renderer-only
probe help is absent in a headless World.

Steps 1 to 3 make a lane ready for a batch. Steps 4 and 5 need a windowed World,
so the integration lane's sweep reads them
([orchestration](../orchestration/SKILL.md#land-in-batches)).

1. A zero-warning, zero-error Release build of what the change touched
   (`dotnet build <project> -c Release -nodeReuse:false`; a coding lane never
   builds `Puck.slnx`, and the integration lane builds it when the merge reaches
   shared projects), on the lane's head with the integration branch's
   current tip merged in. A build or check that passed before the integration
   branch moved says nothing about the merged head: merge the tip, then build
   and run again.
2. The change shown working by running or measuring it: run `Puck.World` or the
   verb, drive a stdin script, take a capture, read a count. The commit body
   says what you ran and what it showed.
3. Tests kept honest: write one only where running cannot show the behavior and
   it will outlive the next rewrite; a test the change breaks is updated or
   deleted in the same change. Run the tests you added or touched.
4. Frame time held, for a change that reaches the running World (simulation,
   presentation, physics, rendering, addons, the debug world's documents): the
   bar devices hold the frame bar at the highest settings (AGENTS.md), and that
   is the owner's floor, not a goal. The integration lane's sweep reads it under
   a granted GPU leg: in a windowed World with its bakes settled, `host.frames`
   after standing still and again after the batch's workload (walk the area it
   touches, `player.move` in a `world.at` script), and the sweep's report states
   both readings (`puck sweep --tour` reads them as `frames-standing` and
   `frames-walking`; a batch's own walk is a signal added to its plan). A coding lane has no GPU leg, so its commit body names that
   workload instead. A frame over 16.7 ms that a change caused turns the batch
   red and the culprit is bisected out, whatever else passed. A reported speed-up
   is measured in frame time, never inferred from one phase: a tick that fell
   from 247 ms to 61 ms is still four frames late. A reading at 7 to 8 FPS
   whatever the load, with the GPU idle and `wait` or `begin` near 100 ms, is
   the compositor's fallback for a powered-off display, not the frame
   ([the hazard](../../../docs/development/contributing.md#hardware-and-toolchain-cautions));
   `world.gpu-timing timeline` shows where the queue's work and its idle gaps
   fall.
5. The player's body seen, for a change that reaches what draws bodies (stamps,
   clips, fades, cameras, portals): the sweep patrols the area and reads the
   result (`world.patrol <x0> <z0> <x1> <z1> <step>` in a windowed World with its
   bakes settled, then `world.wait until <tick>` and `world.patrol`): it poses
   body 0 at each point of the grid, picks the centre of the third-person view
   (`world.view.pick primary 0.5 0.5` names `body=0`), and flags `OFF BODY` and
   `STALL` points. `[world.straddle: ...]` in the log names every body drawn
   clipped at a door. A body that vanishes away from any portal turns the batch
   red. The coding lane names the area to patrol.

Gates, parity, canaries, counters, baselines and `puck laws prove` are tools to
use when they pay for themselves, never preconditions. Review follows
[orchestration](../orchestration/SKILL.md#spend-verification-not-writing):
area fix passes by default, an adversarial review only for a review-only
silent-invariant class, and neither blocks a landing. A
change to simulation state stays deterministic; `puck test --reproduce` shows
recorded input reproduces every authority from its checkpoint in the same worker,
including per-tick authoritative hashes, final machine cores and canonical exports. A differing trace names
its first sampled tick and instance. The authoritative hash folds arena rows from digests cached by
`RowVersion`, so `--reproduce` also checks every replayed tick, and the first `--workers` count every
recorded tick, against a full recompute, and every authority hash samples one row and one segment of the cache
(`[world.state.hash: stale digest ...]`, which fails a probed leg): `differs from its full recompute in Arena`
or a stale digest means a write changed a row without moving its version, or a refold left its segment
stale; move the version at that write rather than widening the recompute. Body continuations encode every
fold; nothing caches them. Use the test preparation/work counts beside
wall time when measuring it on a shared machine; build-lease waits are contention,
and exported authority ticks exclude the extra shutdown ticks.

`puck test` keeps a worker process alive across its scenarios and boots every
scenario in a fresh native host with its own state directory: no scenario
inherits an arena, rule latch, session allocator or input producer from the one
before it, and no document comparison decides what to share. The process's first
boot pays the one-time start-up that later boots skip, which is what the warm
worker saves. Never rewrite singleton lifetimes to reuse a provider: that
changes captive-dependency validation, caller-owned instance disposal and
child-scope singleton identity. Arbitrary factories and extensions require the
native container. `WorldTestHostLawTests` preserves the three native-container
witnesses. `TestCommandLawTests.EveryScenarioStartsPristineInItsOwnHost` checks
one host boot per scenario, replay, and a worker's export and manifest against a
fresh-process boot.

`--reproduce` restores each probe's starting checkpoint into the same authority
(`WorldPersistence.RestoreReplayStart`). Drive grants and presentation clock
seeds are rebound before the restore delivers to attached sinks, and the restore
rebuilds geometry and population when the run edited their configuration.
Checkpoint restoration discards replay-only departure rollback bodies: a
committed departure otherwise keeps its source-scoped transfer id and falsely
refuses the replay as a duplicate. Session epochs reset only in this replay-start
path; a live load preserves retired epochs.
The default bounds concurrent workers at two (one on fewer than four logical
CPUs); a lane beside heavy work still passes `--jobs 1`.
Workers share the per-user device caches. `puck test --cache-dir <scratch-root>`
redirects only those worker caches (reuse that root across a lane's paired runs):
the `puck test` process itself still persists compiles to the per-user
`compilations` directory (`WorldsRoot.Start`), so the compile, compiled-world and
mesh caches stay per-user until [that open item](../../../docs/plans/open-items.md#refactoring-campaign) lands.
An ordinary scheduled boot has no replay probe or `reproduction.json`; a fresh-process
hash comparison runs `puck test --reproduce` in a separate worker so its proof actually
compares the recorded ticks.

Each leg's `timing.json` separates boot, arming, authored time, export and replay;
export is included in authored time. `firstTickMilliseconds` measures the authored
phase through its first completed tick, including host-service startup on a fresh
boot. Read it beside the tick count before calling a short scenario's cost fixed.

When profiling a sweep, sample the coordinator and the simultaneous sum of its
descendants' working sets; report private bytes separately. Do not add individual
process peaks that occurred at different times. Record the full run's world count,
authority ticks, host boots and wall time, and compare all exports/manifests when
changing only the runner. Preparation can dominate coordinator memory: staged
roots used once must not retain a composed image and a scalar-origin snapshot.
Generated test compiles share persisted entries but use a source-local memory cache,
so the process-wide cache cannot retain every staged test's bytes.
Shared imports keep their origins for correct path relocation. Logs stream to
disk, and verdict reading holds only the current world's export. Replay captures
external inputs; it checks deterministic continuation from those inputs, rather
than sampling external sources twice. Keep nondeterministic negative controls in
scratch worlds, never in engine simulation logic.

### Coverage

While the refactoring campaign suspends test requirements (AGENTS.md), the
coverage target and the CI `coverage` job are suspended too, and no change is
held back for a missing test. The standing target, when the owner restores it:
100% line coverage of live `src/` from the CPU suites on every push to `main`
(`puck artifacts coverage`, the `coverage` job in `azure.yml`, outside what
deploy waits for), with new code reached by a test, and dead internals,
duplicates and ledgers that nothing reaches deleted, not tested.
"Unreached" is not "unwanted" (the owner's ruling,
[AGENTS.md](../../../AGENTS.md#puck-is-pre-release)): a product capability
(capture, input devices, audio or speech, probes, a world or exhibit) that
nothing calls yet is wired or covered, and the owner is asked before one is
deleted. Code only real hardware reaches (a GPU,
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

A GPU lease does not reserve CPU or memory. Parallel headless replay workers
can starve startup and decoding while no other windowed World owns the GPU.
Keep such a functional boot separate from a performance comparison: require
readiness, settled bakes and completed timestamp windows, and record concurrent
work when a quiet measurement is unavailable.

The exclusive `.guard` handle proves lease ownership; holder metadata is
diagnostic. A Windows reader can delay directory deletion after a World exits,
leaving an alive adapter PID in that metadata. `CliLease` always releases the
guard, tolerates delayed cleanup, and replaces leftover metadata once it owns
the guard. Its metadata readers permit deletion, and an unreadable queue ticket
counts as occupied until it can be read or disappears. Let the verb recover;
never unlink the guard or infer ownership from the absence of a World alone.

A scratch repository for CLI experiments needs its own `Puck.slnx` (and a
`git init`), or the verb silently uses the shared `.claude/leases` and joins
the live queue.

Judge a performance change by code, disassembly and load-independent counts;
only counts gate it. Wall-clock and GPU timing are evidence read beside those
counts, never alone, and run on a quiet machine: the lead keeps every agent's
builds, tests and GPU legs off the machine while one runs
([the rule](../../../docs/decisions/rendering.md#the-pipeline-foundation)). The
owner's frame-time floor (landing bar step 4) is the one wall-clock reading that
gates, and it is read in the integration lane's sweep on that quiet machine.

## Run verbs from your own CLI copy

Build `src/Puck.Cli` in Release in your worktree, copy
`src/Puck.Cli/bin/Release/net11.0/` to a directory only your lane uses
(`<worktree>/.claude/scratch/<lane>-cli/`), and run `dotnet <copy>/Puck.Cli.dll <verb>` from
the worktree root. Never run the globally installed `puck` on PATH: it is
another build of another head. Refresh the copy after a change the CLI reaches
and after every merge of the integration tip: a copy older than the merge
rejects documents that use new schema members.
A generator verb (`schema`, `registry`, `vocabulary`, `locks`, `baselines`,
`branding`) run without `--check` rewrites the files it owns; `--check` only
reports drift. Restore a fresh worktree once
(`dotnet restore Puck.slnx -nodeReuse:false`) before its first build with
`--no-restore` or `puck references`: both fail on an unrestored tree.
On Windows, refresh the private CLI only after any lease command hosted by that copy has exited: the lease
process itself keeps its loaded DLLs locked while the child build runs. Copy after lease completion, or host
the build lease from another CLI copy.
Copy portable output into a fresh destination, or copy its contents when
refreshing an existing one. PowerShell `Copy-Item <output-directory>
-Destination <existing-directory> -Recurse` nests the output directory and
leaves stale DLLs at the destination root. Check the entry DLL before launch;
publish any queued runner's artifact pointer only after the copy finishes.

When `puck schema` reports missing XML documentation, do not accept the resulting
removal of schema descriptions: on the restricted Windows harness the generator's
XML reader can fail to load files that are present and parseable. Check the files
beside the CLI and rerun under normal file access before checking in output.

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

`puck_world_start` builds into the [World build store](../../../docs/reference/cli.md#where-the-world-artifact-is-built),
not the checkout's `bin`, but compiles incrementally through the checkout's `obj` directories.
Do not overlap that start with another build of projects sharing its dependencies: the compiler can
refuse a locked intermediate DLL (CS2012). Wait for `started` or `refused` before building again;
once started, the World runs from the store and no longer locks the checkout.

## Build before you test

A `git archive <commit> | tar -x` tree (a bisect's or an old commit's) has no
`.git`. The World build key reads such a tree's files from its own disk
(`CheckoutContent`), never through an enclosing repository, so `puck test` run
from the tree builds and reuses that tree's own World. `puck bench world` needs
no build, since it runs the tree's own build in process.

When a brief isolates build outputs with `--artifacts-path`, give semantic
formatting that same closure: set `ArtifactsPath` to its absolute directory and
`UseArtifactsOutput=true` in the environment of `puck format`. Otherwise its
MSBuild query can skip built projects as having an untrustworthy Release closure
because it looks in the default `bin` and `obj` directories.

- A failed build leaves the previous binaries in place, so a test or run after
  it measures old code. Run only after a build that exited 0 following your
  last edit; never pass `--no-build` otherwise.
- The shared Claude and Codex environment sets `MSBUILDDISABLENODEREUSE=1`.
  Keep the hook in all three tools until a probe establishes effective inheritance;
  Antigravity has no project env block. Explicit command overrides still matter.
  Pass `-nodeReuse:false` (or `-nr:false`) to build, msbuild, restore, pack and publish: a reused MSBuild node
  outlives the build and holds memory (`puck host load` counts them as
  `reuseNodes`). Never pass it to `dotnet test`: Microsoft.Testing.Platform
  rejects it (the exact text and exit status vary by version). Build with the flag,
  then run `dotnet test <project> --no-build`.
  `dotnet run` forwards `-nodeReuse:false` to the application, which can reject it.
  Build separately with that flag, then use `dotnet run --no-build`.
- Do not edit the source tree while a canary, parity or test run reads it.
- Read a command's output and exit status: an exit code of 0 is not a verdict.
- MSBuild rebuilds by file time. A file copied back over a mutant keeps its
  older time, so the build keeps the mutated assembly although `git diff` shows
  the source restored. Commit the fix as WIP before injecting the fault, so the
  injected fault is the only uncommitted edit; then restore it with `git restore
  <file>` (or touch the copied-back file), rebuild,
  and confirm the test host loaded the freshly built assembly, using its
  write time/path or a diagnostic in the output; `git diff` and exit 0 alone
  prove neither. `puck laws prove
  <law> --fix <revision>` proves a law against its fix in its own clone and
  never touches your tree.

A Windows project build intended for a Vulkan run must pass
`-p:PuckShaderSpirvEnabled=true` explicitly, including a private World artifact.
A custom test traversal outside `tests/` needs the same flag because it does not
inherit `tests/Directory.Build.rsp`; a Windows project build otherwise removes
SPIR-V outputs. CPU shader tests use a fake device but
still read the compiled bytecode, so missing fixtures cause failures and can leave
asynchronous package tests waiting. Building both bytecode formats uses no GPU.

## Writing a test

Prefer type enforcement when the compiler can hold the contract: a required,
non-null parameter checks every call site, where a law that scans source with a
regular expression misses some. When a scan law misses a real site, replace it
with the type and delete the scan; a law that cannot fail at a real site pins
nothing.

In xUnit v3, `Assert.Throws`, `Xunit.Record.Exception` and their async forms rethrow
the skip exception, so a test that wraps a call which can skip reports
**Skipped** instead of failing; catch it with `try`/`catch` and assert on it.

## The companion custody model check

`CompanionCustodyModelLawTests` (`tests/Puck.World.Protocol.Tests`) is the
executable model of the companion custody protocol
([the specification](../../../docs/plans/companion-redesign.md#8-the-model)). Its
named traces and small scopes run with the ordinary suite; its large scopes
(over about 100,000 states each, seven to seventeen minutes by machine load and a few hundred megabytes in
all) are explicit tests carrying `Category=ModelCheck`:

```text
dotnet test tests/Puck.World.Protocol.Tests/Puck.World.Protocol.Tests.csproj -c Release --no-build --explicit on --filter-trait Category=ModelCheck
```

A change to the protocol (the model, its specification, or the engine code that
implements a transition) is made in the model first, and the model check is the
evidence a protocol invariant holds, so use it for a protocol change that
running the engine cannot demonstrate, or when the brief asks. Nothing makes it
a gate while the campaign suspends test requirements. Without `--explicit on`
the filter selects nothing.

The engine's half of that evidence is `CompanionCustodyRefinementLawTests`
(`tests/Puck.World.Server.Tests`, ordinary suite, under a minute): real
authorities run every single-fault schedule while the model follows, and every
peer call or crossing-log append the model does not enable is a failure. When a
change to an engine transition needs the model's evidence, run it beside the
model check (`--filter-class "*CompanionCustodyRefinementLawTests"`); a move it
reports as deferred is a protocol step the engine has not built.

## What uses the GPU

A windowed World driven by a stdin script spells its backend `--backend directx` or `--backend vulkan` (`auto` picks), waits for bakes with `world.wait bakes <s>` where `<s>` is 1 to 600, and is stopped by its PID once its readings are in: a windowed World has been seen running long past `--exit-after-seconds`.
Use the private CLI copy's `puck lease gpu --holder <lane> --wait 1200 -- <command...>` for a granted GPU leg, and `puck lease heavy --holder <lane> --wait 7200 -- <command...>` for a heavy suite: heavy holders run long recorded suites back to back, and a one-hour wait can time out behind them. Both accept `--kind Step` for bounded focused checks, changing the peak estimate without changing exclusion. Run the request in the background and keep working. Without `--wait` a held lease refuses at once, so a script that runs its next step regardless (a boot after a build) must check the lease's exit code, or it measures a stale binary. The verb owns acquisition, stale recovery and release, including failure and cancellation. A windowed World started with `puck_world_start` takes the same GPU lease itself (`holder`: your lane) and releases it when `puck_world_stop` ends the World or the World exits; `timeoutSeconds` bounds the whole admitted start, lease and admission waits included.
Heavy starts also pass the host admission, so a lane never checks memory or commit charge by hand: `puck lease heavy` and `puck lease gpu` wait after they hold their lease (within what `--wait` has left; refused with exit 2 and the lease released when it does not return), and `puck_world_start` waits after the GPU lease (a windowed World as a device step, a headless one as an ordinary step) and shows the wait as its pending stage (`waiting for memory headroom: commit headroom 16.0GB`). Admission needs free memory over the capacity threshold, a commit charge under `HostLoadThresholds.CommitCapacityPercent` of the commit limit (allocation fails at the limit with RAM still free), commit headroom that covers the start's expected peak, free disk over the pressure threshold, and, for a device step, an idle GPU (`puck host load` reads the same figures). The expected peak is by kind (`HostLoadThresholds.DefaultPeakGb`: windowed World 18 GB, headless World 6 GB, heavy suite 10 GB, `puck test` batch replica 9 GB, ordinary step 2 GB) plus what the Worlds and heavy suites already running still have to grow to their own peaks, read from their live private bytes (`HostStartTracker`), so several starts admitted at a low charge cannot grow into each other; a finished start of the same kind that the admitting process saw raises the figure, and nothing is recorded in a file. A start admitted a moment ago whose process has not appeared holds its peak for 90 seconds, in the memory of the process that admitted it only: two lanes asking in the same instant read the same headroom, so the heavy and GPU leases still serialize the starts that matter. Every verb that boots a World process passes this admission itself before each process it starts (`WorldAdmission`: `puck test` before each worker process, so each replica of `--reproduce` and `--workers` is admitted on its own reading, and `canary`, `sweep`, `motion`, `counters`, `parity`, `qualify`, `docs citations`, `verbs`, `bench startup` and the in-process World boots of `bench` and `determinism`), so a lane never adds a memory check around them; a wait prints `gate: waiting for host capacity before <verb> ...` with a heartbeat, is bounded, and refuses the start when it does not return. A verb under `puck lease heavy` or the gate admits again before its own start, on a fresh reading, and its own child processes never hold a heavy-suite wait back. A CPU-heavy test suite started by hand (`dotnet test`, a built test assembly) still needs `puck lease heavy`. A focused GPU law with a bounded small fixture and no World boot uses `puck lease gpu --kind Step` alone; the explicit kind selects its small peak while the default GPU lease still reserves a windowed World's peak. Do not reserve a heavy suite's peak for a small device check. Broader GPU suites that also do heavy CPU or memory work take both leases.
Sequence a lane's windowed starts and manually started GPU laws. Do not queue a heavy/GPU test behind the lane's own pending windowed start: the test can reserve its heavy peak while waiting for the GPU, taking headroom the World needs. Finish the capture World before queuing the law, or finish the law before queuing the World.
To hold one lease across several commands, run a script the lease starts, with Git Bash named by its full path
as the PowerShell paragraph below says.
Keep a granted batch within its brief's time box (at most 20 minutes for a GPU lease).
A verb that builds the World (`puck test`, `canary`, `sweep`, `parity`, `counters`, `docs citations`) and the MCP start wait for the `build` lease up to two hours (`CliLease.DefaultWait`) with the queue position on stderr, and the build then has its own thirty-minute limit, so a deep queue is not a reason to pass `--world-artifact` or to build privately; the flag is for booting a build made some other way.
`gate` and `affected` hold the same heavy lease through each heavy suite and
pass the same host admission after acquisition. `puck test` holds it too whenever it runs
more than one world (a debug-world sweep is 112 World processes, several GB each);
a single world stays unleased (including `sweep --no-scenarios` without `--tour`,
which passes its own headless-World admission), and `--workers` legs run one after another so they
do not count. When an ancestor already owns it,
validated PID/start-time ancestry admits a serialized child lease, so a run
does not wait on itself and siblings still exclude one another; every
acquisition (heavy, the World build, `puck sweep`'s GPU lease, a World start)
does this; [lease reference](../../../docs/reference/cli.md#puck-leaseexclusive-machine-work).
A timing measurement runs under `puck lease quiet --holder <lane> --wait 600 --settle 30 -- <command...>`,
which holds the heavy, build and GPU leases together, waits until `puck host load`'s
readings show an idle machine for `--settle` seconds, then runs the command, with
`puck sweep` and Worlds nesting inside it; the lead's settle windows use it.

In PowerShell, quote forwarded `'-nodeReuse:false'` after `--`; otherwise the
shell can split the colon-bearing switch before the lease receives it
([toolchain caution](../../../docs/development/contributing.md#hardware-and-toolchain-cautions)).
The lease starts its child without a shell, so on Windows a forwarded bare `bash`
resolves through `PATH` to WSL's `bash.exe`, which cannot see `D:/` paths, has no
.NET 11 and exits 127, and only after the queued lease was finally granted.
Forward Git Bash by its full path (`"C:/Program Files/Git/usr/bin/bash.exe" <script>`)
to run several commands under one lease.

Take the heavy lease once per suite run: one `puck test` invocation (or one
suite) per `puck lease heavy` acquisition, never a batch of runs under one hold,
so the queue rotates between lanes. Keep each hold to about 30 minutes; a run
that genuinely needs longer (Go's `--reproduce` with its full recompute) holds
it alone and releases before the next. A script of several runs wraps each run
in its own `puck lease heavy` call.


GPU work is a verb in `GateComposition.GpuVerbGrammars` (`puck host load --help`
lists them), a canary requiring `gpu`, a windowed or offscreen `Puck.World` run,
and any test run that can select a device law. Device laws are test classes
marked `[Trait("Category", "Gpu")]`, in the assemblies of
`HostProcesses.DeviceTestAssemblies`; a full run of one of those suites is GPU work
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
  postpone a granted leg on that match alone. `puck host load` names a running
  `Puck.World` as holding the GPU only when its arguments open a device
  (`WorldDevices.OpensDevice`): a headless one (`--headless true`) is not counted,
  and neither holds the lease. Read the process's command line, and let the lease
  (`puck_world_start` waits for it) decide.
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
7. Private bytes count the device's own heaps too. A driver can map every
   device-local heap into the process as write-combined private pages with
   almost nothing resident: the debug world's mesh-region destination is one
   such allocation, and gigabytes of a windowed World's private bytes can be
   these. Read system memory as
   the working set, and separate a page probe's write-combined pages by their
   residency before charging them to a host buffer.
8. Attribute managed holders by walking the heap, not by a sampled dump:
   `dotnet-gcdump report` samples a multi-GB heap and its per-type totals do
   not add up to it. A scratch ClrMD program (`Microsoft.Diagnostics.Runtime`
   4.x reads .NET 11; attach with `suspend: true`, since a snapshot of a large
   World fails) that sums live arrays by their referrer chain names the
   holders exactly. A suspended World shows the pause as one long frame in
   `host.frames`, so never attach during a frame-time reading.

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
| [`review-passes`](../review-passes/SKILL.md) | A review pass in either mode, and verifying a review-and-fix pass's fixes. |
| [`maths-laws`](../maths-laws/SKILL.md) | The Maths law suite's tiers and structural completeness. |
| [`gaming-bricks`](../gaming-bricks/SKILL.md) | The emulator suites. |
| [`rendering`](../rendering/SKILL.md) | Captures, parity and GPU counters for render work. |
| [`puck-world`](../puck-world/SKILL.md) | Running `Puck.World`, stdin scripts, replays and `puck test` worlds. |
