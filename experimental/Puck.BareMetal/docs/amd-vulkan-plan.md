# AMD Vulkan Host

Puck.BareMetal hosts Mesa RADV on a purpose-built Linux ABI and supplies the
kernel-side AMDGPU services needed for offscreen Vulkan on Steam Deck hardware.
The target path is compute/render only: it does not provide KMS, DCN, VCN, MES,
RAS, SR-IOV, or a general DRM subsystem.

## System boundary

```text
UEFI image, ring 0
  platform: page tables, PAT, ECAM, APIC, DMA, clock, framebuffer console
  devices: virtio-net, synthetic VFS, renderD128 DRM/AMDGPU seam
  GPU: Van Gogh/Sephiroth probe, GMC/GPUVM, PSP firmware load, KIQ, GFX HQD

musl guest, ring 3
  ld-musl + RADV + ACO
  direct vk_icdGetInstanceProcAddr entry
  offscreen Vulkan resources and command submission
```

RADV is built without LLVM, WSI, or a display stack. ACO compiles shaders in
process. The guest calls the ICD directly, avoiding the Khronos loader. Display
remains on the UEFI GOP framebuffer while GPU bring-up is diagnostic and
interrupt-free.

## Hardware target

The hardware-verified target is a Steam Deck OLED (Sephiroth):

| Resource | Value |
|---|---|
| GPU | PCI `1002:1435`, revision `0xae`, function `04:00.0` |
| Graphics IP | GC 10.3.1 / `gfx1033`, 8 CUs, wave32 |
| Register aperture | BAR5, `0x80500000`, 512 KiB, UC |
| Doorbells | BAR2, `0xf8f0000000`, 2 MiB |
| Framebuffer aperture | BAR0, `0xf8e0000000`, 256 MiB |
| GPU carveout | 1 GiB at DRAM physical `0x4_4000_0000` |
| PSP / SMU | PSP 11.5 sOS warm; SMU responsive |
| IOMMU | No IVRS table; direct DMA |

The LCD Van Gogh device uses the same gfx10.3.1 protocol family but a different
PCI device ID. Device discovery therefore matches AMD display class and validated
IP/register behavior instead of assuming one ID.

## Host substrate

The QEMU-testable substrate provides:

- ACPI RSDP/XSDT/MCFG discovery and PCIe ECAM enumeration;
- 32-bit and 64-bit BAR decoding, high-MMIO page mapping, and PAT UC/WC types;
- x2APIC and MSI-X delivery where firmware exposes x2APIC;
- aligned DMA allocations and IOMMU-table detection;
- a framebuffer console and persisted boot log;
- a render-node VFS topology compatible with libdrm discovery;
- DRM and AMDGPU queries, GEM allocation/mapping, contexts, and synchronization
  handles sufficient for RADV initialization;
- a dynamic musl loader and staged shared-object closure.

QEMU proves Vulkan instance/device creation, buffer allocation and mapping, and an
ACO compute-pipeline compile against the synthetic Van Gogh description. It does
not prove GPU execution.

## Hardware bring-up path

The implemented Deck path is:

1. Discover the GPU and map the smallest non-prefetchable memory BAR as the
   register aperture.
2. Read the warm PSP, SMU, framebuffer, memory-controller, CP, and RLC health
   registers.
3. Create a VMID0 GART and program the GC hub with explicit HDP flush and TLB
   invalidation.
4. Use the warm PSP kernel-management ring for `LOAD_TOC`, `SETUP_TMR`, and
   `LOAD_IP_FW`, including the Van Gogh RLC restore-list components.
5. Request RLC autoload and require both bootload completion and an idle CP.
6. Apply the Van Gogh constants and golden-register set.
7. Establish a live KIQ through a compute MQD/HQD and scheduler designation.
8. Configure the graphics queue through the gfx MQD/HQD interface, unhalt the
   required CP engines, emit the clear-state stream, and kick the 64-bit doorbell.
9. Prove execution with register and memory writes before attempting an indirect
   buffer and `RELEASE_MEM` fence.

The live KIQ and gfx HQD/MQD path are required on the target firmware. The legacy
`CP_RB0_*` interface is retained only as unused diagnostic code and is not a
supported bring-up route.

The detailed register and packet contract is in
[gfx1033 Hardware Reference](gfx103-bringup-spec.md). The exact operator workflow
and unresolved hardware behavior are in
[Steam Deck GPU Bring-up](deck-bringup-handoff.md).

## Current capability

The Deck path has verified:

- register access and warm-firmware health;
- VMID0 GART construction without a reported GCVM protection fault;
- PSP authentication/loading of SDMA, CE, PFP, ME, MEC, MEC2, and RLC payloads;
- RLC autoload completion;
- KIQ activation;
- GFX packet fetch and execution;
- `SET_UCONFIG_REG` writing `SCRATCH_REG0`;
- `WRITE_DATA` reaching CPU-visible memory with bypass cache policy.

The first unresolved boundary is an indirect-buffer submission followed by a
`RELEASE_MEM` fence. A full clear-state stream currently stops at a repeatable
packet boundary, and a separate intermittent failure can clear the GART-backed
ring block after its CPU-side verification. These are tracked as current
investigations, without preserving discarded theories, in the Deck runbook.

RADV execution is therefore not yet connected to the hardware queue. The
render-node seam currently answers the discovery, allocation, and compilation
surface; command submission remains the next functional boundary.

## RADV build and staging

Build the musl closure in WSL:

```sh
radv/build-radv-musl.sh
```

The output is machine-local under `.qemu/radv/`. Build and boot the host in QEMU:

```powershell
samples/EfiLinux/run-qemu.ps1
```

Stage a Deck USB from a Visual Studio Developer PowerShell:

```powershell
samples/EfiLinux/stage-deck.ps1 -Target E:\
```

The staging script copies the EFI image, RADV closure, and required firmware. It
does not write the Deck's internal storage.

## Source and license boundaries

- `amdgpu/firmware/` contains unmodified signed AMD firmware and its upstream
  notice.
- `amdgpu/include/` contains the permissively licensed register, packet, UAPI,
  and data-structure headers used by this implementation.
- Mesa RADV and ACO are built externally and staged as machine-local artifacts.
- GPL Linux AMDGPU implementation files may be consulted for observable
  sequencing but are not copied into this repository.
- Puck owns the freestanding platform, Linux ABI, DRM shim, and hardware-port
  implementation.

See [NOTICE](../NOTICE.md), [AMDGPU assets](../amdgpu/README.md), and the license
files stored beside the vendored assets.
