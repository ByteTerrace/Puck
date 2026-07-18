# GBA capability and gap index

This index summarizes the native GBA core. Follow each subsystem link for
hardware details, sources, and diagnostic procedures.

## Implemented capabilities

| Area | Capability | Evidence surface |
|---|---|---|
| CPU and bus | ARM/Thumb execution, pipeline-visible open bus, Game Pak prefetch, WAITCNT mirror timing, multiply early termination | smoke vectors, jsmolka, FuzzARM, cycle diagnostics |
| DMA | channel-local latches, start latency, count/source/destination latching, forced non-sequential fetch, video-capture timing | `--oracle`, mGBA suite |
| Timers and IRQ | per-cycle prescalers, cascade, overflow/reload/IRQ latency, IE/IF/IME synchronization, HALT and STOP distinction | `--oracle`, mGBA suite, AGS |
| PPU | scanline rendering, OBJ cycle budget and dropout, affine reference latches, windows, blending, HBlank raster effects | render hashes, mGBA suite, AGS |
| APU | DMG-derived PSG channels, Direct Sound ring/playing-buffer model, FIFO DMA threshold, narrow writes, deterministic integer samples | `--oracle`, audio inspection |
| Cartridge | EEPROM, SRAM, Flash, save overrides, RTC, undersized-image validation | jsmolka save stages, save round trip |
| Determinism | full mid-frame snapshot/restore/fork, section-localized hash divergence | Tier A state and fork stages, `--hash-divergence` |
| Link | Normal and multiplayer SIO, cable identity and partner state, deterministic multi-console session | Tier C `link-replay` and commercial replay |
| Demo tools | snapshot slots, rewind, two-instance runahead, debug traces | `agb.*` console verbs |

## Accuracy work

| Item | Status | Effort | Required evidence |
|---|---|---:|---|
| Cycle-exact DMA/CPU bus arbitration and pipeline phase | evidence needed | L | isolate remaining Timing, count-up, and Misc rows with hardware or independent-oracle probes |
| Long-multiply carry flag on Booth termination | candidate | S–M | focused flag-result ROM and independent reference |
| Prefetch-disabled timing anomaly | evidence needed | S–M | WAITCNT-bit-14 micro-ROM |
| Fast-EWRAM waitstate register | candidate | M | hardware timing probe |
| Mosaic source sampling | evidence needed | S–M | vertical and affine mosaic ROM |
| Window coordinate clamping | evidence needed | S | hardware result for inverted and out-of-range bounds |
| Blend edge rules | evidence needed | S–M | semi-transparent OBJ, brightness, and self-blend probes |
| VRAM/OAM access-conflict waitstates | candidate | M | cycle-counted access ROMs |
| Per-dot PPU and VRAM fetch open bus | candidate | XL | games or hardware tests that require mid-scanline state |
| Exact Direct Sound level and SOUNDBIAS model | evidence needed | M | captured hardware amplitude and PWM behavior |
| Band-limited presentation resampling | candidate | L | audible-quality requirement; deterministic integer core remains unchanged |
| GPIO sensors | candidate | M | recordable command design plus per-device protocol tests |
| Undersized-ROM mirroring | evidence needed | S | cartridge-size and open-bus probe |
| Software fast memory | evidence needed | M | profile against the current region dispatcher |
| Cached interpreter | evidence needed | L–XL | profile and parity prototype |
| Full rollback protocol | candidate | L–XL | product requirement beyond the existing snapshot and link substrate |

## Deliberate exclusions

- A floating-point MP2K high-quality mixer inside emulated state.
- Heuristic per-game idle-loop skipping.
- Threaded PPU/APU execution that changes event ordering.
- A JIT or GPU-side PPU without a demonstrated product requirement and parity
  plan.
- Peripheral families with no supported content path, such as e-Reader and
  Play-Yan.

## Probe interpretation

The `--oracle` measurement rows can differ from published corpus values because
probe setup, BIOS path, or stop condition differs. Treat `dma/start-delay`,
`dma/force-nseq`, HALTCNT-exit, timer, and IRQ-dispatch differences as
reproduction tasks until the exact source ROM shape is matched. Do not retune a
model that agrees with an independent emulator merely to match a summary number.
