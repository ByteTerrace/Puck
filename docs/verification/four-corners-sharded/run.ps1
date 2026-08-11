<#
.SYNOPSIS
Runs Four Corners as four independent federated authorities and proves every invisible seam on the real path.

.DESCRIPTION
The runner copies the four quilt documents to unique scratch space, gives each document a distinct TCP authority,
starts four Puck.World processes with one shared federation key, and drives one local body clockwise across each
seam at the same time. NW also drives one autonomous body through the same invisible edge. It requires all four
human transfers, the autonomous ownership migration, routed read-backs after departure, delivered remote entity
addresses, and zero rejected wire commands.

This artifact falsifies the sharding claim if any edge silently colocates: its transfer echo must name the distinct
destination endpoint. It falsifies durable addressability if a neighbour advertises process-local `boot/...`, or if
the four observed endpoint namespaces are not all distinct. Killing or mispointing any companion authority makes
the corresponding required transfer/address observations fail; `unavailable: closed` keeps that edge physically
closed rather than turning it into a hole.

.EXAMPLE
pwsh -File docs/verification/four-corners-sharded/run.ps1
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $utf8NoBom
$OutputEncoding = $utf8NoBom

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$scratch = Join-Path $env:TEMP ('puck-four-corners-sharded-{0:yyyyMMdd-HHmmss}-{1}' -f (Get-Date), $PID)
$worldDir = Join-Path $scratch 'worlds'
$artifact = Join-Path $repoRoot 'src\Puck.World\bin\Release\net10.0\Puck.World.dll'
$keyPath = Join-Path $scratch 'federation.key'

function Get-FreeLoopbackPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint] $listener.LocalEndpoint).Port
    } finally {
        $listener.Stop()
    }
}

$topology = [ordered]@{
    nw = [PSCustomObject]@{ Port = (Get-FreeLoopbackPort); Edge = 'east';  Target = 'ne'; Corner = 'se'; Pose = '-2 0 -12 0 0 0 1';  Fly = '0 1 0 0 0 0 1 1'; Neighbours = @('ne', 'sw', 'se') }
    ne = [PSCustomObject]@{ Port = (Get-FreeLoopbackPort); Edge = 'south'; Target = 'se'; Corner = 'sw'; Pose = '12 0 -2 0 0 0 1';  Fly = '-1 0 0 0 0 0 1 1'; Neighbours = @('nw', 'se', 'sw') }
    se = [PSCustomObject]@{ Port = (Get-FreeLoopbackPort); Edge = 'west';  Target = 'sw'; Corner = 'nw'; Pose = '2 0 12 0 0 0 1';   Fly = '0 -1 0 0 0 0 1 1'; Neighbours = @('ne', 'sw', 'nw') }
    sw = [PSCustomObject]@{ Port = (Get-FreeLoopbackPort); Edge = 'north'; Target = 'nw'; Corner = 'ne'; Pose = '-12 0 2 0 0 0 1';  Fly = '1 0 0 0 0 0 1 1'; Neighbours = @('nw', 'se', 'ne') }
}

function Endpoint([string] $id) {
    return "127.0.0.1:$($topology[$id].Port)"
}

function Require([bool] $condition, [string] $claim) {
    $script:assertions++
    if (-not $condition) {
        $script:failures += $claim
    }
}

New-Item -ItemType Directory -Force -Path $worldDir | Out-Null

Push-Location $repoRoot
try {
    & dotnet build src/Puck.World/Puck.World.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed with exit $LASTEXITCODE"
    }
} finally {
    Pop-Location
}

[byte[]] $key = 1..32
[System.IO.File]::WriteAllBytes($keyPath, $key)

foreach ($id in $topology.Keys) {
    $source = Join-Path $repoRoot "src\Puck.World\Assets\worlds\quilt-$id.world.json"
    $target = Join-Path $worldDir "quilt-$id.world.json"
    $document = Get-Content -Raw -LiteralPath $source | ConvertFrom-Json
    $document.host.listen = Endpoint $id
    $document.host.authority = Endpoint $id
    # Four indices are reserved for local seats. A federated arrival is an ordinary admitted network player, so the
    # sharded fixture must author network capacity instead of relying on the quilt documents' local-only floor.
    $document.population.capacity = 12
    $document.population.networkPlayers = 8
    [System.IO.File]::WriteAllText($target, ($document | ConvertTo-Json -Depth 100), $utf8NoBom)
}

$processes = [ordered]@{}
$assertions = 0
$failures = @()

try {
    foreach ($id in $topology.Keys) {
        $row = $topology[$id]
        $stdin = Join-Path $scratch "$id.stdin.txt"
        $stdout = Join-Path $scratch "$id.stdout.log"
        $stderr = Join-Path $scratch "$id.stderr.log"
        $world = Join-Path $worldDir "quilt-$id.world.json"
        $state = Join-Path $scratch "state-$id"
        $autonomous = if ($id -eq 'nw') { @"
world.population 1
world.wait 2
player.pose -2 0 -12 0 0 0 5
player.fly 0 1 0 0 0 0 1 5
"@ } else { '' }
        $continuation = if ($id -eq 'nw') { @"
player.fly -1 0 0 0 0 0 2 1
world.wait 180
player.where 1
player.fly 0 -1 0 0 0 0 2 1
world.wait 180
player.where 1
player.fly 1 0 0 0 0 0 2 1
world.wait 180
player.where 1
"@ } else { '' }
        $script = @"
world.wait 90
wire.errors reset
world.adjacencies
world.wait 30
world.adjacencies
$autonomous
player.pose $($row.Pose)
player.fly $($row.Fly)
world.wait 120
player.where 1
world.adjacencies
$continuation
wire.errors
"@
        [System.IO.File]::WriteAllText($stdin, $script, $utf8NoBom)

        $arguments = @(
            $artifact,
            '--world', $world,
            '--headless', 'true',
            '--exit-after-seconds', '28',
            '--state-dir', $state,
            '--federation-key-file', $keyPath
        )
        $process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $repoRoot `
            -WindowStyle Hidden -RedirectStandardInput $stdin -RedirectStandardOutput $stdout `
            -RedirectStandardError $stderr -PassThru
        $processes[$id] = [PSCustomObject]@{ Process = $process; Stdout = $stdout; Stderr = $stderr }
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(40)
    while (($processes.Values | Where-Object { -not $_.Process.HasExited }).Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
    foreach ($entry in $processes.Values) {
        if (-not $entry.Process.HasExited) { $failures += "process $($entry.Process.Id) timed out" }
    }

    $observedNamespaces = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

    foreach ($id in $topology.Keys) {
        $row = $topology[$id]
        $entry = $processes[$id]
        $stdout = if (Test-Path -LiteralPath $entry.Stdout) { Get-Content -Raw -LiteralPath $entry.Stdout } else { '' }
        $stderr = if (Test-Path -LiteralPath $entry.Stderr) { Get-Content -Raw -LiteralPath $entry.Stderr } else { '' }
        $combined = $stdout + "`n" + $stderr
        $destinationEndpoint = Endpoint $row.Target

        Require ($entry.Process.HasExited -and ($entry.Process.ExitCode -eq 0)) "$id authority did not exit cleanly"
        Require ($stderr.Contains("[world.listen: bound $(Endpoint $id)]")) "$id did not bind its distinct authority endpoint"
        Require ($combined.Contains("[world.adjacency: 'boot/$($row.Edge)' seat 1 crossed")) "$id/$($row.Edge) did not cross automatically"
        Require ($stdout.Contains("remote authority $destinationEndpoint")) "$id/$($row.Edge) did not use remote authority $destinationEndpoint"
        Require ([regex]::IsMatch($stdout, '\[player\.where:.*instance:.*\]')) "$id traveler was not queryable through its routed remote authority"
        Require ($stdout.Contains('[wire.errors: 0 rejected]')) "$id reported rejected wire commands"
        Require ($stdout.Contains('derived=corner') -and $stdout.Contains("entities=$(Endpoint $row.Corner)/")) "$id did not derive its diagonal corner peer $($row.Corner)"
        Require (-not $stdout.Contains('entities=boot/')) "$id observed a process-local boot entity address"
        Require (-not [regex]::IsMatch($combined, 'Unhandled exception|ABORTED| refused \(')) "$id emitted an exception, abort, or refusal"

        foreach ($neighbour in $row.Neighbours) {
            $namespace = Endpoint $neighbour
            Require ($stdout.Contains("entities=$namespace/") -or $stdout.Contains(",$namespace/")) "$id did not receive addressable entities from $namespace"
            [void] $observedNamespaces.Add($namespace)
        }
    }

    Require ($observedNamespaces.Count -eq 4) "the delivered entity addresses did not cover four distinct authority namespaces"

    $nwTranscript = Get-Content -Raw -LiteralPath $processes.nw.Stdout
    Require (([regex]::Matches($nwTranscript, '\[player\.where:')).Count -ge 4) "nw traveler did not remain queryable after every onward handoff"
    Require ($nwTranscript.Contains("[world.adjacency: 'boot/east' seat 5 crossed")) "nw autonomous body did not cross the invisible east boundary"
    Require ([regex]::IsMatch($nwTranscript, "\[world\.transfer:.*'boot' seat 5 departed -> '.*' seat [5-9][0-9]* arrived \(anonymous\)")) "nw autonomous body did not migrate as an anonymous server-authored entity"
    Require (-not [regex]::IsMatch($nwTranscript, 'no live or forwarded transfer body|no committed onward route|forwarded body:.*no committed destination credential')) "nw traveler hit a dead forwarding route"
    Require ([regex]::IsMatch($nwTranscript, '\[player\.where: p6 .*anchor=body:5\]')) "nw traveler control/presentation route did not advance from the first remote body to the onward body"

    foreach ($id in @('ne', 'se', 'sw')) {
        $transcript = Get-Content -Raw -LiteralPath $processes[$id].Stdout
        Require ([regex]::IsMatch($transcript, "\[world\.adjacency: 'boot/[^']+' seat ([5-9]|[1-9][0-9]+) crossed")) "$id did not forward nw's admitted peer across the next ownership edge"
    }
} finally {
    foreach ($entry in $processes.Values) {
        if (-not $entry.Process.HasExited) {
            Stop-Process -Id $entry.Process.Id -Force -ErrorAction SilentlyContinue
        }
        $entry.Process.Dispose()
    }
}

Write-Output "transcripts: $scratch"
Write-Output "assertions: $assertions; failures: $($failures.Count)"
foreach ($failure in $failures) {
    Write-Output "FAIL: $failure"
}

if ($failures.Count -gt 0) {
    exit 1
}

Write-Output 'PASS: Four Corners ran as four distinct authorities; simultaneous human and autonomous handoffs, diagonal peers, and one full multi-host traveler circuit all held.'
exit 0
