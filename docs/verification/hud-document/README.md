# hud-document — QUARANTINED 2026-08-06

**Not a gate, and no runner lives here.** This directory is the record of a battery
that was retired; validate the contracts below by running the app.

## What it proved

A scripted stdin session through seven segments — capacity, binding validation, band
ordering, replace suppression, seat-scope panels, persistence, screenshots. The last
two segments applied a hand-written sabotage patch
(`scripts/sabotage/hud-skip-writer-emission.patch`) to `HudWriter.cs`, ran the app to
confirm the resulting frame went visibly wrong, then reverted.

## Why quarantined

That patch's context hunk targets the lines immediately after `HudWriter.EmitOver`, on
the assumption `EmitBand`'s declaration follows right after it. `EmitSeatPanels` (and
its doc comment) were inserted between them since the patch was authored, so
`git apply --check` on that patch now fails on a CLEAN checkout of this tree —
confirmed independent of any change: `git stash` first, still fails. The runner exited
non-zero at that phase before ever reaching its later segments. Owner ruling 2026-08-06
(the same one that quarantined `ordered-domain` and `lane-present-deletion`): broken or
out-of-date test fixtures are QUARANTINED WITH A NOTE, not repaired — "it'll just get
broken again at our change rate."

## Validating it today

Validation currency for Puck.World is RUN THE APP over stdin/stdout and read the result
back (both streams), owner-in-the-loop. Every contract this runner covered is still
checkable live: `world.row.set hud.panels` up to
`WorldHudCapacity.MaxWorldPanels`/`MaxElementsPerPanel` plus one refused past-cap call;
an element bound (or templated) to an unknown token refused by name;
`world.screenshot` for the pixel look; `identity.hud` plus a process restart against the
same `--state-dir` for the seat-scope persistence round trip. See
`.claude/skills/puck-world/references/hud.md`'s "Verifying" section for the ad hoc
recipes, world and seat scope alike.

The full historical runner logic and both sabotage patches remain in git history:
`hud-skip-writer-emission.patch`, which needs its context hunk regenerated against the
current `HudWriter.cs` before it can apply again, and
`hud-skip-replace-suppression.patch`, which still applies cleanly. Do NOT wire this
record into any standing battery set.
