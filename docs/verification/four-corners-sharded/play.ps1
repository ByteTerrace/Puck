<#
.SYNOPSIS
Opens the playable Four Corners window while four hidden processes host its other shards and floating island.

.DESCRIPTION
Uses fresh temporary documents/state and five distinct loopback authorities. Closing the playable NW process tears
down NE, SE, SW, and the island. Each authority wakes the requested authored wander population at its own `npc-spawn`; those
bodies use the same automatic adjacency/federation path as the player. Transcripts remain in the printed scratch
directory for diagnosis.
#>

[CmdletBinding()]
param(
    [int] $Width = 1280,
    [int] $Height = 720,
    [ValidateRange(0, 8)]
    [int] $NpcCount = 2
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$artifact = Join-Path $repoRoot 'src\Puck.World\bin\Release\net10.0\Puck.World.dll'
if (-not (Test-Path -LiteralPath $artifact)) {
    throw "Build Puck.World in Release before launching Four Corners: dotnet build src/Puck.World/Puck.World.csproj -c Release"
}

$scratch = Join-Path $env:TEMP ('puck-four-corners-play-{0:yyyyMMdd-HHmmss}-{1}' -f (Get-Date), $PID)
$worldDir = New-Item -ItemType Directory -Force -Path (Join-Path $scratch 'worlds')
$keyPath = Join-Path $scratch 'federation.key'
[System.IO.File]::WriteAllBytes($keyPath, [byte[]] (1..32))
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

function Get-FreeLoopbackPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint] $listener.LocalEndpoint).Port
    } finally {
        $listener.Stop()
    }
}

$ports = [ordered]@{
    nw = Get-FreeLoopbackPort
    ne = Get-FreeLoopbackPort
    se = Get-FreeLoopbackPort
    sw = Get-FreeLoopbackPort
    island = Get-FreeLoopbackPort
}
$peerBudget = (1 + (5 * $NpcCount))

# Sets a nested member on a parsed world document, creating missing intermediate objects — the quilt documents are
# deltas over quilt-base.world.json, so an overridden member is usually absent from the file and inherited at load.
function Set-WorldMember($object, [string[]] $path, $value) {
    for ($i = 0; $i -lt ($path.Length - 1); $i++) {
        $name = $path[$i]
        if ((-not $object.PSObject.Properties[$name]) -or ($null -eq $object.$name)) {
            $object | Add-Member -NotePropertyName $name -NotePropertyValue ([pscustomobject]@{}) -Force
        }
        $object = $object.$name
    }
    $leaf = $path[-1]
    if ($object.PSObject.Properties[$leaf]) { $object.$leaf = $value }
    else { $object | Add-Member -NotePropertyName $leaf -NotePropertyValue $value }
}

# The quilt documents are deltas over this template; a copy beside them keeps their relative `basis` resolvable.
Copy-Item -LiteralPath (Join-Path $repoRoot 'src\Puck.World\Assets\worlds\quilt-base.world.json') -Destination (Join-Path $worldDir 'quilt-base.world.json') -Force

foreach ($id in $ports.Keys) {
    $source = Join-Path $repoRoot "src\Puck.World\Assets\worlds\quilt-$id.world.json"
    $target = Join-Path $worldDir "quilt-$id.world.json"
    $document = Get-Content -Raw -LiteralPath $source | ConvertFrom-Json
    Set-WorldMember $document @('host', 'listen') "127.0.0.1:$($ports[$id])"
    Set-WorldMember $document @('host', 'authority') "127.0.0.1:$($ports[$id])"
    # Every NPC may legally collect on one authority, with one additional peer slot for the walking player.
    Set-WorldMember $document @('population', 'capacity') (4 + $peerBudget)
    Set-WorldMember $document @('population', 'networkPlayers') $peerBudget
    [System.IO.File]::WriteAllText($target, ($document | ConvertTo-Json -Depth 100), $utf8NoBom)
}

$stdin = @{}
foreach ($id in $ports.Keys) {
    $stdin[$id] = Join-Path $scratch "$id.stdin.txt"
    [System.IO.File]::WriteAllText($stdin[$id], "world.population $NpcCount producer:wander`n", $utf8NoBom)
}

$companions = @()
$playable = $null
try {
    foreach ($id in @('ne', 'se', 'sw', 'island')) {
        $arguments = @(
            $artifact,
            '--world', (Join-Path $worldDir "quilt-$id.world.json"),
            '--headless', 'true',
            '--exit-after-seconds', '0',
            '--state-dir', (Join-Path $scratch "state-$id"),
            '--federation-key-file', $keyPath
        )
        $companions += Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $repoRoot `
            -WindowStyle Hidden -RedirectStandardInput $stdin[$id] -RedirectStandardOutput (Join-Path $scratch "$id.stdout.log") `
            -RedirectStandardError (Join-Path $scratch "$id.stderr.log") -PassThru
    }

    $arguments = @(
        $artifact,
        '--world', (Join-Path $worldDir 'quilt-nw.world.json'),
        '--exit-after-seconds', '0',
        '--width', $Width,
        '--height', $Height,
        '--state-dir', (Join-Path $scratch 'state-nw'),
        '--federation-key-file', $keyPath
    )
    $playable = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $repoRoot `
        -RedirectStandardInput $stdin.nw `
        -RedirectStandardOutput (Join-Path $scratch 'nw.stdout.log') `
        -RedirectStandardError (Join-Path $scratch 'nw.stderr.log') -PassThru

    [System.IO.File]::WriteAllText((Join-Path $scratch 'READY'), "playablePid=$($playable.Id)`nscratch=$scratch`n", $utf8NoBom)
    Wait-Process -Id $playable.Id
} finally {
    foreach ($process in $companions) {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
        $process.Dispose()
    }
    if ($null -ne $playable) {
        $playable.Dispose()
    }
}
