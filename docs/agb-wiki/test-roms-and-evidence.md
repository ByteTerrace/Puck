# GBA test ROMs and evidence

Reference ROMs answer specific hardware questions. They are optional evidence
stages, not repository gates, because BIOS identity, suite revision, probe shape,
and emulator stop conditions can change a reported total.

## Evidence map

| Evidence | Best use | Result shape |
|---|---|---|
| Tier A synthetic cartridge and vectors | deterministic construction, CPU/bus smoke, snapshot, fork, save, throughput | pass/fail |
| jsmolka/gba-tests | ARM, Thumb, memory, save, and NES compatibility | verdict register |
| FuzzARM | broad randomized instruction semantics | failure marker in EWRAM |
| Puck `--oracle` probes | DMA, timer, IRQ, SIO, latch, and HALTCNT mechanisms | self-check or measured cycle count |
| mGBA suite | memory, I/O, timing, DMA, timer, SIO, and PPU categories | itemized cells and category totals |
| AGS TCHK10 cartridge | broad retail-hardware conformance | rendered cell grid |
| Render-hash floors | deterministic framebuffer behavior | named 64-bit hash |
| ares lockstep | per-instruction CPU and bus state | first trace divergence |
| mGBA cycle trace | instruction cycle attribution | normalized delta trace |
| Commercial games | integration and content compatibility | scripted observable behavior |

## Running the optional suites

```powershell
$env:PUCK_AGB_BIOS = '<16-KiB retail BIOS>'
$env:PUCK_AGB_TESTROMS = '<corpus root>'
$env:PUCK_AGB_ACCURACY_SUITE = '<mGBA suite ROM>'
$env:PUCK_AGB_AGS = '<TCHK10 AGS cartridge>'

dotnet run --project src/Puck.AdvancedGamingBrick.Post -c Release -- --tier B
```

Commercial render and link stages additionally use `PUCK_AGB_GAMES` and
`PUCK_AGB_LINK_GAME`. Missing assets produce skips.

## AGS aging cartridge

Use the TCHK10 dump because output-patch locations and cell layout vary between
revisions. Run with a retail BIOS. One cell depends on a link partner; classify
that result separately from single-machine failures. Preserve the rendered grid
as an artifact so a summary count can be audited.

## mGBA suite totals

Do not compare an aggregate count across suite revisions or emulator reports
unless the ROM, cell list, BIOS path, and scoring rules match. Prefer itemized
rows. Categories such as Timing, count-up, and Misc can share one underlying
DMA/CPU arbitration issue, while a headline total hides that relationship.

## Puck oracle probes

`--oracle` runs hand-assembled probes with two result classes:

- **self-checking probes** encode a pass/fail rule for behavior such as
  per-channel DMA latches or SIO duration;
- **measurement probes** store an observed cycle count for comparison with an
  external corpus.

A measurement mismatch is not a proven core defect. First reproduce the source
ROM's setup, including BIOS route, WAITCNT, pipeline state, interrupt mask, DMA
register write order, and stop condition. `dma/start-delay`, `dma/force-nseq`,
HALTCNT exit, timer, and IRQ-dispatch rows are particularly sensitive to probe
shape.

## Co-simulation

For ares, use `--lockstep` with the configured co-simulator. For mGBA, use
`--trace-cycles`. Normalize before diffing:

- compare per-instruction deltas because mGBA resets cumulative counts at frame
  boundaries;
- align the pipeline-visible PC representation by four bytes;
- ensure both sides use direct boot or both use the full BIOS path;
- stop on the same completed instruction and memory observation.

A minimal ROM should write its result to EWRAM and reach a stable loop. This
keeps the observable question separate from trace-format details.

## Recording a finding

Record the following next to any score or divergence:

1. ROM and BIOS identities;
2. exact command and environment inputs;
3. direct-boot or full-boot mode;
4. expected and actual observable values;
5. first normalized trace difference, when relevant;
6. whether the source is hardware, documentation, or another emulator;
7. the smallest repository stage or diagnostic that reproduces it.

This information is durable. Session chronology, commit hashes, and before/after
score narration are not.
