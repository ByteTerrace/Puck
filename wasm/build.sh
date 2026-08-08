#!/usr/bin/env bash
# Builds the default addon's release .wasm module, then refreshes the copy Puck.World ships and
# mounts by default. wasm/.cargo/config.toml already pins the default target to
# wasm32-unknown-unknown, so no --target flag is needed here. Building from the workspace root
# builds every member, including puck-stdlib (an rlib with no standalone artifact).
#
# Cargo output: target/wasm32-unknown-unknown/release/puck_addon_default.wasm
# Refreshed copy: ../src/Puck.World/Assets/addons/puck-addon-default.wasm
#
# The committed .wasm's PROVENANCE IS NOT GATE-ENFORCED — no build or Post stage proves the
# committed bytes were built from the Rust beside them. Refreshing it is therefore a DELIBERATE
# step you must remember to take whenever puck-addon-default's (or puck-stdlib's) source changes;
# this script exists so that step is one command instead of a hand-rolled copy. After running it,
# paste the printed hash into WorldDefinition.cs's default WorldAddonRow (its Hash field) — a stale
# pin makes the host refuse the refreshed module rather than silently loading it.
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

cargo build --release

wasm_dir="target/wasm32-unknown-unknown/release"
shopt -s nullglob
modules=("$wasm_dir"/*.wasm)
shopt -u nullglob

if [ ${#modules[@]} -eq 0 ]; then
    echo "Build succeeded but no .wasm file was found under $wasm_dir" >&2
    exit 1
fi

for module in "${modules[@]}"; do
    echo "Built: $(pwd)/$module"
done

default_module="$wasm_dir/puck_addon_default.wasm"

if [ ! -f "$default_module" ]; then
    echo "puck_addon_default.wasm was not among the built modules — skipping the Puck.World refresh" >&2
    exit 1
fi

target_path="../src/Puck.World/Assets/addons/puck-addon-default.wasm"

cp "$default_module" "$target_path"

# AssetContentHash pins the LEADING 64 BITS of the SHA-256 digest, read little-endian off the raw
# bytes (see src/Puck.Assets/AssetContentHash.cs) — NOT the first 16 hex characters of the
# big-endian digest string sha256sum prints. Reverse the first 8 bytes' hex pairs to match.
full_hash="$(sha256sum "$target_path" | cut -d' ' -f1)"
first_eight_bytes="${full_hash:0:16}"
reversed=""
for ((i = 14; i >= 0; i -= 2)); do
    reversed+="${first_eight_bytes:i:2}"
done
content_hash="sha256-64/${reversed,,}"

echo "Refreshed: $(cd "$(dirname "$target_path")" && pwd)/$(basename "$target_path")"
echo "Content hash (paste into WorldDefinition.cs's default WorldAddonRow.Hash): $content_hash"
