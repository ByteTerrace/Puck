<#
.SYNOPSIS
Opens the playable Four Corners window while three hidden processes host its other shards.

.DESCRIPTION
Uses fresh temporary documents/state and four distinct loopback authorities. Closing the playable NW process tears
down NE, SE, and SW. Transcripts remain in the printed scratch directory for diagnosis.
#>

[CmdletBinding()]
param(
    [int] $Width = 1280,
    [int] $Height = 720
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
}

foreach ($id in $ports.Keys) {
    $source = Join-Path $repoRoot "src\Puck.World\Assets\worlds\quilt-$id.world.json"
    $target = Join-Path $worldDir "quilt-$id.world.json"
    $document = Get-Content -Raw -LiteralPath $source | ConvertFrom-Json
    $document.host.listen = "127.0.0.1:$($ports[$id])"
    $document.host.authority = "127.0.0.1:$($ports[$id])"
    $document.population.capacity = 12
    $document.population.networkPlayers = 8
    [System.IO.File]::WriteAllText($target, ($document | ConvertTo-Json -Depth 100), $utf8NoBom)
}

$companions = @()
$playable = $null
try {
    foreach ($id in @('ne', 'se', 'sw')) {
        $arguments = @(
            $artifact,
            '--world', (Join-Path $worldDir "quilt-$id.world.json"),
            '--headless', 'true',
            '--exit-after-seconds', '0',
            '--state-dir', (Join-Path $scratch "state-$id"),
            '--federation-key-file', $keyPath
        )
        $companions += Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $repoRoot `
            -WindowStyle Hidden -RedirectStandardOutput (Join-Path $scratch "$id.stdout.log") `
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
