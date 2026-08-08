# Shared driver for the drag-drop MANUAL harnesses in this directory — see docs/verification/manual/README.md
# for the contract (hand-run, not a battery/gate, requires exclusive desktop foreground, injects global
# SendInput). Dot-sourced by each drag-*/click-*.ps1 script; not runnable on its own.
#
# Real SendInput only; GetForegroundWindow()==hwnd is asserted before every injection.

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$global:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$global:WorldExe = Join-Path $global:RepoRoot 'src\Puck.World\bin\Release\net10.0\Puck.World.exe'
$global:Scratch = Join-Path $env:TEMP 'puck-manual-drag-drop'

if (-not (Test-Path $global:Scratch)) { New-Item -ItemType Directory -Force -Path $global:Scratch | Out-Null }

Write-Host 'building Puck.World (Release)...'
dotnet build (Join-Path $global:RepoRoot 'src\Puck.World\Puck.World.csproj') -c Release | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'FATAL: build failed.' }

Add-Type -ReferencedAssemblies System.Windows.Forms @'
using System;
using System.Runtime.InteropServices;

public static class Native {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT {
        public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MOUSEINPUT mi; }

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);

    const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004, ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000;
    const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    public const int SW_RESTORE = 9;

    static INPUT Make(uint flags, int dx, int dy) {
        var input = new INPUT();
        input.type = 0;
        input.mi.dx = dx; input.mi.dy = dy; input.mi.dwFlags = flags;
        return input;
    }
    static void Absolute(int screenX, int screenY, out int dx, out int dy) {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        dx = (int)Math.Round((screenX - vx) * 65535.0 / (vw - 1));
        dy = (int)Math.Round((screenY - vy) * 65535.0 / (vh - 1));
    }
    public static void MoveTo(int screenX, int screenY) {
        int dx, dy; Absolute(screenX, screenY, out dx, out dy);
        var inputs = new INPUT[] { Make(MOVE | ABSOLUTE | VIRTUALDESK, dx, dy) };
        if (SendInput(1u, inputs, Marshal.SizeOf(typeof(INPUT))) != 1u) throw new InvalidOperationException("SendInput move failed");
    }
    public static void LeftDown() {
        var inputs = new INPUT[] { Make(LEFTDOWN, 0, 0) };
        if (SendInput(1u, inputs, Marshal.SizeOf(typeof(INPUT))) != 1u) throw new InvalidOperationException("SendInput down failed");
    }
    public static void LeftUp() {
        var inputs = new INPUT[] { Make(LEFTUP, 0, 0) };
        if (SendInput(1u, inputs, Marshal.SizeOf(typeof(INPUT))) != 1u) throw new InvalidOperationException("SendInput up failed");
    }
}
'@

function Start-World {
    param([string]$StateDir, [int]$ExitAfter = 120)
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $global:WorldExe
    $psi.Arguments = "--exit-after-seconds $ExitAfter --state-dir `"$StateDir`""
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.StandardInputEncoding = [System.Text.UTF8Encoding]::new($false)
    $psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)
    $psi.StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)
    $p = [System.Diagnostics.Process]::new()
    $p.StartInfo = $psi
    $out = [System.Collections.ArrayList]::Synchronized([System.Collections.ArrayList]::new())
    $err = [System.Collections.ArrayList]::Synchronized([System.Collections.ArrayList]::new())
    $null = Register-ObjectEvent -InputObject $p -EventName OutputDataReceived -MessageData $out -Action {
        if ($null -ne $EventArgs.Data) { [void]$Event.MessageData.Add($EventArgs.Data) }
    }
    $null = Register-ObjectEvent -InputObject $p -EventName ErrorDataReceived -MessageData $err -Action {
        if ($null -ne $EventArgs.Data) { [void]$Event.MessageData.Add($EventArgs.Data) }
    }
    [void]$p.Start()
    $p.BeginOutputReadLine()
    $p.BeginErrorReadLine()
    # Wait for the native window.
    $hwnd = [IntPtr]::Zero
    for ($i = 0; $i -lt 200; $i++) {
        Start-Sleep -Milliseconds 100
        $p.Refresh()
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $p.MainWindowHandle; break }
    }
    if ($hwnd -eq [IntPtr]::Zero) { throw 'world window never appeared' }
    # Wait for the boot line so stdin is being consumed.
    $w = [pscustomobject]@{ Process = $p; In = $p.StandardInput; Out = $out; Err = $err; Hwnd = $hwnd }
    if (-not (Wait-ForLine -List $err -Pattern 'origin|world' -TimeoutSec 30)) {
        # boot narration rides stderr; tolerate absence, the window already exists
    }
    Start-Sleep -Milliseconds 1500
    return $w
}

function Send-Line { param($W, [string]$Line) $W.In.WriteLine($Line); $W.In.Flush() }

function Wait-ForLine {
    param($List, [string]$Pattern, [int]$TimeoutSec = 10, [int]$After = 0)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSec)
    while ([DateTime]::UtcNow -lt $deadline) {
        $snapshot = @($List)
        for ($i = $After; $i -lt $snapshot.Count; $i++) {
            if ($snapshot[$i] -match $Pattern) { return $snapshot[$i] }
        }
        Start-Sleep -Milliseconds 60
    }
    return $null
}

function Count-Matches { param($List, [string]$Pattern) @(@($List) | Where-Object { $_ -match $Pattern }).Count }

function Probe {
    # Send a read-back verb and return its (last) echo line.
    param($W, [string]$Verb, [string]$Pattern, [int]$TimeoutSec = 10)
    $mark = $W.Out.Count
    Send-Line -W $W -Line $Verb
    return Wait-ForLine -List $W.Out -Pattern $Pattern -TimeoutSec $TimeoutSec -After $mark
}

function Ensure-Foreground {
    # Plain SetForegroundWindow is not enough on its own: Windows silently refuses it when the CALLER is not
    # itself already foreground (the documented foreground-lock restriction), so a caller that only loops on
    # SetForegroundWindow can spin its full budget without ever actually taking focus — indistinguishable from a
    # slow success until the budget runs out. ShowWindow(SW_RESTORE) + BringWindowToTop first breaks that lock
    # reliably (the same combination docs/verification/manual/camera-orbit-focus-loss.ps1's Focus-Window already
    # uses successfully). 30 attempts x 300ms (9s) rather than the old 10 x 300ms (3s): on a machine running
    # several concurrent windowed sessions, 3s of desktop contention is common, not exceptional.
    param($W)
    for ($attempt = 1; $attempt -le 30; $attempt++) {
        if ([Native]::GetForegroundWindow() -eq $W.Hwnd) { return }
        [void][Native]::ShowWindow($W.Hwnd, [Native]::SW_RESTORE)
        [void][Native]::BringWindowToTop($W.Hwnd)
        [void][Native]::SetForegroundWindow($W.Hwnd)
        Start-Sleep -Milliseconds 300
        if (($attempt % 5) -eq 0) { Write-Host "Ensure-Foreground: still retrying (attempt $attempt/30)" }
    }
    if ([Native]::GetForegroundWindow() -ne $W.Hwnd) { throw 'FAIL: could not bring the world window to the foreground before injection' }
}

function Get-ClientOrigin {
    param($W)
    $pt = [Native+POINT]::new()
    if (-not [Native]::ClientToScreen($W.Hwnd, [ref]$pt)) { throw 'ClientToScreen failed' }
    return $pt
}

function Get-ClientSize {
    param($W)
    $rect = [Native+RECT]::new()
    if (-not [Native]::GetClientRect($W.Hwnd, [ref]$rect)) { throw 'GetClientRect failed' }
    return @{ W = $rect.Right; H = $rect.Bottom }
}

function Move-CursorClient {
    param($W, [double]$X, [double]$Y)
    Ensure-Foreground -W $W
    $origin = Get-ClientOrigin -W $W
    [Native]::MoveTo([int]($origin.X + $X), [int]($origin.Y + $Y))
    Start-Sleep -Milliseconds 90
}

function Press-Left { param($W) Ensure-Foreground -W $W; [Native]::LeftDown(); Start-Sleep -Milliseconds 120 }
function Release-Left { param($W) Ensure-Foreground -W $W; [Native]::LeftUp(); Start-Sleep -Milliseconds 120 }

function Stop-World {
    param($W)
    try { $W.In.Close() } catch {}
    try { if (-not $W.Process.WaitForExit(8000)) { $W.Process.Kill($true) } } catch {}
    Get-EventSubscriber | Unregister-Event -Force -ErrorAction SilentlyContinue
}

function Dump-Streams {
    param($W, [string]$Prefix)
    Set-Content -Path (Join-Path $global:Scratch "$Prefix.out.log") -Value (@($W.Out) -join "`n") -Encoding utf8
    Set-Content -Path (Join-Path $global:Scratch "$Prefix.err.log") -Value (@($W.Err) -join "`n") -Encoding utf8
}

function Assert { param([bool]$Condition, [string]$What) if (-not $Condition) { throw "FAIL: $What" } else { Write-Host "PASS: $What" } }

# Ray math twin (camera: eye/yaw/pitch known from editor.cam.pose; fov 55 deg vertical; LookAt basis).
function Plane-Point {
    param([double[]]$Eye, [double]$YawDeg, [double]$PitchDeg, [double]$LocalX, [double]$LocalY, [double]$AspectW, [double]$AspectH, [double]$PlaneY)
    $yaw = $YawDeg * [Math]::PI / 180.0
    $pitch = $PitchDeg * [Math]::PI / 180.0
    $cp = [Math]::Cos($pitch)
    $f = @(([Math]::Sin($yaw) * $cp), [Math]::Sin($pitch), ([Math]::Cos($yaw) * $cp))
    # right = normalize(cross(forward, +Y)); up = cross(right, forward)
    $r = @((-$f[2]), 0.0, $f[0]); $rl = [Math]::Sqrt($r[0]*$r[0] + $r[2]*$r[2]); $r = @(($r[0]/$rl), 0.0, ($r[2]/$rl))
    $u = @(($r[1]*$f[2] - $r[2]*$f[1]), ($r[2]*$f[0] - $r[0]*$f[2]), ($r[0]*$f[1] - $r[1]*$f[0]))
    $tanHalf = [Math]::Tan(55.0 * [Math]::PI / 180.0 / 2.0)
    $aspect = $AspectW / $AspectH
    $ndcX = ($LocalX * 2.0) - 1.0
    $ndcY = 1.0 - ($LocalY * 2.0)
    $d = @(
        ($f[0] + $r[0] * ($ndcX * $tanHalf * $aspect) + $u[0] * ($ndcY * $tanHalf)),
        ($f[1] + $r[1] * ($ndcX * $tanHalf * $aspect) + $u[1] * ($ndcY * $tanHalf)),
        ($f[2] + $r[2] * ($ndcX * $tanHalf * $aspect) + $u[2] * ($ndcY * $tanHalf))
    )
    if ([Math]::Abs($d[1]) -lt 1e-9) { return $null }
    $t = ($PlaneY - $Eye[1]) / $d[1]
    if ($t -le 0) { return $null }
    return @(($Eye[0] + $d[0]*$t), ($Eye[1] + $d[1]*$t), ($Eye[2] + $d[2]*$t))
}
