# amdgpu + RADV on Puck.BareMetal — GPU-accelerated Vulkan (Steam Deck target)

> **Continuing this work (esp. on a new machine)? Start with
> [`deck-bringup-handoff.md`](deck-bringup-handoff.md)** — it has the live stage-(e) state, the
> hardware facts, the debug trail, how to get logs off the Deck, and the machine-local rebuild steps.
>
> **Update 2026-07-09:** a deep-research pass produced
> [`deck-demo-research.md`](deck-demo-research.md) (consolidated gap map + verified claims) and
> [`deck-demo-session-plan.md`](deck-demo-session-plan.md) (the next session's plan). Strategic
> pivot: the demo path is now **compute-queue (MEC) + GOP-framebuffer present** — the gfx ring
> (and this roadmap's stage-(h) offscreen triangle) is demoted to a diagnostic sideshow until
> the compute path proves out. Read stages f–h below compute-first.

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

> **Reality check (2026-07-03): the in-hand unit is an OLED** — the first Deck boot of the stage-(b)
> probe reported `1002:1435` rev `0xae` at 04:00.0, which is **Sephiroth** ("AMD Custom GPU 0932",
> per the PCI ID registry). Same gfx10.3.1/PSP v11.5 IP set, so the vendored headers/firmware and
> all register math apply unchanged; Sephiroth is simply now the canonical bring-up unit and LCD the
> second pass. The same boot also proved the full ring-3 RADV+ACO stack on Deck silicon (synthetic
> device): `COMPUTE PIPELINE COMPILED (ACO -> RDNA2 ISA)` on the panel — and Deck firmware
> publishes **no IVRS** (no IOMMU in the way -> direct DMA; a.4-ii is moot on-target) and hides
> x2APIC (fence polling unaffected). The second boot's raw config dump showed firmware **does**
> assign the register BAR (BAR5 `0x80500000`, 32-bit non-prefetch) — the first boot's "no register
> BAR" was a probe bug (BAR decode ran in a for-loop *increment*, i.e. after the body: the first
> entry was sized undecoded and BAR5 landed past `barCount`). Probe v3 decodes-then-sizes; the
> bridge-window assignment path is kept as a fallback only. Deck BAR map (Sephiroth): BAR0 pf
> 256 MiB @ `0xF8_E000_0000` (FB aperture, GOP fb inside), BAR2 pf 2 MiB @ `0xF8_F000_0000`
> (doorbells), BAR4 io 256 B @ `0x1000`, BAR5 mem 512 KiB @ `0x8050_0000` (registers).

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
| **d** | PSP fw-load + GFX/SDMA microcode (MP0 TOC/TMR) | `CP_STAT` ready — **risk peak** ✅ **DONE on Deck 2026-07-03** | DECK |
| **e** | Ring/IB submit + fence | fence seqno in BO — **ring test PASSES on real Deck hardware** (CP fetches + executes PM4, register and memory writes land, `CACHE_POLICY=BYPASS` fix confirmed); IB+`RELEASE_MEM` fence not yet signalled — two open mysteries (a deterministic mid-CSB stall, an intermittent GART-block eraser) tracked in [`deck-bringup-handoff.md`](deck-bringup-handoff.md) | DECK |
| **f** | The amdgpu ioctls RADV needs (INFO/GEM/CS/syncobj) | ioctl test client drives a dispatch | DECK (shape testable QEMU) |
| **g** | RADV `vkEnumeratePhysicalDevices` sees the Deck GPU | reports `gfx1033` w/ correct CU/heaps | DECK |
| **h** | **Offscreen triangle + readback + checksum** | pixel checksum matches reference | DECK |

Substrate (a) is the long pole and is 100% QEMU-parallelizable now. (b) is the cheap day-one Deck
observable; (d) is the risk peak; (e) is "it lives"; (h) is the deliverable.

- [x] **(c)+(d) SUCCEEDED ON THE DECK (2026-07-03, first stage-(c)/(d) boot)**. GART built with
  `VM_L2_FAULT=0` (stage c PASS); the PSP firmware-load chain authenticated the GFX/SDMA/RLC
  microcode and autoloaded the RLC from a cold boot — proven by control flow: reaching the post-load
  un-halt requires `PuckGpuPspLoad` to have returned success, which is gated on
  `RLC_RLCS_BOOTLOAD_STATUS` bit31 + `CP_STAT==0` (the spec's stage-(d)-done condition), and every
  prior cold boot showed `RLC_BOOTLOAD=0` so our load is what set it. **The PSP risk peak is cleared
  under Puck's kernel on real Van Gogh/Sephiroth silicon.** Post-fix nit: the first boot logged a
  spurious "TIMEOUT CP_STAT after un-halt" — an over-strict recheck; a just-un-halted CP with no ring
  correctly reads busy (`CP_STAT=0x80000000`), which is a stage-(e) observable, not a (d) failure.
  Fixed: the un-halt now reports CP_STAT without gating, and a `g_gpuBringUpNote` latch restates the
  outcome on the parked health screen (`PSP LOAD OK` vs `RLC warm` vs a failure).
- [~] **(c)+(d) implementation notes** — `puck-efi.c`:
  `PuckEfiPreloadGpuFw` loads the 9 `vangogh_*.bin` from ESP `\amdgpu\` into a kernel table
  (`g_gpuFw[]`); `PuckGpuGartBringUp` (stage c) maps a 32 MiB UC window at carveout+64 MiB (holds
  TMR + PSP ring/cmd/fence + the GART page table, table-in-VRAM so the non-snooping walker reads
  DRAM), builds a single-level VMID0 GART (256 MiB @ GPU-VA 0, PTE `pa|VALID|SYSTEM|SNOOPED|EXE|
  R|W`, MTYPE_UC), programs GFXHUB PT-root/aperture/L1-TLB/L2/CONTEXT0 (MMHUB + FB_LOCATION
  untouched), HDP-flush + ENG17 invalidate, then asserts `GCVM_L2_PROTECTION_FAULT_STATUS==0`;
  `PuckGpuPspLoad` (stage d) creates the PSP KM ring, LOAD_TOC → SETUP_TMR → LOAD_IP_FW (sdma, ce,
  pfp, me, mec, mec2, RLC v2.1 sublists cntl/gpm/srm, RLC_G) → AUTOLOAD_RLC, polls
  `RLC_RLCS_BOOTLOAD_STATUS` bit31 + `CP_STAT==0`, then un-halts the CP (`CP_ME_CNTL=0`). Every
  register offset header-verified; every poll TSC-deadlined (print-status + return on timeout — no
  hang, no reset, no wbinvd, panel always survives). QEMU: 9 preload lines + clean skip + vktest
  intact. Success line on the Deck: `[gpu] (d) MICROCODE LIVE: RLC bootloaded, CP un-halted`.
  UNVERIFIED (each fail-safe, logged): RLC sublist fw-type ids 15/16/17, mec2→CP_MEC, SETUP_TMR
  virt_phy_addr bit, exact L2_CNTL values, +64 MiB window placement.

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

- [x] **(assets) Van Gogh firmware + register headers vendored** — `amdgpu/firmware/` (9 blobs,
  1.72 MB, linux-firmware 20251125, LICENSE.amdgpu; **no `vangogh_sos.bin` exists upstream** — APU
  sOS lives in system BIOS, consistent with the warm-PSP strategy) + `amdgpu/include/` (19 MIT
  headers, kernel v6.15: vangogh_ip_offset, gc_10_3_0, mp_11_0, nbio_7_2_0, osssys_5_0_0,
  mmhub_2_3_0, athub_2_1_0, hdp_5_0_0, uapi drm). Verified IP versions: GC 10.3.1 / PSP 11.5.0 /
  SDMA 5.2.1 (regs live in GC space — no separate headers) / NBIO 7.2.0. See `amdgpu/README.md`.
- [x] **(b) COMPLETE — real register contact on the Deck (2026-07-03, third boot)**. Health readings
  (Sephiroth, register BAR5 `0x80500000` mapped UC): `GRBM_STATUS=0x3028` (healthy idle),
  `CP_STAT=0`, `CP_ME_CNTL=0x15000000`/`CP_MEC_CNTL=0x50000000` (CPs halted — GOP never starts
  them), **`RLC_BOOTLOAD=0` (RLC not boot-loaded: stage (d) does the full autoload, confirmed)**,
  `FB_LOC 0xf400..0xf43f` + `FB_OFFSET=0x440` (1 GiB carveout at DRAM `0x4_4000_0000`, GMC posted),
  `VM_L2_FAULT=0`, **`PSP_C2P_81=0xfe345` (warm sOS alive)**, `SMU_C2P_90=1` (SMU responsive),
  `carveout=1024 MiB`. The health dump re-prints from the `exit_group` syscall path so it lands on
  the parked final screen (the boot log scrolls past one Deck panel). Probe details below.
- [~] **(b) probe implementation notes** — `PuckGpuProbe()`
  (puck-efi.c, called from EfiEntry after ECAM): scans config space for an AMD display-class
  function, decodes + sizes all six BARs (console-quiet window: the panel scans out of the FB BAR),
  maps the smallest non-prefetch memory BAR (the 512 KiB register aperture) UC, and dumps the
  health set — GRBM_STATUS/2, CP_STAT, CP_ME/MEC_CNTL, RLC_GPM_STAT, GCMC_VM_FB_LOCATION_BASE/TOP,
  MP0 C2PMSG_33/35/58/59/64/81 (PSP residue), RCC_CONFIG_MEMSIZE (carveout MiB). SOC15 dword
  addresses derived from the vendored headers. QEMU: clean "no AMD display device" + full vktest
  battery intact. `samples/EfiLinux/stage-deck.ps1` stages a boot USB (EFI\BOOT\BOOTX64.EFI +
  radv\*.so).

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
