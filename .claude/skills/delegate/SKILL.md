---
name: delegate
description: "Holds the working contract of every delegated Puck lane: grants, builds and leases, the worktree guard, setting work aside, invariants, Worlds, when a lane is done, commits and the hand-back."
---

# Working as a delegated lane

You work autonomously: the lead may be offline, and only your commits and your hand-back reach it.
Proceed on reversible work that follows from your brief; stop only for a product call (what the engine
should do), an owner-only action or the brief's stopping condition. A look value is yours and the
lead's to decide, never a reason to stop (AGENTS.md, Authors rule). [AGENTS.md](../../../AGENTS.md#when-you-are-the-delegate)
holds the rules every family shares, and this skill does not repeat them: Git, pushing and stop
orders, verifying a change by building and running it, and fixing a defect at its named cause
(rule 7 and "the proper fix").

## Start

- Re-read this worktree's AGENTS.md and the skill that owns your area: they correct stale startup
  text, and current owner and system instructions still outrank them.
- Your brief grants your task, what you may write and which World mutations you may make; nothing
  else is granted. Reuse the mechanisms it names.
- Read [the recurring hazards](../../../docs/development/hazards.md) before writing code: most high
  findings are one of its shapes, and its analyzers (`STALE001`, `puck scan --only clones`,
  `identity-counters`, `exact-keys`) run on what you change.
- Before writing any helper, type or method that implements a general technique, search for the
  technique as well as the name (AGENTS.md, vigilance against duplication): the formula, a constant,
  the BCL call it rests on, and the shared home first (`Puck.Maths` for numerics, hashing and
  comparers). The bake-collapse lane searched for "divisor", found none, and wrote a private
  reciprocal divisor that `PrimeExtensions` already carried privately under another name; a search
  for `ulong.MaxValue /` or `Math.BigMul` now finds their shared `ReciprocalDivisor32`. A general technique
  you do write goes in its shared home, public, not `private` beside your caller.

## Build and run

- Build only the projects you touch (`dotnet build <project> -c Release -m:2 -nodeReuse:false`) and
  boot the debug world once. Beyond that, run only the focused checks your brief asks for, scenario
  files by name with `puck test <file> --jobs 1`: no `Puck.slnx` build, full suite, `worlds/debug`
  sweep, benchmark route or GPU leg unless your brief grants it.
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
  Keep the source fixed while a start is pending, including its build-lease wait: a source change
  makes the isolated build discard itself. Finish edits and generated catalogs before starting it.
- Your finish line is headless unless your brief says seeing or measuring a windowed World is the
  work itself (a look you iterate on, a GPU or memory attribution). The GPU, the machine's memory
  and quiet time are shared and scheduled by the lead: a windowed check that only confirms your change
  goes in your hand-back as a **verification request** (the World, the view or stop, the commands, the
  settle state, what you expect to see and what decides it), and the lead runs it in a batched
  window. Use the shared mesh store: pass a fresh `--cache-dir` (a cold bake, about a minute at full
  CPU and up to 18 GB) only when your brief asks for one.

## Shell under the worktree guard

- Claude Code refuses a shell command it cannot prove keeps Git inside your worktree, and the check
  cannot be switched off. Use the worktree as the working directory, literal absolute paths and one
  plain command per call: no `cd` chains and no variable expansion into a command or path. Write
  files with the Write tool; a heredoc, `cd`, loop or nested shell goes into a scratch script run as
  one plain command. The Bash tool refuses `pwsh -File <script>` and some full-path shells that the
  PowerShell tool runs; run a PowerShell script from the PowerShell tool (`& pwsh -File <script>`).
- Name scratch `<worktree>/.claude/scratch/<lane>-*`. Claude removes a worktree holding only ignored
  files when the lane ends, and your Write tool refuses a path outside your worktree, so scratch does
  not survive you. A document your brief asks for (findings, a critique, a draft) goes in full in
  your hand-back, and a document meant for the tree (a plan, a memo) is committed on your branch.
  A Codex or Antigravity lane writes to the path its brief names.
- Before anything takes uncommitted work out of the tree (showing a check fail on the old code,
  trying another approach), commit it as WIP or copy it to a path your brief allows, then bring the
  old code back from a commit. Never let `git checkout --`, `git restore` or a script that runs on
  past a failed copy be what removes uncommitted edits: a fault-injection round wiped a lane's own
  unsaved file that way.

## Evidence

- Paste, never retype. A code quote in a report, review or plan is copied from
  the Read or search output that showed it, with the `file:line` that output
  printed; a figure names the command or capture that produced it. A packet's or
  brief's `file:line` is a lead that has often drifted: open it before relying on
  it, and say so when it moved. A claim you reason toward but did not see is
  labelled as an inference. Run `puck docs links` on what you write; its checks
  verify locations and introduced fenced quotes, not whether a quote supports the surrounding claim.

- Before writing code whose correctness rests on an invariant (a cache's invalidation, an oracle's
  coverage, a geometric assumption, a concurrency handoff, a guard every read or write must pass),
  write down the invariant and the one place that enforces it (the compiler, a type, one door, a
  derived view), build behind it, and name both in the commit body and the hand-back. Enforcement
  means a new caller cannot get around it. When the same guard is needed in several places, make the
  unguarded path unreachable (narrow its visibility, or hand callers a type only the guarded path
  produces) instead of copying the guard or adding a helper every caller must remember to call. If
  nothing enforces the invariant yet, say so plainly rather than implying something does.
- A design lane (a memo, an architecture, a design review) delivers a document. The document names
  its invariant, what enforces it and where that guarantee stops; it answers each finding its review
  raised, one by one; and it gives every implementation step a mechanical check that shows the step
  worked. A question a bounded prototype or measurement settles, the design lane settles itself and
  records in the memo (what ran, where, what it showed and what it decided); a question that needs
  more than that, or a GPU leg the lane has no grant for, becomes a step for another lane, named with
  what it would show and what it decides. The memo never leaves an open question unowned.

## Finish

- A new refusal of something an author, player or addon could write before (a validator error, an
  admission refusal, a door that now answers "no") removes a capability, even when it closes a real
  defect (AGENTS.md "Unreached is not unwanted"). Fix the cause so the capability keeps working; if
  that is out of your scope, land the refusal only with an open item for the cause fix, and name it
  first under **Needs you** with the author or player case it blocks. Two lanes in one batch closed
  defects this way (non-operator row declaration, a solid creation's `sizeBy`), each noted only in
  passing, and the owner had to ask.
- Done is a state: every job you started has exited, its watcher is stopped, your Worlds are stopped,
  your leases are released and the output that must survive is preserved. A pending run is in
  progress or blocked, never done. Every hand-back reaches this state, a blocked one and one a stop
  order forces included: stopping means your own jobs, watchers and Worlds stop too, not that they
  are left running for the lead. Keep one watcher per running job, ending on success, failure or
  exit; no `sleep` timers and no polling a job that reports its own exit
  ([your harness](../orchestration/references/lane-work.md#running-long-tasks)). A foreground wait
  loop (`until grep …; do sleep …; done`) that outlives the tool's timeout is not killed with it: its
  shell is orphaned and keeps polling, and rerunning the wait adds another copy every ten minutes.
  Wait in the background with one notification, or bound a foreground loop below the tool's timeout
  (`timeout 590 bash -c 'until …'`). One waiter per condition: never start a wait on a file or job
  while an earlier wait on it is still running (three bounded copies on one log is still three
  copies); a background wait notifies once, so take the next step on its notification instead of
  arming another. Prefer a bounded foreground wait for a run under about ten minutes; it leaves
  nothing behind. No agent can list its own background tasks, so when the lead resumes you for a
  next step, first stop every background task and monitor your previous step started.
- Evidence is load-independent ([the phases](../orchestration/SKILL.md#spend-verification-not-writing)):
  counters, hashes, bytes, executes, laws, captures. Report no timing as a result. A timing
  question goes in your hand-back as a measurement request (frozen before and after builds, the
  command, what decides it) for the lead's quiet window. Where your brief asks for a check beyond
  the build and the boot, use filtered laws and a reproduce of only the scenarios the step reaches;
  the lead runs the full suites once per batch.
- A build takes no lease beyond the build lease a `puck` verb takes itself: `puck lease heavy` is
  for the memory-heavy test assemblies and reproduce replicas, and a build queued behind them waits
  for nothing. The World build store builds in your checkout's own `bin`/`obj`, and every build a
  `puck` verb starts there queues on the checkout's `obj/puck-build.lock`; a hand `dotnet build`
  does not, so never run one while a `puck test`, `puck canary` or `puck_world_start` of the same
  checkout is building, or run it as `puck lease build -- dotnet build …`. Two builds writing one
  checkout at once leave a half-written reference assembly (`CS0009`).
- Code first (AGENTS.md, "Code first, verification after"). Write the change properly, build what
  you touched, boot the debug world once, and hand back. Windowed runs, benchmarks, look critics and
  broad testing are the lead's batched passes after landing, not your gate: ask for them under
  **Needs you** as a verification request instead of running them, unless your brief makes one your
  finish line. Never re-run the same check to gain confidence.
- Code first defers verification, never code quality. Writing the change properly includes the
  search before you build a mechanism (Start, and AGENTS.md rule 7) and fixing the duplicates you
  read or write in the same change (AGENTS.md, vigilance against duplication); one too large for
  your change goes into the refactoring packet with its file and line, and under **Found**. The two
  checks below that only read your tree, `puck scan --only duplicates` and `puck docs links`, need
  no GPU and no windowed run, so no brief or deadline makes them optional.
- Once your finish line holds on the merged head, hand back. Start no review or hardening round of
  your own (a self-review, extra guards, fuzzing beyond the brief): the lead's area fix passes and any
  invariant review run beside your next work, and what else you noticed goes under **Found**.
- Take each piece of evidence you report once, on the final content: merge the integration tip
  last, then build, boot and run the two scans below and any check your brief names, all before your
  final commit, so no later merge of yours can bring in a duplicate or a broken link unseen. If you
  merge again after the `Brief:` commit (the lead resumes you after the tip moved), repeat them on
  the new merged head and report that; a failure the merge brought in goes under **Needs you** with
  its cause rather than into a second `Brief:` commit. Merge the tip once for that final evidence,
  never again because it moved while your checks ran: during a wave the lead lands lanes often, a
  tip that moves after your merge is the lead's to merge, and a lane that chases it rebuilds for
  nothing (one lane merged the tip eight times in its last hour, each followed by a five-minute
  World build).
- Before your final commit, run `puck scan --only duplicates --against <base>` from your CLI copy
  (`<base>` is the integration tip your brief names; the scan reads the working tree, uncommitted
  files included) and read every `neighbour` and `helper` record
  ([the check](../../../docs/reference/cli.md#the-branch-duplicate-check)). It lists what your branch
  declares new or changes with the declarations elsewhere that carry the same technique under another
  name. Under **Systems considered**, give each record that names a real overlap, and each
  general-looking new declaration the scan could not see (a resolver, a CPU twin of a GPU kernel), the
  searches you ran (the commands and what they returned, pasted) and why no existing mechanism owns it.
  A duplicate you find while doing so is fixed before you hand back.
- Before your final commit, run `puck docs links` from your CLI copy. Fix every failure your change
  introduced (a renamed symbol, a moved file, a closed plan item), in whichever file it shows up,
  including pages you never edited. Only a failure that was already there before your change goes in
  your hand-back.
- A writing lane commits on its own branch: a lowercase `area: sentence` subject, what you ran to
  show it working in the body, and on the assignment's last commit only a final line
  `Brief: <id> done` or `Brief: <id> blocked: <question>`. Exactly one commit carries it: merges of
  the integration tip may follow that commit, so the re-check after such a merge (above) adds no
  second `Brief:` commit (`puck landing handback` refuses two).
- Commit only with `git commit` over an index you inspected. Entries staged by something other than
  you stay staged and uncommitted: name your own paths to `git commit` rather than unstaging,
  resetting or committing theirs. Never build a commit from plumbing (`git commit-tree`,
  `git read-tree`, or anything else that could write an empty or unrelated tree); the lane guard
  refuses both. ASP.NET's agent effort (dotnet/aspnetcore#70219) pushed five
  empty-tree retrigger commits that deleted the whole repository. An `--allow-empty` hand-back commit
  on your own tree stays allowed.
- Hand back under **Needs you** (decisions you could not make, and verification requests and GPU
  legs you need, with options and their cost), **Changed** (what landed and what you ran; a read-only lane gives its findings in full, or names the findings file its brief gave it)
  and **Found** (defects, risks and follow-ups with `file:line`, and what you could not confirm).
  Say what you mean in literal terms, and note in a line where you changed direction from the brief.

## Route adjacent work

| Skill | Route there for |
|---|---|
| [`verification`](../verification/SKILL.md) | Leases, CLI copies, builds and GPU work in detail. |
| [`puck-world`](../puck-world/SKILL.md) | Starting, driving and stopping a World. |
| [`review-passes`](../review-passes/SKILL.md) | A review pass's mode, brief and findings. |
| [`orchestration`](../orchestration/SKILL.md) | The lead's side: briefs, launches and landing. |
