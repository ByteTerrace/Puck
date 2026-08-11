# MANUAL harness — see docs/verification/manual/README.md for the contract (hand-run, not a battery/gate,
# requires exclusive desktop foreground, injects global SendInput). Companion:
# camera-orbit-focus-loss-sink.ps1, an inert window this harness Alt-aways to mid-drag.
#
# Proves the per-seat pointer store's FocusLost handling does not silently kill the camera-orbit drag path:
#
#   CONTROL   right-drag inside the window moves the orbit at all        (T1 != T0)
#   LATCH     post-refocus motion with NO button held leaves it alone    (T3 == T2)
#   LIVENESS  re-arming and dragging again still moves the orbit         (T4 != T3)
#             (T3's null alone would be vacuous — it could mean either "focus loss correctly disarmed the
#             drag" or "the pointer path silently died at the refocus". T4 rules out the second.)
#   WHEEL     PointerWheel is skipped by the window pump, process lives  (exit 0, no unhandled throw)

[CmdletBinding()]
param(
    [string] $RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path,
    [int]    $Attempts = 2
)

$ErrorActionPreference = 'Stop'
$scratch = Join-Path $env:TEMP 'puck-manual-camera-orbit-focus-loss'

if (Test-Path $scratch) { Remove-Item -Recurse -Force $scratch }
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

# BOM-less, or the preamble corrupts the first piped stdin line.
$utf8 = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8

Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class Probe {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)]   public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }

    const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_WHEEL = 0x0800;
    const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string cls, string window);

    // The null class name has to be supplied HERE: PowerShell coerces $null to an empty string for a [string]
    // parameter, and FindWindow then hunts for a window class literally named "", which never matches.
    public static IntPtr FindTop(string title) { return FindWindowW(null, title); }
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);

    static void Send(INPUT[] inputs) { SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT))); }

    static INPUT Mouse(int dx, int dy, uint data, uint flags) {
        INPUT i = new INPUT();
        i.type = INPUT_MOUSE;
        i.u.mi.dx = dx; i.u.mi.dy = dy; i.u.mi.mouseData = data; i.u.mi.dwFlags = flags;
        return i;
    }
    static INPUT Key(ushort vk, bool up) {
        INPUT i = new INPUT();
        i.type = INPUT_KEYBOARD;
        i.u.ki.wVk = vk; i.u.ki.dwFlags = (up ? KEYEVENTF_KEYUP : 0u);
        return i;
    }

    // Relative motion. Raw input reports the injected delta verbatim (pointer ballistics only bend where the
    // CURSOR lands), so the engine sees exactly what is asked for here.
    public static void MoveRelative(int dx, int dy) { Send(new INPUT[] { Mouse(dx, dy, 0, MOUSEEVENTF_MOVE) }); }
    public static void RightDown() { Send(new INPUT[] { Mouse(0, 0, 0, MOUSEEVENTF_RIGHTDOWN) }); }
    public static void RightUp()   { Send(new INPUT[] { Mouse(0, 0, 0, MOUSEEVENTF_RIGHTUP) }); }
    public static void Wheel(int delta) { Send(new INPUT[] { Mouse(0, 0, unchecked((uint)delta), MOUSEEVENTF_WHEEL) }); }

    // A synthetic keystroke from this process clears the foreground lock, so the SetForegroundWindow that follows
    // is honoured instead of just flashing a taskbar button.
    public static void TapAlt() {
        Send(new INPUT[] { Key(0x12, false), Key(0x12, true) });
    }
    public static void AltTab() {
        Send(new INPUT[] { Key(0x12, false), Key(0x09, false), Key(0x09, true), Key(0x12, true) });
    }

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

# --- safety gate -------------------------------------------------------------------------------------------
# SendInput is GLOBAL. A Puck.World this harness did not start would eat the synthetic drag, or steal foreground
# mid-run; either way every reading afterwards is fiction.
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
# Five world.view.camera reads fenced by 2400-tick (10s at 240Hz) waits: T0 boot, T1 post-drag, T2 post-refocus,
# T3 post-unarmed-motion, T4 post-REARMED-drag.
$stdinPath = Join-Path $scratch 'stdin.txt'
[System.IO.File]::WriteAllText($stdinPath, @"
replay.status
world.view.camera
world.wait 2400
world.view.camera
world.wait 2400
world.view.camera
world.wait 2400
world.view.camera
world.wait 2400
world.view.camera
"@.Replace("`r`n", "`n"), $utf8)

# --- the inert focus-steal target --------------------------------------------------------------------------
$sink = Start-Process -FilePath 'powershell.exe' `
    -ArgumentList '-STA', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-WindowStyle', 'Hidden', '-File', (Join-Path $PSScriptRoot 'camera-orbit-focus-loss-sink.ps1') `
    -PassThru
$sinkWindow = [IntPtr]::Zero
foreach ($try in 1..40) {
    $sinkWindow = [Probe]::FindTop('Puck manual harness sink')
    if ($sinkWindow -ne [IntPtr]::Zero) { break }
    Start-Sleep -Milliseconds 250
}
if ($sinkWindow -eq [IntPtr]::Zero) { Note 'FATAL: sink window never appeared.'; exit 4 }
Note ('sink window up: hwnd 0x{0:X}' -f $sinkWindow.ToInt64())

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
            '--world', 'src\Puck.World\Assets\worlds\play.world.json',
            '--exit-after-seconds', '55',
            '--width', '640', '--height', '480',
            '--state-dir', $stateDir
        ) `
        -RedirectStandardInput $stdinPath -RedirectStandardOutput $outLog -RedirectStandardError $errLog
    Note ('launched dotnet run (host pid {0})' -f $proc.Id)

    # Wait for the game window, and prove it belongs to a process THIS harness started before touching input.
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
        Note ('FATAL: "Puck: World" window belongs to pid {0}, which is not among the Puck.World processes this harness started ({1}). Refusing to drive input.' -f $windowPid, ($mine -join ', '))
        return [pscustomobject]@{ Ok = $false; RunDir = $runDir; Reason = 'foreign window' }
    }
    Note ('Puck window up: hwnd 0x{0:X} pid {1}' -f $puck.ToInt64(), $windowPid)

    # Park the two windows apart so the mid-drag release can land on the sink and nothing else.
    $work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $null = [Probe]::MoveWindow($puck, 40, 40, 660, 520, $true)
    $null = [Probe]::MoveWindow($sinkWindow, ($work.Right - 480), ($work.Bottom - 380), 460, 340, $true)
    Start-Sleep -Milliseconds 300

    function Wait-Until([double] $seconds) {
        $remaining = ($seconds - ((Get-Date) - $t0).TotalSeconds)
        if ($remaining -gt 0) { Start-Sleep -Milliseconds ([int] ($remaining * 1000)) }
    }
    function Focus-Window([IntPtr] $hWnd, [string] $label) {
        foreach ($try in 1..6) {
            [Probe]::TapAlt()
            $null = [Probe]::SetForegroundWindow($hWnd)
            Start-Sleep -Milliseconds 350
            if ([Probe]::GetForegroundWindow() -eq $hWnd) { return $true }
            if ($try -ge 3) { [Probe]::AltTab(); Start-Sleep -Milliseconds 500 }
        }
        Note ('FOREGROUND CHECK FAILED: wanted {0}, foreground is {1}' -f $label, (ForegroundLine))
        return $false
    }
    function Get-ClientBox([IntPtr] $hWnd) {
        $rect = New-Object Probe+RECT
        $null = [Probe]::GetClientRect($hWnd, [ref] $rect)
        $origin = New-Object Probe+POINT
        $null = [Probe]::ClientToScreen($hWnd, [ref] $origin)
        [pscustomobject]@{ X = $origin.X; Y = $origin.Y; W = ($rect.Right - $rect.Left); H = ($rect.Bottom - $rect.Top) }
    }
    # 25 relative reports of (14,5). Pointer ballistics can amplify where the CURSOR ends up (never the raw delta
    # the engine reads), so the cursor is nudged back inside whenever it drifts out of the client area - via
    # SetCursorPos, which generates no raw-input motion and so cannot pollute the measurement.
    function Drag-Motion([IntPtr] $hWnd) {
        $box = Get-ClientBox $hWnd
        $anchorX = ($box.X + 70)
        $anchorY = ($box.Y + 70)
        foreach ($step in 1..25) {
            [Probe]::MoveRelative(14, 5)
            Start-Sleep -Milliseconds 20
            $cursor = New-Object Probe+POINT
            $null = [Probe]::GetCursorPos([ref] $cursor)
            if (
                ($cursor.X -lt ($box.X + 20)) -or ($cursor.X -gt ($box.X + $box.W - 60)) -or
                ($cursor.Y -lt ($box.Y + 20)) -or ($cursor.Y -gt ($box.Y + $box.H - 60))
            ) {
                $null = [Probe]::SetCursorPos($anchorX, $anchorY)
            }
        }
    }

    $foregroundFailures = @()

    # --- bring the game forward -----------------------------------------------------------------------------
    Wait-Until 1.5
    if (-not (Focus-Window $puck 'Puck: World')) { $foregroundFailures += 'pre-stage-A' }
    Note ('foreground before stage A: {0}' -f (ForegroundLine))
    $box = Get-ClientBox $puck
    $null = [Probe]::SetCursorPos(($box.X + 70), ($box.Y + 70))
    Note ('cursor parked inside client box x={0} y={1} w={2} h={3}' -f $box.X, $box.Y, $box.W, $box.H)

    # --- stage A: armed drag (the control) ------------------------------------------------------------------
    Wait-Until 2.5
    Note 'STAGE A: right button DOWN'
    [Probe]::RightDown()
    Start-Sleep -Milliseconds 120
    Note 'STAGE A: 25 x (14,5) relative moves, button held'
    Drag-Motion $puck
    Note ('STAGE A: done, button still down. foreground: {0}' -f (ForegroundLine))

    # --- stage B: focus leaves mid-drag, release lands elsewhere --------------------------------------------
    Wait-Until 12.0
    Note 'STAGE B: taking foreground away from Puck (button still down)'
    if (-not (Focus-Window $sinkWindow 'Puck manual harness sink')) { $foregroundFailures += 'stage-B-away' }
    Note ('STAGE B: foreground after switch: {0}' -f (ForegroundLine))
    $sinkBox = Get-ClientBox $sinkWindow
    $null = [Probe]::SetCursorPos(($sinkBox.X + ($sinkBox.W / 2)), ($sinkBox.Y + ($sinkBox.H / 2)))
    Start-Sleep -Milliseconds 200
    Note 'STAGE B: right button UP over the sink window'
    [Probe]::RightUp()
    Start-Sleep -Milliseconds 300
    Note 'STAGE B: returning foreground to Puck'
    if (-not (Focus-Window $puck 'Puck: World')) { $foregroundFailures += 'stage-B-back' }
    Note ('STAGE B: foreground after return: {0}' -f (ForegroundLine))
    $box = Get-ClientBox $puck
    $null = [Probe]::SetCursorPos(($box.X + 70), ($box.Y + 70))

    # --- stage C: unarmed motion + wheel --------------------------------------------------------------------
    Wait-Until 22.0
    Note ('STAGE C: foreground before unarmed motion: {0}' -f (ForegroundLine))
    if ([Probe]::GetForegroundWindow() -ne $puck) { $foregroundFailures += 'pre-stage-C' }
    Note 'STAGE C: 25 x (14,5) relative moves, NO button held'
    Drag-Motion $puck
    Note 'STAGE C: wheel +120'
    [Probe]::Wheel(120)
    Start-Sleep -Milliseconds 150
    Note 'STAGE C: wheel -240'
    [Probe]::Wheel(-240)
    Note ('STAGE C: done. foreground: {0}' -f (ForegroundLine))

    # --- stage D: re-arm and drag again (the liveness discriminator) -----------------------------------------
    # Identical motion to stage C, differing only in the held button. If THIS moves the orbit, stage C's null
    # result is about the arming state and not about a pointer path that quietly died at the refocus.
    Wait-Until 32.0
    Note ('STAGE D: foreground before re-armed drag: {0}' -f (ForegroundLine))
    if ([Probe]::GetForegroundWindow() -ne $puck) { $foregroundFailures += 'pre-stage-D' }
    $box = Get-ClientBox $puck
    $null = [Probe]::SetCursorPos(($box.X + 70), ($box.Y + 70))
    Start-Sleep -Milliseconds 150
    Note 'STAGE D: right button DOWN again'
    [Probe]::RightDown()
    Start-Sleep -Milliseconds 120
    Note 'STAGE D: 25 x (14,5) relative moves, button held'
    Drag-Motion $puck
    [Probe]::RightUp()
    Note ('STAGE D: done, button released over the game. foreground: {0}' -f (ForegroundLine))

    # --- collect --------------------------------------------------------------------------------------------
    if (-not $proc.WaitForExit(60000)) {
        Note 'WARNING: game did not exit within 60s of the driver finishing - killing the process this harness started.'
        $proc.Kill($true)
        $null = $proc.WaitForExit(10000)
    }
    $exitCode = $proc.ExitCode
    Note ('game exited with code {0} after {1:0.0}s' -f $exitCode, ((Get-Date) - $t0).TotalSeconds)

    $stdout = @(Get-Content -Path $outLog -Encoding utf8 -ErrorAction SilentlyContinue)
    $stderr = @(Get-Content -Path $errLog -Encoding utf8 -ErrorAction SilentlyContinue)
    $orbits = @($stdout | Where-Object { $_ -match '^\[world\.view\.orbit:' })

    [pscustomobject]@{
        Ok                 = $true
        RunDir             = $runDir
        ExitCode           = $exitCode
        Orbits             = $orbits
        Stdout             = $stdout
        Stderr             = $stderr
        ForegroundFailures = $foregroundFailures
    }
}

function Get-Angles([string] $line) {
    if ($line -match 'yaw=(\S+)\s+pitch=(\S+?)\]?$') { return ('yaw={0} pitch={1}' -f $Matches[1], $Matches[2]) }
    return '<unparsed>'
}

$result = $null
foreach ($attempt in 1..$Attempts) {
    try {
        $result = Invoke-Attempt $attempt
    } finally {
        # Never leave a synthetic button held at the OS level, whatever went wrong above.
        [Probe]::RightUp()
    }
    if (-not $result.Ok) { continue }
    if ($result.Orbits.Count -lt 5) { Note ('attempt {0}: only {1} orbit readings - retrying' -f $attempt, $result.Orbits.Count); continue }
    $t0a = Get-Angles $result.Orbits[0]
    $t1a = Get-Angles $result.Orbits[1]
    if ($t1a -ne $t0a) { Note ('attempt {0}: CONTROL passed' -f $attempt); break }
    Note ('attempt {0}: CONTROL FAILED (T1 == T0) - the drag never reached the engine' -f $attempt)
}

if ($sink -and -not $sink.HasExited) { $sink.Kill() }

# --- verdicts ------------------------------------------------------------------------------------------------
$report = [System.Collections.Generic.List[string]]::new()
function Say([string] $text) { $report.Add($text); Write-Host $text }

Say ''
Say '================ RESULT ================'
if (-not $result.Ok) {
    Say ('INCONCLUSIVE: {0}' -f $result.Reason)
} else {
    for ($i = 0; ($i -lt $result.Orbits.Count); $i++) { Say ('T{0}: {1}' -f $i, $result.Orbits[$i]) }
    Say ('exit code: {0}' -f $result.ExitCode)

    if ($result.Orbits.Count -ge 5) {
        $a = @($result.Orbits | ForEach-Object { Get-Angles $_ })
        Say ''
        Say ('CONTROL:  {0} - T0 {1} / T1 {2}' -f (($a[1] -ne $a[0]) ? 'PASS (armed drag moved the orbit)' : 'FAIL (drag never reached the engine - harness INCONCLUSIVE)'), $a[0], $a[1])
        Say ('LATCH:    {0} - T2 {1} / T3 {2}' -f (($a[3] -eq $a[2]) ? 'DEAD (post-refocus unarmed motion did not orbit)' : 'ALIVE (post-refocus unarmed motion still orbited)'), $a[2], $a[3])
        Say ('LIVENESS: {0} - T3 {1} / T4 {2}' -f (($a[4] -ne $a[3]) ? 'PASS (re-armed drag still orbits, so the T3 null is about arming, not a dead path)' : 'FAIL (pointer path dead after refocus - the LATCH reading is vacuous)'), $a[3], $a[4])
    } else {
        Say ('INCONCLUSIVE: {0} orbit readings, expected 5' -f $result.Orbits.Count)
    }

    $throws = @(($result.Stdout + $result.Stderr) | Where-Object { $_ -match 'Unhandled exception|ArgumentOutOfRangeException' })
    $wheelOk = (($result.ExitCode -eq 0) -and ($throws.Count -eq 0))
    Say ('WHEEL:   {0} - exit {1}, {2} throw line(s)' -f ($wheelOk ? 'PASS (wheel skipped by the pump)' : 'FAIL'), $result.ExitCode, $throws.Count)
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
