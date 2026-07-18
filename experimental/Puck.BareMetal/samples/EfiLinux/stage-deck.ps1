# Stage the EfiLinux image onto a FAT32 volume (USB stick) for booting a real Steam Deck.
# Lays out the same ESP tree run-qemu.ps1 stages for QEMU:
#
#   EFI\BOOT\BOOTX64.EFI   (the freshly built image)
#   radv\*.so*             (the RADV closure; see radv/build-radv-musl.sh)
#   amdgpu\vangogh_*.bin   (the GPU microcode; vendored under ..\..\amdgpu\firmware)
#
# Boot: plug the stick into the Deck (USB-C hub), hold Vol-Down + Power, pick the USB entry.
#
#   pwsh -File stage-deck.ps1 -Target E:\        # stage to a mounted USB stick
#   pwsh -File stage-deck.ps1 -Target ..\.deck   # or any directory, to copy manually
#   pwsh -File stage-deck.ps1 -Target E:\ -SkipBuild   # reuse the last build
#
# Part of Puck.BareMetal.
param(
    [Parameter(Mandatory = $true)][string]$Target,
    [switch]$SkipBuild,
    [string]$VsDevShell = "C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\Tools\Launch-VsDevShell.ps1"
)
$ErrorActionPreference = 'Stop'

$proj = $PSScriptRoot
$efi = "$proj\bin\Release\net10.0\win-x64\publish\Puck.BareMetal.EfiLinux.exe"

if (-not $SkipBuild) {
    & $VsDevShell -Arch amd64 -HostArch amd64 -SkipAutomaticLocation | Out-Null
    # Clean build: NativeAOT's LinkNative does not track the custom cl/ml64 objs (same reason as
    # run-qemu.ps1), so an incremental publish can ship a stale .efi.
    Remove-Item "$proj\obj", "$proj\bin" -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "=== building (clean) ==="
    $log = dotnet publish $proj -r win-x64 -c Release 2>&1
    $log | Select-Object -Last 4 | ForEach-Object { Write-Host $_ }
}
if (-not (Test-Path $efi)) { throw "no image at $efi (build failed?)" }

$boot = Join-Path $Target "EFI\BOOT"
New-Item -ItemType Directory -Force -Path $boot | Out-Null
Copy-Item $efi (Join-Path $boot "BOOTX64.EFI") -Force

$radvSrc = Join-Path $proj "..\..\.qemu\radv"
if (Test-Path $radvSrc) {
    $radvDst = Join-Path $Target "radv"
    New-Item -ItemType Directory -Force -Path $radvDst | Out-Null
    Get-ChildItem "$radvSrc\*.so*" | ForEach-Object { Copy-Item $_.FullName $radvDst -Force }
    Write-Host "staged RADV closure ($((Get-ChildItem "$radvDst\*.so*").Count) files)"
} else {
    Write-Host "NOTE: no RADV closure at $radvSrc (run radv/build-radv-musl.sh in WSL); staging image only"
}

# GPU microcode: the kernel preloads \amdgpu\vangogh_*.bin from the ESP and feeds it to the PSP
# during PSP firmware initialization; see docs/gfx103-bringup-spec.md.
$fwSrc = Join-Path $proj "..\..\amdgpu\firmware"
if (Test-Path $fwSrc) {
    $fwDst = Join-Path $Target "amdgpu"
    New-Item -ItemType Directory -Force -Path $fwDst | Out-Null
    Get-ChildItem "$fwSrc\vangogh_*.bin" | ForEach-Object { Copy-Item $_.FullName $fwDst -Force }
    Write-Host "staged GPU microcode ($((Get-ChildItem "$fwDst\vangogh_*.bin").Count) blobs)"
} else {
    Write-Host "NOTE: no GPU microcode at $fwSrc; GPU bring-up will skip"
}

Write-Host "=== staged to $Target ==="
Get-ChildItem -Recurse $Target | Select-Object -ExpandProperty FullName | ForEach-Object { Write-Host "  $_" }
