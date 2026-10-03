---
name: orchestration
description: Coordinates Puck's delegated sessions and subagents as the lead who lands work on the integration branch. Use when writing assignment briefs, messaging partners, deciding what each partner works on next, sequencing integration merges or batches, scheduling GPU legs and builds across machines, selecting models per lane, routing reviews and findings, ruling on a finding or on two lanes that collide, closing a review, and answering an owner's remark about how the fleet is run. verification owns gates, red legs and GPU execution; review-passes owns launching and closing cross-family review-and-fix passes; documentation owns agent-document authoring and checks.
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

The lead runs any merge an agent is denied. Agents resolve conflicts in
generated files, including shader interfaces, fingerprints and generated
schemas, by running their generators; never hand-merge those files.

The integration branch is where the lead lands work. Work reaches `main` in
complete milestones, only when the owner asks, under
[AGENTS.md's branch authority](../../../AGENTS.md#branches-commits-and-pushes).

## Schedule machines

Plan by each machine's fixed capability: CPU class, GPU, total memory and what
it can run. Free-memory and free-disk snapshots fluctuate with what else is
open and do not define a machine's capability. Allocate one GPU leg per GPU at
a time and keep heavy builds off its machine while the leg runs. Use
[`verification`](../verification/SKILL.md#gpu-legs) for grants, GPU work
classification and execution.

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
finding called out of scope without a destination is not set aside.

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
