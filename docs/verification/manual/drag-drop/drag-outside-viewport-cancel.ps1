# MANUAL harness — see docs/verification/manual/README.md for the contract (hand-run, not a battery/gate,
# requires exclusive desktop foreground, injects global SendInput).
#
# A genuine release with the cursor OUTSIDE the seat's viewport CANCELS — release-outside-commits-nothing.
# A second joined seat splits the window; seat 1 keeps the left half, and the release lands in seat 2's half.
. "$PSScriptRoot\lib.ps1"

function Get-Dirty { param($W) $s = Probe -W $W -Verb 'world.status' -Pattern '\[world\.status:'; if ($s -match 'dirty (\d+) ') { [int]$Matches[1] } else { throw "no dirty in $s" } }

$state = Join-Path $global:Scratch 'state-outside-viewport'
$w = Start-World -StateDir $state -ExitAfter 300
try {
    $size = Get-ClientSize -W $w
    $join = Probe -W $w -Verb 'player.join 2' -Pattern 'player\.join'
    Write-Host "join: $join"
    Start-Sleep -Milliseconds 500
    $view = Probe -W $w -Verb 'world.view.state' -Pattern '\[world\.view\.state:'
    Write-Host "view: $view"
    Assert ($view -match 'slots=2') "two seats compose a split ($view)"

    $null = Probe -W $w -Verb 'editor.enter' -Pattern '\[editor\.enter:'
    $null = Probe -W $w -Verb 'editor.cam.pose 10 6 -1 0 -35' -Pattern '\[editor\.cam\.pose:'
    Start-Sleep -Milliseconds 300

    # Seat 1 keeps the wide-left sole-editor viewport (0,0,0.7,1): its center sits at client x=0.35.
    $frac = $null
    foreach ($f in 0.40, 0.44, 0.48, 0.52, 0.36, 0.56) {
        Move-CursorClient -W $w -X ($size.W * 0.35) -Y ($size.H * $f)
        $probe = Probe -W $w -Verb 'world.view.pointer' -Pattern '\[world\.view\.pointer:'
        Write-Host "scan y=$f -> $probe"
        if ($probe -match "hover=placements 'arcade-cabinet'") { $frac = $f; break }
    }
    Assert ($null -ne $frac) 'cabinet hovered inside the seat-1 viewport'

    $pre = Join-Path $global:Scratch 'drag-outside-pre.json'
    $null = Probe -W $w -Verb "world.save $pre" -Pattern '\[world\.save:'
    $dirty0 = Get-Dirty -W $w
    $mutBefore = Count-Matches -List $w.Err -Pattern '\[world\.mutation'

    $errMark = $w.Err.Count
    Press-Left -W $w
    $null = Wait-ForLine -List $w.Err -Pattern '\[editor\.mouse\] seat 1 dragging' -After $errMark -TimeoutSec 8
    # Drag a real distance INSIDE the viewport first, then off its edge into seat 2's half.
    Move-CursorClient -W $w -X ($size.W * 0.30) -Y ($size.H * $frac)
    Move-CursorClient -W $w -X ($size.W * 0.45) -Y ($size.H * $frac)
    Move-CursorClient -W $w -X ($size.W * 0.75) -Y ($size.H * $frac)
    $outside = Probe -W $w -Verb 'world.view.pointer' -Pattern '\[world\.view\.pointer:'
    Assert ($outside -match 'reason=outside-viewport') "the cursor stands outside seat 1's viewport ($outside)"
    Release-Left -W $w
    $cancel = Wait-ForLine -List $w.Err -Pattern '\[editor\.mouse\] seat 1 drag cancelled — released outside the seat viewport \(outside-viewport\)' -After $errMark -TimeoutSec 10
    Assert ($null -ne $cancel) "release outside the viewport cancelled ($cancel)"

    Assert ((Count-Matches -List $w.Err -Pattern '\[world\.mutation') -eq $mutBefore) 'no mutation was submitted'
    Assert ((Get-Dirty -W $w) -eq $dirty0) 'the journal is untouched'
    $es = Probe -W $w -Verb 'editor.status' -Pattern '\[editor\.status:'
    Assert ($es -notmatch 'drag=') "the pending row is gone ($es)"
    $post = Join-Path $global:Scratch 'drag-outside-post.json'
    $null = Probe -W $w -Verb "world.save $post" -Pattern '\[world\.save:'
    Start-Sleep -Milliseconds 300
    Assert (([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($post)))) -eq ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($pre))))) 'world.save is byte-identical to the pre-drag save'

    Write-Host 'ALL PASS'
} finally {
    Dump-Streams -W $w -Prefix 'drag-outside-viewport-cancel'
    Stop-World -W $w
}
