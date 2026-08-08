# MANUAL harness — see docs/verification/manual/README.md for the contract (hand-run, not a battery/gate,
# requires exclusive desktop foreground, injects global SendInput).
#
# Client-resize divergence: the client->frame mapping is re-derived every frame, so a mid-drag window resize
# moves the pending row under a STATIONARY physical cursor (raw-client-pixel processing would move nothing),
# and the drop lands where the cursor points post-resize (ray-math prediction).
. "$PSScriptRoot\lib.ps1"

function Get-Local { param([string]$Probe) if ($Probe -match 'local=([-\d.]+),([-\d.]+)') { @([double]$Matches[1], [double]$Matches[2]) } else { throw "no local in $Probe" } }
function Get-DragPos { param([string]$Es) if ($Es -match 'drag=placement .arcade-cabinet. at \(([-\d.]+), ([-\d.]+), ([-\d.]+)\)') { @([double]$Matches[1], [double]$Matches[2], [double]$Matches[3]) } else { throw "no drag pos in $Es" } }

$state = Join-Path $global:Scratch 'state-resize'
$w = Start-World -StateDir $state -ExitAfter 300
try {
    $size = Get-ClientSize -W $w
    $null = Probe -W $w -Verb 'editor.enter' -Pattern '\[editor\.enter:'
    $null = Probe -W $w -Verb 'editor.cam.pose 10 6 -1 0 -35' -Pattern '\[editor\.cam\.pose:'
    Start-Sleep -Milliseconds 300

    $frac = $null
    foreach ($f in 0.40, 0.44, 0.48, 0.36) {
        Move-CursorClient -W $w -X ($size.W * 0.5) -Y ($size.H * $f)
        $probe = Probe -W $w -Verb 'world.view.pointer' -Pattern '\[world\.view\.pointer:'
        if ($probe -match "hover=placements 'arcade-cabinet'") { $frac = $f; break }
    }
    Assert ($null -ne $frac) 'cabinet hovered'

    $pressProbe = Probe -W $w -Verb 'world.view.pointer' -Pattern '\[world\.view\.pointer:'
    $local0 = Get-Local -Probe $pressProbe
    $errMark = $w.Err.Count
    Press-Left -W $w
    $null = Wait-ForLine -List $w.Err -Pattern '\[editor\.mouse\] seat 1 dragging' -After $errMark -TimeoutSec 8
    # A couple of ordinary moved frames first, then hold still.
    Move-CursorClient -W $w -X ($size.W * 0.5 - 60) -Y ($size.H * $frac)
    Move-CursorClient -W $w -X ($size.W * 0.5 - 120) -Y ($size.H * $frac)
    $beforeResize = Probe -W $w -Verb 'editor.status' -Pattern '\[editor\.status:'
    $p1 = Get-DragPos -Es $beforeResize
    $localBefore = Get-Local -Probe (Probe -W $w -Verb 'world.view.pointer' -Pattern '\[world\.view\.pointer:')

    # Resize the window mid-drag; the physical cursor does NOT move.
    $rect = [Native+RECT]::new()
    $null = [Native]::GetWindowRect($w.Hwnd, [ref]$rect)
    $SWP_NOMOVE = 0x0002; $SWP_NOZORDER = 0x0004
    if (-not [Native]::SetWindowPos($w.Hwnd, [IntPtr]::Zero, 0, 0, 1800, 1010, ($SWP_NOMOVE -bor $SWP_NOZORDER))) { throw 'SetWindowPos failed' }
    Start-Sleep -Milliseconds 600
    $newSize = Get-ClientSize -W $w
    Write-Host "client resized to $($newSize.W)x$($newSize.H)"
    Assert ($newSize.W -ne $size.W) 'client extent actually changed'

    $afterProbe = Probe -W $w -Verb 'world.view.pointer' -Pattern '\[world\.view\.pointer:'
    $localAfter = Get-Local -Probe $afterProbe
    Assert ([Math]::Abs($localAfter[0] - $localBefore[0]) -gt 0.01) "a stationary cursor re-maps to a new local X after the resize ($($localBefore[0]) -> $($localAfter[0]))"
    $afterStatus = Probe -W $w -Verb 'editor.status' -Pattern '\[editor\.status:'
    $p2 = Get-DragPos -Es $afterStatus
    Assert (([Math]::Abs($p2[0] - $p1[0]) + [Math]::Abs($p2[2] - $p1[2])) -gt 0.2) "the pending row moved under a stationary cursor from the live re-map (($($p1 -join ',')) -> ($($p2 -join ',')))"

    # Prediction: pending = origin + (P(localNow) - P(localPress)) on the y=0.9 plane through the posed camera.
    $origin = @(10.0, 0.9, 6.0)
    $pp = Plane-Point -Eye @(10.0, 6.0, -1.0) -YawDeg 0 -PitchDeg -35 -LocalX $local0[0] -LocalY $local0[1] -AspectW 1920 -AspectH 1080 -PlaneY 0.9
    $pn = Plane-Point -Eye @(10.0, 6.0, -1.0) -YawDeg 0 -PitchDeg -35 -LocalX $localAfter[0] -LocalY $localAfter[1] -AspectW 1920 -AspectH 1080 -PlaneY 0.9
    $expected = @(($origin[0] + $pn[0] - $pp[0]), 0.9, ($origin[2] + $pn[2] - $pp[2]))
    Write-Host "predicted pending: $($expected -join ', ') actual: $($p2 -join ', ')"
    Assert (([Math]::Abs($p2[0] - $expected[0]) -lt 0.3) -and ([Math]::Abs($p2[2] - $expected[2]) -lt 0.3)) 'the pending row sits where the CURSOR ray points post-resize (frame-mapped, not raw client pixels)'

    # Release inside: the drop commits at the cursor-pointed position.
    Release-Left -W $w
    $commit = Wait-ForLine -List $w.Err -Pattern 'one mutation submitted' -After $errMark -TimeoutSec 8
    Assert ($null -ne $commit) "post-resize release committed ($commit)"
    Send-Line -W $w -Line 'world.wait 30'
    $moved = Join-Path $global:Scratch 'drag-resize-moved.json'
    $null = Probe -W $w -Verb "world.save $moved" -Pattern '\[world\.save:'
    Start-Sleep -Milliseconds 300
    $cab = (Get-Content $moved -Raw | ConvertFrom-Json).placements | Where-Object { $_.id -eq 'arcade-cabinet' }
    Write-Host "dropped at: $($cab.position -join ', ')"
    Assert (([Math]::Abs($cab.position[0] - $expected[0]) -lt 0.3) -and ([Math]::Abs($cab.position[2] - $expected[2]) -lt 0.3)) 'the drop landed where the cursor points post-resize'

    Write-Host 'ALL PASS'
} finally {
    Dump-Streams -W $w -Prefix 'drag-resize-remap'
    Stop-World -W $w
}
