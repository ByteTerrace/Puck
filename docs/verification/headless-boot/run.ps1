#!/usr/bin/env pwsh
# QUARANTINED 2026-08-06 — this runner is no longer a gate.
#
# What it proved: the boot-shape split (headless design §1.2/§1.3) — a --headless
# boot opens no window/GPU/swapchain/audio yet runs the authoritative server,
# console, and tape; a presentation verb refuses as unknown over headless stdin;
# a headless-recorded replay tape verifies MATCH in a windowed run (cross-shape
# determinism); the windowed shape is unregressed; and the wrong-tick apply-order
# sabotage measurably moves live behavior.
#
# Why quarantined: its stdin fixture rotted at the repository's change rate. The
# SetMotion payload it sends in Phase 1 and Phase 2 —
#   world.motion.set {"moveSpeed":6.5,"turnSpeed":3.2}
# — is now REJECTED ("Invalid WorldDefinition: motion.maxSmoothError must be
# finite and positive"): SetMotion gained a required maxSmoothError field the
# payload omits, so the mutation never applies and the runner's "dirty 1 after
# the mutation" assertions fail. Owner ruling 2026-08-06: broken or out-of-date
# test fixtures are quarantined with a note, not repaired — "it'll just get
# broken again at our change rate."
#
# Note on the diagnosis: the rot originally SUSPECTED here — owned-world identity
# seeds refusing on stale top-level 'sets'/'draws' members at headless boot — did
# NOT reproduce under run-the-app. On a fresh --state-dir the four owned worlds
# seed and load clean ("[identity] loaded 4 owned worlds"). The real rot is the
# SetMotion payload above, found by running the app and reading both streams.
#
# Independently re-confirmed the same day by a second pass that did not know of
# this stub (its worktree predated it): four assertions fail identically at
# ef5f59f7 — the dirty-1 read-backs in BOTH shapes, the headless
# replay.record/replay.stop MATCH — and the runner's own SABOTAGE PHASE reports
# the control and sabotaged builds producing the SAME live tail hash
# (0x0E8330A6516FBE26). A sabotage that cannot go red is a runner announcing it
# no longer discriminates, which settles the quarantine on the runner's own
# terms, independent of the fixture rot.
#
# Validation currency is now RUN THE APP over stdin/stdout, owner-in-the-loop.
# The boot-shape contract is still checkable live: boot with and without
# --headless and read back world.status, a mutation, and replay.verify, with a
# deliberate divergence as the control. The full historical runner logic and its
# sabotage patch (scripts/sabotage/headless-boot-wrong-tick-apply-order.patch)
# remain in git history for anyone who deliberately revives this under the gate.
# Do NOT wire this stub into any standing battery set.

Write-Host '[QUARANTINED 2026-08-06] docs/verification/headless-boot - not a gate. Validate by running the app (see this file header).'
exit 3
