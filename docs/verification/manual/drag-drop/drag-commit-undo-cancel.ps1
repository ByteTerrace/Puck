# MANUAL harness — see docs/verification/manual/README.md for the contract (hand-run, not a battery/gate,
# requires exclusive desktop foreground, injects global SendInput).
#
# Drag-and-drop core: one coalesced mutation (dirty +1 exactly), one undo restores the full pre-drag state
# (byte-identity), a committed drag byte-differs with the moved row present, and a grab+cancel leaves the
# document byte-identical (the discriminating control).
. "$PSScriptRoot\lib.ps1"

function Get-Dirty { param($W) $s = Probe -W $W -Verb 'world.status' -Pattern '\[world\.status:'; if ($s -match 'dirty (\d+) ') { [int]$Matches[1] } else { throw "no dirty in $s" } }
function Find-Cabinet {
    param($W, $Size)
    foreach ($frac in 0.40, 0.44, 0.48, 0.52, 0.36) {
        Move-CursorClient -W $W -X ($Size.W * 0.5) -Y ($Size.H * $frac)
        $probe = Probe -W $W -Verb 'world.view.pointer' -Pattern '\[world\.view\.pointer:'
        if ($probe -match "hover=placements 'arcade-cabinet'") { return $frac }
    }
    throw 'FAIL: could not hover the arcade-cabinet'
}

$state = Join-Path $global:Scratch 'state-drag-commit'
$w = Start-World -StateDir $state -ExitAfter 300
try {
    $size = Get-ClientSize -W $w
    $null = Probe -W $w -Verb 'editor.enter' -Pattern '\[editor\.enter:'
    $null = Probe -W $w -Verb 'editor.cam.pose 10 6 -1 0 -35' -Pattern '\[editor\.cam\.pose:'
    Start-Sleep -Milliseconds 300
    $frac = Find-Cabinet -W $w -Size $size

    # Order per the evidence rule: save (compacts) -> read dirty -> drag -> read dirty -> undo -> THEN saves.
    $pre = Join-Path $global:Scratch 'drag-commit-pre.json'
    $null = Probe -W $w -Verb "world.save $pre" -Pattern '\[world\.save:'
    $dirty0 = Get-Dirty -W $w
    Assert ($dirty0 -eq 0) "journal compacted before the drag (dirty $dirty0)"

    # ---- Drag: press on the cabinet, pull the cursor, release.
    $errMark = $w.Err.Count
    $mutBefore = Count-Matches -List $w.Err -Pattern '\[world\.mutation'
    $ptrPre = Probe -W $w -Verb 'world.view.pointer' -Pattern '\[world\.view\.pointer:'
    Assert ($ptrPre -match 'syscount=(\d+)') "pointer echo carries syscount ($ptrPre)"
    $sysPre = [int]$Matches[1]
    Press-Left -W $w
    $dragLine = Wait-ForLine -List $w.Err -Pattern "\[editor\.mouse\] seat 1 dragging placements 'arcade-cabinet'" -After $errMark -TimeoutSec 8
    Assert ($null -ne $dragLine) "press grabbed the row into the drag channel ($dragLine)"
    foreach ($step in 1..6) {
        Move-CursorClient -W $w -X ($size.W * 0.5 - $step * 40) -Y ($size.H * $frac)
    }
    $mid = Probe -W $w -Verb 'editor.status' -Pattern '\[editor\.status:'
    Assert ($mid -match 'drag=') "editor.status shows the live drag ($mid)"
    Release-Left -W $w
    $commit = Wait-ForLine -List $w.Err -Pattern '\[editor\.mouse\] seat 1 placement .arcade-cabinet.*one mutation submitted' -After $errMark -TimeoutSec 8
    Assert ($null -ne $commit) "release committed through the channel ($commit)"
    $ptrPost = Probe -W $w -Verb 'world.view.pointer' -Pattern '\[world\.view\.pointer:'
    Assert ($ptrPost -match 'syscount=(\d+)') "pointer echo carries syscount after release ($ptrPost)"
    Assert (([int]$Matches[1]) -eq $sysPre) "a real release never advances the system-release counter (pre=$sysPre post=$($Matches[1]))"
    Send-Line -W $w -Line 'world.wait 30'
    Start-Sleep -Milliseconds 800
    $mutAfter = Count-Matches -List $w.Err -Pattern '\[world\.mutation'
    Assert (($mutAfter - $mutBefore) -eq 1) "exactly ONE mutation for the whole drag (before=$mutBefore after=$mutAfter)"
    $dirty1 = Get-Dirty -W $w
    Assert ($dirty1 -eq ($dirty0 + 1)) "journal grew by exactly one entry (dirty $dirty0 -> $dirty1)"
    $es = Probe -W $w -Verb 'editor.status' -Pattern '\[editor\.status:'
    Assert ($es -notmatch 'drag=') "the drag retired after its own apply ($es)"
    Assert ($es -match "sel=placements 'arcade-cabinet' at \((?!10\.00, 0\.90, 6\.00)") "the selection resolves at a MOVED position ($es)"

    # ---- Undo: one step restores the whole pre-drag state.
    $null = Probe -W $w -Verb 'world.undo 1' -Pattern 'world\.undo'
    Send-Line -W $w -Line 'world.wait 30'
    Start-Sleep -Milliseconds 800
    $esUndo = Probe -W $w -Verb 'editor.status' -Pattern '\[editor\.status:'
    Assert ($esUndo -match "at \(10\.00, 0\.90, 6\.00\)|sel=none") "undo returned the row to its pre-drag position ($esUndo)"
    $post = Join-Path $global:Scratch 'drag-commit-post.json'
    $null = Probe -W $w -Verb "world.save $post" -Pattern '\[world\.save:'
    Start-Sleep -Milliseconds 300
    $preBytes = [IO.File]::ReadAllBytes($pre); $postBytes = [IO.File]::ReadAllBytes($post)
    Assert (([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($preBytes))) -eq ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($postBytes)))) 'undo leaves world.save byte-identical to the pre-drag save'

    # ---- Control: grab + cancel leaves the document byte-identical and journals nothing (the cabinet is back at
    # its authored spot after the undo above, so the same scan finds it).
    $pre2 = Join-Path $global:Scratch 'drag-commit-pre2.json'
    $null = Probe -W $w -Verb "world.save $pre2" -Pattern '\[world\.save:'
    $dirty2 = Get-Dirty -W $w
    $mutBefore = Count-Matches -List $w.Err -Pattern '\[world\.mutation'
    $frac = Find-Cabinet -W $w -Size $size
    $errMark = $w.Err.Count
    Press-Left -W $w
    $null = Wait-ForLine -List $w.Err -Pattern '\[editor\.mouse\] seat 1 dragging' -After $errMark -TimeoutSec 8
    foreach ($step in 1..4) { Move-CursorClient -W $w -X ($size.W * 0.5 - $step * 40) -Y ($size.H * $frac) }
    $cancel = Probe -W $w -Verb 'editor.cancel' -Pattern '\[editor\.cancel:'
    Assert ($cancel -match 'back at its document pose') "editor.cancel aborted the live drag ($cancel)"
    Release-Left -W $w
    Start-Sleep -Milliseconds 500
    $mutAfter = Count-Matches -List $w.Err -Pattern '\[world\.mutation'
    Assert (($mutAfter - $mutBefore) -eq 0) 'a cancelled drag submits nothing'
    Assert ((Get-Dirty -W $w) -eq $dirty2) 'a cancelled drag journals nothing'
    $cancelSave = Join-Path $global:Scratch 'drag-commit-cancel.json'
    $null = Probe -W $w -Verb "world.save $cancelSave" -Pattern '\[world\.save:'
    Start-Sleep -Milliseconds 300
    Assert (([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($cancelSave)))) -eq ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($pre2))))) 'a grab+cancel drag leaves world.save byte-identical'
    $es = Probe -W $w -Verb 'editor.status' -Pattern '\[editor\.status:'
    Assert ($es -notmatch 'drag=') "no pending row survives the cancel ($es)"

    # ---- Committed drag byte-differs and carries the moved row.
    $frac = Find-Cabinet -W $w -Size $size
    Press-Left -W $w
    foreach ($step in 1..5) { Move-CursorClient -W $w -X ($size.W * 0.5 - $step * 40) -Y ($size.H * $frac) }
    Release-Left -W $w
    Send-Line -W $w -Line 'world.wait 30'
    Start-Sleep -Milliseconds 800
    $moved = Join-Path $global:Scratch 'drag-commit-moved.json'
    $null = Probe -W $w -Verb "world.save $moved" -Pattern '\[world\.save:'
    Start-Sleep -Milliseconds 300
    $movedBytes = [IO.File]::ReadAllBytes($moved)
    Assert (([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($movedBytes))) -ne ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($preBytes)))) 'a committed drag byte-differs from the pre-drag save'
    $movedDoc = Get-Content $moved -Raw | ConvertFrom-Json
    $cab = $movedDoc.placements | Where-Object { $_.id -eq 'arcade-cabinet' }
    Assert ($null -ne $cab) 'the moved row is present in world.save'
    Assert ([Math]::Abs($cab.position[0] - 10) -gt 0.5) "the moved row's X actually moved (x=$($cab.position[0]))"
    Assert ([Math]::Abs($cab.position[1] - 0.9) -lt 0.001) "the drag held the grab plane (y=$($cab.position[1]))"
    Write-Host "moved position: $($cab.position -join ', ')"

    Write-Host 'ALL PASS'
} finally {
    Dump-Streams -W $w -Prefix 'drag-commit-undo-cancel'
    Stop-World -W $w
}
