[CmdletBinding()]
param(
    [string] $OutputDirectory = 'artifacts/azure',
    [ValidateSet('stable')] [string] $Channel = 'stable'
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    & ./build/Test-AzureDeployment.ps1
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path $output) { throw "Use a fresh output directory: $output" }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $commit = (git rev-parse HEAD).Trim()
    $env:CI = 'true'
    dotnet restore src/Puck.Azure.Functions --locked-mode
    dotnet publish src/Puck.Azure.Functions -c Release --no-restore -o "$output/functions"
    foreach ($file in @('host.json', 'worker.config.json', 'functions.metadata', 'Puck.Azure.Functions.dll')) {
        if (!(Test-Path "$output/functions/$file")) { throw "Functions publish omitted $file" }
    }
    Copy-Item src/Puck.Azure.Functions/configuration.json "$output/configuration.json"
    $configuration = Get-Content "$output/configuration.json" -Raw | ConvertFrom-Json
    $versions = @($configuration.items | Where-Object key -eq Version)
    if ($versions.Count -ne 1) { throw 'Expected exactly one App Configuration Version sentinel.' }
    $versions[0].value = $commit
    $configuration | ConvertTo-Json -Depth 32 | Set-Content "$output/configuration.json" -Encoding utf8NoBOM

    dotnet restore src/Puck.World.Browser --locked-mode
    dotnet publish src/Puck.World.Browser -c Release --no-restore
    dotnet restore src/Puck.Cli --locked-mode
    dotnet publish src/Puck.Cli -c Release --no-restore -o src/Puck.Cli/publish
    dotnet src/Puck.Cli/publish/Puck.Cli.dll official build --out "$output/official" --channel $Channel --engine src/Puck.World.Browser/bin/Release/net10.0/browser-wasm/AppBundle
    dotnet src/Puck.Cli/publish/Puck.Cli.dll official verify --base "$output/official" --channel $Channel --expect-commit $commit
    # The studio integration tests use the documented local dev tree and the real engine.
    dotnet src/Puck.Cli/publish/Puck.Cli.dll official build --out artifacts/official --channel dev --engine src/Puck.World.Browser/bin/Release/net10.0/browser-wasm/AppBundle
    # Desktop dependencies can regenerate tracked shader bytecode. Check clean source provenance
    # for both manifests before that build; hosted world definitions still use the same checkout.
    dotnet run build/Prepare-WorldSilo.cs -c Release -- src/Puck.World/Assets/worlds "$output/silo-worlds"

    Push-Location src/Puck.Dashboard/src
    try {
        $env:VITE_PUCK_OFFICIAL_CHANNEL = $Channel
        $env:VITE_PUCK_OFFICIAL_BASE = 'https://puck.byteterrace.com/official'
        npm ci
        npm --workspace portal run check:types
        npm run build
        npm --workspace portal run test
        npm run stage
    } finally { Pop-Location }
    # Publish the existing Storage/Front Door layout, including its Brotli host files.
    Copy-Item src/Puck.Dashboard/dist-deploy "$output/dashboard-storage" -Recurse
    & ./build/Build-Docs.ps1 -OutputDirectory "$output/dashboard-storage"
    $files = @(Get-ChildItem $output -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        @{ path = [IO.Path]::GetRelativePath($output, $_.FullName).Replace('\', '/'); sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    @{ commit = $commit; channel = $Channel; files = $files } | ConvertTo-Json -Depth 5 | Set-Content "$output/release.json" -Encoding utf8NoBOM
} finally {
    # A source-mutating build must identify its changes instead of bypassing the clean-tree gate.
    git status --short
    Pop-Location
}
