# gfx1033 Hardware Reference

This document describes the implemented bare-metal initialization path for the
Steam Deck's gfx10.3.1 GPU. It covers register discovery, VMID0 translation, PSP
firmware loading, KIQ activation, and GFX queue execution. Operator procedures
and unresolved observations live in
[Steam Deck GPU Bring-up](deck-bringup-handoff.md).

## Source and license boundary

The register names, offsets, masks, packet fields, firmware structures, and
command identifiers come from permissively licensed AMD headers vendored under
`../amdgpu/include/`:

| Source | Use |
|---|---|
| `vangogh_ip_offset.h` | SOC15 IP segment bases |
| `gc_10_3_0_*` | GC, GFXHUB, CP, RLC, KIQ, and HQD registers |
| `mp_11_0_*` | PSP and SMU mailboxes |
| `nbio_7_2_0_*` | carveout size, doorbell aperture, and HDP flush |
| `hdp_5_0_0_*`, `mmhub_2_3_0_*`, `osssys_5_0_0_*` | cache, translation, and interrupt registers |
| `amdgpu_ucode.h`, `psp_gfx_if.h`, `v10_structs.h` | firmware and queue structures |
| `nvd.h`, `soc15d.h`, `clearstate_*` | PM4 packets and clear-state data |

The implementation may consult GPL Linux AMDGPU sources to understand externally
observable sequencing, but it does not copy those implementations. Signed
firmware remains unmodified under its upstream notice. See
[`../amdgpu/README.md`](../amdgpu/README.md) and [`../NOTICE.md`](../NOTICE.md).

## Target IP

| Block | Version | Implementation family |
|---|---|---|
| GC / GFXHUB | 10.3.1 | `gfx_v10_0`, GFXHUB 2.1 |
| MMHUB | 2.3.0 | `mmhub_v2_3` |
| PSP | 11.5-class target interface | MP0 / PSP v11 |
| SMU | 11.5.0 | Van Gogh power-management interface |
| NBIO | 7.2.0 | doorbells and memory-controller access |
| SDMA | 5.2.1 | firmware loaded; queue not yet used |
| HDP | 5.0 | CPU/GPU visibility control |

The active OLED unit reports PCI `1002:1435`; LCD Van Gogh reports a different
device ID. Both use the gfx1033 protocol family. The RADV-visible family value is
`AMDGPU_FAMILY_VGH` (`144`), with 8 CUs and wave32.

## PCI resources

The OLED target exposes:

| BAR | Address and size | Purpose |
|---|---|---|
| BAR0 | `0xf8e0000000`, 256 MiB, prefetchable | CPU framebuffer aperture |
| BAR2 | `0xf8f0000000`, 2 MiB, prefetchable | doorbell aperture |
| BAR4 | I/O port range at `0x1000` | VGA-compatible I/O; unused |
| BAR5 | `0x80500000`, 512 KiB, non-prefetchable | register aperture |

Discovery selects the AMD display-class function, decodes all BARs, and maps the
smallest non-prefetchable memory BAR as UC. It does not assume a fixed function or
address. BAR0 and BAR2 require the high-MMIO mapping path.

## SOC15 addressing

A register's byte address within the register aperture is:

```text
(ip_segment_base[BASE_IDX] + register_dword_offset) * 4
```

Relevant segment bases are:

| IP | Segment 0 | Segment 1 | Segment 2 |
|---|---:|---:|---:|
| GC | `0x1260` | `0xA000` | `0x2402C00` |
| MP0 / MP1 | `0x16000` | `0x243FC00` | `0xDC0000` |
| NBIO | `0x0000` | `0x0014` | `0x0D20` |
| MMHUB | `0x13200` | `0x1A000` | `0x2408800` |
| HDP | `0x0F20` | `0x240A400` | — |

The 512 KiB aperture reaches the GC and MP0/MP1 registers used by the current
path. Registers outside it require the NBIO index/data mechanism; do not form an
out-of-range pointer into BAR5.

## Warm-firmware assumptions

UEFI GOP has already posted the GPU. The path requires:

- a readable register aperture;
- nonzero `RCC_CONFIG_MEMSIZE`;
- nonzero `MP0_C2PMSG_81`, indicating the PSP system OS is alive;
- valid framebuffer location registers;
- a responsive SMU mailbox.

The code does not cold-load the PSP bootloader or system OS. It creates the PSP
kernel-management ring and loads the GPU IP firmware through the resident sOS.
Every mailbox and engine poll has a TSC deadline and reports failure without
resetting the panel.

## VMID0 and GART

`PuckGpuGartBringUp` reserves a 32 MiB UC window within the GPU carveout. The
window contains PSP/TMR storage and the VMID0 page table. A separate 2 MiB
DMA allocation provides the GART test and queue working area.

The current VMID0 domain covers GPU virtual addresses `[0, 256 MiB)`. Leaf PTEs
contain the physical page address plus:

```text
VALID | SYSTEM | SNOOPED | EXECUTABLE | READABLE | WRITEABLE
```

The first 2 MiB maps the DMA test allocation; the remainder maps a safe dummy
page. Initialization programs:

- context-0 page-table base and start/end addresses;
- system and AGP apertures;
- L1 TLB and L2 controls;
- context-0 enable and the protection-fault default page.

After CPU writes, issue an HDP flush, request invalidate engine 17, and wait for
the matching acknowledgement. `GCVM_L2_PROTECTION_FAULT_STATUS` must remain zero.

The target firmware exposes no IVRS table, so DMA addresses are physical
addresses. Hardware with an active IOMMU requires an explicit identity or device
domain before this mapping is valid.

## PSP firmware loading

The EFI preload reads these firmware files from `\amdgpu\` before
`ExitBootServices`:

```text
vangogh_toc.bin
vangogh_sdma.bin
vangogh_ce.bin
vangogh_pfp.bin
vangogh_me.bin
vangogh_mec.bin
vangogh_mec2.bin
vangogh_rlc.bin
vangogh_asd.bin
```

The PSP sequence is:

1. Create the KM ring through MP0 `C2PMSG_69..71` and use `C2PMSG_67` as its
   write pointer.
2. Submit `LOAD_TOC`.
3. Submit `SETUP_TMR` for the aligned trusted-memory region.
4. Submit `LOAD_IP_FW` for SDMA, CE, PFP, ME, MEC, MEC2, the RLC restore-list
   control/GPM/SRM payloads, and RLC graphics firmware.
5. Use RLC restore-list firmware type IDs 20, 21, and 22.
6. Request `AUTOLOAD_RLC`.
7. Require `RLC_RLCS_BOOTLOAD_STATUS.BOOTLOAD_COMPLETE` and an idle `CP_STAT`.

Firmware payloads are found by parsing their upstream container headers; do not
assume the file begins with executable microcode. The PSP response status and
fence must be checked for every command.

## Constants and queue selection

Before queue activation, `PuckGpuConstantsInit` configures:

- `GRBM_CNTL.READ_TIMEOUT`;
- `SH_MEM_CONFIG` and `SH_MEM_BASES` for each VMID;
- GDS base and size for VMIDs 1 through 15;
- the Van Gogh golden-register entries represented by the vendored headers.

Four golden-register names absent from the vendored header set are skipped and
reported. Do not substitute guessed offsets.

The target's gfx queue uses the asynchronous HQD/MQD interface. A scheduler byte
alone is insufficient; a live KIQ must exist first.

### KIQ

The selected KIQ identity is `me=2`, `pipe=1`, `queue=0`. Initialization:

1. writes the scheduler designation and valid bit in the required two-step form;
2. constructs a `v10_compute_mqd`;
3. selects the MEC pipe through `GRBM_GFX_CNTL`;
4. commits the MQD directly to the `CP_HQD_*` register file;
5. unhhalts the MEC engines;
6. uses doorbell index `0` for the KIQ.

The current path does not submit `MAP_QUEUES`; direct register programming is the
bring-up mechanism.

### Graphics HQD

The graphics queue uses a `v10_gfx_mqd` and the `CP_GFX_HQD_*` register file.
Program all queue state while PFP, ME, and CE are halted, then unhalt all required
front ends and wait for idle before submitting work. CE remains enabled.

The queue uses VMID0 and 64-bit doorbell index `0x116`. Enable
`RCC_DOORBELL_APER_EN`; writes to `CP_RB0_WPTR` are not the supported kick path.

The queue preamble is the full `gfx10_cs_data` clear-state stream followed by
`CLEAR_STATE` and `SET_BASE` for CE partition 3. The ring is 8 KiB so the complete
stream and diagnostic packets fit without wrapping over live data.

## Execution probes

The first probe emits:

1. `SET_UCONFIG_REG` targeting `SCRATCH_REG0` with `0xDEADBEEF`;
2. `WRITE_DATA` targeting a CPU-visible word;
3. the required synchronization and queue tail.

The memory write uses bypass cache policy. An HDP flush alone does not evict GL2,
so the default cached policy can make a completed GPU write invisible to the CPU.

The next probe places commands in an indirect buffer and appends a `RELEASE_MEM`
fence. Completion requires both the target write and the fence sequence. That
probe is the current unresolved boundary; see the Deck runbook.

## Polling and visibility

The bring-up path intentionally avoids interrupts:

- PSP command completion uses its memory fence;
- RLC autoload uses the bootload and CP status registers;
- VM invalidation uses the engine-17 acknowledgement;
- ring and IB completion use scratch and fence memory.

Flush CPU writes before the GPU reads page tables, MQDs, rings, or IBs. Invalidate
or bypass the relevant GPU cache before treating a CPU read as completion
evidence. Fence memory and queue storage must use the memory type assumed by the
corresponding visibility operation.

## Diagnostic registers

Useful byte offsets in the register aperture:

| Register | Offset | Interpretation |
|---|---:|---|
| `GRBM_STATUS` | `0x008010` | global graphics activity |
| `CP_STAT` | `0x008680` | CP activity; zero is idle |
| `CP_ME_CNTL` | `0x0086D8` | PFP/ME/CE halt state |
| `RLC_RLCS_BOOTLOAD_STATUS` | `0x03BA34` | bit 31 is autoload complete |
| `RCC_CONFIG_MEMSIZE` | `0x00378C` | carveout size in MiB |
| `GCMC_VM_FB_LOCATION_BASE/TOP` | `0x00A570` / `0x00A574` | framebuffer range |
| `GCMC_VM_FB_OFFSET` | `0x00A51C` | carveout DRAM base scale |
| `GCVM_L2_PROTECTION_FAULT_STATUS` | `0x00A0A0` | VM fault status |
| `MP0_C2PMSG_81` | `0x058244` | nonzero when PSP sOS is alive |
| `MP1_C2PMSG_90` | `0x058A68` | nonzero when SMU is responsive |
| `SCRATCH_REG0` | `0x030100` | basic packet-execution probe |

For queue stalls also capture `CP_PFP_INSTR_PNTR`, `CP_ME_INSTR_PNTR`,
`CP_CE_INSTR_PNTR`, the CPF/CPC stalled-status registers, HQD read/write pointers,
header dumps, and the ring words around the stopping pointer. A status bit without
pointer progress and packet context is not sufficient diagnosis.

## Verification criteria

The hardware path is healthy through basic execution when:

- the register health set is readable and plausible;
- GART programming produces no VM fault;
- every PSP command succeeds;
- RLC autoload completes;
- KIQ and graphics HQDs read back active;
- the CP consumes the ring and returns idle;
- `SCRATCH_REG0` and the CPU-visible memory word match their submitted values.

The indirect-buffer boundary is complete only when the `RELEASE_MEM` fence and
the IB target both match their expected values on the first queue attempt after a
cold boot.
