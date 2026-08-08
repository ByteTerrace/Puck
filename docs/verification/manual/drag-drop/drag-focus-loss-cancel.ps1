# MANUAL harness — see docs/verification/manual/README.md for the contract (hand-run, not a battery/gate,
# requires exclusive desktop foreground, injects global SendInput).
#
# Focus loss mid-drag CANCELS, never commits — the synthetic release (system-release counter advanced since
# press) is discriminated from a real one. Focus is stolen by an owned inert WinForms window.
. "$PSScriptRoot\lib.ps1"
Add-Type -AssemblyName System.Windows.Forms

function Get-Dirty { param($W) $s = Probe -W $W -Verb 'world.status' -Pattern '\[world\.status:'; if ($s -match 'dirty (\d+) ') { [int]$Matches[1] } else { throw "no dirty in $s" } }
function Get-Syscount { param([string]$Probe) if ($Probe -match 'syscount=(\d+)') { [int]$Matches[1] } else { throw "no syscount in $Probe" } }

$state = Join-Path $global:Scratch 'state-focus-loss'
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

    $pre = Join-Path $global:Scratch 'drag-focus-loss-pre.json'
    $null = Probe -W $w -Verb "world.save $pre" -Pattern '\[world\.save:'
    $dirty0 = Get-Dirty -W $w
    $sysPre = Get-Syscount -Probe (Probe -W $w -Verb 'world.view.pointer' -Pattern '\[world\.view\.pointer:')
    $mutBefore = Count-Matches -List $w.Err -Pattern '\[world\.mutation'

    # Press, drag a real distance (a commit here would be a REAL byte-visible edit — the discriminating stake).
    $errMark = $w.Err.Count
    Press-Left -W $w
    $null = Wait-ForLine -List $w.Err -Pattern '\[editor\.mouse\] seat 1 dragging' -After $errMark -TimeoutSec 8
    foreach ($step in 1..4) { Move-CursorClient -W $w -X ($size.W * 0.5 - $step * 50) -Y ($size.H * $frac) }
    $mid = Probe -W $w -Verb 'editor.status' -Pattern '\[editor\.status:'
    Assert ($mid -match 'drag=') "drag live before the focus theft ($mid)"

    # Steal focus with an owned inert window (never an arbitrary foreign window).
    $form = [System.Windows.Forms.Form]::new()
    $form.Text = 'puck-manual-harness-inert'
    $form.StartPosition = 'Manual'
    $form.Location = [System.Drawing.Point]::new(50, 50)
    $form.Size = [System.Drawing.Size]::new(300, 120)
    $form.TopMost = $true
    $form.Show()
    $form.Activate()
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 500
    [System.Windows.Forms.Application]::DoEvents()

    # The cancel narration is the act's own echo — poll for it, not for wall-clock.
    $cancel = Wait-ForLine -List $w.Err -Pattern '\[editor\.mouse\] seat 1 drag cancelled — focus was lost mid-drag \(synthetic release\)' -After $errMark -TimeoutSec 10
    Assert ($null -ne $cancel) "focus loss cancelled the drag ($cancel)"

    $form.Close(); $form.Dispose()
    [System.Windows.Forms.Application]::DoEvents()
    Ensure-Foreground -W $w
    # Clean up the OS-side held button over our own window (the store already force-released it).
    Release-Left -W $w

    $ptr = Probe -W $w -Verb 'world.view.pointer' -Pattern '\[world\.view\.pointer:'
    Assert ((Get-Syscount -Probe $ptr) -gt $sysPre) "the system-release counter advanced across the focus loss ($ptr)"
    Assert ((Count-Matches -List $w.Err -Pattern '\[world\.mutation') -eq $mutBefore) 'no mutation was submitted by the synthetic release'
    Assert ((Get-Dirty -W $w) -eq $dirty0) 'the journal is untouched'
    $es = Probe -W $w -Verb 'editor.status' -Pattern '\[editor\.status:'
    Assert ($es -notmatch 'drag=') "the pending row is gone ($es)"
    $post = Join-Path $global:Scratch 'drag-focus-loss-post.json'
    $null = Probe -W $w -Verb "world.save $post" -Pattern '\[world\.save:'
    Start-Sleep -Milliseconds 300
    Assert (([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($post)))) -eq ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($pre))))) 'world.save is byte-identical to the pre-drag save'

    Write-Host 'ALL PASS'
} finally {
    Dump-Streams -W $w -Prefix 'drag-focus-loss-cancel'
    Stop-World -W $w
}
