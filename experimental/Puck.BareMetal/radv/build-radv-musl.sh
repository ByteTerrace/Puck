#!/bin/sh
# Build a LEAN, musl-linked Mesa RADV Vulkan driver (libvulkan_radeon.so) for the bare-metal guest,
# and stage it with its full runtime shared-library closure.
#
# Why lean: Alpine's prebuilt RADV pulls in libLLVM + the whole X11/Wayland/xcb/udev WSI stack
# (~30 libs). We disable LLVM (RADV's ACO compiler needs none), all WSI platforms, Gallium, and
# video, which drops the NEEDED closure to 8 libs:
#   libdrm_amdgpu, libdrm, libelf, libz, libSPIRV-Tools, libstdc++, libgcc_s, libc.musl(=ld-musl)
#
# Runs in WSL (any distro) as root; it stands up an Alpine-musl rootfs on PERSISTENT ext4
# (/root/aroot; /tmp is wiped when the WSL distro idle-restarts) and builds there. Output is staged
# to ../.qemu/radv (gitignored). Re-runnable. Vendoring policy: RADV/Mesa is third-party (MIT); only
# the bare-metal port glue is ours.
#
#   sudo sh build-radv-musl.sh            # or: wsl -d <distro> -u root sh build-radv-musl.sh
#
# Part of Puck.BareMetal.
set -e
ROOT=/root/aroot
MIRROR=https://dl-cdn.alpinelinux.org/alpine
BR=latest-stable
ARCH=x86_64
VER=26.1.1   # Mesa source version (matches Alpine's mesa-vulkan-ati at time of writing)
STAGE_OUT="$(cd "$(dirname "$0")/.." && pwd)/.qemu/radv"

# 1. Alpine-musl rootfs (persistent) + build deps -------------------------------------------------
if [ ! -x "$ROOT/sbin/apk" ]; then
  rm -rf "$ROOT"; mkdir -p "$ROOT"; cd /tmp
  MR=$(wget -qO- "$MIRROR/$BR/releases/$ARCH/" | grep -oE 'alpine-minirootfs-[0-9.]+-x86_64\.tar\.gz' | sort -V | tail -1)
  wget -q "$MIRROR/$BR/releases/$ARCH/$MR" -O minirootfs.tar.gz
  tar xzf minirootfs.tar.gz -C "$ROOT"
  printf '%s/%s/main\n%s/%s/community\n' "$MIRROR" "$BR" "$MIRROR" "$BR" > "$ROOT/etc/apk/repositories"
fi
cp /etc/resolv.conf "$ROOT/etc/resolv.conf" 2>/dev/null || true
for d in proc sys dev; do mkdir -p "$ROOT/$d"; mount --bind "/$d" "$ROOT/$d" 2>/dev/null || true; done
chroot "$ROOT" /sbin/apk update
chroot "$ROOT" /sbin/apk add build-base meson samurai python3 py3-mako py3-yaml py3-packaging \
    pkgconf flex bison glslang glslang-dev libdrm-dev zlib-dev elfutils-dev linux-headers \
    bash curl xz binutils spirv-tools-dev

# 2. Mesa source + minimal meson config ----------------------------------------------------------
chroot "$ROOT" sh -c "cd /root && (test -f mesa-$VER.tar.xz || curl -fsSL https://archive.mesa3d.org/mesa-$VER.tar.xz -o mesa-$VER.tar.xz) && rm -rf mesa-$VER && tar xJf mesa-$VER.tar.xz"
chroot "$ROOT" sh -c "cd /root/mesa-$VER && rm -rf build-radv && meson setup build-radv \
    -Dvulkan-drivers=amd -Dgallium-drivers= -Dplatforms= -Dllvm=disabled \
    -Dvideo-codecs= -Dvulkan-layers= \
    -Dglx=disabled -Degl=disabled -Dgbm=disabled -Dopengl=false -Dgles1=disabled -Dgles2=disabled \
    -Dxmlconfig=disabled -Dzstd=disabled -Dbuildtype=release -Db_ndebug=true"

# 3. Build just the RADV ICD ---------------------------------------------------------------------
chroot "$ROOT" sh -c "cd /root/mesa-$VER && ninja -C build-radv src/amd/vulkan/libvulkan_radeon.so"

# 4. Stage stripped .so + full runtime closure (incl ld-musl) ------------------------------------
SO=/root/mesa-$VER/build-radv/src/amd/vulkan/libvulkan_radeon.so
chroot "$ROOT" sh -c "rm -rf /root/radv-stage && mkdir -p /root/radv-stage && strip -o /root/radv-stage/libvulkan_radeon.so $SO"
chroot "$ROOT" sh -c "ldd $SO | grep '=>' | awk '{print \$3}' | while read f; do cp -L \"\$f\" /root/radv-stage/ 2>/dev/null || true; done; cp -L /lib/ld-musl-x86_64.so.1 /root/radv-stage/ld-musl-x86_64.so.1"
rm -rf "$STAGE_OUT"; mkdir -p "$STAGE_OUT"; cp "$ROOT"/root/radv-stage/* "$STAGE_OUT"/
echo "=== staged $(ls "$STAGE_OUT" | wc -l) files to $STAGE_OUT ==="
ls -la "$STAGE_OUT"
chroot "$ROOT" sh -c "readelf -d /root/radv-stage/libvulkan_radeon.so | grep NEEDED"
