#!/usr/bin/env pwsh
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

$ErrorActionPreference = 'Stop'

Set-Location -Path $PSScriptRoot

cargo build --release

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$wasmDir = Join-Path -Path $PSScriptRoot -ChildPath 'target/wasm32-unknown-unknown/release'
$modules = Get-ChildItem -Path $wasmDir -Filter '*.wasm' -ErrorAction SilentlyContinue

if ($null -eq $modules) {
    Write-Warning "Build succeeded but no .wasm file was found under $wasmDir"
    exit 1
}

foreach ($module in $modules) {
    Write-Host "Built: $($module.FullName)"
}

$defaultModule = $modules | Where-Object { $_.Name -eq 'puck_addon_default.wasm' } | Select-Object -First 1

if ($null -eq $defaultModule) {
    Write-Warning "puck_addon_default.wasm was not among the built modules — skipping the Puck.World refresh"
    exit 1
}

$targetPath = Join-Path -Path $PSScriptRoot -ChildPath '../src/Puck.World/Assets/addons/puck-addon-default.wasm'

Copy-Item -Path $defaultModule.FullName -Destination $targetPath -Force

$hashBytes = (Get-FileHash -Path $targetPath -Algorithm SHA256).Hash
# AssetContentHash pins the LEADING 64 BITS of the SHA-256 digest, read little-endian off the raw
# bytes (see src/Puck.Assets/AssetContentHash.cs) — NOT the first 16 hex characters of the
# big-endian digest string Get-FileHash prints. Reverse the first 8 bytes' hex pairs to match.
$firstEightBytePairs = for ($i = 0; $i -lt 16; $i += 2) { $hashBytes.Substring($i, 2) }
[array]::Reverse($firstEightBytePairs)
$contentHash = "sha256-64/" + (($firstEightBytePairs -join '').ToLowerInvariant())

Write-Host "Refreshed: $targetPath"
Write-Host "Content hash (paste into WorldDefinition.cs's default WorldAddonRow.Hash): $contentHash"
