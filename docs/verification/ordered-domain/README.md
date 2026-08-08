# ordered-domain — QUARANTINED 2026-08-06

**Not a gate, and no runner lives here.** This directory is the record of a battery
that was retired; validate the contract below by running the app.

## What it proved

The ordered-domain envelope's grant/revoke ordering contract, against a document
fixture and a captured transcript.

## Why quarantined

Its expected strings (mutation-kind names, pose coordinates) drift out of date at the
repository's change rate faster than repairing them is worth. Owner ruling 2026-08-06:
broken or out-of-date test fixtures are QUARANTINED WITH A NOTE, not repaired — "it'll
just get broken again at our change rate."

## Validating it today

Validation currency for Puck.World is RUN THE APP over stdin/stdout and read the
result back, owner-in-the-loop. The ordering contract this runner covered is still
checkable live — one stdin batch interleaving a grant and the command that needs it,
plus the reversed order as the discriminating control.

The full historical runner logic and its sabotage patch remain in git history for
anyone who deliberately revives this under the gate. Do NOT wire this record into any
standing battery set.
