# Van Gogh firmware (vendored, verbatim)

AMD signed microcode for the Steam Deck LCD APU (**Van Gogh**, PCI `1002:163F`), copied
**unmodified** from upstream **linux-firmware**, pinned at release tag **20251125**
(commit `4ee5122b3f58e4c07951746c4425e2f4f42e860f`, 2025-11-25). These blobs are loaded
through the PSP (MP0) during GPU bring-up — see `../../docs/amd-vulkan-plan.md` milestone (d).

- **License:** [`LICENSE.amdgpu`](LICENSE.amdgpu) (in this directory, also verbatim from the
  linux-firmware repo root). Binary redistribution permitted, unmodified, notice carried —
  which is exactly what this directory does. The upstream `WHENCE` entry for these files reads:
  `Licence: Redistributable. See LICENSE.amdgpu for details.` (WHENCE section
  "Driver: amdgpu - AMD Radeon").
- **Upstream URL pattern:**
  `https://git.kernel.org/pub/scm/linux/kernel/git/firmware/linux-firmware.git/plain/amdgpu/<file>?h=20251125`
  (`LICENSE.amdgpu` from the repo root: `.../plain/LICENSE.amdgpu?h=20251125`).

## Manifest

| File | Size (bytes) | SHA256 |
|---|---:|---|
| `vangogh_pfp.bin`  | 263,424 | `a52b986a2065fbb353c00ba5e5b0b909feed9009d7e10f8d81a01871592c55d9` |
| `vangogh_me.bin`   | 263,424 | `9505c86b75d6121658444b10cb491d77e2ecc799a167a0e325c8fdbb0057f1f7` |
| `vangogh_ce.bin`   | 263,296 | `484e2738b96886f8b8c220ef51665ff849d0c9b45d86d960ab63578ab1cc2ffc` |
| `vangogh_mec.bin`  | 268,160 | `5b6156470c8fabbacf03d5afcd554f5d73954f6d5e36a9c053224d46fb2b5201` |
| `vangogh_mec2.bin` | 268,160 | `5b6156470c8fabbacf03d5afcd554f5d73954f6d5e36a9c053224d46fb2b5201` |
| `vangogh_rlc.bin`  |  45,368 | `a3512355132203d8aca122e246b3fa8dcf5aa13c064011e8ffedf5674656b58b` |
| `vangogh_toc.bin`  |   1,792 | `2ca90c91f7e1756d5c1bd5443007768c692fbe14a228eb5a87e47953c62f5d75` |
| `vangogh_asd.bin`  | 205,312 | `77ee917e1d516c60fc795468d6c1b47fccf7501b36ee5b6e5f3307bd5cdeee57` |
| `vangogh_sdma.bin` | 135,424 | `7e81b24cb357e3373bf0d7b94bff53c8b0225fd87d07d6940d98241d9ec572fd` |
| `LICENSE.amdgpu`   |   2,938 | `572872598565dc3513470de971a32bf9db301f47afeef3636345eadae33b2eee` |

Total firmware payload: **1,714,360 bytes (~1.64 MiB)**.

## Notes / expected-vs-actual

- **No `vangogh_sos.bin` exists upstream** (the plan doc's firmware list includes `sos`). The
  complete upstream `vangogh_*` set at tag 20251125 is: `asd, ce, dmcub, me, mec, mec2, pfp,
  rlc, sdma, toc, vcn`. On this APU the PSP SOS/bootloader ships in the system BIOS (the PSP is
  already posted when we take over), so there is no separate SOS blob to load — consistent with
  the plan's warm-PSP strategy. The plan doc's list should drop `sos`.
- **`vangogh_vcn.bin` and `vangogh_dmcub.bin` are deliberately not vendored** — we skip video
  decode (VCN) and display microcontroller (DMCUB/DCN); the port is render/compute only.
- `vangogh_mec.bin` and `vangogh_mec2.bin` are **byte-identical** upstream (same SHA256); both
  names are kept because the PSP TOC/load path refers to them as distinct firmware IDs.
- All nine blobs carry the standard amdgpu ucode container header (verified non-HTML binary
  content, little-endian ucode version fields) — parse offsets per the UCODE layout referenced
  from the MIT headers, not by copying GPL loader code.
