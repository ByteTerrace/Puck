# amdgpu/ — vendored gfx1033 GPU assets

Assets for the bare-metal amdgpu + RADV port ([docs/amd-vulkan-plan.md](../docs/amd-vulkan-plan.md)):
the Steam Deck gfx1033 family (**Van Gogh**, PCI `1002:163F`, GFX10.3.1 / RDNA2, PSP v11.5).
The implementation is hardware-verified on Steam Deck OLED; the GPU's upstream firmware and
register resources retain their Van Gogh names.
Two kinds of content, both vendored **verbatim**:

- [`firmware/`](firmware/) — AMD signed microcode from **linux-firmware** tag **20251125**
  (commit `4ee5122b3f58e4c07951746c4425e2f4f42e860f`), redistributable
  binaries under [`firmware/LICENSE.amdgpu`](firmware/LICENSE.amdgpu). Per-file sizes,
  SHA256s, and upstream URLs: [`firmware/README.md`](firmware/README.md).
- [`include/`](include/) — **MIT-licensed** AMD register headers + DRM UAPI headers from the
  Linux kernel tag **v6.15** (commit `0ff41df1cb268fc69e703a08a57ee14ae967d0ca`), fetched from
  `https://git.kernel.org/pub/scm/linux/kernel/git/torvalds/linux.git/plain/<path>?h=v6.15`.
  Register headers come from `drivers/gpu/drm/amd/include/`, UAPI headers from
  `include/uapi/drm/` (kept under `include/uapi/drm/` here). Directory layout mirrors upstream
  so `#include "asic_reg/gc/gc_10_3_0_offset.h"` etc. work unchanged.

## License story

- Every header in `include/` carries the full **MIT permission text** in its file header
  (verified per-file at vendoring time; AMD copyright for the `asic_reg`/ip-offset headers).
  `uapi/drm/drm.h` and `uapi/drm/amdgpu_drm.h` carry the equivalent permissive X11/MIT text
  (Precision Insight / VA Linux / Tungsten Graphics / AMD copyrights).
- The firmware binaries are **not** open source: they are redistributable unmodified under
  `LICENSE.amdgpu` (no reverse engineering; carry the notice). We do exactly that.
- **No GPL code is vendored.** The kernel's amdgpu `.c` driver logic (`gfx_v10_0.c`,
  `psp_v11_0.c`, `gmc_v10_0.c`, …) is GPL-2.0 and is **reference-only**: read it upstream (or
  in a scratch checkout outside the repo) to derive sequences, then clean-room the port against
  these MIT headers + the UAPI. Never copy GPL sources into this tree (NOTICE.md doctrine).

## Van Gogh IP-block version table (verified)

Versions verified against kernel v6.15 sources (and the v5.15 per-ASIC switches, which name
`CHIP_VANGOGH` explicitly — v6.15 reads most of this from the VBIOS IP-discovery table at
runtime, so the older tree is the explicit source-side record).

| IP block | Version | Driver file (reference) | Derived from | Vendored registers |
|---|---|---|---|---|
| GC (gfx)  | **10.3.1** | `gfx_v10_0.c` | `Documentation/gpu/amdgpu/apu-asic-info-table.csv` (VANGOGH row); `amdgpu_discovery.c` GC 10.3.1 → `gfx_v10_0` | `asic_reg/gc/gc_10_3_0_{offset,sh_mask,default}.h` |
| PSP (MP0) | **11.5.0** | `psp_v11_0.c` | same CSV; `amdgpu_discovery.c` MP0 11.5.0 → `psp_v11_0` | `asic_reg/mp/mp_11_0_{offset,sh_mask}.h` |
| SMU (MP1) | 11.5.0 | `smu_v11_0.c` / `vangogh_ppt.c` | `amdgpu_discovery.c` MP1 11.5.0 → `smu_v11_0`; v5.15 `nv.c` CHIP_VANGOGH adds `smu_v11_0_ip_block` | (covered by `mp_11_0` headers) |
| SDMA      | **5.2.1** | `sdma_v5_2.c` | same CSV; `amdgpu_discovery.c` SDMA 5.2.1 → `sdma_v5_2` | **none needed** — `sdma_v5_2.c` includes only `gc/gc_10_3_0_{offset,sh_mask}.h`; SDMA 5.2 registers live in the GC register space (no `asic_reg/sdma` headers exist for 5.2) |
| NBIO      | **7.2.0** | `nbio_v7_2.c` | v5.15 `nv.c` `nv_set_ip_blocks`: `AMD_IS_APU` → `nbio_v7_2_funcs`; `nbio_v7_2.c` includes the 7.2.0 headers | `asic_reg/nbio/nbio_7_2_0_{offset,sh_mask}.h` |
| OSSSYS (IH) | 5.x → `navi10_ih` | `navi10_ih.c` | v5.15 `nv.c` CHIP_VANGOGH adds `navi10_ih_ip_block`; `amdgpu_discovery.c` maps all OSSSYS 5.0.x/5.2.0/5.2.1 to `navi10_ih`, which uses the 5.0.0 registers | `asic_reg/oss/osssys_5_0_0_{offset,sh_mask}.h` |
| MMHUB     | **2.3.0** | `mmhub_v2_3.c` | v5.15 `gmc_v10_0.c` `gmc_v10_0_set_mmhub_funcs`: CHIP_VANGOGH → `mmhub_v2_3_funcs`; v6.15 maps MMHUB 2.3.0 → `mmhub_v2_3` | `asic_reg/mmhub/mmhub_2_3_0_{offset,sh_mask,default}.h` |
| ATHUB     | **2.1.0** | `athub_v2_1.c` | v5.15 `gmc_v10_0.c` clockgating: SIENNA_CICHLID ≤ asic ≤ YELLOW_CARP (incl. VANGOGH) → `athub_v2_1`; v6.15 keys on ATHUB ≥ 2.1.0 | `asic_reg/athub/athub_2_1_0_{offset,sh_mask}.h` |
| HDP       | **5.0.0** | `hdp_v5_0.c` | v5.15 `nv.c`: `adev->hdp.funcs = &hdp_v5_0_funcs` for all nv-family incl. Van Gogh | `asic_reg/hdp/hdp_5_0_0_{offset,sh_mask}.h` |
| IP bases  | — | `vangogh_reg_base_init` (`nv.c` v5.15) | per-IP MMIO segment bases | `vangogh_ip_offset.h` |

Not vendored (out of scope for render/compute): DCN 3.0.1 (display), VCN 3.1.0 / JPEG 3.0.x
(video), DF, UMC. Firmware `vangogh_vcn.bin` / `vangogh_dmcub.bin` likewise skipped.

UAPI: `include/uapi/drm/amdgpu_drm.h` + `drm.h` (v6.15) are the kernel⇄RADV contract the DRM
shim implements — the ~8 amdgpu ioctls + syncobj.
