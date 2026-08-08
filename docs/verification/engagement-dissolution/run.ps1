#!/usr/bin/env pwsh
# QUARANTINED 2026-08-06 — this runner is no longer a gate.
#
# What it proved (engagement dissolution, .claude/tmp-headless-design.md §1.8 phase P3): player.engage/
# player.disengage submit WorldCommand.Engage/Disengage through the ordered submission domain, checked
# server-side against the ACTOR's Control over the screen before any mutation — never a client-side,
# loopback-only call. Phases:
#   (a) [already quarantined separately, 2026-08-06] the authority battery's 04-engage/05-disengage
#       regression net.
#   (b) a scripted session (engage -> play 2 real sim-seconds -> disengage) records to tape, and a
#       COMPLETELY FRESH process's replay.verify reports the identical MATCH/tick-count/hash a
#       same-process replay.stop already reported — general tape/codec round-trip fidelity, including
#       the Engage/Disengage discriminants (an old codec with no case for them would throw at record
#       time, never silently drop them).
#   (c) the engagement-specific proof a hash MATCH alone cannot give: an engaged body's pose is
#       UNCHANGED by engagement, so (b)'s hash matches whether or not the tape carried the engage/
#       disengage commands at all. A SECOND independent live process (no tape) runs the identical
#       script and its screen.state/world.grants engagement read-backs are diffed against the first —
#       the determinism contract (same document + same input -> bit-identical state) guarantees they
#       agree, through the SAME WorldServer.ApplyCommand path an offline re-drive traverses.
#   (d) the same live run proves the cabinet still plays while engaged (FramesStepped strictly
#       increases across a world.wait).
#   (e) an old-magic tape refuses BY NAME (found/reads-magic wording), never silently misparsed.
#   (f) the denied-disengage attack (an actor without Control attempting to disengage a body engaged
#       elsewhere) refuses, state byte-identical before/after.
#   (g) SABOTAGE: neutering WorldEngagement.ResolveDisengage's actor check makes the SAME attack from
#       (f) succeed — proving (f)'s control reading is not a tautology (patch reverted, (f) re-run to
#       confirm return).
#
# Why quarantined: every phase from (b) on drives `screen.insert 0 <fixture> gaming-brick` then
# `player.engage 0` against screen INDEX 0 of the world Puck.World boots with no `--world` override —
# there is no `--world` flag anywhere in this runner, so it always exercised whatever the SHIPPED
# DEFAULT declared. That was `default.world.json` (six screen rows, several engageable). The 2026-08-06
# four-world charter's shipped roster (play/dive/kart/jump) authors NO `screens` row on any of the
# four — `screen.insert 0 ...` now has no screen 0 to insert into, so every phase from (b) onward fails
# structurally, independent of the authority-battery breakage phase (a) already carried. Re-running
# after quarantining phase (a) surfaced this: 7 of 7 remaining assertions miss (0 engaged= readings,
# 0 frames= readings, the magic-refusal wording naming an unreadable tape instead of a real one).
#
# This is the SAME shape of breakage the authority battery's cases 04-06 hit, for the identical root
# cause (both structurally assumed the retired `default` world's screen furniture) — quarantined here
# on the same terms: repairing it would mean authoring screen furniture into one of the four shipped
# worlds for a battery's sake, which the repository's quarantine protocol (see headless-boot's own
# stub) declines to do, and no dedicated fixture world for this battery exists to repoint to instead.
#
# The successor is UNSPECIFIED — owed work, not chartered here. The tape/codec round-trip and
# determinism-contract proof ((b)/(c)/(e)) do not fundamentally need a screen and could migrate to
# tests/Puck.World.Tests against a code-built or non-screen fixture; the engage/disengage authority
# check (d)/(f)/(g) needs the SAME code-built testPattern-screen furniture already chartered as
# authority's own successor law — the two probably converge on one law, not two.
#
# Validation currency is RUN THE APP over stdin/stdout, owner-in-the-loop, against a world you author
# a screen into by hand (`--world <path-to-a-document-with-a-screens-row>`), until a successor lands.
# The full historical runner logic, its sabotage patch
# (scripts/sabotage/engagement-dissolution-disengage-actor-check.patch), and its committed fixture
# reference (verification/authority/fixtures/authority-test.gb, itself untouched by this quarantine)
# remain in git history for anyone who revives this under a rebuilt furniture set.

Write-Host '[QUARANTINED 2026-08-06] docs/verification/engagement-dissolution - not a gate. Every phase from (b) on needs a screen at index 0; no shipped world declares one since the four-world charter. Validate by running the app against a hand-authored screen world (see this file header).'
exit 3
