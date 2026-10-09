---
name: orchestration
description: Coordinates Puck's delegated sessions and subagents as the lead who lands work on the integration branch. Use when writing assignment briefs or build, test and review instructions for a Claude or Codex partner, messaging partners, deciding what each partner works on next, sequencing per-lane integration landings, scheduling GPU legs and builds across machines, selecting models per lane, routing reviews and findings, ruling on a finding or on two lanes that collide, closing a review, auditing the lead's own throughput, and answering an owner's remark about how the fleet is run. verification owns gates, red legs and GPU execution; review-passes owns launching and closing cross-family review-and-fix passes; documentation owns agent-document authoring and checks.
---

# Orchestration

This skill is the lead's procedure for coordinating assignments, machines,
merges and reviews. [AGENTS.md](../../../AGENTS.md) owns branch authority and
repository rules; this skill routes verification and review execution to their
owners. The user's current instruction outranks this skill; a rule that argues
against a requested change is stale and is corrected in the same change.

## Plan from the milestone

Plan backward from the milestone's critical path. Find the least-prepared item
on it and put the best-fit partner there. Busy is not progress: never queue
filler to make a partner look busy. Idleness is a prompt to re-read the path, not
to create work.

## Own tracks, not tasks

Partners own tracks and outcomes. A brief gives the outcome and the
constraints, never the method, the design or the order of the edits. A partner
picks its next work from its track or a shared ready queue and announces it. The
lead keeps cross-track conflicts, merges, owner-level calls, and scope and
order. Every brief ends with the partner's own next one or two items, and the
partner works its queue without waiting for the lead between items. A queue the
partners cannot read is no queue: keep the lead's queue where partners can reach
it, or delegate it to them. Every queue item has exactly one recorded owner, and
a handoff records the new one; an item with two owners is a defect to resolve
before either partner continues.

## Write and track assignments

[AGENTS.md](../../../AGENTS.md#delegated-work) owns what a brief holds and how
shared-file edits are sequenced; its report shape is the three headings that
[Running long tasks](../../../AGENTS.md#running-long-tasks) names. Add to every
brief:

- an id, used in the brief file name, the commit trailer and every message;
- the base commit, verified by the lead before the brief goes out.

Verify each premise of a brief yourself before sending it; a wrong premise costs
every lane that received it. When a brief proves wrong, recall every lane working
from the same premise before replying to the lane that found it, and say that a
stop is a stop, not a pause.

Verify any base claim yourself with
`git merge-base --is-ancestor <base> <integration-head>`, checking its exit
status, and count the commits behind with
`git rev-list --count <base>..<integration-head>`. A two-dot range counts the
commits reachable from the right side and not the left; an empty range does not
establish ancestry.

The assignment's last commit carries one of these trailers:

```text
Brief: <id> done
Brief: <id> blocked: <question>
```

The lead's ledger and plan files that a delegate never writes are the lead's
own scratch checklists in the session scratchpad. The repository's documents,
`docs/plans/open-items.md` and the plans under `docs/plans` among them, belong
to no one lane: a delegate edits them for what its change delivers, ticking an
item it closed and correcting text its change made stale, and the brief says so.

The message is the notification; git is the record. Inspect the reported
changes and checks before merging, following
[`verification`](../verification/SKILL.md). At each merge, turn every GPU leg
or re-record owed in a commit body into a tracked item: the commit records a
debt, but assigns it to no lane. Carry each item into the lane's own GPU run and
close it only with that run's evidence.

## Follow the owner's qualification scope

An owner taking over a prepared campaign may require one combined qualification
on one machine. In that case, integrate the named branches serially in a local
candidate, keep the integration target at its last green head, and use focused
checks while resolving source overlaps. Run the full CPU gate and the owner's
single GPU qualification on the stable combined head before fast-forwarding the
target. One heavy build or gate owns the machine at a time. A failure is fixed
and rechecked with the smallest affected selection; repeating the full
qualification requires a concrete reason. The per-lane route below applies
when the owner has not supplied that combined scope.

## Land each lane on its own

A lane lands as soon as its own head passes: the lead merges the integration
head into it, runs `puck gate --merge-base <integration-branch> --gpu` on that
head, and fast-forwards the integration branch. Qualification is
measured in minutes. Never hold a finished lane for others to make a batch, and
never assemble a multi-lane qualification branch; a lane that cannot land alone
is a dependency to name in the brief, not a reason to wait.

Spread qualification across the fleet. A partner merges the integration head
into its own branch and runs `puck gate` without `--gpu` on its own machine
before it pushes, naming the verdict in its push; the lead then adds only the
GPU legs that need its devices and the fast-forward. The lead's machine
qualifies its own local lanes and the GPU legs, not every partner's CPU suites.

A gate blocks a lane, never a partner: the partner takes its next work from its
track or the ready queue.

The lead machine's local agents can build on an unpushed lane head, so a lane
that depends on another starts there before it lands; merge the landed head into
it afterwards.

Before calling a counted-work change a regression, read the history: a commit may
have deliberately changed what is counted and owed a re-record. An owed line in a
commit body is a debt. Track each one when merging, and settle it on the lane's
merged head, under a GPU grant when it needs the GPU, before it lands, with the
reason in the commit that records it. A counters ceiling the lane moves is
re-recorded on every device the ledger holds, each on its own machine
([`verification`](../verification/SKILL.md#gpu-legs)).

Partners cannot see unpushed lanes. When a partner designs against code that
exists only on the lead's machine, send it those lanes' contracts and answer its
contract questions from the local code; a stale remote makes false "failing"
claims.

When a lane moves counted work, run every counters workload under
`tests/Puck.Counters` it reaches, not just the default. Inspect the directory
before naming the inputs: the worlds, scripts and ceilings sit directly in it.
Read each ledger's workload and script paths to select the matching inputs. Run
each set as
`puck counters --check --world <world> --script <script> --ceilings <file>`.

The lead runs any merge an agent is denied. Agents resolve conflicts in
generated files, including shader interfaces, fingerprints and generated
schemas, by running their generators; never hand-merge those files.

The integration branch is where the lead lands work. Work reaches `main` in
complete milestones, only when the owner asks, under
[AGENTS.md's branch authority](../../../AGENTS.md#branches-commits-and-pushes).

## Schedule machines

Allocate by each machine's fixed capability (CPU class, GPU, RAM, OS, network),
mapped once and checked every turn. Free-memory and free-disk snapshots
fluctuate with what else is open and do not define a machine's capability.
Allocate one GPU leg per GPU at a time, and let CPU work, builds included, run
beside it: a correctness leg's verdict does not depend on load, and a leg that
times out under load is re-run once alone and reported as a flake. Only
wall-clock work needs a quiet machine. Use
[`verification`](../verification/SKILL.md#gpu-legs) for grants, GPU work
classification and execution.

Run a load governor on any machine that hosts many agents, through
`puck host load`, which samples the machine's load and names its state. Under a
watcher, `puck host load --watch` prints one line per transition (`CAPACITY`,
`LOADED` when capacity ends without pressure, `PRESSURE`, `GPU busy` and
`GPU idle`), so each change arrives once:

- Size admission by the job's measured peak. On capacity, admit light work only.
  A heavy job (a solution build plus a full suite, about 7 GB at its peak) needs
  more than 14 GB free on a 32 GB machine, and a full `Puck.World.Tests` or
  `Puck.World.Presentation.Tests` run is one: never run two at once.
- On pressure, admit nothing. Running agents gate their heavy steps on more than
  8 GB of free memory rather than being killed.
- On GPU idle, start the next GPU leg. A device test host counts as GPU work.
- Thresholds follow the machine class. A 32 GB, 16-thread machine has capacity
  while CPU is under 60% and free RAM over 10 GB, and is under pressure below
  4 GB free. A 16 GB, 6-thread machine has capacity while CPU is under 50% and
  free RAM over 5 GB, and is under pressure below 2 GB free or 10 GB of disk.

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

Every build, a Codex brief's included, passes `-nodeReuse:false`
([`verification`](../verification/SKILL.md#build-before-you-test) says why).

## Select models and route findings

Codex and Claude both implement, and no model in either family is restricted or
reserved. Choose the model and effort per lane by fit: put the strongest available
model on subtle or correctness-heavy work. Codex's two models, Astra
(`gpt-6-astra`, the frontier model) and Sol (`gpt-6-sol`, the workhorse), are
both free to use and encouraged, for implementation and for review-and-fix
passes; pass `--model` and `--effort` on every launch
([`review-passes`](../review-passes/SKILL.md#before-launching)). Keep every
Codex slot implementing; a review takes a slot only under the narrow rule in
[`review-passes`](../review-passes/SKILL.md#when-a-lane-gets-a-review).
Delegating to subagents needs no one's permission. A follow-on wave much larger
than the last (dozens of agents, or a second full fan-out) is a spend decision:
state its plan and cost to the owner in one sentence before launching it.

Judge the family balance lane by lane. It is not a quota and not a reason to
reassign work already under way. Owner steering adjusts judgement; do not harden
a small direction into a quota or an always rule.

The lead rules on what a review raises: a scoped fix, a recorded open item, or a
dismissal with its reason. Every finding gets a destination before it is set
aside: a lane, or a named deferral where scope is tracked. A finding called out
of scope without a destination is not set aside. There is no second round.

Route reviews by where the work lives. A pushed branch can be reviewed in any
machine's Codex slot; a local-only lane needs the lead's own slots. When the
lead's slots are full, send reviews of pushed branches to partners.

## Audit the process before the owner does

The lead's job is throughput of landed, correct code. Finding a process defect
is the lead's work; one the owner has to point out is a lead failure. Measure the
thing the owner cares about, not machine load alone:

- time from a lane's last commit to its landing on the integration branch;
- implementation slots (Codex and agent) not implementing right now;
- review passes per landed lane, and qualification wall time per landing;
- the board's age against the newest lane change.

Arm a recurring audit as a background monitor, which reports while the lead is
busy; a session cron fires only when the lead is idle and misses exactly the
drift it exists for. On each audit, act on what degraded in the same turn: a
lane waiting more than an hour to land, an idle slot with ready work, a second
review on a lane outside determinism, formats or federation, or a stale board.

When an owner correction reveals a process rule, change the repository skill
that drives the behavior (this one, `review-passes`, `verification`) in the
same turn. Agents and the lead's own briefs read the skills; a private note
beside a skill that still says otherwise changes nothing.

## Rule on collisions

When two lanes' contracts collide at a merge, bisect to the exact field, then
rule from the principle, not the convenient option. For example, a replay is a
function of its tape, and the authoritative hash covers everything that drives
play. When no written contract settles it, rule by the long-term principle in
[AGENTS.md](../../../AGENTS.md#core-rules), write the ruling into the contract's
owning document in the same change, and let the owner review it with the
delivered batch; take it to the owner first only when it is a product
trade-off. Never weaken or delete a law to make the merge pass.

## Message partners

Keep messages short, with a self-contained first line:

```text
[<brief id>] <started|progress|done|blocked|question>: <sentence>
```

Put large content in a lane-named file in the session scratchpad or the lane's
own worktree, and carry its path in the message. Large content never goes on a
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
| [`verification`](../verification/SKILL.md) | Gate selection, private CLI copies, red legs, GPU execution and finished-lane evidence. |
| [`review-passes`](../review-passes/SKILL.md) | When a lane gets a review, review briefs, companion launch commands, fix verification and landing on the author's branch. |
| [`documentation`](../documentation/SKILL.md) | Writing and checking agent-facing documents and skills. |
