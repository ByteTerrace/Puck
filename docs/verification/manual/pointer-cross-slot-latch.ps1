# MANUAL harness — see docs/verification/manual/README.md for the contract (hand-run, not a battery/gate,
# requires exclusive desktop foreground, injects global SendInput).
#
# Proves three WorldPointer/WorldPointerSink claims by driving a real windowed Puck.World over SendInput:
#
#   CROSS-SLOT LATCH: a held pointer button cannot survive a keyboard-seat reassignment as a phantom drag.
#   The mouse always rides whichever seat currently owns the keyboard; reassigning the keyboard away and back
#   must not leave the OLD seat's held-button bit stuck, or WorldCursorFeed would report a camera-orbit drag
#   still armed on a seat no button-down event has targeted since the reassignment.
#
#     READ A    control: right button held on seat 1                  reason == orbit-drag, syscount == 0
#     READ REL  the SystemReleaseCount discriminator: a REAL release
#               (RightUp) while the button is GENUINELY held since A  reason FLIPS orbit-drag -> visible, syscount UNCHANGED
#               (the button must be freshly armed and untouched since A for this leg to mean anything — a
#               release landing on an ALREADY-cleared button proves nothing, since it would look identical
#               whether delivered-and-correctly-ignored or never sent at all. The FLIP itself is the proof
#               delivery happened; the unchanged counter is what it discriminates.)
#     READ B    the fix: seat 1 after re-arming and an away-and-back
#               reassignment, physical button STILL down the whole time    reason != orbit-drag, syscount advanced by 2
#     READ C    liveness: a FRESH real press on seat 1 after B         reason == orbit-drag again, syscount UNCHANGED
#               (proves B's null is about the stale bit being cleared, not a dead pointer path)
#
#   WHEEL LIVENESS: a burst of real wheel input with no wheel consumer registered must not crash or hang the
#   process. The accumulator's numeric drain-to-near-zero is a code-level guarantee (WorldPointerSink drains it
#   in the same branch it arrives on when no IWorldWheelConsumer is registered) rather than something this
#   harness reads back — nothing consumes the wheel accumulator today to observe it through.

[CmdletBinding()]
param(
    [string] $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path,
    [int]    $Attempts = 2
)

$ErrorActionPreference = 'Stop'
$scratch = Join-Path $env:TEMP 'puck-manual-pointer-cross-slot-latch'

if (Test-Path $scratch) { Remove-Item -Recurse -Force $scratch }
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

$utf8 = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class Probe {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]   public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }

    const uint INPUT_MOUSE = 0;
    const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_WHEEL = 0x0800;
    public const int SW_RESTORE = 9;

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string window);

    // The null class name has to be supplied HERE: PowerShell coerces $null to an empty string for a [string]
    // parameter, and FindWindow then hunts for a window class literally named "", which never matches.
    public static IntPtr FindTop(string title) { return FindWindowW(null, title); }
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);

    static void Send(INPUT[] inputs) { SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT))); }
    static INPUT Mouse(int dx, int dy, uint data, uint flags) {
        INPUT i = new INPUT();
        i.type = INPUT_MOUSE;
        i.u.mi.dx = dx; i.u.mi.dy = dy; i.u.mi.mouseData = data; i.u.mi.dwFlags = flags;
        return i;
    }

    public static void RightDown() { Send(new INPUT[] { Mouse(0, 0, 0, MOUSEEVENTF_RIGHTDOWN) }); }
    public static void RightUp()   { Send(new INPUT[] { Mouse(0, 0, 0, MOUSEEVENTF_RIGHTUP) }); }
    public static void Wheel(int delta) { Send(new INPUT[] { Mouse(0, 0, unchecked((uint)delta), MOUSEEVENTF_WHEEL) }); }

    public static string TitleOf(IntPtr hWnd) {
        if (hWnd == IntPtr.Zero) { return "<none>"; }
        StringBuilder sb = new StringBuilder(512);
        int n = GetWindowTextW(hWnd, sb, sb.Capacity);
        return ((n > 0) ? sb.ToString() : "<untitled>");
    }
    public static string ForegroundTitle() { return TitleOf(GetForegroundWindow()); }
}
'@

$script:log = [System.Collections.Generic.List[string]]::new()
function Note([string] $text) {
    $line = ('{0:HH:mm:ss.fff}  {1}' -f (Get-Date), $text)
    $script:log.Add($line)
    Write-Host $line
}
function ForegroundLine { '{0} (hwnd 0x{1:X})' -f [Probe]::ForegroundTitle(), [Probe]::GetForegroundWindow().ToInt64() }

# --- safety gate: SendInput is global, so a foreign Puck.World would eat this harness's injected input --------
$deadline = (Get-Date).AddMinutes(2)
while ($true) {
    $others = @(Get-Process -Name 'Puck.World' -ErrorAction SilentlyContinue)
    if ($others.Count -eq 0) { break }
    if ((Get-Date) -gt $deadline) {
        Note ('SAFETY GATE: {0} foreign Puck.World process(es) still alive (PIDs {1}) after 2 minutes - refusing to drive global input.' -f $others.Count, ($others.Id -join ', '))
        $script:log | Set-Content -Path (Join-Path $scratch 'driver.log') -Encoding utf8
        exit 3
    }
    Note ('SAFETY GATE: waiting on foreign Puck.World PIDs {0}' -f ($others.Id -join ', '))
    Start-Sleep -Seconds 5
}
Note 'SAFETY GATE: clear - no foreign Puck.World process.'

dotnet build (Join-Path $RepoRoot 'src\Puck.World\Puck.World.csproj') -c Release | Out-Null
if ($LASTEXITCODE -ne 0) { Note 'FATAL: build failed.'; exit 2 }

# --- stdin script ------------------------------------------------------------------------------------------
# 720-tick (3s @240Hz) fences: generous relative to the real-time margin the SendInput stages below carry.
$stdinPath = Join-Path $scratch 'stdin.txt'
[System.IO.File]::WriteAllText($stdinPath, @"
replay.status
world.wait 720
world.view.pointer
world.wait 720
world.view.pointer
world.wait 720
player.assign keyboard1 2
player.assign keyboard1 1
world.wait 720
world.view.pointer
world.wait 720
world.view.pointer
world.wait 240
quit
"@.Replace("`r`n", "`n"), $utf8)

function Invoke-Attempt([int] $index) {
    $runDir = Join-Path $scratch ('run{0}' -f $index)
    if (Test-Path $runDir) { Remove-Item -Recurse -Force $runDir }
    $null = New-Item -ItemType Directory -Path $runDir
    $stateDir = Join-Path $runDir 'state'
    $null = New-Item -ItemType Directory -Path $stateDir
    $outLog = Join-Path $runDir 'out.log'
    $errLog = Join-Path $runDir 'err.log'

    Note ('=== attempt {0} ===' -f $index)

    $proc = Start-Process -FilePath 'dotnet' -WorkingDirectory $RepoRoot -NoNewWindow -PassThru `
        -ArgumentList @(
            'run', '--project', 'src/Puck.World', '-c', 'Release', '--no-build', '--',
            '--world', 'src\Puck.World\Assets\worlds
exus.world.json',
            '--exit-after-seconds', '120',
            '--width', '640', '--height', '480',
            '--state-dir', $stateDir
        ) `
        -RedirectStandardInput $stdinPath -RedirectStandardOutput $outLog -RedirectStandardError $errLog
    Note ('launched dotnet run (host pid {0})' -f $proc.Id)

    $puck = [IntPtr]::Zero
    foreach ($try in 1..200) {
        $candidate = [Probe]::FindTop('Puck: World')
        if (($candidate -ne [IntPtr]::Zero) -and [Probe]::IsWindowVisible($candidate)) { $puck = $candidate; break }
        if ($proc.HasExited) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($puck -eq [IntPtr]::Zero) {
        Note 'FATAL: Puck window never appeared.'
        return [pscustomobject]@{ Ok = $false; RunDir = $runDir; Reason = 'no window' }
    }
    $t0 = Get-Date
    $windowPid = [uint32] 0
    $null = [Probe]::GetWindowThreadProcessId($puck, [ref] $windowPid)
    $mine = @(Get-Process -Name 'Puck.World' -ErrorAction SilentlyContinue).Id
    if ($mine -notcontains [int] $windowPid) {
        Note ('FATAL: "Puck: World" window belongs to pid {0}, not among this harness''s own processes ({1}). Refusing to drive input.' -f $windowPid, ($mine -join ', '))
        return [pscustomobject]@{ Ok = $false; RunDir = $runDir; Reason = 'foreign window' }
    }
    Note ('Puck window up: hwnd 0x{0:X} pid {1}' -f $puck.ToInt64(), $windowPid)

    function Wait-Until([double] $seconds) {
        $remaining = ($seconds - ((Get-Date) - $t0).TotalSeconds)
        if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int] ($remaining * 1000)) }
    }
    function Focus-Puck {
        # Plain SetForegroundWindow is silently refused by Windows when the caller is not itself already
        # foreground (the documented foreground-lock restriction), so a loop that only calls it can spend its
        # whole budget on a call that was never going to take effect. ShowWindow(SW_RESTORE) + BringWindowToTop
        # first breaks that lock reliably (the same combination docs/verification/manual/drag-drop/lib.ps1's
        # Ensure-Foreground and camera-orbit-focus-loss.ps1's Focus-Window both use). 20 attempts x 300ms (6s)
        # rather than the old 6 x 200ms (1.2s): desktop contention from concurrent sessions is this machine's
        # normal state, not an edge case.
        foreach ($try in 1..20) {
            $null = [Probe]::ShowWindow($puck, [Probe]::SW_RESTORE)
            $null = [Probe]::BringWindowToTop($puck)
            $null = [Probe]::SetForegroundWindow($puck)
            Start-Sleep -Milliseconds 300
            if ([Probe]::GetForegroundWindow() -eq $puck) { return $true }
            if (($try % 5) -eq 0) { Note ("Focus-Puck: still retrying (attempt $try/20)") }
        }
        Note ('FOREGROUND CHECK FAILED: wanted Puck: World, foreground is {0}' -f (ForegroundLine))
        return $false
    }
    function Get-ClientBox {
        $rect = New-Object Probe+RECT
        $null = [Probe]::GetClientRect($puck, [ref] $rect)
        $origin = New-Object Probe+POINT
        $null = [Probe]::ClientToScreen($puck, [ref] $origin)
        [pscustomobject]@{ X = $origin.X; Y = $origin.Y; W = ($rect.Right - $rect.Left); H = ($rect.Bottom - $rect.Top) }
    }

    $foregroundFailures = @()

    Wait-Until 1.0
    if (-not (Focus-Puck)) { $foregroundFailures += 'initial-focus' }
    $box = Get-ClientBox
    $null = [Probe]::SetCursorPos(($box.X + 70), ($box.Y + 70))
    Note ('foreground: {0}, cursor parked inside client box x={1} y={2} w={3} h={4}' -f (ForegroundLine), $box.X, $box.Y, $box.W, $box.H)

    # Polls out.log for the Nth "[world.view.pointer:" line rather than guessing a wall-clock offset: the tick
    # loop's real-time pacing is not reliable under concurrent load on a shared machine, so a fixed schedule
    # can race the very state it is trying to observe.
    function Wait-ForPointerReading([int] $count, [int] $timeoutSeconds) {
        $give_up = (Get-Date).AddSeconds($timeoutSeconds)
        while ((Get-Date) -lt $give_up) {
            if ($proc.HasExited) { return $false }
            $seen = @(Get-Content -Path $outLog -Encoding utf8 -ErrorAction SilentlyContinue | Where-Object { $_ -match '^\[world\.view\.pointer:' })
            if ($seen.Count -ge $count) { return $true }
            Start-Sleep -Milliseconds 200
        }
        return $false
    }

    # --- STAGE 1: arm the drag on seat 1 (feeds READ A, the control) ---
    Wait-Until 2.0
    if ([Probe]::GetForegroundWindow() -ne $puck) { $foregroundFailures += 'pre-stage-1' }
    Note 'STAGE 1: right button DOWN on seat 1'
    [Probe]::RightDown()
    Start-Sleep -Milliseconds 150

    # --- STAGE 2: the SystemReleaseCount discriminator - a REAL release while the button is genuinely held
    # (feeds READ REL). The button has been held since stage 1 with nothing else touching it, so the reason
    # FLIP (orbit-drag -> visible) is itself the proof the release was delivered, and the unchanged counter is
    # what it discriminates. ---
    if (-not (Wait-ForPointerReading -count 1 -timeoutSeconds 60)) {
        Note 'FATAL: READ A (1st world.view.pointer line) never appeared within 60s.'
        $foregroundFailures += 'read-a-timeout'
    }
    if ([Probe]::GetForegroundWindow() -ne $puck) { $foregroundFailures += 'pre-stage-2' }
    Note 'STAGE 2: READ A landed. right button UP - the button was genuinely held since stage 1, nothing else has touched it'
    [Probe]::RightUp()
    Start-Sleep -Milliseconds 200

    # --- STAGE 3: re-arm, then the silent reassignment away and back, button STILL down throughout (feeds READ B) ---
    if (-not (Wait-ForPointerReading -count 2 -timeoutSeconds 60)) {
        Note 'FATAL: READ REL (2nd world.view.pointer line) never appeared within 60s.'
        $foregroundFailures += 'read-rel-timeout'
    }
    if ([Probe]::GetForegroundWindow() -ne $puck) { $foregroundFailures += 'pre-stage-3' }
    Note 'STAGE 3: READ REL landed. right button DOWN again (re-arm) - player.assign keyboard1 2/1 runs from stdin next while it stays down'
    [Probe]::RightDown()
    Start-Sleep -Milliseconds 150

    # --- STAGE 4: liveness - release, then a FRESH real press on seat 1 after B (feeds READ C) ---
    if (-not (Wait-ForPointerReading -count 3 -timeoutSeconds 60)) {
        Note 'FATAL: READ B (3rd world.view.pointer line) never appeared within 60s.'
        $foregroundFailures += 'read-b-timeout'
    }
    if ([Probe]::GetForegroundWindow() -ne $puck) { $foregroundFailures += 'pre-stage-4' }
    Note 'STAGE 4: READ B landed. right button UP then a fresh DOWN'
    [Probe]::RightUp()
    Start-Sleep -Milliseconds 200
    [Probe]::RightDown()
    Start-Sleep -Milliseconds 150

    # --- STAGE 5: wheel liveness - a burst with no consumer registered must not crash or hang ---
    if (-not (Wait-ForPointerReading -count 4 -timeoutSeconds 60)) {
        Note 'FATAL: READ C (4th world.view.pointer line) never appeared within 60s.'
        $foregroundFailures += 'read-c-timeout'
    }
    if ([Probe]::GetForegroundWindow() -ne $puck) { $foregroundFailures += 'pre-stage-5' }
    Note 'STAGE 5: READ C landed. wheel burst (20 alternating reports) with no wheel consumer registered'
    foreach ($i in 1..20) {
        [Probe]::Wheel(($i % 2 -eq 0) ? 120 : -120)
        Start-Sleep -Milliseconds 15
    }
    [Probe]::RightUp()
    Note ('STAGE 5: done. foreground: {0}' -f (ForegroundLine))

    if (-not $proc.WaitForExit(60000)) {
        Note 'WARNING: game did not exit within 60s of the driver finishing - killing the process this harness started.'
        $proc.Kill($true)
        $null = $proc.WaitForExit(10000)
    }
    $exitCode = $proc.ExitCode
    Note ('game exited with code {0} after {1:0.0}s' -f $exitCode, ((Get-Date) - $t0).TotalSeconds)

    $stdout = @(Get-Content -Path $outLog -Encoding utf8 -ErrorAction SilentlyContinue)
    $stderr = @(Get-Content -Path $errLog -Encoding utf8 -ErrorAction SilentlyContinue)
    $pointers = @($stdout | Where-Object { $_ -match '^\[world\.view\.pointer:' })

    [pscustomobject]@{
        Ok                 = $true
        RunDir             = $runDir
        ExitCode           = $exitCode
        Pointers           = $pointers
        Stdout             = $stdout
        Stderr             = $stderr
        ForegroundFailures = $foregroundFailures
    }
}

function Get-PlayerReason([string] $line) {
    if ($line -match 'player=(\S+).*reason=(\S+?)\s.*syscount=(\d+)\]$') {
        return [pscustomobject]@{ Player = $Matches[1]; Reason = $Matches[2]; SysCount = [int] $Matches[3] }
    }
    return $null
}

$result = $null
foreach ($attempt in 1..$Attempts) {
    try {
        $result = Invoke-Attempt $attempt
    } finally {
        [Probe]::RightUp()
    }
    if (-not $result.Ok) { continue }
    if ($result.Pointers.Count -lt 4) { Note ('attempt {0}: only {1} world.view.pointer readings - retrying' -f $attempt, $result.Pointers.Count); continue }
    $a = Get-PlayerReason $result.Pointers[0]
    if (($a.Player -eq '1') -and ($a.Reason -eq 'orbit-drag')) { Note ('attempt {0}: CONTROL passed' -f $attempt); break }
    Note ('attempt {0}: CONTROL FAILED (READ A: player={1} reason={2}) - retrying' -f $attempt, $a.Player, $a.Reason)
}

# --- verdicts ------------------------------------------------------------------------------------------------
$report = [System.Collections.Generic.List[string]]::new()
function Say([string] $text) { $report.Add($text); Write-Host $text }

Say ''
Say '================ RESULT ================'
if (-not $result.Ok) {
    Say ('INCONCLUSIVE: {0}' -f $result.Reason)
} else {
    $labels = @('A', 'REL', 'B', 'C')
    for ($i = 0; ($i -lt $result.Pointers.Count); $i++) { Say ('READ {0}: {1}' -f $labels[$i], $result.Pointers[$i]) }
    Say ('exit code: {0}' -f $result.ExitCode)

    if ($result.Pointers.Count -ge 4) {
        $ra = Get-PlayerReason $result.Pointers[0]
        $rrel = Get-PlayerReason $result.Pointers[1]
        $rb = Get-PlayerReason $result.Pointers[2]
        $rc = Get-PlayerReason $result.Pointers[3]
        Say ''
        Say ('CONTROL (READ A): {0} - player={1} reason={2} syscount={3}' -f ((($ra.Player -eq '1') -and ($ra.Reason -eq 'orbit-drag')) ? 'PASS (drag armed on seat 1)' : 'FAIL - harness INCONCLUSIVE'), $ra.Player, $ra.Reason, $ra.SysCount)
        Say ('SYSCOUNT DISCRIMINATOR (READ REL, a REAL release while genuinely held since A): {0} - player={1} reason={2} syscount={3} (expected reason FLIPS orbit-drag->visible AND syscount unchanged from A)' -f ((($rrel.Reason -ne 'orbit-drag') -and ($rrel.SysCount -eq $ra.SysCount)) ? 'PASS' : 'FAIL (either the release did not land, or it moved the counter - wrong implementation shape)'), $rrel.Player, $rrel.Reason, $rrel.SysCount)
        Say ('LATCH FIX (READ B, re-armed then reassigned away and back, button never released): {0} - player={1} reason={2} syscount={3}' -f (($rb.Reason -ne 'orbit-drag') ? 'PASS (no phantom orbit-drag after silent reassignment)' : 'FAIL (stale button bit survived the reassignment)'), $rb.Player, $rb.Reason, $rb.SysCount)
        Say ('LIVENESS (READ C, fresh real press after B): {0} - player={1} reason={2} syscount={3}' -f ((($rc.Player -eq '1') -and ($rc.Reason -eq 'orbit-drag') -and ($rc.SysCount -eq $rb.SysCount)) ? 'PASS (fresh real press still arms - B was about the stale bit, not a dead path)' : 'FAIL (pointer path dead or syscount moved unexpectedly)'), $rc.Player, $rc.Reason, $rc.SysCount)
        Say ('SYSCOUNT (REL->B, the two reassignments): {0} - {1} -> {2} (expected +2: one ReleaseAllButtons per player.assign)' -f ((($rb.SysCount - $rrel.SysCount) -eq 2) ? 'PASS' : 'FAIL'), $rrel.SysCount, $rb.SysCount)
        Say ('SYSCOUNT (B->C, a real release then a real press - no force-release in this span): {0} - {1} -> {2} (expected unchanged)' -f (($rc.SysCount -eq $rb.SysCount) ? 'PASS' : 'FAIL'), $rb.SysCount, $rc.SysCount)
    } else {
        Say ('INCONCLUSIVE: {0} world.view.pointer readings, expected 4' -f $result.Pointers.Count)
    }

    $throws = @(($result.Stdout + $result.Stderr) | Where-Object { $_ -match 'Unhandled exception|ArgumentOutOfRangeException' })
    $wheelOk = (($result.ExitCode -eq 0) -and ($throws.Count -eq 0))
    Say ('WHEEL LIVENESS: {0} - exit {1}, {2} throw line(s) (numeric drainage is a code-level guarantee, see header)' -f ($wheelOk ? 'PASS' : 'FAIL'), $result.ExitCode, $throws.Count)
    if ($throws.Count -gt 0) { $throws | ForEach-Object { Say ('    {0}' -f $_) } }

    if ($result.ForegroundFailures.Count -gt 0) {
        Say ('FOREGROUND VERIFICATION FAILURES: {0}' -f ($result.ForegroundFailures -join ', '))
    } else {
        Say 'foreground verification: clean at every stage'
    }
}
Say ('artifacts: {0}' -f $result.RunDir)

$script:log | Set-Content -Path (Join-Path $scratch 'driver.log') -Encoding utf8
$report     | Set-Content -Path (Join-Path $scratch 'verdict.txt') -Encoding utf8
