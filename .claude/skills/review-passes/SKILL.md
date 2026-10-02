---
name: review-passes
description: Briefs, runs and closes a cross-family review-and-fix pass over a lane's commits, where Codex reviews Claude-written code and a Claude agent reviews Codex-written code, in at most two rounds. Covers who reviews and when the loop ends, the self-contained brief (scope by commit range with every message read, the contract summary, a hunt list per area, the repository rules block, fixes that carry a law failing without them, compiling what the pass changed, reporting laws that cannot fail, the end-list format) and the after-review protocol (commit the unbuilt pass as a local WIP, verify each fix and red leg, replace the WIP, the second round over the fix diff alone, the lead's ruling on what round 2 still raises). Use when writing a review brief, launching a review pass, receiving its result, or verifying and landing a reviewer's fixes. verification owns gates, red-leg proofs and GPU legs; the subsystem skill that owns the changed area supplies the contract and hunt classes (rendering's runtime-contracts reference for the render graph); documentation owns doc-only reviews.
---

# Review passes

A review pass is one run by the other model family that reads a lane's commits
adversarially, fixes what is local and clear, and reports the rest. This skill
owns the brief that starts it and the protocol that turns its uncommitted
output into verified commits. It does not choose the model, effort or
concurrency; the lead's brief names those. The user's current instruction
outranks this skill; a rule here that argues against a requested change is
stale and is corrected in the same change.

## Who reviews, and when it ends

- Review crosses families, with fixes enabled both ways. Codex reviews
  Claude-written code. A Claude agent (Sonnet or Opus) reviews Codex-written
  code, which includes the fixes a Codex pass made.
- Round 1 reviews the lane. Round 2 reviews round 1's fix diff alone, by the
  family that did not write those fixes. After a Codex round 1, the Claude
  agent's verification (After the pass, step 3) is round 2.
- The loop ends when a pass reports no blockers, and after round 2 at the
  latest. The lead rules on what round 2 still raises: a scoped fix, an open
  item, or a dismissal with its reason. There is never a third round.
- A non-blocking finding never extends the loop; report it.

## Before launching

- Run the pass in a worktree of the author's own working branch, at the lane's
  head: the author's worktree when it is clean, or one detached at that head,
  since git checks a branch out in one worktree only. Create and push no branch
  for the pass; its fixes land on the author's branch as commits. Never point
  it at another lane's checkout, and never run two passes in one worktree.
- Restore and build that worktree first so `obj/` and `bin/` exist. The Codex
  sandbox has no network: fetch corpora and packages before the run.
- Launch a Codex pass as the companion's `task --write` with `--cwd` set to the
  review worktree, and a Claude pass as an agent working in that worktree, each
  with the model and effort the lead names. A pass cannot read this
  conversation, message a session or ask a question, so the brief carries
  every string, decision and path it needs.
- Write the brief to a lane-named file (`<scratchpad>/rb/<lane>.md`) so the
  second review and the verifier can reuse it.

## The brief

Write the parts in this order. Each is short; the hunt list is the longest.

1. **Frame.** One sentence: an adversarial review-and-fix pass in the Puck
   repo, naming the domains the diff touches (deterministic fixed-point
   simulation; a render-graph runtime over Vulkan and Direct3D 12; federation
   authority and recipients). Add "thorough" for a large or risky lane.
2. **Tree.** The worktree's absolute path and branch, and "Edit only in this
   tree; don't commit."
3. **Scope.** An exact range, `git diff <base> <head>`, with the base as a
   commit when the integration branch may move. List each lane commit by
   commit and subject and say "read each message". Name exclusions exactly: a
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
   - determinism: a float, clock or RNG reaching simulation state, or a port
     or generated twin diverging from its source;
   - concurrency: the frame thread against a build or capture thread, and
     dispose against in-flight work;
   - layout and synchronization: a C# and HLSL block drifting, a missing
     barrier on either backend;
   - documents and docs: a persisted shape read under a new meaning, docs
     wrong about the change;
   - laws whose red legs cannot fail.

   Add the author's own open questions, and the bug classes earlier passes
   found in the same area.
6. **Evidence gathered.** The suites, counts, gates and mutation proofs the
   lane already ran, so the pass reads instead of re-running them.
7. **Rules block,** verbatim. Codex reads `AGENTS.md` from the worktree root
   on its own; the block names it and restates the rules a review pass most
   often needs in front of it:

   ```text
   Repo rules: AGENTS.md at the worktree root holds the repository's rules; follow it. In particular: no backwards compatibility, ever (never preserve old wrong behaviour; no compat aliases, shims or read-side tolerance for old shapes). No InternalsVisibleTo (make a member public instead). No environment variables. One spelling per thing. LF everywhere; never raise newline issues. Docs state current behaviour in present tense with no dates or SHAs. Determinism: no wall clock, RNG or float in simulation state. A [VerifiedCode] member that changes is re-verified, never unbranded to pass the build.
   ```

8. **Instructions block,** verbatim:

   ```text
   Hunt only for problems that would block the merge. For each give file:line, why it's wrong, and a concrete failing scenario. Fix it in the tree when the fix is local and clear, adding a law (test) that fails without the fix; otherwise describe it. Compile every project you changed (dotnet build <project> -c Release) and leave nothing that fails to compile. Don't run tests unless a finding can't be settled any other way, one heavy command at a time. Never run GPU work (no puck canary, puck parity, or Puck.World runs). Report every existing law you find that cannot fail. Don't commit. End with a list, one line per finding: <id> file:line - fixed (files; law) | open (why) | not a blocker (why). If nothing blocks, say so plainly.
   ```

Do not hand the pass a `puck affected` list, do not tell it to use the Codex
CLI, and do not use the companion's `review` subcommand for a lane: it takes
no brief, so it raises compatibility findings and tries to run tests.

## After the pass

1. **Read the result.** Check every finding against the current files. A
   finding is evidence, not a verdict. Dismiss a compatibility finding under
   `AGENTS.md` rule 5 once nothing checked in uses the old shape.
2. **Commit the pass as a WIP.** Stage the review worktree's changes
   explicitly and commit them unbuilt in that worktree
   (`review: <lane> pass, unverified`), so the output is never lost and its
   diff is one unit. The WIP stays local; never push it.
3. **Verify the fixes.** Brief one agent, in the review worktree, to: build
   each touched project, then the solution; prove every new or changed law red
   with its fix withheld and green with it applied (`verification` § Prove
   each law's red leg); run the suites the fixes reach; run the check forms on
   touched files; repair what does not compile; and resolve or report each
   open finding. The agent reports each finding as verified, repaired, or
   rejected with the reason. When Codex wrote the fixes, this agent is a Claude
   agent, and its pass is round 2: it also reviews the fix diff for blockers.
4. **Replace the WIP.** Rewrite the local WIP into commits with
   `area: sentence` subjects, and land them on the author's working branch (a
   fast-forward when the pass ran detached at its head). Each message names the
   findings fixed, the law that pins each, and how its red leg was proved. No
   `Co-Authored-By` trailer.
5. **Run round 2 when it is owed.** When a Claude agent's round 1 changed code,
   brief a Codex pass over the fix diff alone (`git diff <wip parent>
   <replaced head>`), with the same rules and instructions blocks. Its hunt is
   regressions the fixes introduced and laws that cannot fail. Its output goes
   through steps 1 to 4; step 3 verifies its fixes without opening a third
   round.
6. **Close the loop.** Report what round 2 still raises to the lead, who rules
   on each item (Who reviews, and when it ends).
7. **Hand over.** Push the working branch, fast-forward only. The lane then
   takes its GPU legs under the lead's grant, and the lead merges it into the
   integration branch.

## Route adjacent work

| Skill | Route there for |
|---|---|
| [`verification`](../verification/SKILL.md) | The gates, CLI copy, red-leg proofs, GPU legs and finished-lane list the verifier runs. |
| [`rendering`](../rendering/SKILL.md) | The render-graph runtime contracts and recurring bug classes to put in a render lane's hunt list. |
| [`maths-laws`](../maths-laws/SKILL.md) | A Maths lane's laws, legs and mutation probe. |
| [`puck-world`](../puck-world/SKILL.md) | A World lane's document, authority and replay contracts. |
| [`documentation`](../documentation/SKILL.md) | Reviewing documents and skills. |
