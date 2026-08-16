<#
.SYNOPSIS
Runs Four Corners plus its floating island as five independent federated authorities and proves horizontal and vertical seams on the real path.

.DESCRIPTION
The runner copies the five quilt documents to unique scratch space, gives each document a distinct TCP authority and
its own generated ECDSA federation keypair (pinned into every OTHER authority's admission rows), starts five
Puck.World processes, and drives one local body clockwise across each seam at the same time. After handoff, every
traveler is stopped and driven again through synthesized left/right
stick signals—the exact binding/router path used by physical gamepads. Every authority also wakes two
producer-driven bodies at authored spawns and never submits movement for them. It requires all four human transfers,
retained camera state and movement control under the new authority, eight autonomous ownership migrations, routed read-backs after departure, delivered remote entity
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

# Each authority gets its own ECDSA P-256 keypair rather than one shared HMAC secret — the federation door verifies
# a signed claim against the READING authority's own admission entries (WorldAttestedAuthenticator), so trust is
# pinned per peer key, never handed out as one bearer secret every authority could sign anyone's namespace with.
function New-AuthorityKey {
    $ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
    $spki = $ecdsa.ExportSubjectPublicKeyInfo()
    $fingerprint = [System.Security.Cryptography.SHA256]::HashData($spki)
    $domain = ([System.BitConverter]::ToString($fingerprint) -replace '-', '').ToLowerInvariant()
    $pkcs8 = $ecdsa.ExportPkcs8PrivateKey()
    $ecdsa.Dispose()
    return [PSCustomObject]@{
        Domain          = $domain
        PublicKeyBase64 = [Convert]::ToBase64String($spki)
        Pkcs8           = $pkcs8
    }
}

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
    nw = [PSCustomObject]@{ Port = (Get-FreeLoopbackPort); Edge = 'east';  Target = 'ne'; Corner = 'se'; Pose = '-2 0 -12 0 0 0 1';  Fly = '0 1 0 0 0 0 1 1'; Neighbours = @('ne', 'sw', 'se'); ContactOut = '-0.2 0 -4 0 0 0'; ContactIn = '-4 0 -0.2 0 0 0'; OutAxis = 0; OutSign = -1; InAxis = 2; InSign = -1 }
    ne = [PSCustomObject]@{ Port = (Get-FreeLoopbackPort); Edge = 'south'; Target = 'se'; Corner = 'sw'; Pose = '12 0 -2 0 0 0 1';  Fly = '-1 0 0 0 0 0 1 1'; Neighbours = @('nw', 'se', 'sw'); ContactOut = '20 0 -0.2 0 0 0'; ContactIn = '0.2 0 -4 0 0 0'; OutAxis = 2; OutSign = -1; InAxis = 0; InSign = 1 }
    se = [PSCustomObject]@{ Port = (Get-FreeLoopbackPort); Edge = 'west';  Target = 'sw'; Corner = 'nw'; Pose = '2 0 12 0 0 0 1';   Fly = '0 -1 0 0 0 0 1 1'; Neighbours = @('ne', 'sw', 'nw'); ContactOut = '0.2 0 20 0 0 0'; ContactIn = '20 0 0.2 0 0 0'; OutAxis = 0; OutSign = 1; InAxis = 2; InSign = 1 }
    sw = [PSCustomObject]@{ Port = (Get-FreeLoopbackPort); Edge = 'north'; Target = 'nw'; Corner = 'ne'; Pose = '-12 0 2 0 0 0 1';  Fly = '1 0 0 0 0 0 1 1'; Neighbours = @('nw', 'se', 'ne'); ContactOut = '-4 0 0.2 0 0 0'; ContactIn = '-0.2 0 20 0 0 0'; OutAxis = 2; OutSign = 1; InAxis = 0; InSign = -1 }
}
$islandPort = Get-FreeLoopbackPort
$authorityIds = @($topology.Keys) + @('island')

function Endpoint([string] $id) {
    if ($id -eq 'island') { return "127.0.0.1:$islandPort" }
    return "127.0.0.1:$($topology[$id].Port)"
}

function Require([bool] $condition, [string] $claim) {
    $script:assertions++
    if (-not $condition) {
        $script:failures += $claim
    }
}

# Sets a nested member on a parsed world document, creating missing intermediate objects. The quilt documents are
# deltas over quilt-base.world.json, so a member this fixture overrides (host, population, simulation) is usually
# absent from the file and inherited at load — adding it to the delta deep-merges it over the base, which is exactly
# the authored-override semantics the fixture wants.
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

# The quilt documents are deltas over this template; a copy beside them keeps their relative `basis` resolvable.
Copy-Item -LiteralPath (Join-Path $repoRoot 'src\Puck.World\Assets\worlds\quilt-base.world.json') -Destination (Join-Path $worldDir 'quilt-base.world.json') -Force

# One keypair per authority, generated before any document is written so every authority's admission rows can pin
# every OTHER authority's public key up front.
$keys = @{}
foreach ($id in $authorityIds) {
    $key = New-AuthorityKey
    $key | Add-Member -NotePropertyName Path -NotePropertyValue (Join-Path $scratch "$id.federation.key")
    [System.IO.File]::WriteAllBytes($key.Path, $key.Pkcs8)
    $keys[$id] = $key
}
# The wildcard federation-arrival row quilt-base.world.json already authors, restated here because the runner
# writes the WHOLE admission array as a delta (Set-WorldMember overrides the member outright — see its own remarks
# above), not a composed layer over the base's own row.
function WildcardArrivalRow {
    [PSCustomObject]@{
        domain    = '*'
        subject   = $null
        mode      = 'FederatedAuthority'
        algorithm = ''
        publicKey = ''
        grants    = @(
            [PSCustomObject]@{ capability = 'Control'; subject = 'all' },
            [PSCustomObject]@{ capability = 'Drive'; exclusive = $true; budget = 64 },
            [PSCustomObject]@{ capability = 'Observe'; budget = 64 }
        )
    }
}

foreach ($id in $authorityIds) {
    $source = Join-Path $repoRoot "src\Puck.World\Assets\worlds\quilt-$id.world.json"
    $target = Join-Path $worldDir "quilt-$id.world.json"
    $document = Get-Content -Raw -LiteralPath $source | ConvertFrom-Json
    Set-WorldMember $document @('host', 'listen') (Endpoint $id)
    Set-WorldMember $document @('host', 'authority') (Endpoint $id)
    # The presenting NW world deliberately ticks at half the destination rate. A held physical stick is replicated
    # state, so the NE/SE/SW authorities must integrate it on every one of THEIR ticks rather than turning the gaps
    # between NW samples into phantom releases (the post-transition "viscous movement" falsifier).
    if ($id -eq 'nw') { Set-WorldMember $document @('simulation', 'rateHz') 30 }
    # Four indices are reserved for local seats. A federated arrival is an ordinary admitted network player, so the
    # sharded fixture must author network capacity instead of relying on the quilt documents' local-only floor.
    # Four driven locals plus all eight autonomous travelers can legally collect on one authority. Slot reuse under
    # that crowd is intentional: it is the real-path falsifier for generation-stable appearance and forwarding.
    Set-WorldMember $document @('population', 'capacity') 20
    Set-WorldMember $document @('population', 'networkPlayers') 16
    Set-WorldMember $document @('population', 'seatActivation') @('Eager', 'Eager', 'Eager', 'Eager')
    Set-WorldMember $document @('population', 'defaultPeerSource') 'Idle'
    Set-WorldMember $document @('population', 'distribution', 'region') ([PSCustomObject]@{ '$type' = 'points'; names = @('npc-spawn'); halfExtent = 0 })
    Set-WorldMember $document @('population', 'distribution', 'fill') ([PSCustomObject]@{ name = 'r2'; offset = 0; step = 0 })
    if ($id -eq 'nw') {
        # A keyed-row override: the one-row spawnPoints list patches the base's npc-spawn row in place at load.
        Set-WorldMember $document @('spawnPoints') @([PSCustomObject]@{ id = 'npc-spawn'; position = @(-0.6, 0, -12); yawDegrees = -90 })
    }
    # Each authority trusts every OTHER authority's own pinned key directly — the federation door verifies a
    # SignsDirectly claim against these rows, deriving the connection's identity from the verified key rather than
    # any claimed label (WorldAttestedAuthenticator).
    $admissionRows = [System.Collections.Generic.List[object]]::new()
    $admissionRows.Add((WildcardArrivalRow))
    foreach ($peer in $authorityIds) {
        if ($peer -eq $id) { continue }
        $admissionRows.Add([PSCustomObject]@{
                domain    = $keys[$peer].Domain
                subject   = (Endpoint $peer)
                mode      = 'SignsDirectly'
                algorithm = 'ecdsa-p256-sha256'
                publicKey = $keys[$peer].PublicKeyBase64
                grants    = @()
            })
    }
    Set-WorldMember $document @('admission') $admissionRows.ToArray()
    [System.IO.File]::WriteAllText($target, ($document | ConvertTo-Json -Depth 100), $utf8NoBom)
}

$processes = [ordered]@{}
$assertions = 0
$failures = @()

try {
    foreach ($id in $authorityIds) {
        $row = if ($id -eq 'island') { $null } else { $topology[$id] }
        $stdin = Join-Path $scratch "$id.stdin.txt"
        $stdout = Join-Path $scratch "$id.stdout.log"
        $stderr = Join-Path $scratch "$id.stderr.log"
        $world = Join-Path $worldDir "quilt-$id.world.json"
        $state = Join-Path $scratch "state-$id"
        $startupWait = if ($id -eq 'nw') { 45 } else { 90 }
        $neighbourWait = if ($id -eq 'nw') { 15 } else { 30 }
        # Scripts express time in authority ticks. Keep every feel/control probe at the same wall-clock duration
        # even though NW deliberately runs at 30 Hz while the other authorities run at 60 Hz.
        $halfSecondWait = if ($id -eq 'nw') { 15 } else { 30 }
        $tenthSecondWait = if ($id -eq 'nw') { 3 } else { 6 }
        $oneSecondWait = if ($id -eq 'nw') { 30 } else { 60 }
        $twoSecondWait = if ($id -eq 'nw') { 60 } else { 120 }
        $fourSecondWait = if ($id -eq 'nw') { 120 } else { 240 }
        $fiveSecondWait = if ($id -eq 'nw') { 150 } else { 300 }
        # Give the cross-authority contact loop half a physical second at either authored rate.
        $contactWait = if ($id -eq 'nw') { 15 } else { 120 }
        $autonomous = if ($id -eq 'island') { '' } else { @"
world.population 2 producer:wander
world.wait 30
"@ }
        $continuation = if ($id -eq 'nw') { @"
player.fly -1 0 0 0 0 0 2 1
world.wait 180
player.where 1
world.view.camera 1
player.fly 0 -1 0 0 0 0 2 1
world.wait 180
player.where 1
world.view.camera 1
player.fly 1 0 0 0 0 0 2.5 1
world.wait 210
player.where 1
world.view.camera 1
"@ } else { '' }
        $script = if ($id -eq 'island') { @"
world.wait $startupWait
wire.errors reset
world.adjacencies
world.wait 300
world.adjacencies
wire.errors
"@ } else { 
        # Probe ordinary open air beside the solid platform. The handoff plane sits below the island, so the
        # traveler must enter island authority first and rise around its edge rather than pass through furniture.
        $upCenter = switch ($id) { 'nw' { '-23 1 -23' }; 'ne' { '23 1 -23' }; 'se' { '23 1 23' }; 'sw' { '-23 1 23' } }
        # Once above platform height, walk/fly from that quadrant's open air toward the island centre. The small
        # vertical drive holds altitude without accumulating ballistic velocity; releasing it then exercises an
        # ordinary gravity landing on the authored top surface.
        $islandApproach = switch ($id) { 'nw' { '-1 1 0.1' }; 'ne' { '-1 -1 0.1' }; 'se' { '1 -1 0.1' }; 'sw' { '1 1 0.1' } }
        @"
world.wait $startupWait
wire.errors reset
$autonomous
world.adjacencies
world.wait $neighbourWait
world.adjacencies
player.pose $($row.ContactOut) 2
player.pose $($row.ContactIn) 3
world.wait $contactWait
player.where 2
player.where 3
player.pose $($row.Pose)
player.fly $($row.Fly)
world.wait 120
player.where 1
player.stop 1
world.wait 5
player.where 1
world.view.camera 1
player.signal gamepad.rightStick 1 0
world.wait 12
player.signal gamepad.rightStick 0 0
world.view.camera 1
player.where 1
player.signal gamepad.leftStick 0.5 0
world.wait 8
player.signal gamepad.leftStick 0 0
player.stop 1
player.where 1
player.signal gamepad.buttonSouth 1
player.signal gamepad.rightTrigger 1
world.wait $halfSecondWait
player.signal gamepad.rightTrigger 1
world.wait $halfSecondWait
player.signal gamepad.rightTrigger 1
world.wait $halfSecondWait
player.signal gamepad.rightTrigger 1
world.wait $halfSecondWait
player.signal gamepad.rightTrigger 0
world.wait $halfSecondWait
player.state 1
player.signal gamepad.buttonSouth 0
world.wait $tenthSecondWait
player.where 1
world.wait $oneSecondWait
player.where 1
player.stop 1
player.signal gamepad.leftTrigger 1
world.wait $halfSecondWait
player.signal gamepad.leftTrigger 1
world.wait $halfSecondWait
player.signal gamepad.leftTrigger 1
world.wait $halfSecondWait
player.signal gamepad.leftTrigger 1
world.wait $halfSecondWait
player.signal gamepad.leftTrigger 0
player.stop 1
player.where 1
world.adjacencies
player.pose $upCenter 0 0 0 4
player.fly 0 0 1 0 0 0 2 4
world.wait $twoSecondWait
player.fly $islandApproach 0 0 0 4 4
world.wait $fourSecondWait
player.stop 4
world.wait $twoSecondWait
player.where 4
$continuation
player.where 4
world.contacts 4
player.press jump 1 0.05 4
world.wait $tenthSecondWait
player.where 4
world.wait $fiveSecondWait
player.where 4
world.contacts 4
wire.errors
"@ }
        [System.IO.File]::WriteAllText($stdin, $script, $utf8NoBom)

        $arguments = @(
            $artifact,
            '--world', $world,
            '--headless', 'true',
            '--exit-after-seconds', '56',
            '--state-dir', $state,
            '--federation-key-file', $keys[$id].Path
        )
        $process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WorkingDirectory $repoRoot `
            -WindowStyle Hidden -RedirectStandardInput $stdin -RedirectStandardOutput $stdout `
            -RedirectStandardError $stderr -PassThru
        $processes[$id] = [PSCustomObject]@{ Process = $process; Stdout = $stdout; Stderr = $stderr }
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while (($processes.Values | Where-Object { -not $_.Process.HasExited }).Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
    foreach ($entry in $processes.Values) {
        if (-not $entry.Process.HasExited) { $failures += "process $($entry.Process.Id) timed out" }
    }

    $observedNamespaces = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $contactPositions = @{}

    foreach ($id in $topology.Keys) {
        $row = $topology[$id]
        $entry = $processes[$id]
        [string] $stdout = if (Test-Path -LiteralPath $entry.Stdout) { (Get-Content -Raw -LiteralPath $entry.Stdout) + '' } else { '' }
        [string] $stderr = if (Test-Path -LiteralPath $entry.Stderr) { (Get-Content -Raw -LiteralPath $entry.Stderr) + '' } else { '' }
        $combined = $stdout + "`n" + $stderr
        $destinationEndpoint = Endpoint $row.Target

        Require ($entry.Process.HasExited -and ($entry.Process.ExitCode -eq 0)) "$id authority did not exit cleanly"
        Require ($stderr.Contains("[world.listen: bound $(Endpoint $id)]")) "$id did not bind its distinct authority endpoint"
        Require ($stdout.Contains('[world.population: 2 ')) "$id did not wake both authored producer bodies"
        Require ([regex]::IsMatch($stdout, "\[world\.adjacency: 'boot/[^']+' seat 5 crossed")) "$id first authored producer body never crossed an invisible boundary"
        Require ([regex]::IsMatch($stdout, "\[world\.adjacency: 'boot/[^']+' seat 6 crossed")) "$id second authored producer body never crossed an invisible boundary"
        Require ($combined.Contains("[world.adjacency: 'boot/$($row.Edge)' seat 1 crossed")) "$id/$($row.Edge) did not cross automatically"
        Require ($stdout.Contains("remote authority $destinationEndpoint")) "$id/$($row.Edge) did not use remote authority $destinationEndpoint"
        Require ([regex]::IsMatch($stdout, '\[player\.where:.*instance:.*\]')) "$id traveler was not queryable through its routed remote authority"
        Require ($combined.Contains("[world.adjacency: 'boot/up' seat 4 crossed")) "$id/up did not cross into the floating island"
        Require ($stdout.Contains("remote authority $(Endpoint 'island')")) "$id/up did not use floating-island authority $(Endpoint 'island')"
        $cameraReads = [regex]::Matches($stdout, '\[world\.view\.camera: player=1 authority=([^ ]+).*? epoch=([0-9]+).*? yaw=(-?[0-9.]+)')
        Require ($cameraReads.Count -ge 2) "$id did not expose both routed camera reads"
        if ($cameraReads.Count -ge 2) {
            Require (($cameraReads[1].Groups[1].Value -ne 'boot') -and ([int]::Parse($cameraReads[1].Groups[2].Value, [Globalization.CultureInfo]::InvariantCulture) -ge 2)) "$id camera did not remain attached to the transferred identity/epoch"
            Require ([math]::Abs([double]::Parse($cameraReads[1].Groups[3].Value, [Globalization.CultureInfo]::InvariantCulture)) -gt 1.0) "$id right-stick signal did not rotate the retained camera"
        }
        Require (-not $stdout.Contains('resolved=false')) "$id exposed a camera epoch with no generation-addressed continuum anchor"
        $whereReads = [regex]::Matches($stdout, '\[player\.where: p[0-9]+ pos=\(([^)]+)\) yaw=(-?[0-9.]+)°[^\r\n]* instance:')
        Require ($whereReads.Count -ge 8) "$id did not expose the routed facing, horizontal, and vertical movement reads"
        if ($whereReads.Count -ge 8) {
            $yawBefore = [double]::Parse($whereReads[1].Groups[2].Value, [Globalization.CultureInfo]::InvariantCulture)
            $yawAfter = [double]::Parse($whereReads[2].Groups[2].Value, [Globalization.CultureInfo]::InvariantCulture)
            Require ([math]::Abs($yawAfter - $yawBefore) -ge 5) "$id right-stick camera turn did not turn the authored camera-facing body"
            if ($cameraReads.Count -ge 2) {
                $cameraYaw = [double]::Parse($cameraReads[1].Groups[3].Value, [Globalization.CultureInfo]::InvariantCulture)
                $facingError = [math]::Abs((($yawAfter - $cameraYaw + 540.0) % 360.0) - 180.0)
                # Camera presentation and authoritative turn close on adjacent clocks, and player.where rounds body
                # yaw to whole degrees. Pin visual alignment while allowing that bounded handoff/tick quantization.
                Require ($facingError -le 5.0) "$id body facing drifted $($facingError.ToString('0.##', [Globalization.CultureInfo]::InvariantCulture)) degrees from camera yaw"
            }
            Require ($whereReads[2].Groups[1].Value -ne $whereReads[3].Groups[1].Value) "$id left-stick signal did not move the traveler under remote authority"
            $before = $whereReads[2].Groups[1].Value.Split(',') | ForEach-Object { [double]::Parse($_.Trim(), [Globalization.CultureInfo]::InvariantCulture) }
            $after = $whereReads[3].Groups[1].Value.Split(',') | ForEach-Object { [double]::Parse($_.Trim(), [Globalization.CultureInfo]::InvariantCulture) }
            $distance = [math]::Sqrt((($after[0] - $before[0]) * ($after[0] - $before[0])) + (($after[1] - $before[1]) * ($after[1] - $before[1])) + (($after[2] - $before[2]) * ($after[2] - $before[2])))
            Require ($distance -ge 0.25) "$id remote held-stick movement collapsed to $($distance.ToString('0.###', [Globalization.CultureInfo]::InvariantCulture)) units across 8 source ticks"
            $beforeAscent = [double]::Parse($whereReads[3].Groups[1].Value.Split(',')[1].Trim(), [Globalization.CultureInfo]::InvariantCulture)
            $afterAscent = [double]::Parse($whereReads[4].Groups[1].Value.Split(',')[1].Trim(), [Globalization.CultureInfo]::InvariantCulture)
            # The island frame's origin is six metres above the ground frame. Compare physical height rather than
            # the two authorities' deliberately different local coordinates.
            $afterAscentPhysical = $afterAscent + 6.0
            Require ($afterAscentPhysical -gt ($beforeAscent + 0.25)) "$id ascent did not survive the federated handoff's authored traversal program"
            $afterReleasePhysical = [double]::Parse($whereReads[5].Groups[1].Value.Split(',')[1].Trim(), [Globalization.CultureInfo]::InvariantCulture) + 6.0
            Require ($afterReleasePhysical -le ($afterAscentPhysical + 0.05)) "$id continued ascending after the trigger's completed release edge"
            $afterDescent = [double]::Parse($whereReads[6].Groups[1].Value.Split(',')[1].Trim(), [Globalization.CultureInfo]::InvariantCulture)
            Require ($afterDescent -lt ($afterAscentPhysical - 0.25)) "$id descent did not survive the federated handoff's authored traversal program"
            $islandLanding = [double]::Parse($whereReads[7].Groups[1].Value.Split(',')[1].Trim(), [Globalization.CultureInfo]::InvariantCulture)
            # Any island surface counts (base floor reads ~0.05, the rim ~0.21) - the grounded contact read below proves
# standing; this bound only refuses a body still airborne or hovering. Resting heights are not pinned.
            Require (($islandLanding -ge -0.1) -and ($islandLanding -le 1.0)) "$id open-air ascent did not settle onto an island surface"
            if ($whereReads.Count -ge 11) {
                $jumpBase = [double]::Parse($whereReads[$whereReads.Count - 3].Groups[1].Value.Split(',')[1].Trim(), [Globalization.CultureInfo]::InvariantCulture)
                $jumpPeak = [double]::Parse($whereReads[$whereReads.Count - 2].Groups[1].Value.Split(',')[1].Trim(), [Globalization.CultureInfo]::InvariantCulture)
                $jumpRest = [double]::Parse($whereReads[$whereReads.Count - 1].Groups[1].Value.Split(',')[1].Trim(), [Globalization.CultureInfo]::InvariantCulture)
                Require ($jumpPeak -gt ($jumpBase + 0.25)) "$id post-transition jump did not leave the island surface"
                Require ([math]::Abs($jumpRest - $jumpBase) -le 0.08) "$id post-transition jump did not return to the same authored resting height"
            } else {
                Require $false "$id did not expose the post-transition jump baseline, peak, and landing"
            }
        }
        Require (-not [regex]::IsMatch($stdout, '\[wire\.errors: [1-9][0-9]* rejected\]')) "$id reported rejected wire commands"
        $groundedReads = [regex]::Matches($stdout, '\[world\.contacts: p[0-9]+ grounded=true ')
        Require ($groundedReads.Count -ge 2) "$id post-transition jump was not grounded both before takeoff and after landing"
        Require ($stdout.Contains('derived=corner') -and $stdout.Contains("entities=$(Endpoint $row.Corner)/")) "$id did not derive its diagonal corner peer $($row.Corner)"
        Require (-not $stdout.Contains('entities=boot/')) "$id observed a process-local boot entity address"
        Require (-not $combined.Contains('[world.continuum: committed transfer=')) "$id could not seed a committed authority epoch before publishing its route"
        Require (-not $combined.Contains('has not delivered body:')) "$id exposed an inactive presentation interval between committed authority writers"
        Require (-not $combined.Contains('intent stream to') -and -not $combined.Contains('intent stream update names no')) "$id lost or refused its persistent federated input lane"
        Require (-not $combined.Contains('no committed onward route') -and -not $combined.Contains('release could not follow')) "$id lost a generation-addressed onward route under crowded slot reuse"
        Require (-not $combined.Contains('[world.authority unavailable:')) "$id exposed a transient authority outage on the committed route"
        Require (-not [regex]::IsMatch($combined, 'Unhandled exception|ABORTED| refused \(')) "$id emitted an exception, abort, or refusal"

        foreach ($neighbour in $row.Neighbours) {
            $namespace = Endpoint $neighbour
            Require ($stdout.Contains("entities=$namespace/") -or $stdout.Contains(",$namespace/")) "$id did not receive addressable entities from $namespace"
            [void] $observedNamespaces.Add($namespace)
        }

        $contactOut = [regex]::Match($stdout, '\[player\.where: p2 pos=\(([-0-9.]+), ([-0-9.]+), ([-0-9.]+)\)')
        $contactIn = [regex]::Match($stdout, '\[player\.where: p3 pos=\(([-0-9.]+), ([-0-9.]+), ([-0-9.]+)\)')
        Require ($contactOut.Success -and $contactIn.Success) "$id did not expose both seam-contact bodies"
        if ($contactOut.Success -and $contactIn.Success) {
            $outCoordinate = [double]::Parse($contactOut.Groups[$row.OutAxis + 1].Value, [Globalization.CultureInfo]::InvariantCulture)
            $inCoordinate = [double]::Parse($contactIn.Groups[$row.InAxis + 1].Value, [Globalization.CultureInfo]::InvariantCulture)
            $contactPositions[$id] = [PSCustomObject]@{ Out = $outCoordinate; In = $inCoordinate }
        }
    }

    $islandEntry = $processes.island
    [string] $islandStdout = if (Test-Path -LiteralPath $islandEntry.Stdout) { Get-Content -Raw -LiteralPath $islandEntry.Stdout } else { '' }
    [string] $islandStderr = if (Test-Path -LiteralPath $islandEntry.Stderr) { Get-Content -Raw -LiteralPath $islandEntry.Stderr } else { '' }
    Require ($islandEntry.Process.HasExited -and ($islandEntry.Process.ExitCode -eq 0)) 'island authority did not exit cleanly'
    Require ($islandStderr.Contains("[world.listen: bound $(Endpoint 'island')]")) 'island did not bind its distinct authority endpoint'
    Require (-not ($islandStdout + "`n" + $islandStderr).Contains('[wire.errors: 1 rejected')) 'island rejected a wire command'

    Require ($observedNamespaces.Count -eq 4) "the delivered entity addresses did not cover four distinct authority namespaces"

    foreach ($id in $topology.Keys) {
        $target = $topology[$id].Target
        if ($contactPositions.ContainsKey($id) -and $contactPositions.ContainsKey($target)) {
            Require ([math]::Abs($contactPositions[$id].Out - $contactPositions[$target].In) -ge 0.65) "$id->$target seam-contact pair did not settle to a non-overlapping cross-authority state"
        } else {
            Require $false "$id->$target seam-contact pair had no comparable routed poses"
        }
    }

    $nwTranscript = Get-Content -Raw -LiteralPath $processes.nw.Stdout
    Require (([regex]::Matches($nwTranscript, '\[player\.where:')).Count -ge 6) "nw traveler did not remain queryable after remote controls and every onward handoff"
    Require ($nwTranscript.Contains("[world.adjacency: 'boot/east' seat 5 crossed")) "nw autonomous body did not cross the invisible east boundary"
    Require ([regex]::IsMatch($nwTranscript, "\[world\.transfer:.*'boot' seat 5 departed -> '.*' seat [5-9][0-9]* arrived \(anonymous\)")) "nw autonomous body did not migrate as an anonymous server-authored entity"
    $anonymousAuthorities = 0
    $anonymousTransfers = 0
    foreach ($id in $topology.Keys) {
        $transcript = Get-Content -Raw -LiteralPath $processes[$id].Stdout
        $count = ([regex]::Matches($transcript, '\[world\.transfer:.*arrived \(anonymous\)')).Count
        $anonymousTransfers += $count
        if ($count -gt 0) { $anonymousAuthorities++ }
    }
    Require ($anonymousTransfers -ge 3) "the producer-driven body did not complete a multi-hop autonomous journey"
    Require ($anonymousAuthorities -ge 3) "autonomous ownership did not pass through at least three distinct authorities"
    Require (-not [regex]::IsMatch($nwTranscript, 'no live or forwarded transfer body|no committed onward route|forwarded body:.*no committed destination credential')) "nw traveler hit a dead forwarding route"
    $nwRouteAuthorities = [regex]::Matches($nwTranscript, '\[world\.view\.camera:.*? entity=([^/ ]+)/[0-9]+#[0-9]+ epoch=[0-9]+ resolved=true') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    Require ($nwRouteAuthorities.Count -ge 4) "nw traveler control/presentation route did not advance through four distinct authority writers"
    $nwCameraEpochs = [regex]::Matches($nwTranscript, '\[world\.view\.camera:.*? epoch=([0-9]+) resolved=true')
    Require ($nwCameraEpochs.Count -ge 5) "nw did not read back a resolved camera after every onward authority handoff"
    if ($nwCameraEpochs.Count -ge 5) {
        Require ([int]$nwCameraEpochs[$nwCameraEpochs.Count - 1].Groups[1].Value -ge 5) "nw camera route did not advance through a distinct CAS epoch for every authority handoff"
    }
    $nwCameraMatches = [regex]::Matches($nwTranscript, '\[world\.view\.camera:.*?anchor=\(([-0-9.]+),([-0-9.]+),([-0-9.]+)\)')
    $nwFinalAnchor = $nwCameraMatches | Select-Object -Last 1
    $nwBeforeFinalCamera = if ($null -ne $nwFinalAnchor) { $nwTranscript.Substring(0, $nwFinalAnchor.Index) } else { '' }
    # Pair the camera read with the traveler query immediately preceding that camera epoch. Later p4 island/jump
    # probes name a different body and must never be compared with p1's camera merely because they occur last.
    $nwFinalWhere = [regex]::Matches($nwBeforeFinalCamera, '\[player\.where: p[0-9]+ pos=\(([-0-9.]+), ([-0-9.]+), ([-0-9.]+)\)') | Select-Object -Last 1
    Require (($null -ne $nwFinalWhere) -and ($null -ne $nwFinalAnchor)) "nw did not expose the final traveler and camera anchor poses"
    if (($null -ne $nwFinalWhere) -and ($null -ne $nwFinalAnchor)) {
        $deltaSquared = 0.0
        for ($axis = 1; $axis -le 3; $axis++) {
            $bodyCoordinate = [double]::Parse($nwFinalWhere.Groups[$axis].Value, [Globalization.CultureInfo]::InvariantCulture)
            $anchorCoordinate = [double]::Parse($nwFinalAnchor.Groups[$axis].Value, [Globalization.CultureInfo]::InvariantCulture)
            $deltaSquared += [math]::Pow($bodyCoordinate - $anchorCoordinate, 2)
        }
        Require ($deltaSquared -lt 0.04) "nw camera anchor was not colocated with the traveler after returning through the fourth authority"
    }

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

Write-Output 'PASS: Four Corners plus the floating island ran as five distinct authorities; horizontal and vertical handoffs, simultaneous cross-host body contact, eight autonomous travelers, retained dual-stick control, diagonal peers, and one full human traveler circuit all held.'
exit 0
