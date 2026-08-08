#!/usr/bin/env pwsh
# QUARANTINED 2026-08-06 — this runner is no longer a gate.
#
# It drove a scripted stdin session through seven segments (capacity, binding
# validation, band ordering, replace suppression, seat-scope panels,
# persistence, screenshots) — the last two segments apply a hand-written
# sabotage patch (scripts/sabotage/hud-skip-writer-emission.patch) to
# HudWriter.cs, run the app to confirm the resulting frame goes visibly
# wrong, then revert. That patch's context hunk targets the lines
# immediately after HudWriter.EmitOver on the assumption EmitBand's
# declaration follows right after it; EmitSeatPanels (and its doc comment)
# were inserted between them since the patch was authored, so
# `git apply --check` on that patch now fails on a CLEAN checkout of this
# tree — confirmed independent of any change: `git stash` first, still
# fails. The runner exits non-zero at that phase before ever reaching its
# later segments. Owner ruling 2026-08-06 (the same one that quarantined
# ordered-domain and lane-present-deletion): broken or out-of-date test
# fixtures are QUARANTINED WITH A NOTE, not repaired — "it'll just get
# broken again at our change rate."
#
# Validation currency for Puck.World is now: RUN THE APP over stdin/stdout
# and read the result back (both streams), owner-in-the-loop. Every contract
# this runner covered is still checkable live: world.row.set hud.panels up to
# WorldHudCapacity.MaxWorldPanels/MaxElementsPerPanel plus one refused
# past-cap call, an element bound (or templated) to an unknown token refused
# by name, world.screenshot for the pixel look, identity.hud plus a process
# restart against the same --state-dir for the seat-scope persistence round
# trip. See .claude/skills/puck-world/references/hud.md's "Verifying"
# section for the ad hoc recipes, world and seat scope alike.
#
# The full historical runner logic and both sabotage patches
# (scripts/sabotage/hud-skip-writer-emission.patch, which needs its context
# hunk regenerated against the current HudWriter.cs before it can apply
# again; hud-skip-replace-suppression.patch, which still applies cleanly)
# remain in git history / beside this stub for anyone who deliberately
# revives this under the gate. Do NOT wire this stub into any standing
# battery set.

Write-Host '[QUARANTINED 2026-08-06] docs/verification/hud-document - not a gate. Validate by running the app (see this file header).'
exit 3
