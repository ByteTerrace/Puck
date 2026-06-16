# amdgpu + RADV on Puck.BareMetal — GPU-accelerated Vulkan (Steam Deck target)

Goal: run **real GPU-accelerated Vulkan** on the bare-metal fake-Linux host, by hosting the open
AMD GPU driver stack and testing on a **Steam Deck**. This is the renderer half of "run the engine
bare-metal." Standing rule (as with puck / lwIP / mbedTLS): **vendor the professional resources,
write only the freestanding PORT glue.** We vendor *what the GPU is* (the open `amdgpu` bring-up
logic + register headers as reference, Mesa **RADV** as the userspace Vulkan driver, AMD signed
firmware verbatim); we author *the host substrate the driver assumes Linux already provided*.

## Why AMD/Steam Deck (we abandoned NVIDIA)

1. **Open, specified kernel UAPI.** RADV's kernel contract is ~8 `amdgpu` ioctls (`GEM_CREATE`,
   `GEM_MMAP`, `GEM_VA`, `CTX`, `BO_LIST`/`BO_HANDLES`, `CS`, `INFO`, `FENCE_TO_HANDLE`) + DRM
   syncobj — all public in `amdgpu_drm.h`. NVIDIA's KMD UAPI is closed/opaque.
2. **musl-friendly, open, LLVM-free userspace.** RADV builds on Alpine/musl, compiles shaders with
   **ACO** in-process (no LLVM, no subprocess), needs only a **render node** (no KMS/display for
   offscreen), and exports a single ICD entry point so we bypass the Khronos loader. NVIDIA's UMD is
   a proprietary glibc-only blob — incompatible with our musl static-pie guest.
3. **Expendable, USB-bootable, known-fixed target.** The Deck ships in Secure-Boot setup mode
   (unsigned `.efi` boots), has a boot picker (Vol-Down+Power), boots USB-C media, supports UEFI PXE
   over a dock NIC (reuses our PXE work), never touches internal storage for custom-EFI boot, and has
   one-button Valve recovery. One GPU to bring up: **`gfx1033` / RDNA2 / 8 CU**.

**Target:** Steam Deck **LCD (Van Gogh, PCI `1002:163F`)** as the canonical bring-up unit — most
prior art/firmware maturity. **OLED (Sephiroth)** is a near-identical second pass (same `gfx1033`,
8 CU; deltas are display/memory-clock, outside the 3D engine).

## Feasibility verdict

**Achievable; MONTHS-tier — the largest undertaking in this codebase.** We are building real kernel
HW-management infrastructure that doesn't exist today, then a GPU bring-up driver on top. Honest
blockers:
- **#1 PSP signed-firmware handshake (GFX10.3 / PSP v11).** GFX/SDMA/SMU microcode loads *through*
  the on-die PSP (MP0 mailbox: TOC → TMR → ring-submit blobs → RLC autoload). The PSP is a black box;
  it accepts or silently wedges. Schedule risk peak. Mitigation: we inherit a *warm* PSP (UEFI/GOP
  already posted the GPU) → we do the fw-load handshake, not a cold boot; lean on mode1-reset.
- **#2 No drop-in prior art for RDNA2.** tinygrad's "AM" (kernel-free AMD→compute) is GFX11; Van
  Gogh is GFX10.3 (F32 CP, PSP v11). AM is a *map*; re-derive GFX10.3 specifics from Linux amdgpu
  `gfx_v10_0.c` / `psp_v11_0.c` / `gmc_v10_0.c`.
- **#3 The entire host substrate is missing** (ECAM, LAPIC/MSI, high-BAR mapping, PAT, DMA/IOMMU,
  DRM ioctl plumbing). Most of the calendar time — but it's nearly all QEMU-testable *now*.

## Architecture

```
Puck.BareMetal kernel (ring 0):
  NEW substrate (our port glue):
    PCIe ECAM (ACPI MCFG) · MMIO map incl. >4GiB + PAT(UC/WC) · LAPIC+MSI/MSI-X+EOI · IH ring
    large contiguous DMA + GTT builder + IOMMU domain · clflush/wbinvd/udelay
  VENDORED minimal amdgpu (render/compute only — NO KMS/DCN/VCN):
    GMC/GPUVM (gfx1033) · PSP v11 fw-load · GFX F32 CP ring + MQD + doorbell · fence
    TTM-lite BO mgr (pin-everything, UMA) · firmware: vangogh_{pfp,me,ce,mec,mec2,rlc,toc,asd,sos,sdma}.bin
  DRM SHIM (our glue): /dev/dri/renderD128 · _IOC decode · the ~8 amdgpu ioctls + syncobj · fd-backed mmap
ring-3 musl guest:
  RADV (libvulkan_radeon, -Dvulkan-drivers=amd -Dllvm=disabled) · ACO (in-proc, no LLVM)
  dlopen ICD → vk_icdGetInstanceProcAddr (skip loader) · offscreen: render → CopyImageToBuffer → map → checksum
```

Decisions: render-node only (no KMS); skip DCN/VCN/MES/RAS/SR-IOV; clean-room against the UAPI +
RADV winsys (don't copy GPL `.c`); vendor MIT register headers + `LICENSE.amdgpu` firmware verbatim;
poll fences before wiring IH.

## Staged roadmap

| # | Milestone | Verify | Where |
|---|-----------|--------|-------|
| **a** | **Kernel substrate**: ECAM ✓ · 64-bit/high-BAR map · PAT UC/WC · LAPIC+MSI-X+EOI · contiguous DMA + GTT · IOMMU domain · `/dev/dri/renderD128` ioctl + fd-mmap · missing musl syscalls | unit-prove each vs QEMU devices | **QEMU** |
| **b** | Enumerate APU GPU + map MMIO + read `GRBM_STATUS`/`RCC_CONFIG_MEMSIZE` | sane non-FF values | DECK |
| **c** | GMC/GPUVM (+ optional IH) | R/W a BO via GPU VA; no page-fault status | DECK |
| **d** | PSP fw-load + GFX/SDMA microcode (MP0 TOC/TMR) | `CP_STAT` ready — **risk peak** | DECK |
| **e** | Ring/IB submit + fence | fence seqno in BO — **first silicon executes** | DECK |
| **f** | The amdgpu ioctls RADV needs (INFO/GEM/CS/syncobj) | ioctl test client drives a dispatch | DECK (shape testable QEMU) |
| **g** | RADV `vkEnumeratePhysicalDevices` sees the Deck GPU | reports `gfx1033` w/ correct CU/heaps | DECK |
| **h** | **Offscreen triangle + readback + checksum** | pixel checksum matches reference | DECK |

Substrate (a) is the long pole and is 100% QEMU-parallelizable now. (b) is the cheap day-one Deck
observable; (d) is the risk peak; (e) is "it lives"; (h) is the deliverable.

### Progress
- [x] **(a.1) PCIe ECAM** — ACPI RSDP captured pre-ExitBootServices → XSDT → MCFG → ECAM base;
  MMIO config enumeration cross-validated against the legacy `0xCF8` path (0 mismatches) + extended
  config (≥`0x100`) reachable. `puck-efi.c` `PuckAcpiCaptureRsdp` / `PuckPciEcamDump`.
- [x] **(a.2-i) LAPIC (x2APIC) bring-up** — `beb9926`: x2APIC enabled (MSR-based), LINT0 left in
  virtual-wire so the PIC timer survives; self-IPI proves vector → IDT gate → ISR → EOI. Reusable
  kernel-context IRQ stub `PuckIrqTestIsr` for every future MSI/MSI-X vector. `PuckInitLapic`/
  `PuckLapicSelfTest`/`PuckLapicEoi`.
- [x] **(a.2-ii) MSI-X on a real device** — `PuckNetMsixProve`: program the MSI-X table entry
  (x2APIC physical message), enable MSI-X (Function-Mask forced clear), route virtio-net's TX queue
  to vector 0x42, fire a TX in a brief IF=1 window (8259 mask saved/restored), confirm ISR+EOI, then
  tear MSI-X down to the legacy layout before lwIP. Verified: real TX-completion interrupt delivered,
  and DHCP/TLS still sing afterward. The IRQ stack (a.2) is complete.
- [x] **(a.3) 64-bit/high-BAR decode + PAT UC/WC** — `PuckBarDecode`/`PuckBarSize` (64-bit
  two-dword sizing), `PuckInitPat` (PA6=WC, PA7=UC), `PuckMapMmio` (low path retunes the
  existing 2MB PDE in place; high path >4GiB builds the pml4[n] chain with 4KB leaves), `wbinvd`/
  `clflush`/`mfence` wrappers. QEMU exercised BOTH paths (virtio-gpu BAR4 landed at 768 GiB).
  Mechanics verified; real WC/UC *semantics* are Deck-only (QEMU guest RAM == host RAM).
- [x] **(a.4-i) DMA-coherent allocator + IOMMU detection** — `PuckDmaAlloc` (explicit {cpu,phys},
  4KB/64KB/2MB alignment verified), `PuckDetectIommu` (ACPI IVRS/DMAR via the reused XSDT walk);
  virtio-net vrings+buffers routed through it, proven by a full TLS session over real device DMA.
- [ ] (a.4-ii) IOMMU passthrough/identity domain *programming* (needs an emulated IOMMU in QEMU to
  test; AMD-Vi on the Deck) + the GTT page-table builder (amdgpu GPUVM — part of the GPU port).
- [~] **(a.5-i) `/dev/dri/renderD128` DRM seam (core)** — render node in the synthetic VFS;
  `_IOC`-decoded ioctl dispatch; `DRM_IOCTL_VERSION` two-call protocol answers "amdgpu"; fd-backed
  mmap (fake BO). Proven by a musl `guest/drm.c` (DRM_VERSION roundtrip + BO mmap). `mkguest.ps1`
  reconstructed as a committed helper.
- [x] **(a.5-ii) amdgpu `AMDGPU_INFO`** — `PuckDrmIoctl` answers `DEV_INFO` (device_id=0x163F,
  family=0x8B=VGH, 8 CU, APU/FUSION, gfx10.3 wave32), `HW_IP_INFO`, `ACCEL_WORKING`; unknown queries
  return benign zeros. **The hard "family gate" is cleared.**
- [x] **(a.5-iii) drmGetDevices2 shape** — `fstat`/`newfstatat`/`statx` (S_IFCHR, rdev 226:128),
  `/dev/dri`+`/sys` tree (new directory + symlink VFS kinds), `getdents64`/`readlink(at)`, plus
  `fcntl`(dup)/`sched_getaffinity`(1 CPU)/`sysinfo`/`madvise`/`mremap`/`rt_sigaction`/`getpid` stubs.
  fd-backed mmap already served by the anonymous path. **a.5 complete: the kernel side of the GPU
  userspace seam is done — everything RADV needs to enumerate the device.** Proven by `guest/drm.c`.
- [~] **(a.6-i) lean RADV built on musl** — `radv/build-radv-musl.sh`: Alpine-musl rootfs in WSL,
  Mesa 26.1.1, `meson -Dvulkan-drivers=amd -Dllvm=disabled -Dplatforms= -Dgallium-drivers=` →
  `libvulkan_radeon.so` with the ICD entry `vk_icdGetInstanceProcAddr`. **LLVM-free (ACO), WSI-free**:
  the NEEDED closure dropped from ~30 libs (Alpine prebuilt) to **8** (libdrm_amdgpu, libdrm, libelf,
  libz, libSPIRV-Tools, libstdc++, libgcc_s, libc.musl). Full runtime closure = 10 files / 24.6 MB
  (+ ld-musl), staged to `.qemu/radv`.
- [x] (a.6-ii) **dynamic ELF loader** (the hard half) — musl static-pie CAN'T `dlopen`, so Route A:
  the guest is a dynamic musl PIE with `libvulkan_radeon.so` NEEDED. Program.cs handles `PT_INTERP`
  (loads ld-musl, sets AT_BASE/AT_PHDR/AT_ENTRY auxv), and the VFS serves the `.so` files with **real
  file-backed `mmap`** (ld-musl maps their segments). De-risked with `guest/dynhello.c` (libc only)
  then `guest/{libfoo,foouser}.c` (an external .so). The RADV closure is ESP-loaded pre-ExitBootServices
  (`PuckEfiPreloadSos`, too big to base64-embed) rather than via envp force-family.
- [x] **(a.6-iii) RADV ENUMERATES THE SYNTHETIC GPU** — `guest/vktest.c` links the ICD directly
  (`extern vk_icdGetInstanceProcAddr`, no loader) and runs `vkCreateInstance` →
  `vkEnumeratePhysicalDevices` → `vkGetPhysicalDeviceProperties` bare-metal: **"VULKAN DRIVER IS
  RUNNING / device 0: ... (RADV VANGOGH) vendor 0x1002 device 0x163f"**. Getting there hardened the
  seam: (1) **per-file `st_dev`/`st_ino`** — ld-musl `map_library` dedups DSOs by (dev,ino); all-zero
  folded every dependency into the first lib (no symbols). (2) a **symlink-resolving path canonicalizer**
  (`PuckResolvePath`) + intermediate dirs/`uevent`/`subsystem` — libdrm stats *through* the
  `/sys/dev/char/MAJ:MIN` symlinks. (3) `stat`/`lstat`/`access`, readlink returning EINVAL (not ENOENT)
  on non-symlinks, uid/gid stubs. (4) DRM ioctls: `GET_CLIENT`(auth), `GET_CAP`, `SET_CLIENT_CAP`,
  `SYNCOBJ_*` (faked handles, waits=signalled). (5) DEV_INFO fixes: **family = 144/AMDGPU_FAMILY_VGH
  (was 0x8B/139 — wrong)**, `external_rev = VANGOGH_A0`, DRM version ≥ 3.54, real `wave_front_size`
  offset. **This is the milestone: the Vulkan driver runs bare-metal and sees the Van Gogh node.**
- [x] **(a.6-iv) LOGICAL DEVICE + RESOURCES + ACO SHADER COMPILE** — `guest/vktest.c` now drives the
  driver as far as a GPU-less host allows: `vkCreateDevice` → `vkCreateBuffer`/`vkAllocateMemory`/
  `vkBindBufferMemory`/`vkMapMemory` (CPU-write a BO) → `vkCreateComputePipelines` from embedded SPIR-V.
  All succeed: **"COMPUTE PIPELINE COMPILED (ACO → RDNA2 ISA)"** — AMD's ACO compiler runs bare-metal
  and lowers SPIR-V to real RDNA2 machine code. New kernel surface: a **host-RAM BO allocator**
  (`g_puckBos`: GEM_CREATE allocs user-accessible pages, GEM_MMAP returns the BO's CPU addr as the
  mmap offset, an mmap on the render-node fd returns that page, GEM_VA is a no-op — no GPUVM modelled),
  **AMDGPU_CTX** (submission context), **AMDGPU_INFO_MEMORY** (256 MB carveout + 3 GB GTT) + `cu_bitmap`
  (8 CU), `GEM_CLOSE`. envp plumbed in Program.cs (`RADV_DEBUG=startup,info` drove the bring-up; empty
  by default). **This is the ceiling QEMU can reach: everything except command EXECUTION.** The one gap
  to a real frame is `AMDGPU_CS` (ring submit) + fences hitting actual RDNA2 silicon — the on-Deck port.
  KNOWN ISSUE: an intermittent #PF during ld-musl linking (~1 boot in N) — a wild low-address write,
  likely RDRAND-seeded layout nondeterminism exposing a latent pointer bug; re-running clears it.
  What it does NOT yet do: real GPUVM / GFX-ring submission / fences — those are the on-Deck port
  (b)–(h); device creation + shader compilation need none of them.

## Dev loop

```
build .efi on Windows box  →  USB stick OR TFTP/PXE  →  boot Deck (Vol-Down+Power / dock-PXE)  →  observe  →  iterate
```
Reuses PXE (`80c1e7a`) + lwIP (`03aa334`). **QEMU validates the whole substrate** (a); it has no
RDNA model, so (b)–(h) need the real Deck.

### Open questions for the operator (gate the on-Deck phase only)
1. Have a Deck in hand? (LCD chosen as canonical.)
2. Staging: USB stick first, or straight to dock-PXE?
3. Console: GOP framebuffer text first (handle the 90° portrait rotation), then a USB CDC-ACM serial
   gadget (needs BIOS *USB Dual Role Device → DRD*, re-flip after each firmware update)? netconsole
   over the dock NIC is the fallback once lwIP is up.
4. Supporting HW: USB-C hub (power+keyboard+Ethernet), USB keyboard, SteamOS recovery USB.

### Firmware + licensing
`vangogh_*.bin` (`pfp,me,ce,mec,mec2,rlc,toc,asd,sos,sdma`; skip `vcn`/`dmcub`) from upstream
`linux-firmware/amdgpu/`, vendored verbatim under `LICENSE.amdgpu` (binary, unmodified, carry the
notice). RADV/Mesa = MIT. amdgpu register headers = MIT (verify per-file SPDX). amdgpu `.c` logic =
GPL → **reference only**, clean-room our port against the UAPI + RADV winsys.
