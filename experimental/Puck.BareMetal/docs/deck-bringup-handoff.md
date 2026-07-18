# Steam Deck GPU Bring-up

This runbook covers the hardware-only portion of the BareMetal Vulkan path. Read
[AMD Vulkan Host](amd-vulkan-plan.md) for the system boundary and
[gfx1033 Hardware Reference](gfx103-bringup-spec.md) for register and packet
details.

## Verified target

The active development unit is a Steam Deck OLED (Sephiroth) with PCI device
`1002:1435`, GC 10.3.1, and PSP 11.5. The LCD Van Gogh device belongs to the same
protocol family but has not replaced the OLED as the hardware-verified target.

| Resource | Hardware value |
|---|---|
| PCI function | `04:00.0`, revision `0xae` |
| Register BAR | BAR5 `0x80500000`, 512 KiB, UC |
| Doorbell BAR | BAR2 `0xf8f0000000`, 2 MiB |
| Framebuffer BAR | BAR0 `0xf8e0000000`, 256 MiB |
| Framebuffer location | MC `0xf400..0xf43f`, offset `0x440` |
| GPU carveout | 1 GiB at DRAM physical `0x4_4000_0000` |
| Firmware state | PSP sOS and SMU are alive after GOP |
| IOMMU | No IVRS table; direct DMA |

## Supported initialization sequence

The current implementation performs these operations in order:

1. Map the register aperture and capture the GPU health set.
2. Build the VMID0 GART in the GPU carveout, program GFXHUB, flush HDP, and
   invalidate the translation cache.
3. Create the PSP KM ring and submit `LOAD_TOC`, `SETUP_TMR`, and the Van Gogh
   firmware loads.
4. Load the RLC restore-list payloads with firmware type IDs 20, 21, and 22.
5. Request RLC autoload and wait for `RLC_RLCS_BOOTLOAD_STATUS` bit 31 and an
   idle `CP_STAT`.
6. Apply `GRBM_CNTL`, per-VMID shared-memory setup, GDS clearing, and the
   available Van Gogh golden registers.
7. Establish a live KIQ: designate the scheduler and program a compute MQD into
   the selected HQD register file.
8. Program the GFX MQD/HQD, keep CE active, unhalt the CP, wait for idle, emit
   the complete clear-state stream, and ring doorbell index `0x116`.
9. Use bypass cache policy for CPU-observed `WRITE_DATA` results.

Do not replace this path with `CP_RB0_*`, a scheduler-designation write without
a live KIQ, a halted CE, or a minimal empty preamble. Those configurations do
not describe the target firmware's working queue path.

The PSP/RLC path owns the PFP, ME, and CE instruction-cache base registers. The
current diagnostic helper reads all three and attempts a CE-base write; the write
does not persist on the target. Treat the readback as diagnostic evidence and do
not depend on software reprogramming of that register.

## Current execution boundary

The basic ring test passes on hardware:

- the CP consumes the submitted stream;
- `SET_UCONFIG_REG` updates `SCRATCH_REG0` to `0xDEADBEEF`;
- `WRITE_DATA` reaches the test allocation when cache policy is bypass;
- the CP returns to idle.

The indirect-buffer/fence path is not yet complete. With the full clear-state
stream, execution repeatedly stops at ring index `0xde`, the boundary at which
the second `SET_CONTEXT_REG` block begins. The PFP reports a fetch wait at
instruction pointer `0x2fd`/`0x2fe`; the CE instruction pointer remains live.
`CE_WAITING_ON_DE_COUNTER_UNDERFLOW` alone is therefore not evidence that CE is
frozen.

There is also an intermittent memory failure in which the GART-backed ring block
reads as zeros after the CPU wrote and verified it. The CPU mapping remains
readable. KIQ state shares the larger GART allocation, so queue scribbles,
visibility, and mapping errors remain possible causes. The carveout-backed test
has not yet been isolated as the first attempt after a cold boot.

A failed first queue attempt can persist across a CP halt/unhalt within the same
boot. Later attempts in that boot are not independent evidence.

## Investigation order

Use this order for the next hardware iteration:

1. Run the carveout-backed queue first after a full power cycle. Place KIQ
   MQD/EOP/ring storage in the carveout as well so the run contains no GART-backed
   queue state.
2. Dump ring words `0xd8..0xe4` and compare them with the generated second
   `SET_CONTEXT_REG` block. Check packet count and section-boundary arithmetic.
3. If the GART clearing behavior remains relevant, sample the ring head and
   read-pointer during the poll loop to identify when the contents change.
4. Preserve PFP/ME/CE instruction-pointer and stalled-status dumps, but interpret
   them with the packet boundary and forward progress rather than a single status
   bit.
5. Prefer the permissively licensed AMD headers and clear-state data already
   vendored in `amdgpu/include/` over re-deriving packet encodings.

Each diagnostic build should test one causal distinction. Always power the Deck
off between compared runs.

## Build and stage

Machine-local prerequisites:

- Visual Studio C++ tools for `cl.exe`, `ml64.exe`, and the Windows SDK;
- QEMU and OVMF for the host-side smoke;
- WSL for building the musl RADV closure;
- a FAT-formatted USB device and USB-C adapter or dock for the Deck.

Build the RADV closure when it is missing:

```sh
radv/build-radv-musl.sh
```

Run the QEMU smoke before staging hardware media:

```powershell
samples/EfiLinux/run-qemu.ps1
```

Stage the USB device:

```powershell
samples/EfiLinux/stage-deck.ps1 -Target E:\
```

Power off the Deck, insert the device, hold Volume Down while pressing Power,
and choose the USB entry. The QEMU model has no RDNA GPU; its GPU path must skip
cleanly while the ring work runs only on the Deck.

## Retrieving logs

The panel is the live console. The kernel also stores its RAM log in the UEFI
`PuckLog` variable before parking. At the start of the next boot,
`PuckEfiPreloadGpuFw` writes that saved log to `\PuckLog.txt` on the ESP and
clears the variable.

To retrieve an exact log:

1. Cold-boot the diagnostic image and let it reach the parked verdict screen.
2. Power off and cold-boot the USB once more to flush the saved log.
3. Read `\PuckLog.txt` on the development machine.

The file is always one boot behind the image currently running. Keep the build
identity in the log output; do not infer it from the order in which media was
booted.

## Verification record

For each Deck run, retain:

- build identity and cold/warm state;
- PCI identity and BAR map;
- GART and PSP completion lines;
- KIQ and GFX HQD readbacks;
- ring pre-kick and failure-window words;
- CP pointers, stalled-status registers, VM fault status, and final verdict;
- whether the test storage was GART-backed or carveout-backed and whether it was
  the first queue attempt in the boot.

QEMU success is the ACO pipeline compile and `exit_group(0)`. Deck success for
the current boundary is a signaled `RELEASE_MEM` fence from an indirect buffer;
that result has not yet been observed.
