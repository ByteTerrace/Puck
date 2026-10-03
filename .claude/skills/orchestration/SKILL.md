---
name: orchestration
description: Coordinates Puck's delegated sessions and subagents as the lead who lands work on the integration branch. Use when writing assignment briefs or build, test and review instructions for a Claude or Codex partner, messaging partners, deciding what each partner works on next, sequencing integration merges or batches, scheduling GPU legs and builds across machines, selecting models per lane, routing reviews and findings, ruling on a finding or on two lanes that collide, closing a review, and answering an owner's remark about how the fleet is run. verification owns gates, red legs and GPU execution; review-passes owns launching and closing cross-family review-and-fix passes; documentation owns agent-document authoring and checks.
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
filler to make a partner look busy, and meet an owner's remark that partners
look idle by checking the path, not by inventing work.

## Own tracks, not tasks

Partners own tracks and outcomes. A brief gives the outcome and the
constraints, never the method, the design or the order of the edits. A partner
picks its next work from its track or a shared ready queue and announces it. The
lead keeps cross-track conflicts, merges, owner-level calls, and scope and
order.

## Write and track assignments

[AGENTS.md](../../../AGENTS.md#delegated-work) owns what a brief holds and how
shared-file edits are sequenced; its report shape is the three headings that
[Running long tasks](../../../AGENTS.md#running-long-tasks) names. Add to every
brief:

- an id, used in the brief file name, the commit trailer and every message;
- the base commit, verified by the lead before the brief goes out.

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

The message is the notification; git is the record. Inspect the reported
changes and checks before merging, following
[`verification`](../verification/SKILL.md).

## Assemble and refresh batches

Assemble a batch on a local-only branch in a worktree under
`.claude/worktrees/<name>`, cut from the current integration head. When one
batch lands, merge the integration head into every open batch before its
qualification run ([`verification`](../verification/SKILL.md#gpu-legs) owns
the run).

A gate (a batch landing, a review slot) blocks a lane, never a partner: the
partner takes its next work from its track or the ready queue. A lane's GPU legs
run as soon as it is GPU-ready; the batch's GPU run re-confirms them and does not
wait on them.

The lead machine's local agents can build on the unpushed integration head, so a
lane that depends on a batch starts there before the batch lands; merge the
landed head into it afterwards.

Before calling a counted-work change a regression, read the history: a commit may
have deliberately changed what is counted and owed a re-record. An owed line in a
commit body is a debt. Track each one when merging, and settle it on the merged
head, under a GPU grant when it needs the GPU, before the batch lands, with the
reason in the commit that records it.

Partners cannot see local batches. When a partner designs against code that
exists only in an unpushed batch, send it those lanes' contracts and answer its
contract questions from the local code; a stale remote makes false "failing"
claims.

The audit of owed GPU legs covers the unlanded lane branches entering a batch as
well as the commits already integrated. Run a canary's pending legs before the
batch, since a bound recorded in an unlanded branch is stale by the time the
batch runs it.

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
Allocate one GPU leg per GPU at a time and keep heavy builds off its machine
while the leg runs. Use [`verification`](../verification/SKILL.md#gpu-legs) for
grants, GPU work classification and execution.

Run a load governor on any machine that hosts many agents, through
`puck host load`, which samples the machine's load and names its state:

- Size admission by the job's measured peak. On capacity, admit light work only.
  A heavy job (a solution build plus a full suite, about 7 GB at its peak) needs
  more than 14 GB free on a 32 GB machine, and a full `Puck.World.Tests` run is
  one: never run two at once.
- On pressure, admit nothing. Running agents gate their heavy steps on more than
  8 GB of free memory rather than being killed.
- On GPU idle, start the next GPU leg. A device test host counts as GPU work.
- Thresholds follow the machine class. A 32 GB, 16-thread machine has capacity
  while CPU is under 60% and free RAM over 10 GB, and is under pressure below
  4 GB free. A 16 GB, 6-thread machine has capacity while CPU is under 50% and
  free RAM over 5 GB, and is under pressure below 2 GB free or 10 GB of disk.

Every build, a Codex brief's included, passes `-nodeReuse:false`
([`verification`](../verification/SKILL.md#build-before-you-test) says why).

## Select models and route findings

Codex (Astra and Sol) and Claude (Sonnet, Opus, and Fable when genuinely needed)
both implement and both review. Choose per lane by fit: subtle or
correctness-heavy work goes to Astra or Opus; well-specified work goes to Sol or
Sonnet. [`review-passes`](../review-passes/SKILL.md#who-reviews-and-when-it-ends)
owns which family reviews and the two-round limit.

Judge the family balance lane by lane. It is not a quota and not a reason to
reassign work already under way. Owner steering adjusts judgement; do not harden
a small direction into a quota or an always rule.

The lead rules on what the second round still raises: a scoped fix, a recorded
open item, or a dismissal with its reason. Every finding gets a destination
before it is set aside: a lane, or a named deferral where scope is tracked. A
finding called out of scope without a destination is not set aside. After the
final review round the lead decides each remaining blocker as a scoped fix, sends
it to the author with the ruling, and reads the diff; there is no third round.

Route reviews by where the work lives. A pushed branch can be reviewed in any
machine's Codex slot; a local-only lane needs the lead's own slots. When the
lead's slots are full, send reviews of pushed branches to partners.

## Rule on collisions

When two lanes' contracts collide at a merge, bisect to the exact field, then
rule from the principle, not the convenient option. For example, a replay is a
function of its tape, and the authoritative hash covers everything that drives
play. Take the question to the owner when no written contract settles it, and
never weaken or delete a law to make the merge pass.

## Message partners

Keep messages short, with a self-contained first line:

```text
[<brief id>] <started|progress|done|blocked|question>: <sentence>
```

Put large content on a branch and carry its path in the message. Silence is
not agreement.

## Route adjacent work

| Skill | Route there for |
|---|---|
| [`verification`](../verification/SKILL.md) | Gate selection, private CLI copies, red legs, GPU execution and finished-lane evidence. |
| [`review-passes`](../review-passes/SKILL.md) | Review briefs, companion launch commands, two-round review scope, fix verification and landing on the author's branch. |
| [`documentation`](../documentation/SKILL.md) | Writing and checking agent-facing documents and skills. |
