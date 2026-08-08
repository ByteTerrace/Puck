# MANUAL harness — see docs/verification/manual/README.md for the contract (hand-run, not a battery/gate,
# requires exclusive desktop foreground, injects global SendInput).
#
# Play-mode inert control, then click-select and click-empty-clears with the editor active.
. "$PSScriptRoot\lib.ps1"

$state = Join-Path $global:Scratch 'state-click'
$w = Start-World -StateDir $state -ExitAfter 180
try {
    $size = Get-ClientSize -W $w
    Write-Host "client=$($size.W)x$($size.H)"

    # Park the cursor mid-window and confirm the store sees it.
    Move-CursorClient -W $w -X ($size.W * 0.5) -Y ($size.H * 0.5)
    $pointer = Probe -W $w -Verb 'world.view.pointer' -Pattern '\[world\.view\.pointer:'
    Assert ($null -ne $pointer -and $pointer -match 'visible=true') "cursor visible mid-window ($pointer)"

    # ---- CONTROL: editor INACTIVE — a click must select nothing, mutate nothing, narrate nothing.
    Press-Left -W $w
    $held = Probe -W $w -Verb 'world.view.pointer' -Pattern '\[world\.view\.pointer:'
    Assert ($held -match 'buttons=L') "injected press visible in the store (buttons=L): $held"
    Release-Left -W $w
    Start-Sleep -Milliseconds 400
    Assert ((Count-Matches -List $w.Out -Pattern '^\[editor\.select') -eq 0) 'play-mode click dispatched no editor.select'
    Assert ((Count-Matches -List $w.Err -Pattern '\[editor\.mouse\]') -eq 0) 'play-mode click produced no editor.mouse act'
    Assert ((Count-Matches -List $w.Err -Pattern '\[world\.mutation') -eq 0) 'play-mode click produced no mutation'
    $status = Probe -W $w -Verb 'world.status' -Pattern '\[world\.status:'
    Assert ($status -match 'dirty 0 ') "play-mode click left the journal empty ($status)"

    # ---- Editor ON.
    $enter = Probe -W $w -Verb 'editor.enter' -Pattern '\[editor\.enter:'
    Assert ($enter -match 'editing') "editor entered ($enter)"
    $pose = Probe -W $w -Verb 'editor.cam.pose 10 6 -1 0 -35' -Pattern '\[editor\.cam\.pose:'
    Assert ($null -ne $pose) "camera posed ($pose)"
    Start-Sleep -Milliseconds 300

    # Scan the mid-column until the cabinet hovers.
    $hit = $null
    foreach ($frac in 0.40, 0.44, 0.48, 0.52, 0.56, 0.60, 0.64, 0.36, 0.32) {
        Move-CursorClient -W $w -X ($size.W * 0.5) -Y ($size.H * $frac)
        $probe = Probe -W $w -Verb 'world.view.pointer' -Pattern '\[world\.view\.pointer:'
        Write-Host "scan y=$frac -> $probe"
        if ($probe -match "hover=placements 'arcade-cabinet'") { $hit = $frac; break }
    }
    Assert ($null -ne $hit) 'cursor hover resolves the arcade-cabinet placement'

    # ---- Click-select: the press dispatches the existing editor.select verb; no mutation for a motionless click.
    $outMark = $w.Out.Count
    Press-Left -W $w
    Release-Left -W $w
    $select = Wait-ForLine -List $w.Out -Pattern "^\[editor\.select: seat 1 placements 'arcade-cabinet'" -After $outMark -TimeoutSec 8
    Assert ($null -ne $select) "click dispatched editor.select naming the row ($select)"
    $es = Probe -W $w -Verb 'editor.status' -Pattern '\[editor\.status:'
    Assert ($es -match "sel=placements 'arcade-cabinet'") "editor.status names the clicked selection ($es)"
    $status = Probe -W $w -Verb 'world.status' -Pattern '\[world\.status:'
    Assert ($status -match 'dirty 0 ') "a motionless click committed nothing ($status)"

    # ---- Click on nothing clears: the grass below the cabinet picks nothing (hover=none), click there.
    Move-CursorClient -W $w -X ($size.W * 0.5) -Y ($size.H * 0.62)
    $sky = Probe -W $w -Verb 'world.view.pointer' -Pattern '\[world\.view\.pointer:'
    Assert ($sky -match 'hover=none') "sky cursor hovers nothing ($sky)"
    $outMark = $w.Out.Count
    Press-Left -W $w
    Release-Left -W $w
    $clear = Wait-ForLine -List $w.Out -Pattern '^\[editor\.select: seat 1 cleared' -After $outMark -TimeoutSec 8
    Assert ($null -ne $clear) "empty-space click cleared the selection ($clear)"
    $es = Probe -W $w -Verb 'editor.status' -Pattern '\[editor\.status:'
    Assert ($es -match 'sel=none') "editor.status shows no selection ($es)"

    Write-Host 'ALL PASS'
} finally {
    Dump-Streams -W $w -Prefix 'click-select-and-clear'
    Stop-World -W $w
}
