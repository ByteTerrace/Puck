#!/usr/bin/env bash
# Builds the addon's release .wasm module. .cargo/config.toml already pins the default target to
# wasm32-unknown-unknown, so no --target flag is needed here.
#
# Output: target/wasm32-unknown-unknown/release/<crate-name>.wasm
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

cargo build --release

wasm_dir="target/wasm32-unknown-unknown/release"
shopt -s nullglob
modules=("$wasm_dir"/*.wasm)
shopt -u nullglob

if [ ${#modules[@]} -eq 0 ]; then
    echo "Build succeeded but no .wasm file was found under $wasm_dir" >&2
else
    for module in "${modules[@]}"; do
        echo "Built: $(pwd)/$module"
    done
fi
