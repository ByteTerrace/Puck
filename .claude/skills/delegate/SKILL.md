---
name: delegate
description: "Holds the working contract of every delegated Puck lane: grants, builds and leases, the worktree guard, setting work aside, invariants, Worlds, when a lane is done, commits and the hand-back."
---

# Working as a delegated lane

You work autonomously: the lead may be offline, and only your commits and your hand-back reach it.
Proceed on reversible work that follows from your brief; stop only for a look or product call, an
owner-only action or the brief's stopping condition. [AGENTS.md](../../../AGENTS.md#when-you-are-the-delegate)
holds the Git, push and stop-order rules every family shares.

## Start

- Re-read this worktree's AGENTS.md and the skill that owns your area: they correct stale startup
  text, and current owner and system instructions still outrank them.
- Your brief grants your task, what you may write and which World mutations you may make; nothing
  else is granted. Reuse the mechanisms it names and add no second one.
- Read [the recurring hazards](../../../docs/development/hazards.md) before writing code: most high
  findings are one of its shapes, and its analyzers (`STALE001`, `puck scan --only clones`,
  `identity-counters`, `exact-keys`) run on what you change.

## Build and run

- Build only the projects you touch (`dotnet build <project> -c Release -m:2 -nodeReuse:false`) and
  run only focused checks, scenario files by name with `puck test <file> --jobs 1`: no `Puck.slnx`
  build, full suite, `worlds/debug` sweep or GPU leg unless your brief grants it.
- Run `puck` verbs from a lane-named private copy of your worktree's built CLI, rebuilt after every
  merge of the integration tip ([verification](../verification/SKILL.md#run-verbs-from-your-own-cli-copy)).
- Heavy and GPU work go through `puck lease` as [verification](../verification/SKILL.md#what-uses-the-gpu)
  says: one `puck test` per heavy hold, and Git Bash by its full path,
  `"C:/Program Files/Git/usr/bin/bash.exe" <script>`, since a bare `bash` is WSL's and has no .NET.
  Keep working while a ticket waits.
- To see your edits running, start a World with `puck_world_start` and end it with `puck_world_stop`
  ([the live World is shared](../puck-world/SKILL.md#the-live-world-is-shared)). `windowed: true`
  holds the GPU lease: use it only on a granted GPU leg, and stop it once its captures are in, never
  keeping it open across code edits.

## Shell under the worktree guard

- Claude Code refuses a shell command it cannot prove keeps Git inside your worktree, and the check
  cannot be switched off. Use the worktree as the working directory, literal absolute paths and one
  plain command per call: no `cd` chains and no variable expansion into a command or path. Write
  files with the Write tool; a heredoc, `cd`, loop or nested shell goes into a scratch script run as
  one plain command. The Bash tool refuses `pwsh -File <script>` and some full-path shells that the
  PowerShell tool runs; run a PowerShell script from the PowerShell tool (`& pwsh -File <script>`).
- Name scratch `<worktree>/.claude/scratch/<lane>-*`. Claude removes a worktree holding only ignored
  files when the lane ends, so output that must survive goes to the path your brief names.
- Set work aside with a WIP commit or a copy to a path your brief allows, never `git checkout --` or
  `git restore` over uncommitted edits: a fault-injection round wiped a lane's own unsaved file that way.

## Evidence

- Show a change working by building what it touched and running what it changed. Work whose
  correctness rests on an invariant (a cache's invalidation, an oracle's coverage, a geometric
  assumption, a concurrency handoff) names the invariant and what enforces it (the compiler, one
  door, a derived view) in its report; "every caller remembers" is not enforcement.
- Showing a new check failing without the fix it guards (fault injection or the old code) is a
  technique for when it is cheap and telling, never a standing requirement: AGENTS.md suspends tests
  and red legs during the campaign. Commit the fix as WIP before injecting a fault.

## Finish

- Done is a state: every job you started has exited, its watcher is stopped, your Worlds are stopped,
  your leases are released and the output that must survive is preserved. A pending run is in
  progress or blocked, never done. Keep one watcher per running job, ending on success, failure or
  exit; no `sleep` timers and no polling a job that reports its own exit
  ([your harness](../orchestration/references/lane-work.md#running-long-tasks)).
- A writing lane commits on its own branch: a lowercase `area: sentence` subject, what you ran to
  show it working in the body, and on the assignment's last commit only a final line
  `Brief: <id> done` or `Brief: <id> blocked: <question>`. Never modify or push `main`.
- Commit only with `git commit` over an index you inspected. Never build a commit from plumbing
  (`git commit-tree`, `git read-tree`, or anything else that could write an empty or unrelated tree);
  the lane guard refuses both. ASP.NET's agent effort (dotnet/aspnetcore#70219) pushed five
  empty-tree retrigger commits that deleted the whole repository. An `--allow-empty` hand-back commit
  on your own tree stays allowed.
- Hand back under **Needs you** (decisions you could not make and GPU legs you need, with options and
  their cost), **Changed** (what landed and what you ran; a read-only lane names its findings file)
  and **Found** (defects, risks and follow-ups with `file:line`, and what you could not confirm).

## Route adjacent work

| Skill | Route there for |
|---|---|
| [`verification`](../verification/SKILL.md) | Leases, CLI copies, builds and GPU work in detail. |
| [`puck-world`](../puck-world/SKILL.md) | Starting, driving and stopping a World. |
| [`review-passes`](../review-passes/SKILL.md) | A review pass's mode, brief and findings. |
| [`orchestration`](../orchestration/SKILL.md) | The lead's side: briefs, launches and landing. |
