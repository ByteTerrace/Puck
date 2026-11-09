---
name: review-passes
description: "Runs a Puck review pass, findings-only or review-and-fix. Use for review briefs and scope ranges, Codex task flags (--write, --cwd), job status, findings, blockers, follow-up fix branches."
---

# Review passes

A review pass is one run by the selected reviewer that reads a lane's commits
adversarially and reports what breaks; in review-and-fix mode it also fixes
what is local and clear. This skill owns the brief that starts it, the finding
format, and the protocol that turns a pass's uncommitted fixes into a verified
follow-up branch. It does not choose the model, effort or
concurrency; the lead's brief names those. The user's current instruction
outranks this skill; a rule here that argues against a requested change is
stale and is corrected in the same change. What the owner says in a session
overrides this skill and is written into it in that session.

## When a lane gets a review

- Two kinds of pass exist
  ([orchestration](../orchestration/SKILL.md#spend-verification-not-writing)):
  - **Area fix pass**, the default: a reader from another family takes an area
    as it is now, not a lane's diff, and fixes what is local and clear. Codex
    passes over Claude-written areas, a Claude `specialist` over Codex-written
    ones, and Argon reviews either family's areas, with security,
    large-context and UI areas its strongest fit. A design memo's review can
    take a second family as an independent critic.
  - **Adversarial review** of a change, required only when the change falls in
    a review-only class of
    [hazards](../../../docs/development/hazards.md#silent-invariants). The
    brief gives the reviewer the diff and the author's claim, not the author's
    reasoning, and asks it to break the change.
- A pass runs beside the next work and never blocks a landing or pauses an
  author. Docs and world content get none.
- A pass fixes what it finds. Only a finding too large for the pass goes into
  the open items'
  [work packets](../../../docs/plans/open-items.md#work-packets), with its
  severity, one owner, its file and line and its failing case, in the packet
  that shares its cause or area or in a new one; a high finding goes to the top
  of Next. A pass's fixes are a follow-up branch of their own, never edits to
  the author's branch.
- The brief chooses one of two modes, and a pass never turns into the other:
  - **Findings-only** reads the named revisions and reports, changing no
    source, merging nothing and committing nothing: the `reviewer` agent, or a
    `codex exec` lane with `--sandbox read-only`. Use it for design and memo
    reviews, invariant reviews of a change, and whenever the owner asks for a
    read-only review. Its highs go to the lane that wrote the code or to their
    packet's owner.
  - **Review-and-fix**, the mode of every area fix pass, also fixes what is
    local and clear, in a worktree of its own: the `specialist` agent, or Codex Sol (`gpt-6.1-sol`) with write access
    (or Astra, `gpt-6-astra`, where the factors in AGENTS.md favour it). The installed Codex
    plugin's result-handling guidance not to auto-apply a review's fixes does
    not apply to this mode in this repository.
- An explicit owner request starts a review with the owner's named model and
  effort, including a same-family reviewer.
- There is no second round. The lead reads the pass's fix diff and rules on
  anything left: a scoped fix, a recorded open item, or a dismissal with its
  reason.

## Before launching

Use [verification](../verification/SKILL.md#what-uses-the-gpu) for private CLI
copies, granted GPU legs and heavy-work leases.

- A findings-only pass needs no tree of its own: it reads the revisions the
  brief names through Git's object commands. A findings-only Codex lane still
  gets a worktree the lead creates, for the fallback findings path
  [lane work](../orchestration/references/lane-work.md#delegated-work) names.
- Run a review-and-fix pass in a worktree of its own, detached at the lane's
  head, so the author's branch and worktree stay untouched and the author keeps working. Push
  nothing for the pass: its fixes are commits on that detached head, and the
  lead puts a local follow-up branch on the verified head for the next batch.
  Never point it at another lane's checkout, and never run two passes in one
  worktree. Keep the worktree under the checkout's `.claude/worktrees/<name>`.
- Never launch a `--write` pass into a tree a canary, counters or parity run
  builds from: an edit mid-run changes the source state that run reuses. Wait
  for the run to finish or give the pass a separate worktree.
- Restore and build the review worktree before the pass starts, so `obj/` and
  `bin/` exist: the companion `task --write` sandbox has no network, so the
  brief tells the pass to build with `--no-restore`. A documentation-only pass needs no build unless XML
  comments change.
- Native `codex exec` and Antigravity `agy -p` passes use the
  [launch capability table](../orchestration/SKILL.md#launch-capabilities) and
  recipes there; native writers commit their WIP on their own branch.
- Launch a findings-only Codex pass as native `codex exec --sandbox read-only`,
  which sees no `puck` World tools
  ([launch capabilities](../orchestration/SKILL.md#launch-capabilities)).
- Launch a review-and-fix Codex pass as the companion's `task --write`. The subcommand takes
  `--write`, `--model`, `--effort`, `--cwd`, `--prompt-file`, `--background`
  and `--resume-last` (with `--resume` and `--fresh`).
  Set `--cwd` to the review worktree and use the model and effort the lead
  names, passing both `--model` and `--effort` on every launch: without them
  the job runs the local Codex configuration's default model and effort.
  `--effort` takes `none`, `minimal`, `low`, `medium`, `high` or `xhigh`, but
  Astra and Sol refuse `none` (and Sol `minimal`), so pass `low` through
  `xhigh`. `max` is reachable only through `codex exec -c
  model_reasoning_effort='"max"'`.
  Pass the brief with `--prompt-file <path>`, an absolute path because it
  resolves against `--cwd`, never as command-line prompt text. `task` has no
  `--help`: `task --help` is sent to the model as a prompt.
- Wait for a background job with `status <job-id> --wait` (240 seconds by
  default, `--timeout-ms` to change it) rather than a polling loop, and never
  grep a job log for words such as "completed", which also match echoed
  commands. `--resume-last` sees only jobs the current Claude session started;
  another session continues a Codex thread with `codex exec resume
  <session-id>`. The companion `task --write` sandbox cannot write `.git` or reach the network, so
  merges, commits and restores run outside it.
- A companion pass cannot read this conversation or message a peer session, so
  the brief carries every string, decision and path it needs, and tells it to
  answer its own questions from the code and the brief. A Codex pass that ends
  on a question has done nothing: answer it in a prompt file, keeping what the
  brief and the repository rules already decided, continue the same thread
  with `task --resume-last`, and tighten later briefs so the pass settles that
  kind of question itself.
- Launch a Claude pass as a project agent (the roster is in
  [`orchestration`](../orchestration/SKILL.md#the-claude-roster)). A
  findings-only pass is `reviewer`, which runs in the lead's checkout with no
  worktree; its brief names the base and head commits and the findings path. A
  review-and-fix pass is `specialist`, which runs in a worktree of its own,
  branched from the lead's HEAD, and is refused git work in any other tree, so
  its brief names the lane's head commit and tells it to run
  `git checkout --detach <lane head>` in its own worktree before reading
  anything. It may commit WIP as `review: <lane> pass, unverified`; the lead
  then puts the follow-up branch on the verified head as in step 4 below.
- Preserve the launch's working-directory spelling for `status`, `result` and
  `cancel`: the companion keys job state by that spelling. A Git Bash `/c/...`
  launch is invisible to a PowerShell `C:/...` query, and the reverse. A job
  that looks missing under another spelling is still running: query it with
  the launch's spelling, and never relaunch it.
- Cancel from PowerShell with the launch's path form, or use
  `MSYS_NO_PATHCONV=1` under Git Bash: MSYS path conversion turns taskkill's
  `/PID` into a path and makes `cancel` fail. Then stop the companion's
  app-server broker for that working directory before removing the worktree;
  the broker can outlive the job and hold the worktree open.
- Write the brief to a lane-named file (`<worktree>/.claude/scratch/<lane>-review.md`) so the
  verifier can reuse it.
- Use lane-named scratch as [lane work](../orchestration/references/lane-work.md#delegated-work)
  describes, including its worktree-removal caveat.

## The brief

Write the parts in this order. Each is short; the hunt list is the longest.

1. **Frame.** One sentence: an adversarial findings-only or review-and-fix
   pass in the Puck repo, naming the domains the diff touches (deterministic
   fixed-point simulation; a render-graph runtime over Vulkan and Direct3D 12;
   federation authority and recipients). Add "thorough" for a large or risky
   lane.
2. **Tree.** For findings-only, the findings file's absolute path outside the
   repository and "Change nothing." For review-and-fix, the worktree's absolute
   path and branch, and "Edit only in this tree." Add "don't commit" for
   companion `task`; for a native writer or Claude pass add "commit WIP as
   `review: <lane> pass, unverified`." Name the detached tree's head; its
   commits become a follow-up branch, never commits on the author's branch.
3. **Scope.** An exact range, `git diff <base> <head>`, with the base as a
   commit when the integration branch may move, and both as commits for a
   findings-only pass, which reads them by revision. Two dots compare the two tips
   (`git diff A..B` is `git diff A B`) and three dots start at their merge base;
   neither says A is an ancestor of B, so check that with
   `git merge-base --is-ancestor A B` before calling the range the lane's own
   ([ancestry](../verification/SKILL.md#branches)). List each lane commit by commit and
   subject and say "read each message". Name exclusions exactly: a
   merge of already-reviewed work is reviewed only for its conflict
   resolutions, in the files you name.
4. **Contract.** What the change claims, in the code's own names: the types,
   members, enum members, refusal codes, sizes, caps and thresholds, the
   decisions the lead or owner already took (so the pass does not reopen
   them), and the plan paragraph that owns the work
   (`docs/plans/<plan>.md` and its step id).
5. **Hunt.** A list per area. Phrase each item as a scenario that would break
   the contract: an input class, an ordering, a boundary, a teardown. Cover the
   classes that apply:
   - lifetime: use after release, a lease or reference never retired, a
     double release, teardown and device-loss order;
   - boundaries: resize, zero, maximum, wrap, the first and last frame or tick;
   - silent results: anything that resolves to nothing, or reports success,
     where it should refuse by name;
   - cadence and history: a state reset by a refresh gap, or kept across a
     real cut;
   - determinism: a float, wall clock or ambient randomness reaching simulation state, or a port
     or generated twin diverging from its source;
   - concurrency: the frame thread against a build or capture thread, and
     dispose against in-flight work;
   - layout and synchronization: a C# and HLSL block drifting, a missing
     barrier on either backend;
   - documents and docs: a persisted shape read under a new meaning, docs
     wrong about the change.

   Add the author's own open questions, and the bug classes earlier passes
   found in the same area. Write the hunt list before reading the diff:
   checks written after reading it inherit its blind spots.
6. **Evidence gathered.** What the lane already ran and measured, so the pass
   reads instead of re-running it.
7. **Rules block,** verbatim. The repository's rules live in `AGENTS.md`,
   contributing, documentation and the owning skills, which the pass reads itself; the block restates none of them,
   and the rest of the brief adds only scope, authority, exceptions and the
   finish line:

   ```text
   Repo rules: read AGENTS.md at the worktree root, docs/development/contributing.md, docs/development/documentation.md for docs, and the skills that own the areas the diff touches. This brief adds scope, authority, exceptions and the finish line.
   ```

8. **Instructions block,** verbatim, for the brief's mode. Findings-only:

   ```text
   Hunt for problems that break the contract; landing proceeds independently of this review. Read only the revisions this brief names (git diff, git show, git log). For each problem give file:line, why it's wrong, a concrete failing scenario, a severity (high, medium or low), and whether you reproduced it or argued it from the source. Report every finding, marking the uncertain ones rather than dropping them; don't file style preferences or the suspended test requirements as defects. Change no file and no Git state, and build nothing. Write the findings to the path this brief names. End with a list, one line per finding: <id> file:line severity - summary. If you find nothing substantiated, say so plainly.
   ```

   Review-and-fix:

   ```text
   Hunt for problems that break the contract; landing proceeds independently of this review. For each give file:line, why it's wrong, a concrete failing scenario and a severity (high, medium or low). Fix it in the tree when the fix is local and clear; add a test only where running cannot show the fix and the test will outlive the next rewrite. Otherwise describe it. Compile every project you changed (dotnet build <project> -c Release -nodeReuse:false) and leave nothing that fails to compile. Don't run tests unless a finding can't be settled any other way, one heavy command at a time. Never run GPU work (no puck canary, puck parity, or Puck.World runs). Follow the Tree paragraph's launch-specific commit instruction. End with a list, one line per finding: <id> file:line severity - fixed (files) | open (why) | dismissed (why). If you find nothing, say so plainly.
   ```

   For a documentation-only review-and-fix pass, append: "Edit only Markdown, eval case files,
   and XML comments; do not add tests. Check changed Markdown links and
   anchors. Build only when XML comments change."

Do not hand the pass a `puck affected` list, do not tell it to use the Codex
CLI, and do not use the companion's `review` subcommand for a lane: it takes
no brief, so it raises compatibility findings and tries to run tests.

## Running a pass

These are the reviewer's own rules, whichever family runs the pass.

- Read the named revisions through Git's object commands
  (`git diff <base> <head>`, `git show <rev>:<path>`, `git log <base>..<head>`),
  never the working tree or whatever HEAD is now. A `reviewer` runs in the
  lead's checkout, where the lead keeps merging, so its working tree holds
  batches that landed after the change under review. A code review whose brief
  names no revisions is blocked: say so and stop, rather than guessing the
  range from a branch's log.
- A findings-only pass changes no tracked file and no Git state (no commit,
  checkout, switch, restore, reset, stash, merge or clean) and builds nothing.
  It runs `puck` search verbs from the CLI build its brief names, or reports
  the evidence it could not gather. The read-only hook sees only shell
  commands, so the rest of this boundary is the reviewer's to keep. Reads and
  captures of the live World are allowed; scripted input and every other
  mutation are not. A finding that needs a build or a World to settle is
  reported as argued from the source, with what would settle it, and the lead
  routes it. A defect the pass could fix is reported, not fixed.
- Load the skill that owns each area the diff touches, and read the code
  around every changed site and its callers, not the author's summary of it.
- Hunt each [recurring hazard](../../../docs/development/hazards.md) by name in
  the diff: an invariant kept by convention, a result a caller can drop, a
  reference captured across a replacement, two copies of one computation,
  recorded versus derived, one local fault poisoning a global query, a cache
  that cannot help, and unbounded retention. Where a `puck scan` analyzer
  covers one, run it over the touched paths. A finding of a new shape is also
  a proposal for that page.
- Write the findings to the path the brief names, outside the repository.
  Without the Write tool, use PowerShell
  `Set-Content -LiteralPath <path> -Value @'...'@` (a single-quoted here-string
  expands nothing). The hand-back repeats them in short and never replaces the
  file, because a read-only lane makes no commit and its hand-back alone can be
  lost.
- A review-and-fix pass reports every finding with its severity and
  `file:line`, fixes what is local and clear in its own tree, and leaves the
  rest open in its end list, as its instructions block says.

## After the pass

1. **Read the result.** Check every finding against the current files. A
   finding is evidence, not a verdict. Read the fix diff's deleted lines, not
   only its net size, and check a fact by reading the file, never by searching
   for your own phrasing of it. A claim that a file is missing is checked in
   the author's branch, not only in the pass's tree, which may be detached at
   an older head. Dismiss a compatibility finding under `AGENTS.md` rule 5 once
   nothing checked in uses the old shape, and remove any alias or shim the pass
   added for it. A pass's
   build, test and format claims count only when its own environment could
   run them: a sandbox that could not reach the SDK proved nothing, so step 3
   builds and runs the fixes outside it.
   Record the fix range before continuing. A blocker the pass left open goes
   to the lead before anyone repairs it; the lead rules on each one (a scoped
   fix, a recorded open item, or a dismissal with its reason) and places it in
   its work packet with its severity and one owner. A scoped fix the lead directs is
   verified in step 3, not reviewed again. A non-blocking
   finding is reported, and its fix, if any, is verified the same way; neither
   ever opens another review, and findings never go back and forth between the
   reviewer and the author. A findings-only pass, or a
   pass that changes nothing, has no follow-up branch: copy its findings file
   out of any worktree before that worktree is removed, skip steps 2 to 5, place
   its findings in their work packets with their severity and owners, and end.
   Never create an empty WIP.
2. **Commit the pass as a WIP.** Stage the review worktree's changes
   explicitly and commit them unbuilt in that worktree
   (`review: <lane> pass, unverified`), so the output is never lost and its
   diff is one unit. Keep every commit that may be rewritten local, including
   verification repairs; never push them before step 5.
3. **Verify the fixes.** Brief one agent, in the review worktree, to build
   each touched project with zero warnings, show each fix working by running
   or measuring it, run a test only where a finding cannot be settled another
   way or the brief asks for one, and resolve or report each open finding as verified, repaired, or rejected with the
   reason. A law a fix added that still passes with the fix withheld pins
   nothing, so a passing law does not make a fix right; a rejected fix is not
   landed as written. For documentation-only fixes, run `puck docs links <changed .md
   files>` and build only for changed XML comments. The lead reads the
   verified diff; nothing here opens another pass, whether the verifier
   repairs a fix, rejects one, or finds a regression the pass introduced.
4. **Replace the WIP.** Rewrite the local WIP into commits with
   `area: sentence` subjects on the follow-up branch the lead puts at the pass's
   detached head. Rewrite only the pass's unpublished commits. The author's
   branch is never reset, rebased or edited, and it need not have stood still: if
   the author has moved on, the integration lane resolves the overlap when it
   merges both. Each message names the findings fixed and how each fix was shown
   working, or the documentation checks for doc-only fixes. No
   `Co-Authored-By` trailer.
5. **Hand over.** Only a pass that changed files reaches this step. Merge the
   integration branch's current tip into the follow-up
   branch and rebuild what it touched. It is then an ordinary ready lane for the
   integration lane's next batch ([land in
   batches](../orchestration/SKILL.md#land-in-batches)); a high finding it fixes
   or leaves open is scheduled into that batch. A local lane never pushes; a
   separately authorized remote partner follows AGENTS.md's explicit-refspec
   rule. Only the lead merges into the integration branch the lead's brief
   names.

## Route adjacent work

| Skill | Route there for |
|---|---|
| [`orchestration`](../orchestration/SKILL.md) | Choosing the reviewer's model, effort and concurrency, and coordinating assignments, machines, integration merges and finding destinations. |
| [`verification`](../verification/SKILL.md) | The landing bar, CLI copy and GPU legs the verifier uses. |
| [`rendering`](../rendering/SKILL.md) | The render-graph runtime contracts and recurring bug classes to put in a render lane's hunt list. |
| [`maths-laws`](../maths-laws/SKILL.md) | A Maths lane's laws, legs and mutation probe. |
| [`puck-world`](../puck-world/SKILL.md) | A World lane's document, authority and replay contracts. |
