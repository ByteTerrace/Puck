#!/bin/sh
# Build the Vulkan bring-up guest (samples/EfiLinux/guest/vktest.c) against the lean musl RADV built
# by build-radv-musl.sh, and stage it next to the RADV closure. vktest links the RADV ICD directly
# (extern vk_icdGetInstanceProcAddr -- no Khronos loader, since musl static can't dlopen and the
# bare-metal loader links dynamically anyway) and drives vkCreateInstance -> vkEnumeratePhysicalDevices.
#
# Run AFTER build-radv-musl.sh, in WSL as root (it reuses the same /root/aroot Alpine rootfs):
#   wsl -d <distro> -u root sh build-vktest-musl.sh
#
# The staged binary is embedded into GuestElf.cs via samples/EfiLinux/embed-dyn.ps1:
#   embed-dyn.ps1 -Main ..\..\.qemu\radv\vktest -Libs @{}     # the .so closure is ESP-loaded, not embedded
# Vendoring policy: RADV/Mesa is third-party (MIT); only vktest.c + the port glue are ours.
#
# Part of Puck.BareMetal.
set -e
ROOT=/root/aroot
VER=26.1.1
SRC="$(cd "$(dirname "$0")/.." && pwd)/samples/EfiLinux/guest/vktest.c"
STAGE_OUT="$(cd "$(dirname "$0")/.." && pwd)/.qemu/radv"

for d in proc sys dev; do mount --bind "/$d" "$ROOT/$d" 2>/dev/null || true; done
chroot "$ROOT" /sbin/apk add vulkan-headers >/dev/null 2>&1 || true
cp "$SRC" "$ROOT/root/vktest.c"

SO=/root/mesa-$VER/build-radv/src/amd/vulkan/libvulkan_radeon.so
chroot "$ROOT" sh -c "cd /root && gcc -O2 vktest.c -o vktest $SO"
echo "=== vktest NEEDED ==="; chroot "$ROOT" sh -c "readelf -d /root/vktest | grep NEEDED"
echo "=== run on the Alpine host (no GPU; expect instance OK, 0 devices) ==="
chroot "$ROOT" sh -c "cd /root && ./vktest 2>&1 | head"

cp "$ROOT/root/vktest" "$STAGE_OUT/vktest"
echo "staged vktest -> $STAGE_OUT"
