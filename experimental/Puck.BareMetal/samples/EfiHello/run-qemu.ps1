# Build the EfiHello UEFI image and boot it headless in QEMU+OVMF, capturing the firmware console
# (which OVMF mirrors to COM1) to a file. Used to prove the Puck.Runtime core library boots.
#
#   pwsh -File run-qemu.ps1 [-BootSeconds 12]
param(
    [int]$BootSeconds = 12,
    [string]$VsDevShell = "C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\Tools\Launch-VsDevShell.ps1",
    [string]$QemuDir = "C:\Program Files\qemu"
)
$ErrorActionPreference = 'Stop'

$proj = $PSScriptRoot
$work = (New-Item -ItemType Directory -Force -Path (Join-Path $PSScriptRoot ".qemu")).FullName
$espDir = Join-Path $work "esp\EFI\BOOT"
New-Item -ItemType Directory -Force -Path $espDir | Out-Null
if (-not (Test-Path "$work\code.fd")) { Copy-Item "$QemuDir\share\edk2-x86_64-code.fd" "$work\code.fd" -Force }
if (-not (Test-Path "$work\vars.fd")) { Copy-Item "$QemuDir\share\edk2-i386-vars.fd" "$work\vars.fd" -Force }

& $VsDevShell -Arch amd64 -HostArch amd64 -SkipAutomaticLocation | Out-Null

$efi = "$proj\bin\Release\net10.0\win-x64\publish\Puck.BareMetal.EfiHello.exe"
# Clean obj/bin so the custom cl/ml64 objs are re-linked (NativeAOT doesn't track them).
Remove-Item "$proj\obj", "$proj\bin" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "=== building (clean) ==="
$log = dotnet publish $proj -r win-x64 -c Release 2>&1
$log | Select-Object -Last 6 | ForEach-Object { Write-Host $_ }
if (-not (Test-Path $efi)) { $log | ForEach-Object { Write-Host $_ }; throw "no efi produced" }
Copy-Item $efi "$espDir\BOOTX64.EFI" -Force

$serial = "$work\serial.log"
$espFat = "fat:rw:$(($work -replace '\\','/'))/esp"
$qargs = @(
    '-machine', 'q35', '-m', '256', '-cpu', 'max',
    '-drive', "if=pflash,format=raw,readonly=on,file=$work\code.fd",
    '-drive', "if=pflash,format=raw,file=$work\vars.fd",
    '-drive', "format=raw,file=$espFat",
    '-display', 'none', '-no-reboot', '-serial', "file:$serial"
)
Write-Host "=== booting ($BootSeconds s budget) ==="
$p = Start-Process "$QemuDir\qemu-system-x86_64.exe" -ArgumentList $qargs -PassThru -NoNewWindow -RedirectStandardError "$work\qemu.stderr.log"
$deadline = (Get-Date).AddSeconds($BootSeconds)
while (-not $p.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
if (-not $p.HasExited) { $p.Kill(); Write-Host "(killed QEMU after timeout)" }

Write-Host "=== serial ==="
if (Test-Path $serial) { Get-Content $serial -Raw }
