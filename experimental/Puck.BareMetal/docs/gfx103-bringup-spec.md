# gfx1033 (Van Gogh / Steam Deck LCD) bare-metal bring-up specification

Clean-room behavioral spec for stages (b)-(e) of
[amd-vulkan-plan.md](amd-vulkan-plan.md): bringing up the AMD Van Gogh APU
(PCI `1002:163F`, `gfx1033` / GC 10.3.1, PSP v11, F32 CP) from a bare-metal
kernel that inherits a **warm** GPU (UEFI GOP already posted it). The kernel
substrate — ECAM, MMIO map w/ PAT, MSI-X, DMA allocator, DRM ioctl shim,
RADV enumerating a synthetic device — already exists (plan stage (a)). This
document supplies the missing real-hardware protocol knowledge.

## Provenance & licensing

Everything below is **our own prose** describing hardware protocol (register
sequences, bit meanings, HW-defined data structures). Register names, offsets,
bitfields, command IDs and binary struct layouts are cited from the **MIT**
AMD headers (SPDX MIT, verified per-file: `Copyright … Advanced Micro Devices
… Permission is hereby granted, free of charge …`):

| Header (asic_reg / amdgpu) | Used for |
|---|---|
| `vangogh_ip_offset.h` | SOC15 IP base addresses (segment table) |
| `gc/gc_10_3_0_offset.h`, `gc/gc_10_3_0_sh_mask.h`, `gc/gc_10_3_0_default.h` | GC/GFX/GMC(GC-hub)/CP/RLC registers |
| `mp/mp_11_0_offset.h`, `mp/mp_11_0_sh_mask.h` | PSP (MP0) + SMU (MP1) mailbox registers |
| `nbio/nbio_7_2_0_offset.h`, `_sh_mask.h` | NBIO: memsize, PCIE/MM index-data, doorbell aperture, HDP flush |
| `mmhub/mmhub_2_3_0_offset.h` | MMHUB mirror of the VM registers |
| `oss/osssys_5_0_0_offset.h` | IH (interrupt handler) block — not used in poll-only path |
| `hdp/hdp_5_0_0_offset.h` | HDP cache flush/invalidate |
| `amdgpu/amdgpu_ucode.h` | firmware container header structs (`common_firmware_header`, `gfx/rlc/sdma_firmware_header_*`) |
| `amdgpu/psp_gfx_if.h` | PSP GFX command IDs, `psp_gfx_cmd_resp`, `psp_gfx_rb_frame`, fw-type enum |
| `amdgpu/nvd.h`, `amdgpu/soc15d.h` | PM4 packet opcodes and field encodings |
| `pm/…/smu_v11_5_ppsmc.h` | SMU PPSMC message IDs (mode2 reset) |

Behavior (sequencing, poll conditions, timeouts) was **derived by reading**
the GPL `amdgpu` `.c` files online — `gfx_v10_0.c`, `gmc_v10_0.c`,
`gfxhub_v2_1.c`, `mmhub_v2_3.c`, `psp_v11_0.c`, `amdgpu_psp.c`, `nbio_v7_2.c`,
`nv.c`, `vangogh_ppt.c`, `smu_cmn.c`, `amdgpu_gart.c` — **but no GPL code is
transcribed here**. Structurally-instructive MIT prior art: **tinygrad AM**
driver (`tinygrad/runtime/support/am/{amdev,ip}.py`, MIT) — gfx11, but the
same SOC15 / PSP / GMC shapes; its bit-level choices are cited where they
corroborate a derivation.

All byte offsets below were **computed** as `(IP_base_segment + reg_dword) * 4`
and are meant to be dropped straight into `reg32(bar5_base + BYTE_OFFSET)`.
Arithmetic was machine-checked; still, treat any value tagged **UNVERIFIED**
as needing a spot-check on-silicon.

---

## 0. Van Gogh IP-version map (what "gfx1033" decomposes into)

Discovery would normally read these from the on-die IP discovery table; for a
single fixed target we hardcode them (values from amdgpu's Van Gogh case list):

| IP block | version | ASIC-specific driver family |
|---|---|---|
| GC (graphics core) | 10.3.1 | `gfx_v10_0`, gfxhub `v2_1` |
| MMHUB | 2.3.0 | `mmhub_v2_3` |
| MP0 (PSP) | 11.0.5 | `psp_v11_0` (autoload-capable, `boot_time_tmr = false`) |
| MP1 (SMU) | 11.5.0 | `vangogh_ppt` (mode2 reset) |
| NBIO | 7.2.0 | `nbio_v7_2` |
| OSSSYS (IH) | 5.0.x | `navi` IH (unused in poll path) |
| SDMA | 5.2.1 | `sdma_v5_2` |
| HDP | 5.0.x | `hdp_v5_0` |

Family = `AMDGPU_FAMILY_VGH` = **144** (`0x90`); `external_rev_id = rev_id +
0x01` (= `VANGOGH_A0`). Device is an APU (`AMD_IS_APU`, UMA — VRAM is a
carved-out region of system DRAM, not a discrete BAR-mapped framebuffer). CU
count = 8, wave32.

---

## 1. Stage (b): MMIO geography + safe read-only probe set

### 1.1 PCI BAR layout (confirmed from Steam Deck `lspci -v`, `1002:163F`)

| BAR | Region | Contents | Notes |
|---|---|---|---|
| 0 | `f8e0000000`, 64-bit prefetchable, **256 MB** | **Frame-buffer / VRAM aperture** (CPU-visible window into the UMA carveout) | On an APU the *real* VRAM base for the GPU is `GCMC_VM_FB_OFFSET<<24`, not this CPU aperture; see §3. |
| 2 | `f8f0000000`, 64-bit prefetchable, **2 MB** | **Doorbell aperture** (`adev->doorbell.base = pci_resource_start(pdev, 2)`) | 64-bit doorbells; ring wptr is poked here (see §5). |
| 4 (BAR5 in old code) | `80300000`, 32-bit non-prefetchable, **512 KB** | **Register (MMIO) aperture** = `rmmio` | This is the "reg32" window. `adev->rmmio_base/size`. |
| — | I/O ports `0x1000` size 256 | legacy VGA I/O | ignore |

> NOTE on BAR indexing: amdgpu uses `pci_resource_start(pdev, 5)` for the
> register BAR on `>= CHIP_BONAIRE`. On the Deck the register block is the
> 512 KB non-prefetchable region reported by `lspci` (this is the resource
> Linux labels as index 5 internally after 64-bit BAR pairing — physical
> address `0x80300000`). Match it by **size+flags** (512 KB, 32-bit,
> non-prefetchable), not by a hardcoded index, since 64-bit BARs consume two
> slots. The **register aperture is only 512 KB = `0x80000` bytes = `0x20000`
> dwords.** Any register whose byte offset ≥ `0x80000` is **not directly
> reachable** and must go through the indirect path (§1.3).

### 1.2 SOC15 register addressing

A SOC15 register address is `(IP_base_for_segment + register_dword_offset)`,
in **dwords**, then `*4` for the byte offset into the register BAR. Each IP has
up to 6 base "segments"; the register header's `..._BASE_IDX` tells you which
segment index to use. From `vangogh_ip_offset.h`:

| IP | seg0 | seg1 | seg2 | seg3 | seg4 |
|---|---|---|---|---|---|
| GC   | `0x00001260` | `0x0000A000` | `0x02402C00` | — | — |
| MP0  | `0x00016000` | `0x0243FC00` | `0x00DC0000` | `0x00E00000` | `0x00E40000` |
| MP1  | `0x00016000` | `0x0243FC00` | `0x00DC0000` | `0x00E00000` | `0x00E40000` |
| NBIO | `0x00000000` | `0x00000014` | `0x00000D20` | `0x00010400` | `0x0241B000` |
| MMHUB| `0x00013200` | `0x0001A000` | `0x02408800` | — | — |
| HDP  | `0x00000F20` | `0x0240A400` | — | — | — |
| OSSSYS | `0x000010A0` | `0x0240A000` | — | — | — |
| ATHUB | `0x00000C00` | `0x00013300` | `0x02408C00` | — | — |

Byte offset = `(segbase[BASE_IDX] + reg_dword) * 4`. Example: `GRBM_STATUS` has
dword `0x0da4`, `BASE_IDX 0` → GC seg0 `0x1260` → `(0x1260+0xda4)*4 =
0x2004*4 = 0x8010`.

Most GC registers used at bring-up fall in seg0 (`0x1260`) or seg1 (`0xA000`).
seg1 registers (RLC, CP ucode) land at byte offsets ≥ `0x38000` — still inside
the 512 KB aperture. **The PSP (MP0) registers at seg0 `0x16000` land at byte
`0x58000`–`0x58A6C`, which is inside 512 KB — directly reachable.** Good: the
whole PSP handshake works with plain `reg32`.

### 1.3 Indirect access (registers above the 512 KB aperture, or SMN space)

Two mechanisms, both driven through registers that ARE in the aperture:

- **PCIE index/data pair** (`nbio_v7_2` uses this as the extended path):
  `BIF_BX0_PCIE_INDEX2` @ byte `0x38`, `BIF_BX0_PCIE_DATA2` @ byte `0x3C`.
  Protocol: write the full register dword address to INDEX2, read INDEX2 back
  (posting/serialize), then read/write DATA2. Used to reach registers whose
  computed dword offset is beyond the BAR window, and for SMN-space addresses.
- **MM index/data pair** (used by amdgpu to reach VRAM and high MMIO):
  `BIF_BX_PF0_MM_INDEX` @ byte `0x00`, `BIF_BX_PF0_MM_DATA` @ byte `0x04`,
  `BIF_BX_PF0_MM_INDEX_HI` @ byte `0x18`. For a 32-bit MMIO reg: write
  `addr | 0x80000000` to MM_INDEX then access MM_DATA. (The `0x80000000` flag
  selects register vs. VRAM addressing.)

For gfx1033 bring-up, **every register in the tables below is inside the
512 KB aperture except SMN-only addresses** (e.g. `smnMP1_FIRMWARE_FLAGS =
0x3010024`, used only for optional SMU sign-of-life). Keep an indirect helper
around but you will rarely need it in stages (b)-(e).

### 1.4 SAFE read-only probe set (compute-verified byte offsets)

All are pure reads; none perturb a warm GPU. `reg32(x) = *(volatile u32*)(rmmio + x)`.

| Register | IP/seg | dword | **byte off** | Healthy warm value | Wedged / cold |
|---|---|---|---|---|---|
| `GRBM_STATUS` | GC/0 | `0x0da4` | **`0x008010`** | bit31 `GUI_ACTIVE` may be 0 when idle; `CP_BUSY`(b29)/`SPI_BUSY`(b22)/`CB_BUSY`(b30) sane; **not** `0xFFFFFFFF` | `0xFFFFFFFF` (bus dead) or stuck all-busy |
| `GRBM_STATUS2` | GC/0 | `0x0da2` | **`0x008008`** | `RLC_RQ_PENDING`(b14) transient; not all-F | all-F |
| `GRBM_STATUS3` | GC/0 | `0x0da7` | **`0x00801C`** | small/zero | all-F |
| `GRBM_CHIP_REVISION` | GC/0 | `0x0dc1` | **`0x008084`** | non-F revision byte | all-F |
| `CP_STAT` | GC/0 | `0x0f40` | **`0x008680`** | **`0` when CP idle & fw loaded** (key readiness signal) | non-zero-busy forever, or all-F |
| `RLC_RLCS_BOOTLOAD_STATUS` | GC/1 | `0x4e8d` | **`0x03BA34`** | bit31 `BOOTLOAD_COMPLETE = 1` on a warm/autoloaded GPU | bit31 = 0 |
| `RLC_STAT` | GC/1 | `0x4c04` | **`0x03B010`** | small, stable | all-F |
| `RLC_GPM_STAT` | GC/1 | `0x4e6e` | **`0x03B9B8`** | stable | all-F |
| `RLC_CNTL` | GC/1 | `0x4c00` | **`0x03B000`** | bit0 `RLC_ENABLE` likely 1 (warm) | 0 or all-F |
| `RCC_DEV0_EPF0_0_RCC_CONFIG_MEMSIZE` | NBIO/2 | `0x00c3` | **`0x00378C`** | carveout size in **MB** (e.g. `0x400`=1 GB, `0x100`=256 MB) — this is `real_vram_size/1MB` | 0 or all-F |
| `GCMC_VM_FB_LOCATION_BASE` | GC/0 | `0x16fc` | **`0x00A570`** | non-zero: `FB_BASE<<24` = VRAM MC base — proves GMC posted | 0 = GMC unconfigured |
| `GCMC_VM_FB_LOCATION_TOP` | GC/0 | `0x16fd` | **`0x00A574`** | `> BASE` | 0 |
| `GCMC_VM_FB_OFFSET` | GC/0 | `0x16e7` | **`0x00A51C`** | APU: `<<24` = physical DRAM base of carveout | — |
| `MP0_SMN_C2PMSG_81` | MP0/0 | `0x0091` | **`0x058244`** | **non-zero ⇒ sOS is alive** (sign-of-life) | 0 ⇒ no secure OS |
| `MP0_SMN_C2PMSG_35` | MP0/0 | `0x0063` | **`0x05818C`** | bit31 set ⇒ PSP bootloader ready | bit31 clear (busy) |
| `MP0_SMN_C2PMSG_64` | MP0/0 | `0x0080` | **`0x058200`** | bit31 set ⇒ sOS ready for ring cmds | bit31 clear |
| `MP1_SMN_C2PMSG_90` | MP1/0 | `0x029a` | **`0x058A68`** | non-zero ⇒ SMU alive (response reg) | 0 |
| `GCVM_L2_PROTECTION_FAULT_STATUS` | GC/0 | `0x15c8` | **`0x00A0A0`** | **`0` = no VM fault** (re-read after any GPU access) | non-zero ⇒ fault latched (CID/WALKER/PERM bits) |
| `SCRATCH_REG0` | GC/1 | `0x2040` | **`0x030100`** | scratch; use for ring test (§5) | — |

Harvest / CU-fuse registers (read-only, tell you the shader-array config —
useful to confirm 8 CU but not required to bring up):

| Register | IP/seg | dword | byte off |
|---|---|---|---|
| `CC_GC_SA_UNIT_DISABLE` | GC/0 | `0x0fe9` | `0x008924` |
| `GC_USER_SA_UNIT_DISABLE` | GC/0 | `0x0fea` | `0x008928` |
| `CC_GC_SHADER_ARRAY_CONFIG` | GC/0 | `0x100f` | `0x0089BC` |
| `GC_USER_SHADER_ARRAY_CONFIG` | GC/0 | `0x1010` | `0x0089C0` |
| `CC_RB_BACKEND_DISABLE` | GC/0 | `0x13dd` | `0x0098F4` |
| `GC_USER_RB_BACKEND_DISABLE` | GC/0 | `0x147f` | `0x009B7C` |

**Stage-(b) done** = `RCC_CONFIG_MEMSIZE` returns a plausible MB count,
`GRBM_STATUS != 0xFFFFFFFF`, `MP0_C2PMSG_81 != 0` (sOS resident), and
`GCMC_VM_FB_LOCATION_BASE != 0` (GMC posted). That combination proves the
MMIO map, the aperture math, and the "warm" inheritance all at once.

---

## 2. What "warm from GOP" means, and mode1/mode2 reset

### 2.1 State the firmware leaves

When UEFI's GOP driver posted the GPU, the vBIOS ran the ASIC init table and
the on-die PSP loaded and is running. On a warm Van Gogh you inherit:

- **PSP sOS resident** — `MP0_C2PMSG_81 != 0`. The bootloader → sysdrv → sOS
  chain already ran. `psp_v11_0_is_sos_alive()`-equivalent returns true, so
  you must **skip** the bootloader-load-sysdrv / load-sos steps (they early-out
  when sOS is alive) and go straight to ring-create + fw-load.
- **SMU (MP1) alive** — `MP1_C2PMSG_90 != 0`; clocks/power posted.
- **GMC posted** — `GCMC_VM_FB_LOCATION_BASE/TOP` programmed by vBIOS to the
  carveout, so VRAM addressing is already sane. You can *read* the fb location
  to learn the layout rather than inventing it.
- **DCN (display) owned by firmware** — GOP is scanning out of a framebuffer
  in the carveout. **Do NOT touch DCN/DCE registers, do not re-init display,
  do not reprogram the memory controller's display apertures.** We are
  render/compute only; leaving DCN alone is what keeps the GOP console alive
  for debug output.
- **GFX/CP possibly still warm** — but the CP microcode engines' run state is
  unknown to us. `RLC_RLCS_BOOTLOAD_STATUS` bit31 tells you whether RLC
  autoload completed. If GOP only used the display block, the 3D pipe may
  never have been brought up; the safe assumption is "RLC is up, CP needs our
  fw-load + resume sequence."

### 2.2 Safe to rely on vs. must redo

| Inherited | Rely on it? |
|---|---|
| PSP sOS alive | **Yes** — this is the whole point of warm-boot; it removes the cold sOS-load risk. |
| SMU clocks | Yes (don't reset the SMU). |
| GMC fb-location registers | Read them; re-assert GART/system-aperture (§3) since we own GPUVM now. |
| Display / DCN | Leave alone entirely. |
| CP/GFX engines running | **No** — redo the fw-load through PSP + CP resume (§4, §5). |
| Any GPUVM page tables | **No** — build our own GART table (§3). |

### 2.3 mode1 reset (PSP v11) — the escape hatch

If the GPU is wedged (CP stuck busy, VM-fault storm, ring won't advance),
recover with a **PSP mode1 reset** rather than a bus reset. Protocol
(derived from `psp_v11_0_mode1_reset`):

1. Read `MP0_C2PMSG_64` (byte `0x058200`); confirm PSP is responsive: wait for
   `(val & 0x8000FFFF) == 0x80000000` (bit31 set, low-16 clear).
2. Write command `GFX_CTRL_CMD_ID_MODE1_RST = 0x00070000` to `MP0_C2PMSG_64`.
3. Sleep ~500 ms.
4. Poll `MP0_C2PMSG_33` (byte `0x058184`) for `(val & 0x80000000) ==
   0x80000000` — that is the "reset done" acknowledgement.

After mode1 you are effectively cold: sOS re-loads itself, and you re-run the
full fw-load. Because mode1 also resets the display, expect the GOP console to
blank — use it sparingly, and only if serial/netconsole debug is up.

**mode2 reset (SMU path, the amdgpu default for Van Gogh)**: amdgpu's
`nv_asic_reset_method` returns `AMD_RESET_METHOD_MODE2` for MP1 11.5.0. It is a
lighter reset driven by an SMU message: send
`PPSMC_MSG_GfxDeviceDriverReset = 0x14` with argument `MODE2_RESET = 2` via the
SMU mailbox (§4.5 protocol), then `mdelay(10)`. Prefer mode2 for "just restart
the GFX pipe" and mode1 only when the PSP itself looks wedged. **UNVERIFIED**
which reset best preserves the GOP display on this specific warm-boot path;
test mode2 first (it is designed for TDR recovery and is gentler).

---

## 3. Stage (c): GMC v10 / GPUVM (GC-hub)

Goal: a GART (VMID 0, identity/page-table for system memory) so the CP can
fetch an IB and write a fence to a GPU virtual address that maps to our DMA
buffers. Van Gogh has two hubs — **GFXHUB** (`gfxhub_v2_1`, GC-block regs
prefixed `GCMC_/GCVM_`) and **MMHUB** (`mmhub_v2_3`, regs prefixed
`MMMC_/MMVM_`). For a render-only port drive the **GFXHUB**; the MMHUB mirror
has identical field layouts at its own offsets (table in §3.6) and only needs
setup if SDMA/other MM clients page-fault. Bring up GFXHUB first.

### 3.1 Read the inherited FB location, then place GART

- `GCMC_VM_FB_LOCATION_BASE` (byte `0x00A570`): `base = (reg &
  0x00FFFFFF) << 24` = MC address of VRAM start.
- `GCMC_VM_FB_LOCATION_TOP` (byte `0x00A574`): top likewise.
- `GCMC_VM_FB_OFFSET` (byte `0x00A51C`): `<<24` = **physical DRAM address** of
  the carveout (APU-specific; `vram_base_offset`). This is the value you use to
  translate an MC/GPU address back to a CPU/DMA physical address.
- On an APU the AGP aperture is disabled: set AGP default
  `agp_base=0, agp_bot=0xFFFFFFFF…, agp_top=0` (i.e. empty). amdgpu's
  `amdgpu_gmc_set_agp_default` sets `agp_start=0xffffffffffff, agp_end=0`.
- `mc_mask = 0xFFFFFFFFFFFF` (48-bit MC address space).
- GART size for 10.3.1 defaults to **1 GB** (`1024<<20`).
- Place GART on a 4 GB-aligned base that doesn't overlap FB (amdgpu
  `gart_location` best-fit; simplest correct choice: put GART below FB if
  `fb_start >= gart_size`, else above `ALIGN(fb_end+1, 4GB)`). `gart_start &=
  ~(4GB-1)`.

### 3.2 GART page-table format (gfx10 PTE/PDE bits)

64-bit little-endian PTE/PDE. Bit meanings (from `amdgpu_vm.h`, MIT):

| Bit(s) | Name | Meaning |
|---|---|---|
| 0 | VALID | entry valid |
| 1 | SYSTEM | address is system (GTT/DMA) memory, not VRAM |
| 2 | SNOOPED | coherent/snooped access (set for system RAM) |
| 4 | EXECUTABLE | shader-executable |
| 5 | READABLE | |
| 6 | WRITEABLE | |
| 7..11 | FRAG(x) | fragment/contiguity hint (`(x & 0x1f) << 7`) |
| 48..50 | MTYPE_NV10 (`mtype << 48`, mask `7<<48`) | memory type; `MTYPE_UC=0` uncached, `MTYPE_NC`, etc. |
| 54 | PDE_PTE | this PDE is really a PTE (huge page) |
| 57..58 | (see MTYPE_VG10 for the *page-table root* pde flags) | |
| 63 | IS_PTE | leaf marker in some contexts |

The physical page frame occupies bits ~[47:12] of the entry (the address is
shifted so the low 12 bits are flags). amdgpu's GART default PTE flags:
`AMDGPU_PTE_MTYPE_NV10(0, MTYPE_UC) | AMDGPU_PTE_EXECUTABLE` — i.e. **UC,
executable**, and per-page you OR in `VALID | SYSTEM | SNOOPED | READABLE |
WRITEABLE` for a system-memory (DMA) page. So a GART leaf entry for a DMA page
at physical `pa` is:

```
pte = (pa & 0x0000FFFFFFFFF000)          // 4K-aligned frame
    | VALID | SYSTEM | SNOOPED | READABLE | WRITEABLE | EXECUTABLE
    | (MTYPE_UC << 48)                    // = 0
```

GART is a **single-level** flat table for VMID0 (page-table depth 0): one
contiguous array of 8-byte PTEs, `num_gpu_pages = gart_size / 4096`, table
size = `num_gpu_pages * 8`. Entry index for GPU-VA `va` is `(va -
gart_start) / 4096`. `GPU_PAGE_SIZE = 4096`, and on x86 CPU_PAGE==GPU_PAGE so
one CPU page = one PTE. Fill the whole table with the "system default"
(dummy-page) entry first, then bind real pages.

### 3.3 GC-hub register programming (VMID0 / GART)

All GC/0 unless noted. Sequence (behavioral, from `gfxhub_v2_1_gart_enable`):

**(a) Page-table root for context 0** — write the GART table's MC address
(`pd_addr = physical_of_gart_table` with `VALID` flag or'd, since Van Gogh ≥
Vega uses `pde_for_bo`):

| Register | dword | byte off | value |
|---|---|---|---|
| `GCVM_CONTEXT0_PAGE_TABLE_BASE_ADDR_LO32` | `0x1667` | `0x00A31C` | low32 of PT base |
| `GCVM_CONTEXT0_PAGE_TABLE_BASE_ADDR_HI32` | `0x1668` | `0x00A320` | high32 |
| `GCVM_CONTEXT0_PAGE_TABLE_START_ADDR_LO32` | `0x1687` | `0x00A39C` | `gart_start >> 12` low |
| `GCVM_CONTEXT0_PAGE_TABLE_START_ADDR_HI32` | `0x1688` | `0x00A3A0` | `gart_start >> 44` |
| `GCVM_CONTEXT0_PAGE_TABLE_END_ADDR_LO32` | `0x16a7` | `0x00A41C` | `gart_end >> 12` low |
| `GCVM_CONTEXT0_PAGE_TABLE_END_ADDR_HI32` | `0x16a8` | `0x00A420` | `gart_end >> 44` |

**(b) System aperture** (byte offsets):

| Register | dword | byte off | value |
|---|---|---|---|
| `GCMC_VM_AGP_BASE` | `0x1700` | `0x00A580` | `0` |
| `GCMC_VM_AGP_BOT` | `0x16ff` | `0x00A57C` | `agp_start >> 24` (empty ⇒ large) |
| `GCMC_VM_AGP_TOP` | `0x16fe` | `0x00A578` | `agp_end >> 24` (empty ⇒ 0) |
| `GCMC_VM_SYSTEM_APERTURE_LOW_ADDR` | `0x1701` | `0x00A584` | `min(fb_start,agp_start) >> 18` |
| `GCMC_VM_SYSTEM_APERTURE_HIGH_ADDR` | `0x1702` | `0x00A588` | `max(fb_end,agp_end) >> 18` |
| `GCMC_VM_SYSTEM_APERTURE_DEFAULT_ADDR_LSB` | `0x16e8` | `0x00A520` | `(dummy_mc >> 12)` low |
| `GCMC_VM_SYSTEM_APERTURE_DEFAULT_ADDR_MSB` | `0x16e9` | `0x00A524` | `(dummy_mc >> 44)` |
| `GCVM_L2_PROTECTION_FAULT_DEFAULT_ADDR_LO32` | `0x15cb` | `0x00A0AC` | `dummy_page_pa >> 12` |
| `GCVM_L2_PROTECTION_FAULT_DEFAULT_ADDR_HI32` | `0x15cc` | `0x00A0B0` | `dummy_page_pa >> 44` |

**(c) TLB control** — `GCMC_VM_MX_L1_TLB_CNTL` dword `0x1703`, byte
`0x00A58C`. Set: `ENABLE_L1_TLB=1` (bit0), `SYSTEM_ACCESS_MODE=3` (bits[4:3]),
`ENABLE_ADVANCED_DRIVER_MODEL=1` (bit6), `SYSTEM_APERTURE_UNMAPPED_ACCESS=0`
(bit5), `MTYPE = MTYPE_UC` (bits[13:11] = 0). Resulting value ≈ `0x00000059`
before MTYPE (UC=0). Field shifts/masks:
`ENABLE_L1_TLB` (mask `0x1`), `SYSTEM_ACCESS_MODE` (mask `0x18`),
`ENABLE_ADVANCED_DRIVER_MODEL` (mask `0x40`), `MTYPE` (mask `0x3800`).

**(d) L2 cache** — enable, using defaults from `gc_10_3_0_default.h`:

| Register | dword | byte off | value |
|---|---|---|---|
| `GCVM_L2_CNTL` | `0x15bc` | `0x00A070` | `ENABLE_L2_CACHE(b0)=1`, `ENABLE_DEFAULT_PAGE_OUT_TO_SYSTEM_MEMORY=1`, `CONTEXT1_IDENTITY_ACCESS_MODE=1`; fragment-processing off |
| `GCVM_L2_CNTL2` | `0x15bd` | `0x00A074` | `INVALIDATE_ALL_L1_TLBS=1`, `INVALIDATE_L2_CACHE=1` |
| `GCVM_L2_CNTL3` | `0x15be` | `0x00A078` | start from default `0x80100007`, set `BANK_SELECT=9`, `L2_CACHE_BIGK_FRAGMENT_SIZE=6` (non-translate-further) |
| `GCVM_L2_CNTL4` | `0x15d4` | `0x00A0D0` | default `0x000000c1`, clear `VMC_TAP_PDE_REQUEST_PHYSICAL`, `VMC_TAP_PTE_REQUEST_PHYSICAL` |
| `GCVM_L2_CNTL5` | `0x15dc` | `0x00A0F0` | default `0x00003fe0`, set `L2_CACHE_SMALLK_FRAGMENT_SIZE=0` |

**(e) Enable context 0 (VMID0 = GART/system domain)** —
`GCVM_CONTEXT0_CNTL` dword `0x15fc`, byte `0x00A170`: `ENABLE_CONTEXT=1`
(bit0), `PAGE_TABLE_DEPTH=0` (bits[2:1]=0, flat single-level),
`RETRY_PERMISSION_OR_INVALID_PAGE_FAULT=0` (bit7). Value ≈ `0x00000001`.

**(f) Disable identity aperture** — write the four
`GCVM_L2_CONTEXT1_IDENTITY_APERTURE_*` registers (dwords `0x15ce`–`0x15d1`,
bytes `0x00A0B8`–`0x00A0C4`): LOW = `0xFFFFFFFF` / `0x0000000F`, HIGH = `0` /
`0`, and the two `PHYSICAL_OFFSET` regs (`0x15d2`/`0x15d3`) = 0. This makes any
stray context-1 access fault rather than silently translate.

**(g) Per-VMID config** — for a minimal port you only need VMID0 (GART). The
per-process VMIDs 1..14 (`GCVM_CONTEXT1_CNTL` dword `0x15fd` + stride) are
policy for multi-process; **skip them** until RADV needs process isolation.
VMID0 with an identity-ish GART covering all your DMA buffers is enough to
execute IBs. (This is the "what HW requires vs what Linux does for policy"
line: HW needs one valid context whose PT covers the addresses the CP touches;
Linux sets up 15 for scheduling.)

### 3.4 TLB invalidate protocol (per VM flush)

After writing PTEs, flush the TLB before the GPU reads them. Use invalidation
engine **17** for GART (amdgpu convention). Per-engine registers are strided;
engine 0 base + `eng * eng_distance`. From the headers, consecutive engines
are 1 dword apart for REQ/ACK/SEM:

| Register (eng0) | dword | byte off |
|---|---|---|
| `GCVM_INVALIDATE_ENG0_SEM` | `0x160d` | `0x00A1B4` |
| `GCVM_INVALIDATE_ENG0_REQ` | `0x161f` | `0x00A1FC` |
| `GCVM_INVALIDATE_ENG0_ACK` | `0x1631` | `0x00A244` |
| `GCVM_INVALIDATE_ENG0_ADDR_RANGE_LO32` | `0x1643` | `0x00A28C` |
| `GCVM_INVALIDATE_ENG0_ADDR_RANGE_HI32` | `0x1644` | `0x00A290` |
| `GCVM_INVALIDATE_ENG17_REQ` | `0x1630` | `0x00A240` |
| `GCVM_INVALIDATE_ENG17_ACK` | `0x1642` | `0x00A288` |
| `GCVM_INVALIDATE_ENG17_SEM` | `0x161e` | `0x00A1F8` |

(Verify the stride empirically: ENG0_REQ=`0x161f`, ENG1_REQ=`0x1620`,
ENG17_REQ=`0x1630` ⇒ **stride = 1 dword per engine** for REQ; ACK likewise
(`0x1631`→`0x1642` = 0x11 = 17), SEM decrements (`0x160d`→`0x161e`). These are
computed above; trust the ENG17 explicit values.)

Flush sequence (GFXHUB, before KIQ/MES exist — the "bare-metal early" path):

1. **Flush HDP** first (make CPU PTE writes visible to GPU): write 0 to the
   remapped HDP flush register (§3.5).
2. `GCVM_L2_PROTECTION_FAULT_STATUS` — read to clear any stale fault.
3. Build the invalidate request word for `GCVM_INVALIDATE_ENG17_REQ`:
   `PER_VMID_INVALIDATE_REQ = (1 << vmid)` (mask `0xFFFF`),
   `FLUSH_TYPE = flush_type` (bits[18:16]), and set
   `INVALIDATE_L2_PTES | L2_PDE0 | L2_PDE1 | L2_PDE2 | L1_PTES`
   (bits 19..23). For a full GART flush of VMID0: `req = (1<<0) |
   (0<<16) | (0x1F << 19) = 0x00F80001`.
4. (GFXHUB early path uses **no semaphore** — the semaphore dance is MMHUB-only
   in amdgpu. So: skip SEM acquire for GFXHUB.)
5. Write `req` to `GCVM_INVALIDATE_ENG17_REQ` (byte `0x00A240`).
6. Poll `GCVM_INVALIDATE_ENG17_ACK` (byte `0x00A288`) until `(ack & (1<<vmid))
   != 0`. Timeout ~1 s (`adev->usec_timeout`, default 1e6 µs).

For MMHUB (if you bring it up) the semaphore *is* used: acquire
`MMVM_INVALIDATE_ENG*_SEM` (poll read==1), issue REQ, wait ACK, release SEM=0.

### 3.5 HDP flush (CPU→GPU memory visibility)

The HDP flush register is *remapped* into a page-hole in the register BAR at
`rmmio_remap.reg_offset = MMIO_REG_HOLE_OFFSET = 0x80000 - PAGE_SIZE = 0x7F000`
(when PAGE_SIZE ≤ 4096). Flush = write `0` to `reg32(0x7F000 +
KFD_MMIO_REMAP_HDP_MEM_FLUSH_CNTL)`. **UNVERIFIED**: the exact
`KFD_MMIO_REMAP_HDP_MEM_FLUSH_CNTL` sub-offset (a small constant, on the order
of `0x0`; confirm against `kfd_pm4_headers` / the remap constant). Alternative
direct path: `BIF_BX_PF0_HDP_MEM_COHERENCY_FLUSH_CNTL` (NBIO/2 dword `0x00f7`,
byte `0x00385C`) — write any value to flush. Use the NBIO direct register if
the remap constant is uncertain; it is the same physical flush.

### 3.6 MMHUB mirror offsets (only if needed)

MMHUB regs live in MMHUB seg1 (`0x1A000`). Byte off = `(0x1A000 + dword)*4`.

| Register | dword | byte off |
|---|---|---|
| `MMVM_CONTEXT0_CNTL` | `0x0740` | `0x069D00` |
| `MMMC_VM_FB_LOCATION_BASE` | `0x08ec` | `0x06A3B0` |
| `MMMC_VM_FB_OFFSET` | `0x08d7` | `0x06A35C` |
| `MMVM_INVALIDATE_ENG0_SEM` | `0x0a00` | `0x06A800` |
| `MMVM_INVALIDATE_ENG0_REQ` | `0x0a01` | `0x06A804` |
| `MMVM_INVALIDATE_ENG0_ACK` | `0x0a02` | `0x06A808` |

(Note: MMHUB seg1 `0x1A000` + dwords put these ≥ `0x69000` byte — still inside
the 512 KB / `0x80000` aperture. Good.)

**Stage-(c) done** = write a known dword to a DMA page, map it through the
GART at a chosen GPU-VA, flush HDP+TLB, and later have the CP read it back
(proven in §5); `GCVM_L2_PROTECTION_FAULT_STATUS` stays `0`.

---

## 4. Stage (d): PSP v11 firmware load (the risk peak)

On a warm GPU sOS is already alive, so this stage is: create the PSP KM ring,
load TOC → set up TMR → push each GFX/SDMA ucode blob through PSP → trigger
RLC autoload. All PSP interaction is either the **C2PMSG mailbox** (used for
bootloader + ring lifecycle) or the **KM ring** (used for the actual per-fw
`LOAD_IP_FW` commands).

### 4.1 MP0 mailbox (C2PMSG) register roles

All MP0/seg0 (`0x16000`), directly reachable. C2PMSG_N dword = `0x0040 + N`
(so 33→`0x61`, 35→`0x63`, 64→`0x80`, 81→`0x91`).

| Reg | byte off | Role |
|---|---|---|
| `C2PMSG_35` | `0x05818C` | bootloader command/status; bit31 = ready/done |
| `C2PMSG_36` | `0x058190` | bootloader arg = `fw_buffer_mc_addr >> 20` (1 MB-aligned) |
| `C2PMSG_33` | `0x058184` | mode1-reset done flag (bit31) |
| `C2PMSG_64` | `0x058200` | ring cmd/status (sOS): bit31 = ready; ring-init/destroy commands written here |
| `C2PMSG_69/70/71` | `0x058214/18/1C` | ring create: mem addr lo / hi / size |
| `C2PMSG_67` | `0x05820C` | KM ring write-pointer (get/set wptr) |
| `C2PMSG_81` | `0x058244` | **sOS sign-of-life** (non-zero ⇒ alive) |

### 4.2 Create the PSP KM ring

Because sOS is alive we skip bootloader loads. Ring create (from
`psp_v11_0_ring_create`, non-SR-IOV):

1. Allocate a ring buffer in memory the PSP can read (VRAM carveout or GTT).
   Ring size = **`0x1000`** bytes (amdgpu) — tinygrad AM uses `0x10000`;
   either works, must be a multiple of the 64-byte frame. Zero it.
2. Wait `C2PMSG_64` for `(val & 0x80000000)==0x80000000` (sOS ready).
3. Write ring MC addr low → `C2PMSG_69`, high → `C2PMSG_70`, size →
   `C2PMSG_71`.
4. Write ring-init command to `C2PMSG_64`: `(PSP_RING_TYPE__KM << 16)` =
   `(2 << 16) = 0x00020000`.
5. `mdelay(20)` (documented HW handshake delay).
6. Wait `C2PMSG_64` for `(val & 0x8000FFFF) == 0x80000000` (bit31 set, low-16 =
   status 0). Non-zero low-16 ⇒ error.

Also allocate two more PSP buffers up front:
- **cmd buffer** `PSP_CMD_BUFFER_SIZE = 0x1000`, holds a `psp_gfx_cmd_resp`.
- **fence buffer** `PSP_FENCE_BUFFER_SIZE = 0x1000`, zeroed; PSP writes the
  fence value here on command completion.

### 4.3 KM-ring command submission + fence

Each PSP command (LOAD_TOC, SETUP_TMR, LOAD_IP_FW, AUTOLOAD_RLC) is submitted
by writing a **ring frame** and bumping the wptr. Frame + cmd structs are
HW-defined (`psp_gfx_if.h`):

`struct psp_gfx_rb_frame` (64 bytes): `cmd_buf_addr_lo/hi` (+0/+4),
`cmd_buf_size` (+8), `fence_addr_lo/hi` (+12/+16), `fence_value` (+20), rest 0.

`struct psp_gfx_cmd_resp` (1024 bytes): `buf_size` (+0), `buf_version` (+4,
must be `PSP_GFX_CMD_BUF_VERSION`), `cmd_id` (+8), then the command-specific
union at +28, and the **response** at +864 (`status` at +864, `tmr_size` at
+864+16, `fw_addr_lo/hi` at +864+8/+12).

Submit (from `psp_ring_cmd_submit` + `psp_cmd_submit_buf`):

1. Fill the cmd buffer: `buf_size`, `buf_version`, `cmd_id`, and the union.
2. Pick a monotonically increasing `fence_value` (index).
3. Get current wptr = `reg32(C2PMSG_67)`. Compute the frame slot:
   `frame = ring_start + (wptr / (64/4))` (wptr is in dwords, frame is 64 B =
   16 dwords).
4. Write the frame: `cmd_buf_addr_{lo,hi}` = cmd buffer MC addr,
   `fence_addr_{lo,hi}` = fence buffer MC addr, `fence_value` = index.
5. **Flush HDP** so PSP sees the frame + cmd buffer.
6. New wptr = `(wptr + 16) % (ring_size/4)`; write it to `C2PMSG_67`. Writing
   the wptr is what tells the PSP to consume the frame.
7. **Poll the fence buffer** (`*(u32*)fence_buf == index`). Invalidate HDP
   between reads so you see the PSP's write. Timeout = `psp_timeout = 20000`
   iterations of ~10 µs.
8. Read the response `status` at cmd+864. `0` = success. (Some PSP fw versions
   don't zero status even on success — for TOC/TMR treat non-zero as a warning
   unless the follow-on read (tmr_size) is implausible.)

### 4.4 Load order for Van Gogh (what goes through PSP)

Van Gogh is **PSP-autoload** (`load_type = AMDGPU_FW_LOAD_PSP`,
`autoload_supported = true`, `boot_time_tmr = false`). The driver does **not**
write CP/RLC/SDMA microcode to the engines directly (no `CP_*_UCODE_DATA`
banging on the autoload path). Instead every blob is handed to the PSP, which
authenticates it and places it in the TMR, then RLC autoload starts the
engines. Sequence:

1. **LOAD_TOC** (`cmd_id = GFX_CMD_ID_LOAD_TOC = 0x20`). Copy `vangogh_toc.bin`
   payload into the PSP fw-private buffer, set `cmd_load_toc.toc_phy_addr_lo/hi`
   + `toc_size`, submit. **Response `tmr_size`** tells you how big the TMR must
   be. (tinygrad caps `max_tmr_size = 0x1300000`; amdgpu default
   `PSP_TMR_SIZE = 0x400000` when TOC parsing is skipped, but on autoload you
   use the TOC-returned size.)
2. **Allocate TMR** (Trusted Memory Region): size from step 1, aligned to
   `PSP_TMR_ALIGNMENT = 0x100000` (1 MB), "naturally aligned" (start divisible
   by size preferred). On an APU with VRAM present, place it in the VRAM
   carveout.
3. **SETUP_TMR** (`cmd_id = GFX_CMD_ID_SETUP_TMR = 0x05`). Fill
   `cmd_setup_tmr.buf_phy_addr_lo/hi` = TMR MC addr, `buf_size`,
   `bitfield.virt_phy_addr = 1`, and `system_phy_addr_lo/hi` = the TMR's
   *physical* (DRAM) address. Submit.
4. **LOAD_IP_FW for each blob** (`cmd_id = GFX_CMD_ID_LOAD_IP_FW = 0x06`). For
   each ucode: copy the ucode payload to a PSP-readable buffer, set
   `cmd_load_ip_fw.fw_phy_addr_lo/hi`, `fw_size`, and `fw_type` (table below),
   submit. Van Gogh blob set and their `GFX_FW_TYPE`:

   | Blob file | UCODE_ID | `GFX_FW_TYPE` (psp_gfx_if.h) |
   |---|---|---|
   | `vangogh_sdma.bin` | SDMA0 | `SDMA0 = 9` |
   | `vangogh_ce.bin` | CP_CE | `CP_CE = 3` |
   | `vangogh_pfp.bin` | CP_PFP | `CP_PFP = 2` |
   | `vangogh_me.bin` | CP_ME | `CP_ME = 1` |
   | `vangogh_mec.bin` | CP_MEC1 | `CP_MEC = 4` |
   | `vangogh_mec2.bin` | CP_MEC2 | `CP_MEC = 4` |
   | `vangogh_rlc.bin` | RLC_G + restore-list/iram/dram sublists | `RLC_G = 8`, plus `RLC_RESTORE_LIST_*`, `RLC_IRAM`, `RLC_DRAM` |

   The `rlc.bin` container (rlc_firmware_header_v2_2/2_3) actually holds
   several logical blobs — RLC_G, the save/restore-list cntl+gpm+srm, and
   IRAM/DRAM — each submitted as its own `LOAD_IP_FW` with the corresponding
   fw_type (`RLC_RESTORE_LIST_SRM_CNTL`, `..._GPM_MEM`, `..._SRM_MEM`,
   `RLC_IRAM`, `RLC_DRAM_BOOT`). Parse the sub-offsets from the RLC v2 header
   (see §4.7).
   MEC JT (jump-table) sub-blobs are **skipped** on the autoload path.
5. **AUTOLOAD_RLC** (`cmd_id = GFX_CMD_ID_AUTOLOAD_RLC = 0x21`). Sent right
   after the RLC_G blob is loaded — it tells the PSP "all graphics fw is in,
   start RLC autoload." This kicks the RLC to boot the CP engines from the TMR.

> **Van Gogh sOS quirk**: amdgpu's `psp_v11_0_init_microcode` for MP0 11.5.0
> loads only **asd + toc** — there is **no `vangogh_sos.bin`** in
> linux-firmware (confirmed: the amdgpu dir has vangogh_{asd,ce,dmcub,me,mec,
> mec2,pfp,rlc,sdma,toc,vcn} — no sos). The sOS is baked into the platform
> firmware / loaded by the vBIOS at post; on warm boot it's already resident
> (`C2PMSG_81 != 0`), so we never load an sos blob. This is exactly why the
> warm-boot approach de-risks stage (d): the signed sOS handshake is done for
> us; we only feed it the GFX/SDMA/RLC ucode it will authenticate.

### 4.5 SMU (MP1) message protocol (needed for mode2 reset; SMU already up)

MP1 mailbox (MP1/seg0 `0x16000`, same as MP0 base — MP1 regs are distinct
dwords): `C2PMSG_66` = message id (byte `0x058A08`), `C2PMSG_82` = argument
(byte `0x058A48`), `C2PMSG_90` = response (byte `0x058A68`). Protocol
(`__smu_cmn_send_msg`): write `0` to resp(90), write arg to param(82), write
msg id to msg(66); poll resp(90) until `== SMU_RESP_OK (1)`. For mode2 reset:
send `SMU_MSG_GfxDeviceDriverReset` (asic message → PPSMC `0x14`) with arg
`MODE2_RESET = 2`, without waiting, then `mdelay(10)`.

### 4.6 RLC autoload handshake / completion polling

After `AUTOLOAD_RLC`, the RLC boots the microengines. Poll for completion (from
`gfx_v10_0_wait_for_rlc_autoload_complete`):

- `CP_STAT` (byte `0x008680`) must read **`0`** (CP idle, fw resident), AND
- `RLC_RLCS_BOOTLOAD_STATUS` (byte `0x03BA34`) bit31 `BOOTLOAD_COMPLETE == 1`.
- Timeout ~1 s. tinygrad AM polls the identical pair.

On the PSP-autoload path RLC "resume" then does only light work: init the CSB
(clear-state buffer) pointer, set SPM VMID, and enable the RLC SRM (save/restore
machine): read-modify-write `RLC_SRM_CNTL` (byte `0x03B200`) setting
`SRM_ENABLE` + `AUTO_INCR_ADDR`. (`RLC_CSIB_ADDR_LO/HI/LENGTH` at bytes
`0x03B288/8C/90` hold the clear-state buffer GPU addr+len.) You do **not**
disable CG/PG or bang RLC ucode on the autoload path.

### 4.7 Firmware container header (how to find the payload in a blob)

`common_firmware_header` (front of every amdgpu blob, little-endian):
`size_bytes` (+0, whole file), `header_size_bytes` (+4),
`header_version_major/minor` (+8/+10, u16), `ip_version_major/minor`
(+12/+14), `ucode_version` (+16), `ucode_size_bytes` (+20),
`ucode_array_offset_bytes` (+24, **payload offset from start of file**),
`crc32` (+28). So the ucode payload is `file + ucode_array_offset_bytes`, of
length `ucode_size_bytes`. GFX (pfp/me/ce/mec) add `gfx_firmware_header_v1_0`
(feature_version, jt_offset, jt_size). RLC uses `rlc_firmware_header_v2_2/2_3`
which carries the sub-blob offsets: `save_restore_list_{cntl,gpm,srm}_{size,
offset}_bytes`, `rlc_iram_ucode_{size,offset}_bytes`,
`rlc_dram_ucode_{size,offset}_bytes` — each of those (offset,size) pairs is a
separate `LOAD_IP_FW` payload. SDMA uses `sdma_firmware_header_v1_0`.

### 4.8 Failure symptoms (what a wedged PSP looks like)

- Ring create times out at step 6 ⇒ `C2PMSG_64` never sets bit31: sOS not
  actually alive or ring addr not PSP-readable (wrong TMR/GTT mapping, HDP not
  flushed). Re-check `C2PMSG_81 != 0`.
- `LOAD_IP_FW` fence never reaches `index` ⇒ PSP silently rejected the blob
  (wrong fw_type, unaligned/mis-addressed payload, or an unsigned/edited blob).
  The response `status` (cmd+864) may hold a `TEE_ERROR_*` code
  (`0xFFFF000A` = NOT_SUPPORTED, `0xFFFF0002` = CANCEL).
- Autoload never completes ⇒ `CP_STAT` stuck non-zero or
  `BOOTLOAD_COMPLETE` stays 0: a required blob was missing/out-of-order (RLC
  must precede AUTOLOAD_RLC; all GFX ucode before that).
- Recovery: mode2 reset (§2.3), re-run from ring-create.

**Stage-(d) done** = `CP_STAT == 0` and `RLC_RLCS_BOOTLOAD_STATUS` bit31 == 1.

---

## 5. Stage (e): CP / GFX ring bring-up + IB submit + fence

With autoload complete, the ME/PFP/CE are resident but **halted**. Minimal path
to run an IB on the primary GFX ring (`CP_RB0`), poll-only.

### 5.1 Ring buffer + writeback allocations

- **GFX ring buffer** in GTT/VRAM, power-of-two size (e.g. 4 KB..1 MB), MC addr
  `rb_addr`. amdgpu writes `rb_addr >> 8` to the BASE register.
- **rptr writeback** (`rptr_gpu_addr`): a dword in memory where the CP writes
  its read pointer.
- **wptr poll** (`wptr_gpu_addr`): a dword where the driver mirrors the wptr
  (CP polls it when doorbell isn't used).
- **fence/scratch BO**: a dword the IB will write via RELEASE_MEM.
- All must be GART-mapped (§3) and HDP-flushed before the CP reads them.

### 5.2 Un-halt the CP and program the ring (GC/0 registers)

From `gfx_v10_0_cp_gfx_enable` + `cp_gfx_resume`:

| Register | dword | byte off | action |
|---|---|---|---|
| `CP_ME_CNTL` | `0x0f56` | `0x0086D8` | clear `ME_HALT`(b28)/`PFP_HALT`(b26)/`CE_HALT`(b24) → un-halt. Then poll `CP_STAT`==0. |
| `CP_MAX_CONTEXT` | `0x1e4e` | `0x00C2B8` | `max_hw_contexts - 1` (typically 7) |
| `CP_DEVICE_ID` | `0x1deb` | `0x00C12C` | `1` |
| `CP_RB_WPTR_DELAY` | `0x0f61` | `0x008704` | `0` |
| `CP_RB_VMID` | `0x1df1` | `0x00C144` | `0` (ring uses VMID0/GART) |
| `CP_RB0_CNTL` | `0x1de1` | `0x00C104` | `RB_BUFSZ = log2(ring_size/8)`, `RB_BLKSZ = RB_BUFSZ-2` |
| `CP_RB0_WPTR` / `_HI` | `0x1df4`/`0x1df5` | `0x00C150`/`0x00C154` | `0` |
| `CP_RB0_RPTR_ADDR` / `_HI` | `0x1de3`/`0x1de4` | `0x00C10C`/`0x00C110` | rptr writeback MC addr lo/hi (hi masked) |
| `CP_RB_WPTR_POLL_ADDR_LO`/`_HI` | `0x1e8b`/`0x1e8c` | `0x00C3AC`/`0x00C3B0` | wptr-poll MC addr |
| `CP_RB0_BASE` / `_HI` | `0x1de0`/`0x1e51` | `0x00C100`/`0x00C2C4` | `rb_addr >> 8` lo/hi |
| `CP_RB_ACTIVE` | `0x1f40` | `0x00C680` | `1` |

Then run `cp_gfx_start`: allocate on the ring and emit the init packet stream —
`PREAMBLE_CNTL(BEGIN_CLEAR_STATE)`, `CONTEXT_CONTROL 1 / 0x80000000 /
0x80000000`, the gfx10 clear-state `SET_CONTEXT_REG` blocks (the CSB payload),
`PA_SC_TILE_STEERING_OVERRIDE`, `PREAMBLE_CNTL(END_CLEAR_STATE)`,
`CLEAR_STATE 0`, `SET_BASE`. Commit by bumping wptr. (For a *bare* ring test
you can skip most CSB init and just do the scratch-write test in §5.4 — the CP
executes SET_UCONFIG_REG without full clear-state.)

### 5.3 Doorbell vs. wptr-poll

Two ways to tell the CP the wptr moved:

- **Doorbell** (preferred): `CP_RB_DOORBELL_CONTROL` (dword `0x1e8d`, byte
  `0x00C3B4`): set `DOORBELL_OFFSET = doorbell_index` (bits[27:2]) and
  `DOORBELL_EN` (bit30). `CP_RB_DOORBELL_RANGE_LOWER`/`UPPER` (dwords
  `0x1dfa`/`0x1dfb`, bytes `0x00C168`/`0x00C16C`) bracket the valid range;
  Sienna/Vangogh field is `DOORBELL_RANGE_LOWER` (mask `0xFFC`, shift 2).
  GFX ring0 doorbell index = `AMDGPU_NAVI10_DOORBELL_GFX_RING0 = 0x08B`, and
  amdgpu uses `doorbell_index = 0x08B << 1` (byte-addressed 64-bit doorbell).
  To ring: write the 64-bit wptr to `doorbell_bar_base + doorbell_index*? `
  — specifically `WDOORBELL64(index, wptr)` writes to
  `doorbell.base + index*4` (index is already the `<<1` value → 8-byte stride
  for 64-bit). Update the wptr mirror in memory first.
- **wptr-poll** (no doorbell): write `CP_RB0_WPTR`/`_HI` directly, or let the
  CP poll `WPTR_POLL_ADDR`. Simpler for first light: just write `CP_RB0_WPTR`.

For stage (e) first-light, **skip doorbells** and write `CP_RB0_WPTR` directly
(the doorbell aperture, BAR2, is a stage-(f)/RADV concern).

### 5.4 Ring test (proves the CP executes) — the "it lives" check

From `gfx_v10_0_ring_test_ring`: write `0xCAFEDEAD` to `SCRATCH_REG0` (byte
`0x030100`), then emit on the ring:

```
PACKET3(PACKET3_SET_UCONFIG_REG, 1)          // 0xC0007900 | (1<<16)
(SCRATCH_REG0_dword - PACKET3_SET_UCONFIG_REG_START)   // 0x2040 - 0xC000
0xDEADBEEF
```

Commit (bump wptr). Poll `SCRATCH_REG0` until it reads `0xDEADBEEF`. That
proves the CP fetched from the ring and executed a register write. `PACKET3(op,
n)` = `(3<<30) | ((n & 0x3FFF)<<16) | ((op & 0xFF)<<8)`.

### 5.5 IB submit + fence (the deliverable path)

From `gfx_v10_0_ring_test_ib`:

1. Put PM4 into an **IB** buffer (GART-mapped). Minimal IB that writes a value:
   ```
   PACKET3(PACKET3_WRITE_DATA, 3)              // op 0x37
   WRITE_DATA_DST_SEL(5) | WR_CONFIRM          // dst_sel=5 (memory), wr_confirm
   lower_32_bits(gpu_addr)
   upper_32_bits(gpu_addr)
   0xDEADBEEF
   ```
2. On the **ring**, emit an INDIRECT_BUFFER pointing at the IB:
   ```
   PACKET3(PACKET3_INDIRECT_BUFFER, 2)         // op 0x3F, count 2
   lower_32_bits(ib_gpu_addr)                  // (dword-aligned; BUG if &0x3)
   upper_32_bits(ib_gpu_addr)
   control = ib_length_dw | (vmid << 24)       // vmid=0
   ```
   (`INDIRECT_BUFFER_VALID = 1<<23` may be OR'd into the header's reserved
   field per nvd.h; amdgpu's gfx10 path puts length+vmid in the control dword.)
3. Emit a **fence** via RELEASE_MEM so you can poll completion
   (`gfx_v10_0_ring_emit_fence`), op `PACKET3_RELEASE_MEM = 0x49`, count 6:
   ```
   PACKET3(RELEASE_MEM, 6)
   dw1 = GCR_SEQ | GCR_GL2_WB | GCR_GLM_INV | GCR_GLM_WB
       | CACHE_POLICY(3) | EVENT_TYPE(CACHE_FLUSH_AND_INV_TS_EVENT) | EVENT_INDEX(5)
   dw2 = DATA_SEL(1 or 2) | INT_SEL(0)         // 1=32-bit, 2=64-bit; INT_SEL 0 = no irq (poll)
   dw3 = lower_32_bits(fence_addr)             // qword-aligned if 64-bit
   dw4 = upper_32_bits(fence_addr)
   dw5 = lower_32_bits(seq)
   dw6 = upper_32_bits(seq)
   dw7 = 0
   ```
   Bit encodings from nvd.h: `DATA_SEL(x)=x<<29`, `INT_SEL(x)=x<<24`,
   `EVENT_TYPE(x)=x<<0`, `EVENT_INDEX(x)=x<<8`, `GCR_GL2_WB=1<<21`,
   `GCR_SEQ=1<<22`, `GCR_GLM_INV=1<<13`, `GCR_GLM_WB=1<<12`,
   `CACHE_POLICY(x)=x<<25`.
4. Commit (write wptr). **Poll the fence dword** in memory until it equals
   `seq` (invalidate HDP between reads). Then read back the IB's target dword
   and confirm `0xDEADBEEF`.

`ACQUIRE_MEM` (op `0x58`) is used for cache/coherency barriers around IBs but
is **not required** for this minimal write-and-fence test; RELEASE_MEM's GCR
flush bits cover the writeback. Add ACQUIRE_MEM later if a real RADV workload
needs pre-IB cache invalidation.

**MQD?** For the primary GFX ring on gfx10, the direct `CP_RB0_*` register path
(above) does **not** require an MQD. MQDs (memory queue descriptors) are needed
for **compute** rings and for KIQ/MES-managed queues. For the deliverable
offscreen triangle RADV will use the GFX ring, so an MQD is not on the critical
path for first-light; defer compute-ring/MQD/KIQ setup to stage (f)+ if RADV
routes work through a compute queue.

**Stage-(e) done** = fence dword == seq and IB target dword == `0xDEADBEEF`.
**First silicon executes.**

---

## 6. Interrupt-free operation (confirming poll suffices)

Every readiness/completion signal used in stages (b)-(e) is a **memory or
register poll**, never an interrupt:

- PSP command completion → poll the **fence buffer** dword (§4.3).
- RLC autoload → poll `CP_STAT` + `RLC_RLCS_BOOTLOAD_STATUS` (§4.6).
- TLB flush → poll `GCVM_INVALIDATE_ENG17_ACK` (§3.4).
- CP ring executed → poll `SCRATCH_REG0` (§5.4).
- IB/fence → poll the fence dword written by **RELEASE_MEM with `INT_SEL(0)`**
  (no interrupt requested) (§5.5).

So the IH (interrupt handler) ring / OSSSYS block is **not needed** for
(b)-(e), matching the plan's "poll fences before wiring IH." The only care
points: (1) **HDP flush before** the GPU reads CPU-written memory (rings,
frames, PTEs, IBs) and **HDP invalidate before** the CPU reads GPU-written
memory (fences, rptr) — otherwise the poll sees stale cache; (2) map all
polled/DMA memory as **WC or UC** via PAT (already handled by the substrate) so
CPU writes post promptly. IH wiring becomes relevant only when RADV wants
real async fences / VM-fault reporting (later).

---

## 7. References (per source, license noted)

**MIT — free to cite offsets/structs verbatim** (git.kernel.org / elixir,
linux `v6.12`, `drivers/gpu/drm/amd/`):
- `include/vangogh_ip_offset.h` — IP segment bases. MIT.
- `include/asic_reg/gc/gc_10_3_0_{offset,sh_mask,default}.h` — GC/GFX/GMC/CP/RLC
  registers, fields, reset defaults. MIT.
- `include/asic_reg/mp/mp_11_0_{offset,sh_mask}.h` — MP0/MP1 C2PMSG. MIT.
- `include/asic_reg/nbio/nbio_7_2_0_{offset,sh_mask}.h` — memsize, index/data,
  doorbell aperture, HDP flush. MIT.
- `include/asic_reg/mmhub/mmhub_2_3_0_offset.h`,
  `oss/osssys_5_0_0_offset.h`, `hdp/hdp_5_0_0_offset.h`. MIT.
- `amdgpu/amdgpu_ucode.h` — firmware container header structs. MIT.
- `amdgpu/psp_gfx_if.h` — PSP GFX cmd IDs, `psp_gfx_cmd_resp`,
  `psp_gfx_rb_frame`, fw-type enum. MIT.
- `amdgpu/nvd.h`, `amdgpu/soc15d.h` — PM4 packet opcodes/fields. MIT.
- `pm/swsmu/inc/pmfw_if/smu_v11_5_ppsmc.h` — PPSMC message IDs. MIT.

**GPL — READ for behavior, NOT transcribed** (same tree):
`amdgpu/gfx_v10_0.c`, `gmc_v10_0.c`, `gfxhub_v2_1.c`, `mmhub_v2_3.c`,
`psp_v11_0.c`, `amdgpu_psp.c`, `nbio_v7_2.c`, `nv.c`, `hdp_v5_0.c`,
`amdgpu_gart.c`, `amdgpu_gmc.c`, `amdgpu_device.c`, `sdma_v5_2.c`,
`pm/swsmu/smu11/vangogh_ppt.c`, `pm/swsmu/smu_cmn.c`. GPL-2.0.

**MIT prior art (structurally instructive, gfx11):**
- tinygrad AM driver — `tinygrad/runtime/support/am/amdev.py`, `ip.py`
  (MIT). Corroborates: SOC15 addressing, `is_sos_alive`/`C2PMSG_81`,
  ring-create, TOC→TMR→LOAD_IP_FW→AUTOLOAD_RLC ordering, `CP_STAT==0` +
  `BOOTLOAD_COMPLETE` autoload poll, SMU `C2PMSG_66/82/90` mailbox, GART PTE
  flag assembly, HDP flush via `REMAP_HDP_MEM_FLUSH_CNTL`.

**Public hardware facts:**
- Steam Deck `lspci -nnkv` (`1002:163F`) — BAR layout in §1.1
  (`https://pastebin.com/D89XnewH`).
- linux-firmware `amdgpu/` directory — the Van Gogh blob set (no `sos`), §4.4
  (`https://gitlab.com/kernel-firmware/linux-firmware`).
- AMD RDNA2 ISA / "RDNA2 Instruction Set Architecture" reference guide and the
  Sienna Cichlid / Navi2x register reference — background for PM4 and shader
  ISA (public AMD GPUOpen docs).

---

## Appendix A: consolidated stage-(b) probe offsets (drop-in)

```c
// reg32(x) reads BAR-with-512KB register aperture at byte offset x.
#define VGH_GRBM_STATUS                 0x008010u
#define VGH_GRBM_STATUS2                0x008008u
#define VGH_GRBM_STATUS3               0x00801Cu
#define VGH_GRBM_CHIP_REVISION          0x008084u
#define VGH_CP_STAT                     0x008680u   // ==0 when CP idle+fw ready
#define VGH_CP_ME_CNTL                  0x0086D8u
#define VGH_RLC_CNTL                    0x03B000u
#define VGH_RLC_STAT                    0x03B010u
#define VGH_RLC_RLCS_BOOTLOAD_STATUS    0x03BA34u   // bit31 = BOOTLOAD_COMPLETE
#define VGH_RCC_CONFIG_MEMSIZE          0x00378Cu   // carveout size in MB
#define VGH_GCMC_VM_FB_LOCATION_BASE    0x00A570u
#define VGH_GCMC_VM_FB_LOCATION_TOP     0x00A574u
#define VGH_GCMC_VM_FB_OFFSET           0x00A51Cu
#define VGH_GCVM_L2_PROT_FAULT_STATUS   0x00A0A0u   // ==0 = no VM fault
#define VGH_MP0_C2PMSG_81               0x058244u   // !=0 = sOS alive
#define VGH_MP0_C2PMSG_35               0x05818Cu   // bit31 = BL ready
#define VGH_MP0_C2PMSG_64               0x058200u   // bit31 = sOS ring-ready
#define VGH_MP0_C2PMSG_33               0x058184u   // bit31 = mode1 reset done
#define VGH_MP1_C2PMSG_90               0x058A68u   // !=0 = SMU alive
#define VGH_SCRATCH_REG0                0x030100u
// indirect pairs (inside aperture):
#define VGH_PCIE_INDEX2                 0x000038u
#define VGH_PCIE_DATA2                  0x00003Cu
#define VGH_MM_INDEX                    0x000000u
#define VGH_MM_DATA                     0x000004u
#define VGH_MM_INDEX_HI                 0x000018u
```

Warm-healthy signature: `GRBM_STATUS != 0xFFFFFFFF` &&
`RCC_CONFIG_MEMSIZE ∈ [64..4096]` && `MP0_C2PMSG_81 != 0` &&
`GCMC_VM_FB_LOCATION_BASE != 0`.
