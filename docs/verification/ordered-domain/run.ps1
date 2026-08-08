#!/usr/bin/env pwsh
# QUARANTINED 2026-08-06 — this runner is no longer a gate.
#
# It asserted the ordered-domain envelope's grant/revoke ordering contract
# against a document fixture and a captured transcript whose expected strings
# (mutation-kind names, pose coordinates) drift out of date at the repository's
# change rate faster than repairing them is worth. Owner ruling 2026-08-06:
# broken or out-of-date test fixtures are QUARANTINED WITH A NOTE, not repaired
# — "it'll just get broken again at our change rate."
#
# Validation currency for Puck.World is now: RUN THE APP over stdin/stdout and
# read the result back, owner-in-the-loop. The ordering contract this runner
# covered is still checkable live — one stdin batch interleaving a grant and the
# command that needs it, plus the reversed order as the discriminating control.
#
# The full historical runner logic and its sabotage patch remain in git history
# for anyone who deliberately revives this under the gate. Do NOT wire this stub
# into any standing battery set.

Write-Host '[QUARANTINED 2026-08-06] docs/verification/ordered-domain - not a gate. Validate by running the app (see this file header).'
exit 3
