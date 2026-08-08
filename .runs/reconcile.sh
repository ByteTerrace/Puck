#!/usr/bin/env bash
# Rebase reconciliation for the verb census. Re-runs enumeration source (a) — the runtime
# `help` — in both composition tiers and diffs against the recorded baseline, so the delta
# after another session's landing is named rather than guessed.
#
#   bash .runs/reconcile.sh
#
# Baselines (checked in, diffed against every run): .runs/verbs-headless.txt and
# .runs/verbs-win.txt (name lists per tier), and .runs/afford-baseline.txt (raw stdout of a
# windowed boot fed `world.affordances`, carrying per-verb routing/valueKind/bindable AND
# per-channel shape/consumer). A missing baseline is refused loudly below rather than
# silently diffed against nothing — record a replacement the same way each was recorded
# (boot the tier, extract() for the name lists or redirect the raw affordances stdout,
# write the .runs path) and commit it before re-running. The name diffs are informational;
# the metadata diff FAILS the run (exit 1, via .runs/afford-diff.cs) on any drift in
# either table, naming each row and field.
#
# Everything else this script produces is scratch: it lives under a throwaway temp directory
# (mktemp -d, removed on exit) rather than inside the repo, so a run never leaves untracked
# litter in .runs/.
set -u
cd "$(dirname "$0")/.."

require_baseline() {
  if [[ ! -f "$1" ]]; then
    echo "reconcile.sh: missing baseline '$1' — nothing to diff against. Record a replacement (see this file's header) before running." >&2
    exit 3
  fi
}
require_baseline .runs/verbs-headless.txt
require_baseline .runs/verbs-win.txt
require_baseline .runs/afford-baseline.txt

SCRATCH="$(mktemp -d)"
trap 'rm -rf "$SCRATCH"' EXIT

printf 'help\n' > "$SCRATCH/help.txt"

dotnet build src/Puck.World/Puck.World.csproj -c Release >/dev/null 2>&1 || {
  echo "BUILD FAILED — reconcile aborted"; exit 2; }

dotnet run --project src/Puck.World -c Release --no-build -- \
  --headless --exit-after-seconds 10 --state-dir "$SCRATCH/state-rc-h" \
  < "$SCRATCH/help.txt" > "$SCRATCH/rc-h.out" 2> "$SCRATCH/rc-h.err"

dotnet run --project src/Puck.World -c Release --no-build -- \
  --exit-after-seconds 15 --width 640 --height 480 --state-dir "$SCRATCH/state-rc-w" \
  < "$SCRATCH/help.txt" > "$SCRATCH/rc-w.out" 2> "$SCRATCH/rc-w.err"

extract() {
  awk 'match($0, /^[a-z][A-Za-z0-9._-]* - /) { print substr($0, 1, RLENGTH-3) }' "$1" | sort -u
}
extract "$SCRATCH/rc-h.out" > "$SCRATCH/rc-headless.txt"
extract "$SCRATCH/rc-w.out" > "$SCRATCH/rc-win.txt"

echo "headless: $(wc -l < .runs/verbs-headless.txt) -> $(wc -l < "$SCRATCH/rc-headless.txt")"
echo "windowed: $(wc -l < .runs/verbs-win.txt) -> $(wc -l < "$SCRATCH/rc-win.txt")"
echo
echo "=== ADDED (windowed) ==="; comm -13 .runs/verbs-win.txt "$SCRATCH/rc-win.txt"
echo "=== REMOVED (windowed) ==="; comm -23 .runs/verbs-win.txt "$SCRATCH/rc-win.txt"
echo
echo "=== headless not a subset of windowed (must be empty) ==="
comm -23 "$SCRATCH/rc-headless.txt" "$SCRATCH/rc-win.txt"

# A name diff cannot see a metadata change. A bindability flip once moved world.console
# Unbindable->Bindable with no rename, and this second pass is the only thing that catches
# that class -- so any metadata drift fails the run rather than scrolling past.
printf 'world.affordances\n' > "$SCRATCH/afford.txt"
dotnet run --project src/Puck.World -c Release --no-build -- \
  --exit-after-seconds 15 --width 640 --height 480 --state-dir "$SCRATCH/state-rc-a" \
  < "$SCRATCH/afford.txt" > "$SCRATCH/rc-afford.out" 2> "$SCRATCH/rc-afford.err"

echo
dotnet run -c Release .runs/afford-diff.cs -- .runs/afford-baseline.txt "$SCRATCH/rc-afford.out"
afford_status=$?
if [[ $afford_status -ne 0 ]]; then
  echo "reconcile.sh: affordance metadata differs from .runs/afford-baseline.txt (differences above). If the change is intended, re-record the baseline (see header) in the same change." >&2
  exit "$afford_status"
fi
