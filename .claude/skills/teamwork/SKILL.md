---
name: teamwork
description: Coordinates multi-session repository work through a lead and cross-trained lanes. Defaults to a LIGHT posture — lanes build fast, verify by running, and hand the result to the owner to test — and treats briefs, pre-registered review, batteries, and serialized landings as an opt-in heavy posture the owner asks for. Use when starting or resuming a distributed effort, directing or monitoring another session, landing work that other lanes build on, resolving ownership, or diagnosing a silent or stalled lane. Also read it before adding any coordination step, because most coordination failures are process added under uncertainty.
---

# Teamwork

Coordinate distributed work as one lead session and several lanes, with the
owner directing the effort. Integrate all lane work into the live trunk.

Use a mechanical control wherever one exists. Keep prose as the map to the
control, not a substitute for it.

## Start at the light posture

**The default is speed. The apparatus below is OPT-IN, and the owner opts in.**
Lanes build, verify by running, and hand the result to the owner to test. The
owner testing it IS the acceptance. Roles are soft: every lane is cross-trained
and builds.

Escalate to the heavy posture — pre-registered checks, an adversary who does not
build, batteries, serialized landings — only when the owner asks for it, when
the work is irreversible, or when a defect already escaped. Announce the switch;
never drift into it.

Read a request for speed as binding until the owner lifts it. A stop order is
not a pause: say "this is a stop order, do not pick up the next item," and
expect one more report anyway from work already in flight.

## Do not add process under uncertainty

Every failure mode below is the same reflex — reaching for coordination when the
cheaper move was to act narrowly or ask one question.

- **When the owner names a lane, message that lane.** Broadcasting a specific
  instruction to everyone wastes four contexts and buries the signal.
- **Never brief an agent to fetch what another can read.** A lane reads the code
  itself; inserting a courier adds a hop, a transcription risk, and a second
  context for no new information.
- **Do not assign confirmation work.** If a lane already established something,
  re-tasking another to agree costs a lane and buys nothing.
- **Do not escalate a decision that is yours.** Rulings, sequencing, and scope
  are the lead's. Reserve the owner for taste and visual judgement — how
  something LOOKS is theirs, and asking is expected — and for irreversible acts.
- **Acknowledge, do not narrate.** When the owner gives a direction, act on it
  and confirm in a line. Explaining the plan back is not progress.
- **Prefer hot reload to fresh runs**, and one process to many, wherever the
  system supports driving change into a running session.

## Assign roles

These are the HEAVY-posture roles. Under the default light posture every lane
builds and the distinctions below collapse; adopt them only when the owner opts
in.

- **Lead:** Route work, issue briefs and rulings, check for collisions between
  lanes, track the board, and record corrections. When a review confirms a
  defect class, broadcast it before landing to every in-flight lane that may
  contain the same class. Decide routing, sequencing, review dispositions,
  integration, and record-keeping. Escalate owner-only decisions.
- **Feature lane:** Build and verify one squash in an isolated worktree. Do not
  work in the shared checkout; it belongs to the owner, and in-progress state
  there can be misattributed.
- **Battery guardian:** Own `docs/verification/` and its evolution. Require
  every battery to earn its place. Accept a reasoned decision not to add one;
  reject both flaky red and vacuous green.
- **Adversary:** Try to refute each landing's claims. Pre-register checks before
  seeing the artifact they judge. Report ranked findings only, mark each one
  `CONFIRMED` (reproduced) or `PLAUSIBLE` (argued), and make no fixes. Accept
  an empty report as valid, but at this repository's change velocity, inspect
  its coverage before celebrating it — and respond by widening the coverage,
  never by lowering the finding bar. Assign a different reviewer to the
  adversary's own feature work. Treat pre-registration as a method available
  to every lane, not as the adversary's exclusive responsibility.
- **Quick lane:** Perform reconnaissance, bisects, measurements, and small
  hygiene tasks. Report findings only unless the brief explicitly assigns a
  landing.

## Run the coordination workflow

1. Record the live trunk tip and assign non-overlapping ownership. When two
   lanes touch the same seam, assign the lead or a pre-registered reviewer to
   evaluate the composition explicitly.
2. Send each lane a self-contained brief. Include the objective, owned scope,
   exclusions, authored-from base hash, expected output, required verification,
   and report format. Do not rely on another session's private task state.
3. Require the lane to acknowledge a ruling in the same turn by beginning work
   or naming a blocker. Treat a response containing only future intent as a
   stall.
4. Apply the role boundary: feature lanes prepare verified squashes, quick
   lanes report, adversaries review, the guardian owns verification
   infrastructure, and the lead serializes integration by default.
5. Verify and integrate using the landing workflow below.
6. Treat finishing and reporting as one step. Do not call lane work complete
   until the lead receives its report, because downstream actions fan out from
   there.
7. Have the lead record the review disposition, resolve follow-up ownership,
   and check downstream lanes for changed assumptions.

When a lane goes quiet, inspect origin and the lane's worktree for commits and
file activity; do not rely only on the inbox. Re-trigger it with: "Begin now or
name the blocker." Sessions do not self-resume.

Re-check a lane's tree after every instruction that can produce work, not only
after silence. The instruction invalidates the lead's earlier observation;
"I looked earlier" is not current evidence.

Treat a session's loaded memory as a start-of-session snapshot. When a brief
mentions an unfamiliar memory or tool, re-read the memory directory and
`git log` before concluding that it does not exist. When joining or resuming an
effort whose landings you did not sequence, read the landing chain's commit
bodies, the board, and the seams between in-flight briefs before issuing an
instruction. Reconcile the last-known board with origin first.

## Land a feature lane

1. Create a worktree from the live trunk and record the authored-from commit
   hash.
2. Produce one squash with a hand-written declarative subject. In the body,
   state what was proven and how each proof can fail.
3. Do not squash against a moving ref. In particular, do not use
   `git reset --soft origin/<branch>`: if origin advances, the resulting commit
   can silently revert the advance while retaining valid ancestry and green
   batteries.
4. Before reporting the squash as ready, run:
   - `puck landing --against <landing-tip> --base <authored-from-base>`
   - `puck citations`
   - Every battery touched by the change
5. After a rebase, keep the original authored-from base for `--base`; do not use
   the new parent. Ensure `--against` is the current landing tip. The landing
   gate intentionally refuses equal base and tip because that check is vacuous.
6. If the landing changes behavior pinned by a battery, update that battery in
   the same squash. Afterward, have the guardian prove the edited battery can
   fail: sabotage it, observe red, and restore it.
7. Put new checks in a durable test project or a `puck` verb. Use a one-off
   script only when directed. Put deliberate manual exceptions in
   `docs/verification/manual/` and label them as manual.
8. Hold the verified squash unpushed and report it to the lead. Include actual
   command output, commit and base hashes, and review status; do not replace
   evidence with adjectives.
9. Have the lead rebuild the squash in an isolated worktree, reconcile any
   conflicts while holding both briefs, rerun the gates on the exact tree that
   will push, and push it. A deletion gate can detect missing content but cannot
   decide whether conflict resolution preserves both landings' meaning.
10. Use lane-direct pushes only when the effort explicitly chooses that
    velocity tradeoff. Record that choice; this repository has seen skipped
    gates, merge bubbles, and tree-state confusion from lane-direct pushes, and
    `puck landing` controls only the worst of those hazards.

## Review evidence

Before designing verification or reviewing a landing, read
[the recurring failure shapes](references/failure-shapes.md) and apply every
relevant discriminator.

For every claim:

1. Name the observation or mechanical check that supports it.
2. Demonstrate that a proof can fail when practical.
3. Exercise a configuration capable of exposing the claimed defect.
4. Check the composition of concurrent landings that touch the same seam.
5. Re-check negative results produced by other sessions before relaying them;
   their search scope is otherwise invisible downstream.

Do not infer provenance or production method from authorship or a diff. Ask the
author when the method matters: a diff establishes what changed, not how it was
produced.

## Use repository controls

| Hazard | Control |
|---|---|
| Dropping another lane's landed work | Run `puck landing`; use the authored-from commit as `--base`. |
| Citing a dead or misspelled verb | Run `puck citations`: exit 1 means defect, exit 3 means stale input, and exit 0 means no defects. |
| A stale enumeration accusing current documentation | Trust `puck citations`' refusal; it identifies the suspect input, not the citation. |
| Answering structural or reference questions with text matching | Run `puck references` or `puck declarations`. |
| Capping a content search or depending on shell-specific search | Run `puck search -M 0`. |

Hold every instrument to the same standard as the proof it supports:

- Preserve stderr in harnesses.
- Read a program's exit status directly, not through a readability pipeline.
- Assert the HEAD, foreground window, and actual consumed input of anything the
  harness drives.
- Wait for observed output instead of wall-clock offsets.
- Distrust a result that changes without an identified cause.

## Run contribution rounds

After each major body of work, invite each lane to propose one small,
self-chosen improvement in correctness, robustness, ergonomics, or performance.
Send the scope to the lead for collision checking, then let the lane retain the
choice. Hold every contribution to the landing bar. Prefer improvements that
turn an observed failure into a mechanical control.

## Respect ownership

Escalate product forks, invalidation of persisted replays or sealed
(`VerifiedCode`) members, outward-facing publication, and standing directions
to the owner. Let the owner also set the board's posture, including a freeze to
absorb and confer, a pause at a verdict, or collection mode without landings.
Treat a pacing directive as overriding normal flow until the owner lifts it;
have the lead re-brief every lane when posture changes. Do not let a standing
push or report convention override the active posture.

Let the lead decide routing, sequencing, review dispositions, integration, and
the record. Credit corrections to whoever found them, including lead
misbriefs, and update the record in either direction.
