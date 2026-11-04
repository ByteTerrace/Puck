# Lane work

Procedures loaded by [orchestration](../SKILL.md) for branches, delegation and long runs.

## Branches, commits and pushes

- Preserve unrelated work in a shared checkout. Stage explicit paths, check each
  staging result, and inspect the whole index before committing; an amend takes
  the index too. Prefer a separate commit when another task may have staged
  work.
- Commit subjects and bodies follow the [`delegate` skill](../../delegate/SKILL.md#finish)
  (`vulkan: …`), the lead's own commits included.
- A delegated session has exactly one long-lived working branch, and every
  brief it receives lands there. A brief starts by merging the integration
  branch's current tip into it: merge, never rebase. A local lane never pushes.
  A ready lane (its own focused checks hold on that merged head) joins the
  integration lane's next batch, and the lead handles any push
  ([land in batches](../SKILL.md#land-in-batches)).
- Create no per-review or per-batch remote branch, and no per-lane one except
  the backup of a finished lane branch that waits to merge (`lane/<id>`,
  [land in batches](../SKILL.md#land-in-batches)), deleted once it lands. A review pass runs
  in its own worktree detached at the lane's head, and its fixes become a
  follow-up branch for the next batch, never commits on the author's branch
  ([`review-passes`](../../../../.claude/skills/review-passes/SKILL.md)). A local
  subagent's branch stays local and is never pushed.
- Worktrees live under the checkout's git-ignored `.claude/worktrees/<name>`,
  never at a drive root.
- Before every push, account for each line the push deletes. After merging the
  integration tip, every line `git diff <integration tip> HEAD` removes must be
  one you meant to remove: an unexplained deletion is someone else's work lost
  in a merge or squash, and green suites prove only the behavior they cover.
  `git diff` shows it; `puck landing loss --against <tip> --base <authoring base>`
  makes the same Git-loss check mechanically; it reads Git state only and
  boots no World. To squash, reset to the
  commit your work started from, never to a remote ref that may have moved.
- Every commit carries the checkout's configured git identity, whoever made it.
  Establish a commit's origin from `git reflog` and by diffing it against what
  the remote already holds, never from its author line.
- Pushing `main`, force-pushing, and deleting a remote branch that holds
  unmerged work are the owner's: ask first. The lead deletes a remote branch
  whose work has landed on the integration branch.
- The lead deletes a local branch only once `git cherry` shows its work landed and no
  worktree holds it.


## Delegated work

When you delegate, inventory the work first, assign each agent explicit file
ownership and its skills, and sequence edits to shared files. Each brief holds
the whole task, the integration branch, a finish line (the command that must
pass or the state that must hold), a stopping condition, and whether it grants
the GPU. A stopping condition is for a look call or an owner-only action; it
never protects a file format, replay, checkpoint, ABI or recorded hash, since
the owner rules those change freely (AGENTS.md). The integrator inspects the
combined result and runs its checks; a
worker's report is evidence to check, not a substitute. Check a reported defect
against the current files before acting on it. Delegating to subagents needs no
one's permission; choose each one's model by fit
([`orchestration`](../../../../.claude/skills/orchestration/SKILL.md#select-models-and-route-findings)).

A delegate works under the [`delegate` skill](../../delegate/SKILL.md) and
[AGENTS.md's delegate rules](../../../../AGENTS.md#when-you-are-the-delegate):
Claude's project agents preload the skill, and a Codex or Antigravity brief
names it to read first. Choose the launch path from the
[launch capability table](../SKILL.md#launch-capabilities), not from
assumptions about a model family.

A read-only brief (a review, an audit) makes no commit, so its output must
outlive the lane: the brief names a findings path outside any worktree Claude
may remove, and the lead never relies on the hand-back alone. A `codex exec`
lane's command hook sometimes refuses a write outside its worktree; one review
wrote outside it and another could not. So a Codex review brief names
`<worktree>/.claude/scratch/<lane>-findings.md` as the fallback, in a worktree
the lead creates; the lead checks both paths when the lane ends and copies the
findings out before removing the worktree. A remote partner that pushes uses an
explicit refspec (`git push origin HEAD:refs/heads/<branch>`).


## Running long tasks

When a step needs no input from the owner, keep going, and put status notes in
the message that carries the next action. Stop and ask only when the work
cannot continue without the owner, or before an owner-only action: pushing
`main`, force-pushing or deleting a remote branch that holds unmerged work (the branch rules above),
deploying to Azure, publishing a package, or changing or deleting anything
outside this repository. Refactoring, deleting inside the repository, and
changing tests, gates and CI need no one's permission.

The lead and every agent are trusted experts with free rein to refactor and
improve Puck. Decide, deliver the work in batches for review on your working
branch, and report each decision with its evidence; the owner reviews delivered
batches and raises objections there. Never ask permission because a change is
large, never settle for a cheaper patch over the structurally right fix, and
never offer a change that makes the engine healthier or faster as something to
discuss: state it as the plan and do it, delegating it if it is large. Ask the
owner only about product trade-offs (what the engine should do), taste and
visual judgement, and the owner-only actions above. Acknowledge an instruction
in a line rather than restating it.

In Claude, a command started in the background already reports when it exits. Never start
a second job that only waits for or polls another; do other work meanwhile, or
run the command in the foreground with a long timeout when the next step needs
its result. Watch each thing you need to see mid-run (failure lines,
progress) with one Monitor, never one per milestone or threshold. A bash
wait loop or "wait until" command in place of that Monitor is the same
waiter in another tool, and a second watcher on a job is waste whichever
tool runs it. Stop each Monitor once its job ends. A background `sleep N;
echo t` is not a timer either: when a turn ends with work still running, the
job's own exit notice resumes the lane. Before arming a watcher, stop any
earlier one on the same job, and give
each watcher an end condition that covers success, failure and the process
exiting, so none outlives its job.
Codex uses yielded exec sessions and `write_stdin` to collect progress and
completion; Antigravity uses its command's completion handle or a foreground
run with an adequate timeout. Neither assumes Claude's Monitor or exit notices.

For a run with many parts, keep a checklist under `<worktree>/.claude/scratch/<lane>-*`, named
for the run, tick items as they finish, and add what you find. A question the
owner has settled stays settled unless new evidence contradicts it; then say
what the evidence is.

End a run with three headings, in order: **Needs you** (decisions or approvals
waiting on the owner), **Changed** (what landed and how it was verified), and
**Found** (defects, risks, follow-ups). In research reports, mark anything you
could not confirm and say where you looked.

