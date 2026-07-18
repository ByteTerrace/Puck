# Puck.AdvancedGamingBrick.Post

This executable verifies the deterministic AGB-native machine and hosts the
cycle, render, state, and co-simulation diagnostics used for accuracy work.

## Run the battery

```powershell
dotnet run --project src/Puck.AdvancedGamingBrick.Post -c Release
```

Optional battery arguments:

| Argument | Purpose |
|---|---|
| `--tier A|B|C` | Run one tier. |
| `--filter <text>` | Run stages whose names contain the text. |
| `--roms <directory>` | Override the reference-ROM root. |
| `--games <directory>` | Override the commercial-ROM root. |
| `--artifacts <directory>` | Override `artifacts/agb-post`. |

Exit code 0 means every selected stage passed or skipped. Exit code 1 means a
check failed. Exit code 2 means infrastructure prevented a stage from running.

## Tiers

| Tier | Coverage | Assets |
|---|---|---|
| A | CPU and bus smoke vectors; determinism; state round trip; fork determinism; save round trip; bounded queued-host backpressure and immutable frame publication; throughput; zero-alloc-per-frame | none |
| B | conformance CPU/save/misc suites; ARM fuzz corpus; render hashes; accuracy suite; AGS aging cartridge | assets listed below; stages skip when absent |
| C | deterministic multiplayer cable replay and a commercial link-game replay | synthetic replay needs none; commercial replay needs a retail BIOS and `PUCK_AGB_LINK_GAME` |

## External assets

None of these ship with the repository; each stage skips cleanly when its asset
is absent. Corpus names and download sources are listed here as the one place
they are needed (to fetch the right ROMs); the rest of the codebase refers to
them only by role.

| Asset | Source | Configuration |
|---|---|---|
| 16 KiB AGB BIOS | (retail dump) | `PUCK_AGB_BIOS` |
| conformance + PPU corpus root | jsmolka `gba-tests` (`github.com/jsmolka/gba-tests`) | `--roms` or `PUCK_AGB_TESTROMS` |
| ARM fuzz corpus | `github.com/DenSinH/FuzzARM` — clone into a `FuzzARM` directory beside the corpus root | discovered beside `--roms` |
| commercial-ROM root | (user ROMs) | `--games` or `PUCK_AGB_GAMES` |
| accuracy-suite ROM | `mgba-emu/suite` | `PUCK_AGB_ACCURACY_SUITE` |
| TCHK10 AGS aging cartridge | (aging-cartridge dump) | `PUCK_AGB_AGS` |
| commercial multiplayer cartridge | (user ROM) | `PUCK_AGB_LINK_GAME` |

Diagnostics that depend on retail BIOS timing reject replacement or unknown
images unless `--allow-replacement-bios` is supplied deliberately. Missing
optional assets skip the corresponding stage.

## Performance diagnostics

`--bench [--bench-rom <rom>] [--bench-frames N] [--bench-fleet N,N,...]` runs
the machine-fleet bench: fleet scaling (independent/choir streams ×
serial/parallel), burst catch-up, and `Create`/`Snapshot`/`Restore`/`Fork`
latency and allocation. `--bench-rom` defaults to the same zero-asset
synthetic cartridge the throughput stage runs. Every fleet cell ends with a
serial-vs-parallel bit-lock check; a divergence exits 1. Mirrors the Humble
Post's `--bench`.

## Determinism diagnostics

`--hash-divergence [rom] [--frames N] [--fine] [--perturb-at N]` compares two
machines and identifies the first differing snapshot section and byte. Use
`--state-roundtrip <rom>` to inspect a focused snapshot/restore case and
`--save-test <rom>` to inspect cartridge backup persistence.

## ROM and render diagnostics

- `--render <rom> <output>` captures a framebuffer.
- `--render-hash <rom>` prints deterministic render hashes.
- `--pctrace`, `--statetrace`, and `--trace-crash` record execution state at
  different levels of detail.
- `--iodump` records I/O state.
- `--gen-rom` writes a hand-assembled diagnostic cartridge.
- `--oracle` runs the self-contained cycle-probe set.
- `--ags` and `--accuracy-suite` run their configured evidence cartridges.
- `--dump-snapshot [--frames N] [--rom <path>] [--out <file>]` boots the
  synthetic cartridge (or `--rom`), runs `N` frames (default 300), and writes
  the raw snapshot image plus a `<file>.sections.txt` sidecar (name/offset/
  length per component) to `artifacts/agb-post/snapshot.bin` by default.
  Prints the output paths and the snapshot's FNV-1a fingerprint. Offline input
  for diffing two builds' snapshot images byte-for-byte — `--hash-divergence`
  only proves a single build's internal determinism.

## Co-simulation

`--lockstep` compares Puck with the configured `ares-cosim` co-simulator (its
path from `PUCK_ARES_COSIM`). `--trace-cycles` compares instruction timing with
the cosim oracle. Normalize the traces before interpreting a mismatch:

- the oracle's cumulative cycle count restarts at frame boundaries, so compare
  per-instruction deltas;
- Puck's pipeline representation exposes PC four bytes ahead of the oracle's trace;
- direct-boot and full-BIOS runs have different initial state. Set
  `PUCK_AGB_FULLBOOT=1` only when both sides are configured for the BIOS path.

Use a minimal self-checking ROM when isolating one timing rule. Store the result
in emulated memory and compare the observable value before using an instruction
trace to explain it.

## Accuracy workflow

1. Reproduce the behavior in a focused diagnostic or reference ROM.
2. Confirm BIOS identity, boot mode, stop condition, and trace normalization.
3. Compare hardware documentation and at least one independent implementation
   when the behavior is not directly hardware-tested.
4. Change the smallest hardware model that explains the evidence.
5. Run Tier A and the affected evidence stages. Run Tier C for SIO or link
   changes.

Reference-suite totals are measurements, not substitute specifications. Record
itemized failures and preserve the ROM, BIOS profile, and command needed to
reproduce them.
