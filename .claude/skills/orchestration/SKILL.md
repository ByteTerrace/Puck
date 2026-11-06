---
name: orchestration
description: "Leads Puck lanes: briefs, agent and model choice, landing, branch and worktree cleanup, GPU and build scheduling, settle windows, owner questions, routing findings; review-passes runs reviews."
---

# Orchestration

This skill is the lead's procedure for coordinating assignments, machines,
merges and reviews. [AGENTS.md](../../../AGENTS.md) owns branch authority and
repository rules; this skill routes verification and review execution to their
owners. The user's current instruction outranks this skill; a rule that argues
against a requested change is stale and is corrected in the same change. What
the owner says in a session overrides this skill and is written into it in that
session.

Load [lane work](references/lane-work.md) before branch operations, delegation or a long run; it owns the detailed procedures.

## Plan from the milestone

Plan backward from the milestone's critical path. Find the least-prepared item
on it and put the best-fit partner there. Busy is not progress: never queue
filler to make a partner look busy. Idleness is a prompt to re-read the path, not
to create work.

## Spend verification, not writing

**Three phases, each with its own evidence (the owner's ruling: measurements
on a noisy machine have no value, and measurement must never become the
bottleneck).**

| Phase | Who and when | Evidence | Never |
|---|---|---|---|
| Iterate | every lane, in parallel | load-independent only: counters, state hashes, bytes, executes, law results, captures; laws filtered to the step; a reproduce of only the scenarios the change reaches | a timing claim; a full suite per step; a second heavy job queued behind one's own |
| Integrate | the lead, once per landing batch | full reproduce suites, the sweep against the tip report, windowed boots on both backends, over the merged batch | per-lane repetition of the same suites |
| Measure | the lead, in a scheduled quiet window after each batch (about every two to three hours) | lanes queue `puck measure request` with frozen before/after builds, metrics and what decides it; the lead reads `puck measure list` and drains it with `puck measure run`: one priority quiet lease, repeated ABAB, median, IQR and paired difference from the existing bench/sweep reports | a lane timing its own change; a window that waits for the machine to go quiet by itself |

A lane with a timing question uses `puck measure request` (the frozen builds
or commits, the bench startup or sweep command, JSON-pointer metrics and what
decides it) and hands back on its load-independent evidence; `puck measure run`
answers it in the lead's window. The [CLI reference](../../../docs/reference/cli.md#puck-measurequeued-measurement-windows)
owns the forms. Lanes' processes keep running; their next leased grant waits
for the measurement window. Quiet priority is enforced at `CliLease.TryAcquire`,
under the same short lock that registers tickets, before any resource grant.

Writing is cheap and verifying is expensive: builds, boots, suites and GPU
windows on one shared machine set the pace, so the work in flight is bounded by
what the machine can verify. How many lanes run at once has no fixed maximum
or minimum (the owner's ruling): the lead judges it each time from the work in
hand (concurrent builds, measuring lanes that load would skew, GPU legs,
shared files, its own capacity to inspect and merge) and launches more only
when that judgement says the machine and the lead can absorb them. The owner's
direction:

- **Check what running cannot show, early, without gating it.** A change in a
  class [hazards](../../../docs/development/hazards.md#silent-invariants) marks
  review-only (deleting files, a trust boundary, GPU lifetimes, the ISA pair,
  rewriting persisted data) has its adversarial review started the moment it
  hands back, and the lead reads its diff for that hazard before merging; it
  still lands on its evidence and is never held for the review. A pruning
  change that could delete outside its install root and a cap that trusted the
  sender both had to be reverted or refixed after landing.
- **The lead's verification window outranks lane legs.** Windowed checks owed by
  several lanes are one session the lead runs with GPU priority; it is never
  queued behind the lanes it verifies.
- **Speed claims get a quiet window.** A lane proves its win on load-independent
  counters (allocated bytes, march and ray step counts, `world.budget` work
  counts) and lands on them. It queues the timing question with `puck measure
  request`; the lead's `puck measure run` reserves a priority quiet window and
  answers the batch with interleaved A/B rounds. A heavy lease alone does not
  make the machine quiet. A timing claim stands only once measured there, and
  the window never holds a landing. Every benchmark or counter check first shows that its steady
  state runs the changed branch, not a warm cache or a default path: in the
  ASP.NET agent effort (dotnet/aspnetcore#70219), review repeatedly found
  benchmarks and tests that never reached the changed code. A change is
  judged by what it measures and what it makes possible, never by its isolated
  delta alone (AGENTS.md, "Changes pay dividends";
  [measured changes](../dotnet-performance/references/measured-changes.md)).
- **An Antigravity lane is bounded, its claims are checked against the code
  before it merges, and its review starts first**: its output has carried
  fabricated claims and high findings.

- **Batch, including unrelated areas.** Several lanes work independent areas at
  once, and the integration lane verifies them together in one sweep of the
  debug world
  ([decision](../../../docs/decisions/engine-design.md#the-debug-world-is-the-engines-instrument)),
  which also shows breakage in partner systems that a single-feature check
  misses. Isolate a change only for a benchmark against a baseline, a
  determinism or fixed-point change, or a regression hunt.
- **Brief along the whole path a change travels**, not one system: the lane maps
  the systems its change reaches (`puck references`, the owning skills) and
  checks those partners before handing back.
- **Improve by reading, not by reviewing diffs (the owner's ruling).** Velocity
  comes from reading the code as it is and improving it continually, anchored
  to running and measuring it. A diff review checks the change and not the
  system around it, splits finding from fixing across three contexts, and fills
  a findings queue that goes stale as the code moves, so it is not a standing
  step.
  - Every lane reads around the code it touches and fixes what it finds there,
    scoped by the [`boy-scout`](../boy-scout/SKILL.md) skill; a brief says so.
  - Fresh eyes come as area fix passes: a reader from another family (Codex for
    Claude-written areas, a Claude `specialist` for Codex-written ones, Argon for
    security, large-context and UI areas) takes an area as it is now, not a
    diff, fixes what is local and clear in its own worktree, and proves each fix
    by running or measuring it. The lead schedules them by risk and churn: the
    areas a milestone changed most and those a silent invariant guards.
    Determinism, maths and renderer areas get Astra or an Opus `specialist`,
    ordinary engine code Sol or `specialist`, docs and world content none.
  - Adversarial review is required only for the review-only classes in
    [hazards](../../../docs/development/hazards.md#silent-invariants). The
    reviewer gets the diff and the author's claim, not the author's reasoning,
    and tries to break it with a repro, a counterexample or a missed caller. A
    class caught twice gets a check or a structure, which retires its review.
  - The lead checks every claim before merging: it runs what a lane says it
    proved, and reads a benchmark, harness or counter as code. A measurement
    that cannot fail, or that runs another path, is a defect at the severity of
    the claim it supports.
  - A change justified only by preference is not an improvement (AGENTS.md,
    "Changes pay dividends"): passes never trade one agent's taste for
    another's.
  - A finding too large to fix in its pass goes into the open items'
    [work packets](../../../docs/plans/open-items.md#work-packets) with its
    severity, one owner, its file and line and its failing case. A filed finding
    is rechecked against the current code when its packet is next worked, and is
    fixed, or deleted when the code has moved past it.
  - Nothing waits on a pass: the author starts its next item when it hands back.
    The integration lane may revert a landed change when leaving it in is worse
    than removing it.
  - Before the owner pushes `main`, the milestone's riskiest areas have had a
    fresh fix pass and their invariant checks ran; a high finding from those
    passes is fixed or explicitly deferred by the owner.
- **One integration lane, fed in batches.** It is the only heavy job on the
  machine. It takes ready branches and batches unrelated changes together on
  purpose; it isolates a change only for a benchmark against a baseline, a
  determinism or fixed-point change, or a regression hunt. It merges the batch,
  builds what the merge touched, runs the batch's sweep, bisects a red batch, and
  hands the lead the green head to fast-forward
  ([land in batches](#land-in-batches)).

## Own tracks, not tasks

Partners own tracks and outcomes. A brief gives the outcome and the
constraints, never the method, the design or the order of the edits. A partner
picks its next work from its track or a shared ready queue and announces it. The
lead keeps cross-track conflicts, merges, owner-level calls, and scope and
order. Every brief ends with the partner's own next one or two items, and the
partner works its queue without waiting for the lead between items. A lane
waits for its own evidence (its GPU leg) before it is ready, and once ready it
waits for nothing ([land in batches](#land-in-batches)). Neither wait blocks
the partner: the
partner takes its next item from its track or the ready queue and announces
it. A queue the
partners cannot read is no queue: keep the lead's queue where partners can reach
it, or delegate it to them. Every queue item has exactly one recorded owner, and
a handoff records the new one; an item with two owners is a defect to resolve
before either partner continues.

A delegated lane does its brief itself and starts no sub-agents of its own. A
lane that fans out hands back before its children finish, their reports never
reach it, and the integration it owed falls to another round. When work splits,
the lead splits it into lanes.

## Write and track assignments

[Lane work](references/lane-work.md#delegated-work) owns what a brief holds and how
shared-file edits are sequenced; its report shape is the three headings that
[Running long tasks](references/lane-work.md#running-long-tasks) names. Add to every
brief:

- an id, used in the brief file name, the commit trailer and every message;
- its family and model, chosen from the whole roster (Claude, Codex and
  Antigravity, [select models](#select-models-and-route-findings)) with a
  one-line reason: a batch the lead briefs entirely to one family is a
  lapse to correct, not a default;
- the base commit, verified by the lead before the brief goes out;
- the task's premise, checked in the code by the lead before the brief goes
  out (rule 7). A packet's claim of what is missing or what causes a cost is
  a lead to verify, not a fact. One lane was sent to add a source-generated
  JSON context that already existed, because the packet said none did;

- for a Codex or Antigravity lane, an instruction to read the
  [`delegate` skill](../delegate/SKILL.md) before anything else (Claude's
  agents other than `scout` preload it); it owns the lane's own working contract.

When a lane demonstrates stale-rule behavior, the lead starts a fresh session
before launching its continuation. Editing a skill alone does not require a new session.

Verify each premise of a brief yourself before sending it; a wrong premise costs
every lane that received it. When a brief proves wrong, recall every lane working
from the same premise before replying to the lane that found it, and say that a
stop is a stop, not a pause.

A brief also names the existing mechanisms its change must reuse (found with
`puck references` and `puck declarations` while checking the premise) and
says no second one may be added. Reviews kept finding duplicates that lanes
had just written, a second retry loop in a change built to remove one among
them, so duplicates were created about as fast as they were found; naming the
mechanism up front is cheaper than the review that catches it.

A brief asks for the fix at the cause and never lists a stopgap as an
acceptable direction (AGENTS.md: the proper fix is always preferred). A lane
takes the cheapest option a brief offers: a rebake brief that listed
"quantize the change" as one shape got whole-step water levels (`surfaceStep`)
that visibly jump, when the cause was that a moved surface needed a new mesh at
all. When the lead does not yet know the proper fix, the brief asks the lane
to find it and say why, or names it as a design step before the code; a
mitigation that lands in the meantime is recorded as one, beside the open item
that removes it.

A performance sweep partitions its lanes by target type or method, never by
benchmark scenario: two scenarios share hot code, so lanes split by scenario
rewrite the same method twice. ASP.NET's agent effort produced pairs of PRs
that rewrote one method the same way (dotnet/aspnetcore#70148 and #70199) and
four new pooled-buffer helpers, two of them byte-identical (#70187 and #70191).

When a second fix lane in one area comes back and its review finds the same
class of defect in places the fix did not reach (disclosure took two lanes
and three reviews, each finding new leaking doors), stop patching. The
property is enforced in too many places; brief a design for the one
chokepoint that makes the property structural (a type or project boundary,
one derived view), then migrate behind it.

Better still, do it first. Work whose correctness rests on an invariant (a
cache and its invalidation, an oracle and what it records, a visibility or
shadow term and the geometry it assumes, a concurrency primitive and its
wakeups) gets a written design and a Codex design review before any code,
and its brief names the invariant and what enforces it: the compiler, a
single door, or a derived view, never "every caller remembers". First drafts
of such work keep coming back with high findings, each from an invariant kept
by convention; a design review costs a quarter hour, a failed fix round a
lane's afternoon.

Before briefing a step that decides how a pipeline works (what the renderer
draws and submits, how the tick schedules, a camera or physics model), ask
whether a research round has covered that design, even when the step arrives
as a row in another plan. A plan written for one purpose (the job-system memo's
R2, "move level selection to the GPU", written to free the frame thread) carries
an unexamined design for another (what visibility should be): that lane moved
the work without reducing it. If no round covers it, run one first (AGENTS.md,
"Regular research is worth it"); the round costs an hour beside other lanes, a
wrong design costs a lane, its review and the redo.

Verify any base claim yourself with
`git merge-base --is-ancestor <base> <integration-head>`, checking its exit
status, and count the commits behind with
`git rev-list --count <base>..<integration-head>`
([why a two-dot range is not ancestry](../verification/SKILL.md#branches)).

Only the assignment's last commit carries the `Brief:` trailer the
[`delegate` skill](../delegate/SKILL.md#finish) names (`done` or
`blocked: <question>`); progress and WIP commits carry none, and no other value
follows `Brief: <id>`. Before merging a lane the lead runs
`puck landing handback --against <integration branch> --checkout <lane worktree>`,
which checks the trailer, every subject's format, Co-Authored-By trailers,
plumbing-built commits and `puck docs links` at the lane's head.

The lead's ledger and plan files that a delegate never writes are the lead's
own scratch checklists under `<worktree>/.claude/scratch/<lane>-*`. A scratch
checklist holds only what the next hour needs. Plans another session would need
to continue on another machine go into `docs/plans/` and are pushed: wave
plans, grounding reports, pending notes and owner answers. A session's job
directory and scratch are lost with the machine or the session. A lane branch
is merged, pushed to `origin` as `lane/<id>` while it waits, or deleted once a
later design supersedes it, so no machine holds a branch whose fate only one
session knew. Before deleting one, search `docs/` and `.claude/` for its name:
a search of the code alone misses a plan that keeps a superseded branch as
prior art for code no other branch holds. A remote branch whose
work never landed is the owner's to delete (AGENTS.md), so the lead brings the
search's result to the owner rather than deleting it. The repository's documents,
`docs/plans/open-items.md` and the plans under `docs/plans` among them, belong
to no one lane: a delegate edits them for what its change delivers, deleting an
item it closed (after moving the durable fact it carries to its owning home),
placing what it found and left into a work packet, and correcting text its
change made stale, and the brief says so.

The message is the notification; git is the record. Inspect the reported
changes and what the lane ran to show them working before merging
([landing bar](../verification/SKILL.md#the-landing-bar)).

## Land in batches

The main checkout tracks the integration branch (the owner's ruling). Claude
Code loads `.claude/agents/`, hooks and unscoped skills from the project root,
the main checkout, not from the lead's worktree, so a lane runs whatever agent
definitions and skills the main checkout holds. After each push of the integration
branch the lead fast-forwards the main checkout's branch to it: leave the
worktree with `ExitWorktree` (`keep`), confirm the main checkout is clean, run
`git merge --ff-only <integration branch>` there, and return with
`EnterWorktree` (`path`). The main checkout keeps its own branch name, because
the integration branch is checked out in the lead's worktree; it never carries
work of its own.

A lane is ready when it meets its own focused checks
([landing bar](../verification/SKILL.md#the-landing-bar)): it builds what it
touched, its commit body says what it ran to show the change working, and it has
merged the integration tip. A ready lane joins the integration lane's next batch
and is held for nothing else: not another lane, not its review. The integration
lane merges the batch, builds what the merge touched, runs what the batch
changed and looks into a red result; the lead fast-forwards the integration
branch to the head. A test or sweep that failed or went unrun does not by itself
block integration while the campaign lifts test requirements. "What the merge
touched" includes what consumes it:
- a change to a shader, interface or generated header builds `Puck.slnx`, test projects included;
- a change that adds a project reference to a packable project (`IsPackable` in its project file) runs `puck nuget pack --output <scratch> --no-build` after a solution build, as CI's artifact job does: a reference to a non-packable project builds cleanly and fails only at packing;
- a change to a world document is shown by booting the world; the shipped-source suites (`Puck.World.Transpiler.Tests`, `Puck.Cli.Worlds.Tests`) are an optional technique for it;
- a change to simulation state, maths or determinism can run the `worlds/debug/*-tests.puck` files it touches with `--reproduce`, which shows two runs agree;
- a change that adds teaching to a skill or agent lands with its eval results in the commit message: the
  capability case for that teaching, plus a regression run of the skill's suite, with arms, runs, models
  and the delta. Otherwise the commit says plainly that the teaching is unproven, and the lead queues
  the eval ([evaluate an agent skill](../../../docs/development/documentation.md#evaluate-an-agent-skill));
- every batch runs `puck docs links`, because two lanes that each pass can still break each other's
  citations, and CI checks only `main`; a failure the batch caused is fixed before the fast-forward;
- every check runs from a CLI and test binaries rebuilt after the merge, never stale copies;
- never merge into a tree while checks are running against it: `puck test` reads the tree's `.puck` sources
  live, so a mid-run merge pairs new sources with old binaries and fails for no reason in the code. One
  batch's tests failed with PUCK030 on `engage.seats` that way. Wait, or run checks from a frozen worktree.

A merge is not finished until every line of the lane's Found section, and every
Needs-you item it hands the lead, is routed. Each one is filed in its owning
packet, sent to a running lane, or dismissed with a reason in the merge
report. A finding read and left in the hand-back is lost: one about authors'
worlds failing to boot on the standard sat unrouted until a side observer
caught it.

Keep a merged lane's worktree until its invariant review, if it has one, is
back. Most review highs go back to the lane that wrote the code, which keeps its context
and resumes in place (`SendMessage`); once its worktree is removed, a fresh
lane must rebuild that context from the brief and the findings. Prune after
the review's findings are routed, and delete the lane branch once its fix
round is merged too.

The sweep is the batch's proof: the whole debug world, every signal compared with
the previous sweep ([decision](../../../docs/decisions/engine-design.md#the-debug-world-is-the-engines-instrument)).
`puck sweep --tour --baseline <previous report.json>` runs it under the GPU lease
(or `--attach` names a World `puck_world_start` started) and writes the report
and contact sheets ([CLI reference](../../../docs/reference/cli.md#puck-sweepthe-debug-worlds-sweep)).
The full debug-world replay (every test world, the longest heavy job) is the
integration lane's next heavy job after a batch lands, never beside another heavy
job and never a condition of a landing (the owner's ruling: "The whole idea was
to be EFFICIENT"); what it finds is ordinary work for the next lane. A red check
on the integration branch is the next piece of work, not a reason to stop
landing, and the integration lane may revert a landed change when leaving it in
is worse than removing it.

The open items are the batches' queue
([work packets](../../../docs/plans/open-items.md#work-packets)):
- each batch's lanes take the top packets of its
  [Next](../../../docs/plans/open-items.md#next) list, one packet a lane, with
  any high finding first;
- the lead re-ranks Next at each batch by payoff, folding in what the batch
  landed and what its sweep and reviews found;
- a finished packet is deleted, not ticked, and so is a closed sub-point, once
  the durable fact it carries is in its owning home (AGENTS.md rule 9);
- a programme's phase-scale work stays in its plan's open items, and a packet
  points at it.

Before the owner's push to `main`, rehearse the release on the hosted runners,
not only locally: push the integration branch and dispatch Release Azure on it
without deploying (`gh workflow run azure.yml --ref <branch> -f deploy=false`),
then read every job's result. A local rehearsal of the build and checks missed
the published World's startup step, which boots the published debug world from
its compiled package and failed once that package imported another
(`worlds/standard`); the dispatched run shows exactly what a push to `main`
will do.

### Coding lanes and the integration lane

Heavy verification runs in one place, one job at a time; this is the owner's
direction: concurrent whole-solution builds exhaust a machine's memory.
- **Coding lanes** keep to the [`delegate` skill's](../delegate/SKILL.md#build-and-run)
  build bounds and hand back a branch that builds and passes their focused
  checks.
  Several run at once (the owner's direction: don't over-serialize for
  memory). Concurrency is set by measured load, not a fixed count (the
  owner's direction): the lead samples available memory, commit charge, CPU
  and live compilers and Worlds while lanes run, adds lanes while there is
  headroom (writers spend most of their time reading, not building), and holds
  or sheds them when memory runs low. Commit charge against the commit limit is
  the line that matters: allocations fail at the limit while physical memory
  can still look free. The tooling holds that line for every heavy start, so a
  lane polices nothing by hand: `puck lease heavy`, `puck lease gpu`,
  `puck_world_start` (windowed as a device step, headless as an ordinary one),
  `gate` and `affected` each wait, after they hold their lease, until the commit
  charge is under its share of the limit (`HostLoadThresholds.CommitCapacityPercent`)
  and the headroom covers the start's expected peak plus what the Worlds and heavy
  suites already running have left to grow (`HostStartTracker`), beside free memory and disk, and name what holds them
  ([admission](../../../docs/reference/cli.md#puck-host-loadadmission-lines-for-the-machine)).
  Every verb that boots a World process (`puck test`, `canary`, `sweep`, `motion`,
  `counters`, `parity`, `qualify`, `docs citations`, `verbs`, `bench startup`) waits
  on the same admission before each process it starts, so a lane never wraps those
  in a check of its own, and a run that boots several replicas in sequence (`puck
  test --reproduce` or `--workers`) admits before every one of them.
  A windowed debug World grows past ten gigabytes, so a
  GPU lane closes its World between capture batches. A test run that holds
  gigabytes (the mesh-irradiance laws trace a whole hall and reached 9 GB
  private) is a heavy suite: the brief says to run it under `puck lease heavy`
  and by name, never the whole class beside a windowed World: a windowed World
  and such a run peaking together took the machine to 95% of its commit limit.
  Admission holds a start back until the headroom covers its expected peak and
  the growth the running Worlds and heavy suites still have left (it reads their
  live private bytes), so starts admitted at a low charge no longer grow into
  each other. It cannot see a start another process admits in the same
  instant, or a process it does not classify (a hand-started `dotnet test` of a
  suite that is not heavy, a build), and a kind's peak is a conservative
  default, so a lane whose run outgrows its kind says so under **Found**.
  The lead cannot stop another lane's process; it asks the lane. On the lead
  machine, more than about three lanes that
  boot Worlds or heavy suites at once kept commit charge between 80% and 94%
  for hours without speeding anything up. Memory, not CPU, bounds how many
  World-heavy lanes run together; lanes that only read, write documents or
  build fit beside them freely. CPU saturation does not
  endanger lanes but voids timings: a lane whose evidence is a time (a tick, a
  frame, a bake) attributes cost by samples and counts while the machine is
  busy and takes its before/after times in a quiet window the lead clears; a
  paired timing taken at full CPU swung both ways by 30%. Busy timings also
  hide regressions and inflate gains: a feature lane that added a 20x
  simulation-tick regression (5.8 to 120 ms) reported "no measurable
  difference" from runs on a loaded machine, and the produce lanes' busy
  readings (43 to 11 ms) became 15.5 to 14.4 ms quiet. So every landing that
  touches the World compares load-independent per-tick counters (body
  advances, contact queries, rules evaluations, draw records changed) on a
  headless boot against the integration tip, and a jump stops the landing
  until it is explained. The tool is `puck sweep --no-scenarios --baseline
  <tip report.json>`: its headless leg reads `world.counters physics` and
  `world.counters state` and turns a counter red when it moves more than 50%
  (`relative:0.5` in `worlds/debug/sweep.json`); a lane runs it before hand-back
  and names any change over 10%. The lead regenerates the tip's report under the
  main checkout's ignored scratch directory (a `tip-sweep` folder) whenever the tip moves, and briefs
  name that path. A report stores each signal's rule colour (`status`) apart
  from its baseline comparison's (`comparison`), and a later comparison reads
  only the rule colour, so a tip report recorded with or without `--baseline`
  serves equally. The lead runs it, and every lead verb, from a CLI built at the tip, and reinstalls the
  global `puck` tool ([publishing](../../../docs/reference/cli.md#publishing)) once `puck --version`
  warns that it is stale: a stale global tool refuses the current sweep plan's keys, and a report taken
  against an old baseline blames the landing for the tip's own drift. A single-project `Puck.World`
  build carries no SPIR-V, so a Vulkan boot follows a solution build or
  `-p:PuckShaderSpirvEnabled=true`, and the Direct3D 12 backend is named `directx`. The lead
  boots the merged head headless from its own shell before pushing: a lane's
  environment can hide a dependency (a toolchain on the lane's PATH but not the
  machine's), and lanes merge the local integration branch, so a bad merge is
  undone before it spreads, and the lane fixes it at the cause. An output-identity gate (a whole mesh store by hash)
  compares every stored payload: the mesh bake is byte-reproducible from its
  inputs (three cold bakes per backend match), so any difference is a finding.
  The hard limits are one heavy integration job and one GPU
  leg at a time. A windowed debug World holds a large share of
  the machine's memory within a minute of boot (`host.memory` reads it), so
  only a couple of building lanes fit beside one. Keep windowed capture
  batches short (boot, capture, stop), and tune on a headless World. The
  delegate skill's `--jobs 1` serializes headless worker processes, including
  reproduce replicas, with one independent native host live at a time, where a
  higher `--jobs` beside a replay can exhaust memory and kill it; a brief names
  the scenario files a lane may run.
- **Reaping** is prevented by that limit, because Claude Code reaps background
  jobs when memory runs critically low. A reaped lane is handled once: the lead
  saves its tree as a WIP commit, removes the cause, and restarts it with a
  continuation note. A second reap of the same lane stops it; it goes into its
  work packet in the open items with what was running, and the owner decides.
- **The integration lane** is one long-running lane. It takes ready branches in
  the order the lead names, batches unrelated changes together on purpose,
  merges each batch onto its own staging branch, and resolves generated-file
  conflicts by running their generators. It builds `Puck.slnx` when the merge
  reaches shared projects. Then it runs, once on the merged result, the builds
  and suites that [the merge touched](#land-in-batches) and the batch's sweep:
  the whole debug world as the
  [instrument decision](../../../docs/decisions/engine-design.md#the-debug-world-is-the-engines-instrument)
  defines it, every signal it produces compared with the previous sweep, plus
  the workloads the batch's changes declare. Under a granted GPU leg it reads the
  windowed frame-time and patrol readings
  ([landing bar](../verification/SKILL.md#the-landing-bar)); the lead first
  clears the machine of other lanes' builds, suites and World runs for the quiet
  frame-time reading. It reports green or
  red per batch and splits a red batch to find the culprit. It also lists each
  branch's deleted files (`git diff --diff-filter=D` against the integration tip)
  and compares them with what the brief allowed. A deletion the brief didn't name
  is reported to the lead before landing, never merged silently.
- **Briefs size their checks to the lease they hold.** A heavy suite or a
  windowed World holds an exclusive lease, so a brief that re-runs the full
  reproduce set after every step makes every other lane that needs that lease
  wait: one writer re-running 17-world reproduce runs per step held the heavy
  lease 20+ minutes at a time while its sibling waited. Per step, run a small
  representative subset; run the full set once at the end and again at
  integration. Two lanes that each need the same exclusive lease for long
  stretches are not parallel work; sequence them or split the lease-bound part.
- **Workflows are a standing permission (the owner's ruling):** the lead runs the
  Workflow tool whenever a multi-agent orchestration pays for itself (a wave, a
  fan-out of independent reviews or research, a pipeline of write then verify),
  without asking first; the agent-count guidance in the tool still applies.
- **A wave** fans many small fixes out as several writers and one integrator: a
  scout pass inventories, the lead triages into file-disjoint batches (each
  names its items, the files it may touch, and which batch alone owns the
  shared generated files), the writers build only what they touched, and one
  integration agent merges every writer branch and validates once. Write the
  triage to a file under `.claude/scratch/` before launching writers: a
  Workflow resumes only within its session, so a reboot or restart otherwise
  loses the triage, and the restarted writers start from the file plus each
  stopped writer's WIP branch. A scout that runs `puck` verbs uses a CLI built
  from the head it inventories; a copy left from an older head reports stale
  verbs and refusals as findings.
- **Briefs** that allow deletion say what may be deleted. A finding capture names
  its settle state: bakes settled and irradiance settled, or marked partial. A
  capture taken before settling can't support a lighting finding.
- **The lead** fast-forwards the integration branch to each green staging head,
  routes a red result back to the lane that owns it, and decides any merge
  conflict that needs judgement.

A ready branch is never held back for others to arrive. A lane that cannot land
alone is a dependency to name in the brief. Review follows its own rule
([spend verification](#spend-verification-not-writing)): it runs beside the
batch and never holds a branch or its author. Gates, parity, canaries and
counters checks run when the owner asks for a qualification, on the head the
owner names.

Use [verification](../verification/SKILL.md#what-uses-the-gpu) for GPU grants,
lease acquisition, time boxes and failure handling. Load it before a GPU leg or
classifying a flake. Investigation of a failure is a lane of its own.

### Launch capabilities

This table describes configured capabilities and known launch behavior; a grant
is not proof a command succeeded. Record runtime probes in the landing commit.

| Launch | Network | Git writes | Push | GPU | MCP | Hooks | Skills |
|---|---|---|---|---|---|---|---|
| Native `codex exec` | Configured on | Own-branch commits; checkout and common Git directory granted by profile | Refused for a lane | Lead grant and lease | Project `puck` registration | Current definitions trusted through `/hooks` | `.agents/skills` link |
| Codex companion `task --write` | Sandbox has no network | `workspace-write` excludes Git; lead commits | Refused | No sandbox GPU; lead verifies | Project `puck` registration | Same per-user trust, on supported tool paths | Same link |
| Claude worktree agent | Harness permissions | Own-branch commits; own-tree isolation | Refused | Lead grant and lease | Shared `puck` server | Project registration | `.claude/skills` |
| Antigravity `agy -p` | Terminal sandbox has no network | Own-branch commits | Refused by git-safety | No sandbox GPU; lead verifies | Workspace plugin registration (server `puck_operator`) | `.agents/hooks.json`; hooks start in `.agents/` | `.agents/skills.json` |

A native Codex lane commits directly; git-safety and git.rules guard shared
state. Linked worktrees need a grant to their separate common Git directory
and the main checkout's shared lease directory; discover them rather than
requiring one machine's checkout path. An untrusted Codex hook is skipped:
review and trust each current definition and re-review changed definitions.
Hooks cover supported activated tool paths, not every possible execution path.
See [machine setup](../../../docs/development/contributing.md#setting-up-a-machine).

Codex and Antigravity resolve their hooks from the worktree they run in, so a
branch older than the current `.claude/hooks/` has none, and Codex runs a command
whose hook fails. Claude Code runs the main session's
`${CLAUDE_PROJECT_DIR}/.claude/hooks` for every worktree agent, whatever the
agent's branch holds. Before handing an old branch to any lane (its skills and
rules come from the branch too), the lead merges the integration tip into it,
leaving any conflicts for the lane to resolve. A worktree another agent
created can deny a Codex lane its `.git/worktrees/<name>` writes; give the lane a
worktree the lead creates.

Before each fan-out, and after each fast-forward of the main checkout, the
lead runs `puck agents provenance --project-root <the session's project root>`
from its own checkout ([CLI reference](../../../docs/reference/cli.md#puck-agents-linknative-skill-discovery)).
It computes keys over `.claude/agents`, `.claude/skills`, `.claude/hooks`,
`.claude/settings.json` and `AGENTS.md` in the lead's checkout, the project
root and the main checkout, records nothing, and exits 1 naming each differing
path with both checkouts' paths and branches. On a mismatch the lead
dispatches nothing until the main checkout is fast-forwarded
([land in batches](#land-in-batches)) or the session is restarted from the
integration checkout. The SessionStart hook runs the same check on startup,
resume and compact and shows a mismatch to the owner and the session. Tell
the owner to start a lead session from the integration checkout. Whether that
also moves agent discovery is unprobed: a lane cannot start a `claude`
session, so the lead probes it once (a scratch worktree whose `.claude/agents`
holds a marker agent, and `claude -p` started there asked which subagent types
it has) and records the answer here. What is observed: a session rooted at the
main checkout lists a worktree's skills only as directory-scoped variants
(`.claude/worktrees/lead-next:orchestration`), and the bare skill name loads
the main checkout's copy.

Before launching Codex in a main checkout or fresh worktree, build/copy its CLI
as [verification](../verification/SKILL.md#run-verbs-from-your-own-cli-copy)
describes and run `dotnet <lane>-cli/Puck.Cli.dll agents link` there. Then launch
`codex exec --cd <worktree> -m <model> -c model_reasoning_effort='"<effort>"' "$(cat <brief>)" < /dev/null`.
Without the `< /dev/null`, a background launch can stop at "Reading additional input from stdin..." and never
start. A lead session isolated in a worktree has its Bash commands checked
for Git that could leave the worktree, and the check refuses a command whose
program or input it cannot read statically (a `"$(cat <brief>)"` argument, a
quoted `-c` value, `printf … |` into `dotnet`). Put such a launch, or a World
boot fed a stdin script, in a reviewed script file that takes the lane,
worktree, model and effort as arguments, and run `bash <script> …` (the
owner's approval). Keep the output in a lane-named log. A resumed Codex session retains its launch
sandbox; after changing permissions, start a fresh continuation instead of
`--resume-last`. Companion restrictions are specific to `task`, not native exec.

A Codex lane that needs the `puck` World tools launches without `--sandbox` (the
project's `puck` permission profile) or with `--sandbox workspace-write`. Under
`--sandbox read-only` the same prompt that lists all eight `mcp__puck__*` tools
in those two launches lists none of them, though the project registration is
enabled (`codex mcp list`), the launcher runs (it installs the worktree's
`.claude/mcp-install`) and other servers stay visible. A read-only review lane therefore reads sources and cannot drive a World.

An Antigravity lane runs `agy -p "$(cat <brief>)" --output-format text` from
its worktree, without `--mode`: headless flag validation rejects `--mode auto`
before sign-in, while the user-level `agentMode: "auto"` applies afterwards.
Auto mode ignores the ask list; git-safety refuses pushes and shared-state Git
operations. A headless run executes only commands its user-level allow list
names. The owner-approved list allows every command
([contributing](../../../docs/development/contributing.md#antigravity-user-settings));
a machine whose list names only `git`, `dotnet`, `pwsh` and `node` auto-denies any
other command, such as `rm` or `Remove-Item`, and the run ends with no output, so
a brief that deletes files says to use `git rm`. Reading a file outside the
workspace is auto-denied the same way ("a tool required the read_file
permission that headless mode cannot prompt for"), so the lead copies any
shared brief, findings or facts into the lane's worktree (`.claude/scratch/`)
and the brief names workspace-relative paths only. A findings-only lane writes its file under
`.claude/scratch/` and leaves it uncommitted: that directory is ignored, and a
brief that says to commit it gets a force-add that the next merge carries into
the tree. The lead copies the file into the main checkout's `.claude/scratch/`
before removing the lane's worktree: `git worktree remove` deletes ignored
files without asking, and one research file was lost that way and recovered
only from the lane's log. A headless run also ends when the agent
goes idle waiting on a background task of its own (a background build), leaving
its edits uncommitted, so a brief tells it to build and test in the foreground.
A headless run can also stall with its process alive and its log empty, so the
lead watches the lane's worktree for file writes and applies [no bad
loops](#no-bad-loops). The home policy is the block contributing
records; login and trust material stay local. The workspace plugin exposes the MCP
configuration because a bare `.agents/mcp_config.json` is not a discovery root.

Antigravity lists the `puck_operator` tool names in its context and loads their
input schemas lazily, from `<appDataDir>/mcp/puck_operator/<toolName>.json`; it calls
them through `call_mcp_tool`. A brief for an Antigravity lane that drives a World
says to read the schema file of each `puck_operator` tool before its first call,
to pass the `world` argument on every call, and to wait for a start with
`puck_world_start_wait` `timeoutMs` 60000 or more, so a cold build costs few polls.

The lead and Claude lanes drive Worlds through the `puck` MCP tools
(`puck_world_start`, `puck_exec`, `puck_world_stop`) by default: they take the
GPU lease for a windowed World, keep its lifetime with the session, and need
no stdin script; hand-written stdin scripts run into the isolation guard.

The lead's local agents can build on an unpushed lane head, so a lane
that depends on another starts there before it lands; merge the landed head into
it afterwards.

Partners cannot see unpushed lanes. When a partner designs against code that
exists only on the lead's machine, send it those lanes' contracts and answer its
contract questions from the local code; a stale remote makes false "failing"
claims.

The lead runs any merge an agent is denied. Agents resolve conflicts in
generated files by running their generators, never by hand-merging: the world
schemas and the portal's TypeScript (`puck schema`), the name registry
(`puck registry`), the vocabulary page (`puck vocabulary`), the console verb
catalog (`puck verbs`, which boots a headless and a windowed World, a GPU leg) and
`packages.lock.json` (`puck locks`).

The integration branch is where the lead lands work. Work reaches `main` in
complete milestones, only when the owner asks and never on a lane's request,
because a push to `main` deploys to Azure, under
[lane branch procedures](references/lane-work.md#branches-commits-and-pushes).

## Schedule machines

Allocate by each machine's fixed capability (CPU class, GPU, RAM, OS, network),
mapped once and checked every turn. Free-memory and free-disk snapshots
fluctuate with what else is open and do not define a machine's capability.
Allocate one GPU leg per GPU at a time, and let CPU work, builds included, run
beside it: a correctness leg's verdict does not depend on load, and a leg that
times out under load is re-run once alone and reported as a flake. Only
wall-clock work needs a quiet machine, and the lead takes it: a lane judges
by counts and lists the timing reads it wants, and the lead runs them in a
window it has cleared of every other lane's builds, suites, evals and World
runs, or grants the lane interleaved A/B rounds under the heavy lease. A
brief that measures boot, compile or frame time grants no GPU and takes no
timing while other lanes run.

`puck lease list` is how the lead sees lease pressure: each of build, gpu and
heavy with its holder, lane and how long it has held, then its queue in
admission order with each waiter's wait. Read it before granting a GPU or heavy
leg and while draining for a settle window, to see which lane a window waits on
(a holder far past its time box is the one to ask), and `puck lanes` for the same
queues per lane.

Settle windows are how the lead clears it. The owner's loose deadlines were
meant as these periods; the lead now schedules them by state, not the clock:
- **Trigger:** a wave of related lanes has landed, or a decision waits on a
  measurement (the culling order waits on M1). Never on a timer, and never
  by waiting for the machine to go quiet on its own: with a dozen lanes it
  never does. The lead drains deliberately.
- **Drain:** launch no new lanes; let running ones reach a stopping point.
  Lanes may keep editing; their builds, tests and GPU legs queue.
- **Hold:** `puck lease quiet --holder lead -- <recipe>` takes the build, GPU
  and heavy leases together and waits for `puck host load` to report idle.
- **Measure:** one fixed recipe so windows compare: `puck sweep --baseline
  <last>` (frames at the standard stops, both backends, the day cycle
  running), a cold bake per backend, and the measurement of the moment.
- **Record and release:** the numbers go in the commit message of the change
  the window measured; the owning plan says only whether its bar is met and
  what comes next, with no reading, date or SHA. Then the leases return.
- **Sweep the corpus:** a wave's closing window also sweeps the documentation
  ([keep documents from rotting](../../../docs/development/documentation.md#keep-documents-from-rotting)).
  1. Run `puck docs links` over every hand-written page.
  2. Split the corpus into areas, one reader family each, and give the least
     reliable family's area a second full reader.
  3. Each reader sorts every page and large section into keep, shrink (with a
     target size), merge or delete before writing fix findings.
  4. A second family checks every finding against the code.
  5. One lane applies the result; the lead reviews its diff.

  The report states each area's reader count, because consensus on findings
  is not coverage of pages.
A lane's own timing taken under load is recorded as load-bound, and the plan
does not rank on it. Booting a windowed `Puck.World` is a GPU
leg even when only its startup is measured; a headless one is not. Use
[`verification`](../verification/SKILL.md#what-uses-the-gpu) for GPU work
classification and execution.

A brief's finish line follows [the integration lane](#coding-lanes-and-the-integration-lane):
a coding lane's brief names its touched projects and focused tests, never a
whole-solution build, a full suite or a debug-world replay, which the
integration lane (or the lead acting as it) runs one at a time. A finish line
shows a feature by running it or by a sweep signal; it asks for a `test`
scenario only by naming the invariant running cannot show
([puck-world](../puck-world/SKILL.md#running-it)), never one per demonstration. Writing the
landing bar into every coding brief turns each lane into an integration lane:
heavy jobs then run side by side and Claude Code reaps every background command
when memory runs critically low. Reaped worktrees survive and Codex lanes resume
by session id; [reaping](#coding-lanes-and-the-integration-lane) owns what the
lead does next.

Run a load governor on any machine that hosts many agents, through
`puck host load`, which samples the machine's load and names its state. Under a
watcher, `puck host load --watch` prints one line per transition (`CAPACITY`,
`LOADED` when capacity ends without pressure, `PRESSURE`, `GPU busy` and
`GPU idle`), so each change arrives once:

- On capacity, admit the next job. A heavy job (a solution build plus a full
  suite) is one at a time per machine: a full
  `Puck.World.Tests` or `Puck.World.Presentation.Tests` run never runs beside
  another. `puck gate` and `puck affected` acquire the existing heavy lease
  around each heavy suite, sample headroom while holding it, and release it on
  every exit. Nested runs under `puck lease heavy` serialize on child leases
  after validating the ancestor holder's process identity.
- A lane never runs a full `puck test worlds/debug` sweep (every test world, a
  long heavy run): the brief names the test files its
  change can reach, and the lead runs one full sweep on the integrated head. A lane
  waiting on a lease keeps working (code, docs, its hand-back) instead of
  idling on the ticket.
- Lease admission is a queue that rotates between lanes, not a race:
  `puck lease` waiters take tickets, and the lease goes to the oldest ticket of
  the lane that held it least recently. A lane is the first word of `--holder`
  (or `--lane`), so start every holder with your lane's name
  (`--holder "bars-and-pick timing"`); one lane with several runs queued gets one
  turn per round, and a lane that releases and re-acquires waits behind every
  other lane already queued. `puck_world_start` takes the same `holder` and
  `lane`, defaulting to the checkout's directory name. A waiter that is killed
  leaves a ticket the next waiter drops; a stale `<name>.queue` entry needs no
  manual cleanup
  ([CLI reference](../../../docs/reference/cli.md#puck-leaseexclusive-machine-work)).
- The GPU is the scarce resource (the owner's direction): most running lanes are
  GPU-free (CPU work shown headless), and a lane whose finish line needs a
  windowed World is started only when the GPU queue can take it. Windowed checks
  owed by several lanes are batched into one lead verification window rather than
  each lane waiting on the lease.
- Refresh private CLI copies when lease admission changes. A FIFO waiter and a
  lane-rotating waiter can each select the other's ticket and leave an idle
  resource unclaimed. Stop the old queued caller and use one scheduling policy;
  `puck lease gpu --help` states the policy. Do not keep retrying World starts
  against that mixed queue.
- A check that needs the owner present (their hardware, their eyes on a window)
  goes ahead of the GPU queue for its short run: the owner's time is the scarcest
  resource. The lead keeps it to a minute or two and discounts any lane timing
  it overlapped.
- On pressure, admit nothing. `puck gate` and `puck affected` admit each heavy
  step only with more free memory than the capacity threshold and more free
  disk than the pressure threshold, so a running agent waits rather than being
  killed.
- On GPU idle, start the next GPU leg. A device test host counts as GPU work.
- The thresholds scale with the machine; the code is their source of truth
  (`HostLoadThresholds.For`, [CLI reference](../../../docs/reference/cli.md#puck-host-loadadmission-lines-for-the-machine)).

Deliberate CPU contention, such as a burner for a flake proof, is CPU work like
any other and may run beside another lane's GPU leg. A leg of that lane that
times out under it is re-run once alone and reported as a flake
([`verification`](../verification/SKILL.md#flake-or-failure)); a wrong value
under load is a failure whatever ran beside it.

An agent stops only the processes it started, by the PIDs it recorded at launch,
and never kills by a command-line pattern. On a shared box a filter on a common
string (a scratchpad path, a lane prefix) matches processes the agent cannot
attribute, other lanes' build and test commands among them, and those fail with
verdicts that are not evidence, so a lane that failed while the kill ran re-runs
its failed legs alone before anyone acts on them. A brief that starts background
work says to record each PID and to stop only those.

Every `dotnet build`, `msbuild`, restore, pack and publish passes
`-nodeReuse:false`, a Codex brief's included, because a reused MSBuild node
outlives the build and holds memory. `dotnet test` never takes it, since it
rejects the flag ([`verification`](../verification/SKILL.md#build-before-you-test)).

## Select models and route findings

All three families implement. Choose the model and effort per lane by fit: put the strongest available
model on subtle or correctness-heavy work. Codex's two models, Astra
(`gpt-6-astra`, the frontier model) and Sol (`gpt-6.1-sol`, the workhorse), are
both free to use and encouraged, for implementation and for review-and-fix
passes; pass `--model` and `--effort` on every launch
([`review-passes`](../review-passes/SKILL.md#before-launching)). Use Codex
slots both to implement and to review Claude authors' changes
([spend verification](#spend-verification-not-writing)).
Delegating to subagents needs no one's permission. A follow-on wave much larger
than the last (dozens of agents, or a second full fan-out) is a spend decision:
state its plan and cost to the owner in one sentence before launching it.

### The Claude roster

When the owner pairs with the lead, the lead stays free for that conversation
and subagents do the work: implementation, investigation, verification runs and
reviews. The lead writes briefs, inspects results, merges and reports. The five
project agents under `.claude/agents/` use the four Claude models the owner
allows, in order of rising cost. Each agent's description says what it takes
and what it does not, and the lead routes by those descriptions; the table
holds only what they leave out:

| Agent | Model | Effort | Worktree | Preloads |
|---|---|---|---|---|
| `scout` | Haiku 5.5 | medium | none | `content-search`, `symbol-analysis` |
| `implementer` | Sonnet 5.5 | high | own | `delegate` |
| `reviewer` | Opus 5.5 | high | none | `delegate`, `review-passes`, `content-search`, `symbol-analysis` |
| `specialist` | Opus 5.5 | high | own | `delegate` |
| `principal` | Fable 5.1 | high | own | `delegate` |

Route each brief to the role that fits it, and say why in the brief: a brief
with a known cause or a specified result goes to `implementer` even when it is
large, since length alone is no reason for a stronger role. Escalate when
quality or progress calls for it (AGENTS.md: a stronger model is used wherever
it pays for itself): a stalled lane moves to a stronger role or the other
family, never to a third attempt on the same footing. `principal` (Fable, the
most capable model) takes the work where its judgement changes the outcome:
designs, architectures and design reviews; breaking a spiral
([no bad loops](#no-bad-loops)); and a hard diagnosis or an invariant-heavy
implementation whose brief says why Fable fits. Codex is part of the same
lineup: Sol is a peer of `implementer` and takes the same briefs, and Astra,
the frontier model, takes demanding implementation, diagnosis and review, the
work of `specialist`. Spread implementer briefs across
`implementer` and Sol, so both families' slots stay busy. The owner's
direction: Codex gets specialist work whenever a Codex slot is free (a
fresh-eyes area fix pass over another family's recent work, a diagnosis, an
invariant-heavy implementation), not only the leftovers, and a feature that
needs visual reference gets a Codex art lane beside it, its pack under
`docs/game/art/` written before or while the feature lane builds. Concept art and
other image generation go to Codex as the primary artist and judge (the
owner's ruling): its built-in `image_generation` tool makes the images and its
verdicts decide which to keep, and a Claude partner writes the brief and
checks verifiable constraints, overruling only a hard-constraint violation.
Every panel passes the written audit in
[concept art review](../../../docs/game/art/README.md#concept-art-review), and
the lead reviews every panel before merging.

Pass `effort` on the Agent call to move one lane up or down: `xhigh` for long
agentic coding on a hard lane, `max` only when correctness outweighs cost.
Opus 5.5 and Haiku 5.5 run at `medium` when nothing sets an effort, which is
why each agent file names one. Pass `model` only to put a role on another
model.

Writers run in their own worktree (`isolation: worktree`), branched from the
lead's HEAD (`worktree.baseRef` in `.claude/settings.json`), so their edits
never touch the checkout the owner is pairing in. A Claude agent writes only
inside its own worktree: the harness refuses its writes to another one. To
continue a branch another lane holds (a Codex branch, say), brief the agent to
fast-forward its own branch to that branch first, then land its branch. Every agent reaches the live
World through the session's `puck` MCP server. That World runs the main
checkout's build and sources, so a lane sees its own edits running only after
the lead merges and reloads, or in a World of its own, named by `world` on
every call. A brief tells such a lane to start that World with
`puck_world_start` (`checkout`: the worktree's absolute path; it boots the build
of the worktree's current sources, building it first when no run has) and to end it with `puck_world_stop`, never with a hand-written
boot line or a PID kill; the tool passes `--devices none`, a state directory
of its own and `--control`, and a `windowed: true` start, the GPU grant's leg,
holds the GPU lease until the World exits. Reads and captures of the live World are always
allowed; a brief names the live mutations a lane may make. The machine belongs
to the agents (the owner's ruling): the lead boots a World for a check and stops
it when the check ends, and it leaves one running for the owner only when the
owner asks to play or pair in it.

Codex lanes reach the same World: the repository's `.codex/config.toml` starts
the same `puck` MCP launcher, so a companion `task` run in this checkout or a
worktree of it can call `puck_worlds` and the rest.

| Codex model | Id | Role | Effort |
|---|---|---|---|
| Astra | `gpt-6-astra` | The frontier model, for the most demanding work | `low` to `max`; refuses `none` |
| Sol | `gpt-6.1-sol` | The workhorse, near Astra for complex coding | `low` to `max`, default `medium`; refuses `none` and `minimal` |

The companion's `--effort` stops at `xhigh`; `max` needs `codex exec -c
model_reasoning_effort='"max"'`, which the Codex CLI applies (its header prints
the effort). Codex draws on the plan's allowance. Effort is not capability
across models: OpenAI's Codex guidance says Astra at `low` can outperform Sol at
`high`, so a lane that needs Sol at `high` or above tries Astra at `low` or
`medium` first. Raising effort never supplies a missing file, tool or
permission; fix the brief first. Astra asks more questions when input
could change the result and follows `AGENTS.md` and skills more literally, so an
Astra brief states its authority and finish line outright. The owner's own Codex Desktop thread holds a writer lock while it is
open, so the lead never resumes it with `codex exec resume`; the lead and that
thread talk through the owner, or through a file both read.

Antigravity is the third family, running Gemini 4 Argon (Google's flagship;
an Antigravity lane launches as above). It
is strong at repository issue fixing, app and UI generation, security
remediation and large-context analysis, and weaker on long terminal-driven and
long-horizon work. Route it accordingly:

| Give Argon | Keep from Argon |
|---|---|
| Independent review and audit passes (a third family's blind spots differ from Claude's and Codex's) | Long implementation lanes with many build/test/World steps |
| Security review of a change or an area | Lanes that need network or GPU legs from inside its sandbox |
| Portal, dashboard and other TypeScript/UI work | Anything that must push or mutate shared Git state |
| Research synthesis, as one voice beside a family that opens its sources | Finding where time or memory goes: a cost claim read from code without a measurement |
| | A code-reading pass whose claims nobody will measure before acting |

A produce-path read it wrote ranked ten per-frame costs from the code; a
measuring lane confirmed most of the code it quoted and found every cost wrong
(none above 3.6% of the frame's samples), while the real hot spot, which no
reading named, took 58%. Its value there was zero beside a trace; give a cost
question to a lane that measures.

An `agy -p` lane cannot open web pages directly. Its search tool returns
summaries, and in one research round it labelled those summaries "Opened URL"
until a cross-family consensus round made it retract them. Treat its citations,
figures and quotes as leads for a Claude or Codex lane to open and verify.
Never take them as sources, and never let them carry a verdict alone. A
research round therefore always runs a consensus step in which other families
check its citations. The same holds for its reading of Puck's own code: in the
culling round it cited six shader and C# names that do not exist, a constant
absent from the file it named, and wrong level-of-detail facts, and built a
ranked technique on a problem Puck does not have. A Claude or Codex reader
checks each `file:line` it gives before the plan uses it.

Judge the family balance lane by lane. It is not a quota and not a reason to
reassign work already under way. Owner steering adjusts judgement; do not harden
a small direction into a quota or an always rule.

A finding too large for its pass goes into a
[work packet](../../../docs/plans/open-items.md#work-packets) in the open items
with a severity and one owner: the packet that shares its cause or area, or a
new one. The lead rules on what a review raises: a scoped fix, a recorded open
item, or a dismissal with its reason; a finding called out of scope still sits
in its packet with its owner. There is no second round.

Route reviews by where the work lives. A pushed branch can be reviewed in any
machine's Codex slot; a local-only lane needs the lead's own slots. When the
lead's slots are full, send reviews of pushed branches to partners.

## Audit the process before the owner does

The lead's job is throughput of landed, correct code. Finding a process defect
is the lead's work; one the owner has to point out is a lead failure. Measure the
thing the owner cares about, not machine load alone:

- time from a lane's last commit to its landing on the integration branch;
- implementation slots (Codex and agent) not implementing right now;
- each family's implementation and review turns in the current wave, as a
  check that routing still follows fit rather than habit (a reading, not a
  quota);
- the board's age against the newest lane change.

Read lane state with `puck lanes` ([reference](../../../docs/reference/cli.md#puck-lanesone-line-per-worktree)),
never an ad hoc scan of the worktrees: one line per worktree with its commits
ahead, changed files, last commit, newest write, log exit, GPU lease state and a
`STALL` mark, in seconds, read-only, and without walking `bin/` or `obj/`.
`puck lanes --watch 0` is the stall watcher: run it as the one background
monitor, and it prints only a lane finishing, stalling or resuming, a lease
changing, or a lane appearing or leaving. A findings-only lane's launch writes
`<worktree>/.claude/scratch/<lane>.task` holding `mode: findings-only` and
`report: <its findings path>` (a Codex log whose header names a read-only
sandbox needs none), so `puck lanes` judges it by its log and report rather
than by writes, and tells a delivered report (`reported`) from a process that
exited without one (`exited`).

In Claude, arm a recurring audit as a background monitor, which reports while the lead is
busy; a session cron fires only when the lead is idle and misses exactly the
drift it exists for. On each audit, act on what degraded in the same turn: a
ready lane waiting more than an hour for a batch, an idle slot with ready work,
a lane held for anything but the integration lane's next batch, a stalled lane
([no bad loops](#no-bad-loops)), or a stale board.

When an owner correction reveals a process rule, change the repository skill
that drives the behavior (this one, `review-passes`, `verification`) in the
same turn. Agents and the lead's own briefs read the skills; a private note
beside a skill that still says otherwise changes nothing.

## No bad loops

Every lane and the lead follow these rules, so that a failure costs one retry
and not a spiral:

- Retry an unchanged failing step (a build, test, boot, start, review or
  restart) once; a new attempt after diagnosing and fixing a cause is not a
  retry. If the unchanged step fails again, stop, record what failed and what
  was tried, and take the next item the brief or track already authorizes; a
  one-shot lane with none hands back blocked.
- Every poll has a deadline and a spacing that fits the work. A lane waits on a
  run of minutes by starting it in the background and checking every few
  minutes (or blocking on its exit), never by re-running a status script every
  few seconds: each check is a model turn that spends tokens without progress. A brief with a long run says how to wait for it.
- The lead starts each lane as its own background task, so each lane's end
  arrives as its own notification. Lanes launched together in one background
  command report only when the last one ends, so a lane that finishes first
  sits unseen.
- A design memo's one review finishes before its first build lane is briefed:
  a running Codex lane takes no messages, so findings that land mid-build
  cost a stop and a relaunch (the simulation-thread memo's review found a
  phase 1 hazard after phase 1 had started). Code reviews still run beside
  the next work.
- Stopping a Codex lane means stopping its `codex.exe` process tree and the
  builds it started; stopping the background task that launched it leaves
  them running.
- Findings never bounce between a reviewer and an author: a finding goes into
  its work packet with its owner and is not argued back and forth.
- Forward progress over review turns (the owner's ruling): the
  goal is to keep closing open items. A change gets at most one review pass,
  run beside the next work, never in front of it; its findings are fixed in the
  next batch or filed into their packet, and that fix lands on its own evidence
  without a second review round. A lane that lands is followed at once by the
  next open item, and each batch deletes the items it closed.
- Timings are owed, not skipped: while lanes keep the machine busy, every
  wall-clock before/after a lane reports is indicative, and the lead keeps a
  list of the quiet-window readings owed. When a wave lands, or at least every
  few hours, the lead stops launching, lets running builds finish, and takes
  every owed reading in one quiet window before relaunching.
- Fundamentals do not crowd out features (AGENTS.md, "Fundamentals, driven by
  features"): waves of findings feed the next wave without end, so whenever no
  feature lane is running, the next free slot goes to a debug-world feature
  chosen for the systems it stresses, not to another cleanup wave.
- The lead's audit stops a stalled lane, saves a writing lane's tree as a WIP
  commit, and hands the work to another lane. A lane is stalled when it repeats
  a failure, or when its progress stops for 30 minutes: writes for a writing
  lane; findings, job output or exit for a read-only lane (`reviewer`, `scout`,
  a findings-only Codex lane), which writes no source by design. A leased job
  still producing output is not stalled.
- When the review of a fix finds more instances of the class it fixed, the next
  fix is structural: one check that holds the invariant everywhere and a test
  over every case, not another path-by-path round. When the structural round
  still draws same-class highs, stop patching: either settle the threat model
  in a decision (pruning's three rounds were all hostile same-user
  interleavings, outside the install root's model) or have a design memo
  written and reviewed before any more code (the companion custody rework).
- Break a spiral early (AGENTS.md, "No spirals and no tunnel vision"). A lane
  or an area is in a spiral when two attempts, or about two hours of the lead's
  attention, have produced no measurable progress: no cause named, no check
  turned green, no counter moved. The lead stops it and changes something
  real, and first thinks at a higher level of abstraction (the owner's
  guidance): name the general mechanism the stuck problem is one instance of,
  and ask whether the fix belongs there instead. The approach (a `principal` lane steps back over the design and the
  record of what failed, and implements the new approach when its brief says
  so), the model (a stronger role or the other family), or the work (park it
  in its packet with the record and take other work). More effort on the same
  approach is not a change.
- No tunnel vision. The engine is large, so other meaningful work is always
  ready: the lead keeps several tracks moving and never lets one hard problem
  hold the machine or the lead's attention. A parked problem is picked up again
  with fresh eyes, often after other work has changed what it rests on.
- Before putting a semantics question to the owner, look for the decision it
  follows from (`docs/decisions/`, the plan's requirements). A behaviour a
  recorded contract implies is derived, written down with its derivation, and
  never asked about or labelled the owner's ruling. A question two contracts
  answer differently is the lead's to rule ([rule on collisions](#rule-on-collisions)).
  Only what the engine should do or how it should look, where no requirement
  speaks, goes to the owner. (The lead once put "a faulted Edge rule holds its
  latch" to the owner; the fault contract already implied it.)
- A defect the owner can see (a walkthrough, a sweep still, a capture) ranks
  at the top of Next and gets a lane at once, aimed at the root cause, not a
  packet line: the hall stairs' holes sat as "dark patches" in the sweep packet
  for weeks while engine work ran, and the owner found them unfixed.
- At each landing the lead checks that the top of Next has lanes on it. Review
  rounds, fix lanes and landings fill a lead's attention without filling the
  machine: once, only three review-bound lanes ran for hours while Next's top
  packets sat idle.
- After each landing, the lead runs `puck compile worlds/debug/debug.puck
  --validate` on the merged head before the next landing: two lanes that each
  fit a shared budget (the rule-work ceiling) can exceed it together, as the
  card table and a corrected influence price did.
- A Codex run that ends `Selected model is at capacity` is retried once on the
  other Codex model (Sol to Astra or back), not on the same one.
- A read-only lane makes no commit: it writes its findings to the path its
  brief names and repeats them in its hand-back, and the lead copies any
  findings out of a worktree before removing it
  ([lane work](references/lane-work.md#delegated-work)).

## Rule on collisions

When two lanes' contracts collide at a merge, bisect to the exact field, then
rule from the principle, not the convenient option. For example, a replay is a
function of its tape, and the authoritative hash covers everything that drives
play. When no written contract settles it, rule by the long-term principle in
[AGENTS.md](../../../AGENTS.md#core-rules), write the ruling into the contract's
owning document in the same change, and let the owner review it with the
delivered batch; take it to the owner first only when it is a product
trade-off. Change a test only to match the ruled behavior.

## Message partners

Cross-session messaging below is Claude-only. Codex's in-session agent messaging
is not a bridge to an independent peer session. Codex and Antigravity use a
shared lane-named file, the hand-back report and the `Brief:` trailer when no
peer-session transport is available.

Keep messages short, with a self-contained first line:

```text
[<brief id>] <started|progress|done|blocked|question>: <sentence>
```

Put large content in a lane-named file under `<worktree>/.claude/scratch/<lane>-*`, and carry its path in the message. Large content never goes on a
pushed branch: a branch carries the lane's commits, not its analyses, logs or
notes. Silence is not agreement.

A message arrives in the partner's session as a user turn, so state your role,
the ask and where to reply. Message only the lane the owner named, and never
relay through a third agent what the recipient can read itself. Work already in
flight can land after a stop order, so expect one more report. The
session-messaging tools load on demand in each session, so a brief that expects
a reply says to load them. A session cannot read its own id: ask for replies to
the session the message came from, and address a peer on another machine by its
listed name, which survives a restart where its bridge address does not.

## Route adjacent work

| Skill | Route there for |
|---|---|
| [`verification`](../verification/SKILL.md) | The landing bar, optional checks, private CLI copies and GPU execution. |
| [`review-passes`](../review-passes/SKILL.md) | Review passes in either mode, findings-only or review-and-fix: briefs, launch commands and the follow-up branch a fixing pass hands over. |
| [`boy-scout`](../boy-scout/SKILL.md) | Keeping agent-facing documents and skills in step with a change; [Writing documentation](../../../docs/development/documentation.md) owns their prose and the skill evaluation procedure. |
