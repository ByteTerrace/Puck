# Build the EfiLinux UEFI image and boot it headless in QEMU+OVMF, capturing COM1 to a file.
# Self-contained: stages a scratch ESP + a writable OVMF vars store under ../.qemu (gitignored) on
# first run. Machine-specific paths (VS, QEMU) are below — adjust for another box.
#
#   pwsh -File run-qemu.ps1 [-BootSeconds 16] [-Pxe]
#
# -Pxe network-boots instead of booting off the FAT ESP: QEMU's slirp serves the SAME staged
# BOOTX64.EFI over its built-in TFTP, and iPXE's UEFI option ROM (efi-virtio.rom) supplies the
# virtio-net UEFI network driver (SNP) the bundled OVMF lacks, which the firmware's PXE Base Code
# then uses to DHCP + TFTP the image. Verified serial: ">>Start PXE over IPv4 ... NBP file
# downloaded successfully" then our own "[boot] Puck.BareMetal UEFI image entered". The downloaded
# image runs exactly as the disk-booted one. iPXE's virtio driver needs modern virtio, so the NIC
# drops `disable-modern=on` in this mode; the kernel's legacy PIO driver still binds the transitional
# device's BAR0 (with MSI-X forced off in PuckVirtioNetInit so the register layout is right after
# the firmware handed the NIC back). The ESP stays at a lower bootindex as a fallthrough.
#
# Part of Puck.BareMetal.
param(
    [int]$BootSeconds = 16,
    [switch]$Pxe,
    [string]$VsDevShell = "C:\Program Files\Microsoft Visual Studio\18\Enterprise\Common7\Tools\Launch-VsDevShell.ps1",
    [string]$QemuDir = "C:\Program Files\qemu"
)
$ErrorActionPreference = 'Stop'

$proj = Join-Path $PSScriptRoot ""                 # the EfiLinux project dir
$work = Join-Path $PSScriptRoot "..\.qemu" | Resolve-Path -ErrorAction SilentlyContinue
if (-not $work) { $work = (New-Item -ItemType Directory -Force -Path (Join-Path $PSScriptRoot "..\.qemu")).FullName }
$work = (Resolve-Path $work).Path

# Stage firmware (copied to a space-free path so QEMU's -drive parser is happy) + ESP + vars store.
$espDir = Join-Path $work "esp\EFI\BOOT"
New-Item -ItemType Directory -Force -Path $espDir | Out-Null

# Stage the dynamic guest's shared-library closure onto the ESP at \radv\ (the native EfiEntry reads
# it pre-ExitBootServices into RAM; too big to base64-embed). Sourced from the radv build's staging.
$radvSrc = Join-Path $PSScriptRoot "..\..\.qemu\radv"
if (Test-Path $radvSrc) {
    $radvDst = Join-Path $work "esp\radv"
    New-Item -ItemType Directory -Force -Path $radvDst | Out-Null
    Get-ChildItem "$radvSrc\*.so*" | ForEach-Object { Copy-Item $_.FullName $radvDst -Force }
}
if (-not (Test-Path "$work\code.fd")) { Copy-Item "$QemuDir\share\edk2-x86_64-code.fd" "$work\code.fd" -Force }
# PXE mode flips the boot order via NIC bootindex; a vars store that remembers "disk first" from a
# prior run would override it, so re-seed a clean vars.fd from the firmware default each PXE boot.
if ($Pxe) { Remove-Item "$work\vars.fd" -Force -ErrorAction SilentlyContinue }
if (-not (Test-Path "$work\vars.fd")) { Copy-Item "$QemuDir\share\edk2-i386-vars.fd" "$work\vars.fd" -Force }
# Stage iPXE's UEFI ROM to the space-free .qemu dir (its real path has a space that Start-Process
# -ArgumentList mangles, same reason the firmware is copied here).
if ($Pxe -and -not (Test-Path "$work\efi-virtio.rom")) { Copy-Item "$QemuDir\share\efi-virtio.rom" "$work\efi-virtio.rom" -Force }

& $VsDevShell -Arch amd64 -HostArch amd64 -SkipAutomaticLocation | Out-Null

$efi = "$proj\bin\Release\net10.0\win-x64\publish\Puck.BareMetal.EfiLinux.exe"
# NativeAOT's LinkNative does not track our custom cl/ml64 objs, so an incremental publish can leave
# a STALE .efi when only the native .c/.asm changed. Clean obj/bin to force a real re-link.
Remove-Item "$proj\obj", "$proj\bin" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "=== building (clean) ==="
$log = dotnet publish $proj -r win-x64 -c Release 2>&1
$log | Select-Object -Last 4 | ForEach-Object { Write-Host $_ }
if (-not (Test-Path $efi)) { $log | ForEach-Object { Write-Host $_ }; throw "no efi produced" }

# Staleness guard: the .efi must be at least as new as every native obj it should embed.
$newestObj = Get-ChildItem "$proj\bin\Release\net10.0\win-x64" -Filter *.obj -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($newestObj -and (Get-Item $efi).LastWriteTime -lt $newestObj.LastWriteTime) {
    throw "STALE EFI: link did not re-run ($efi older than $($newestObj.Name))."
}
Copy-Item $efi "$espDir\BOOTX64.EFI" -Force

$serial = "$work\serial.log"
$qerr = "$work\qemu.stderr.log"
$espFat = "fat:rw:$(($work -replace '\\','/'))/esp"
$tftp = "$(($work -replace '\\','/'))/esp/EFI/BOOT"          # TFTP root = the staged ESP (holds BOOTX64.EFI)
if ($Pxe) {
    # Network boot: NIC first (bootindex=0) with iPXE's UEFI ROM; ESP demoted to a fallthrough.
    # slirp serves BOOTX64.EFI over TFTP (DHCP option 67 -> next-server 10.0.2.2); ipv6 off to skip
    # the slow IPv6 PXE attempt. Modern virtio (no disable-modern) so iPXE can bind the NIC.
    $bootDevs = @(
        '-drive', "format=raw,file=$espFat,if=none,id=esp0",
        '-device', 'ide-hd,drive=esp0,bus=ide.0,bootindex=1',
        '-netdev', "user,id=n0,ipv6=off,tftp=$tftp,bootfile=BOOTX64.EFI,tftp-server-name=10.0.2.2",
        '-device', "virtio-net-pci,netdev=n0,romfile=$work\efi-virtio.rom,bootindex=0"
    )
} else {
    $bootDevs = @(
        '-drive', "format=raw,file=$espFat",
        '-netdev', 'user,id=n0',                                  # slirp: NATs the guest to the host internet
        '-device', 'virtio-net-pci,netdev=n0,disable-modern=on'   # legacy virtio-net (PIO virtqueue setup)
    )
}
$qargs = @(
    '-machine', 'q35', '-m', '512', '-rtc', 'base=utc', '-cpu', 'max',
    '-drive', "if=pflash,format=raw,readonly=on,file=$work\code.fd",
    '-drive', "if=pflash,format=raw,file=$work\vars.fd"
) + $bootDevs + @(
    '-display', 'none', '-no-reboot',
    '-serial', "file:$serial"
)
Write-Host "=== booting ($BootSeconds s budget) ==="
$p = Start-Process "$QemuDir\qemu-system-x86_64.exe" -ArgumentList $qargs -PassThru -NoNewWindow -RedirectStandardError $qerr
$deadline = (Get-Date).AddSeconds($BootSeconds)
while (-not $p.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
if (-not $p.HasExited) { $p.Kill(); Write-Host "(killed QEMU after timeout)" }

Write-Host "=== COM1 serial ==="
if (Test-Path $serial) { Get-Content $serial -Raw }
