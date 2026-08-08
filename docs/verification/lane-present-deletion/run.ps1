#!/usr/bin/env pwsh
# QUARANTINED 2026-08-06 — this runner is no longer a gate.
#
# It asserted the AddonLane/WorldCapability.Present deletion landing against two
# intentional-staleness fixtures (stale-channel-kind.world.json must parse clean
# so its guest wasm faults downstream on a retired channel-kind ordinal;
# stale-lane-field.world.json must refuse only on a deleted 'lane' field). Those
# fixtures carry the full world-document schema, which drifts (camera rigs,
# views, kits, population) out of date at the repository's change rate faster
# than repairing them is worth. Owner ruling 2026-08-06: broken or out-of-date
# test fixtures are QUARANTINED WITH A NOTE, not repaired — "it'll just get
# broken again at our change rate."
#
# Quarantine preserves the fixtures' deliberate staleness by default, which is
# safer than repair. Validation currency for Puck.World is now: RUN THE APP over
# stdin/stdout and read the result back, owner-in-the-loop.
#
# The full historical runner logic (including its step-8 standing-battery re-run
# of the other runners) and its sabotage patch remain in git history for anyone
# who deliberately revives this under the gate. Do NOT wire this stub into any
# standing battery set. The fixtures remain beside this stub as dormant data.

Write-Host '[QUARANTINED 2026-08-06] docs/verification/lane-present-deletion - not a gate. Validate by running the app (see this file header).'
exit 3
