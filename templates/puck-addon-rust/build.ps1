#!/usr/bin/env pwsh
# Builds the addon's release .wasm module. .cargo/config.toml already pins the default target to
# wasm32-unknown-unknown, so no --target flag is needed here.
#
# Output: target/wasm32-unknown-unknown/release/<crate-name>.wasm

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
} else {
    foreach ($module in $modules) {
        Write-Host "Built: $($module.FullName)"
    }
}
