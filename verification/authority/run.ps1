#requires -version 7
# QUARANTINED 2026-08-06 — this runner is no longer a gate.
#
# What it proved: the acting-principal/administration contract across the player-facing command
# surface — denial+control pairs (actor lacks the grant -> a loud, named refusal, state provably
# unchanged; actor holds the grant -> the identical request succeeds), with actor always distinct
# from the target/recipient (every stdin line dispatches through the text door, which stamps
# Console unconditionally, so actor != target is structural, not just chosen) so a broken check
# (one that consults the target's own pre-seeded grant) and a fixed one are actually discriminated.
# Seven cases: 01-join-leave-setprofile, 02-confirm, 03-assign (the relocation cascade's SOURCE
# authorization, not just the destination), 04-engage, 05-disengage (all four latch/route
# combinations, plus the repair direction's own authorization gate), 06-addon-lifecycle
# (world.addon.reload/enable/disable), 07-identity-create (the owned-identity seat gate). See
# README.md in this directory for the full case-by-case table and the two rounds of adversarial
# review this closed.
#
# Why quarantined: cases 04-06 assume furniture only the retired `default` world ever shipped — a
# `screen:0` to engage/disengage against (04/05) and a mounted `default` addon to
# reload/enable/disable (06). The 2026-08-06 four-world charter's whole shipped roster
# (play/dive/kart/jump) authors no `screens` row and no `addons` row, so those three fixtures no
# longer boot into the state the scripts were written against. Repairing them in place would mean
# re-authoring screen/addon furniture into one of the four shipped worlds for a battery's sake —
# exactly the kind of fixture-chasing this repository's quarantine protocol (see headless-boot's
# own stub) declines to do; the fix belongs in a successor that builds its own furniture instead of
# borrowing a shipped world's.
#
# The successor: the acting-principal/administration contract this battery proved now lives in
# tests/Puck.World.Tests's AuthorityAdministrationLawTests (a law-based test project; NOT yet wired
# into Puck.slnx or any build gate — see that project's own README). An engage-authority law
# exercising cases 04-06's ground (screen engage/disengage, addon lifecycle) with CODE-BUILT
# testPattern-screen furniture — never borrowed from a shipped world's own document — is chartered
# to follow there, closing the gap this quarantine opens.
#
# Validation currency is RUN THE APP over stdin/stdout, owner-in-the-loop, until the successor
# lands. Cases 01/02/03/07 (join/leave/confirm/assign/identity-create — no screen or addon
# dependency) still describe live, checkable behavior; drive them by hand against a shipped world
# and read both streams. The full historical runner logic, its fixtures, and its two
# adversarial-review discriminator proofs remain in git history and in this directory's README for
# anyone who revives cases 04-06 under a rebuilt furniture set.

Write-Host '[QUARANTINED 2026-08-06] verification/authority - not a gate. Cases 04-06 assume the retired default world''s screen/addon furniture; successor is tests/Puck.World.Tests (see this file header). Validate by running the app.'
exit 3
