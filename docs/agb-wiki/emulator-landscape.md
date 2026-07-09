# The Emulator Landscape

The GBA emulation field shares one vocabulary — endrift's *cycle-accurate* /
*cycle-count-accurate* / *HLE* taxonomy — and one credibility anchor: the AGS
aging cartridge plus the community timing / ARMWrestler / FuzzARM suites. That is
exactly Puck's own posture (two live co-sim oracles, AGS 38/38 with a real BIOS).
This page places each project and carries the standing "GBATEK cannot be trusted
uncritically" argument, plus the dubious-claim list the reviewers refused to
launder back in.

Provenance: `digest-6` (gatherer), with cross-checks from `digest-0`, `digest-1`,
`digest-2`, and the three reviews' calibration sections.

---

### endrift's accuracy taxonomy (the field's shared vocabulary)

- **Source:** mGBA blog, *Emulation Accuracy, Speed, and Optimization*,
  https://mgba.io/2017/04/30/emulation-accuracy/ ; *Cycle Counting, Memory Stalls,
  Prefetch and Other Pitfalls*, https://mgba.io/2015/06/27/cycle-counting-prefetch/.
- **Finding:** three tiers the whole field implicitly uses. **Cycle accuracy** —
  every operation lands at the precise relative time against every other piece of
  hardware ("legendarily accurate, infamously slow," higan/ares). **Cycle-count
  accuracy** — components complete atomically with the correct total duration but
  don't necessarily interleave perfectly ("much easier to design, implement, and
  maintain"). **HLE** — native-code approximations of programmable components,
  trading synchronization fidelity for speed. endrift's explicit quote: "It is a
  common misconception that mGBA is now or will become cycle accurate."
- **Puck status:** Puck runs the ares-side per-cycle model with the fleet-friendly
  event queue instead of ares's cothreads — cycle-accurate semantics where it
  matters, cycle-count-cheap where it doesn't. Maps directly onto decisions this
  wiki documents explicitly rather than leaving implicit.

### mGBA (endrift) — the pragmatic reference

- **Source:** https://mgba.io/ , https://github.com/mgba-emu/mgba ; *"Holy Grail"
  Bugs in Emulation, Part 1*, https://mgba.io/2017/05/29/holy-grail-bugs/.
- **Finding:** deliberately *cycle-count* accurate for its GBA core, by explicit
  design. The `mTiming` sorted-linked-list scheduler drives components that run to
  completion atomically. Its "Holy Grail Bugs" and WAITCNT bit-15 writeups are the
  field's best public evidence that GBATEK cannot be trusted uncritically — and
  that independently-written emulators inherit *identical* doc-sourced bugs.
- **Role for Puck:** our co-sim oracle and the source of the FIFO ring model (#4),
  the Holy Grail DMA open-bus (#6), the override DB (#3), and the mix formula
  (#18) techniques.

### NanoBoyAdvance (fleroviux) — the cycle-accuracy specialist

- **Source:** https://github.com/nba-emu/NanoBoyAdvance ; PR #258,
  https://github.com/nba-emu/NanoBoyAdvance/pull/258.
- **Finding:** targets *true* cycle accuracy (the opposite tradeoff from mGBA) —
  cycle-accurate CPU, DMA, timers, prefetch, and a dedicated cycle-accurate PPU
  renderer (PR #258). First software emulator (predating even the MiSTer FPGA core)
  to pass all AGS aging-cartridge tests. Ships an opt-in MP2K HQ audio mixer as an
  HLE enhancement architecturally decoupled from the accuracy core. Its
  crowd-sourced hardware-research fixes (WIN0/WIN1 corrected per `destoer`) mirror
  the same pattern as mGBA's WAITCNT bug.
- **Role for Puck:** the specialist accuracy leader (with SkyEmu), and the
  higher-value candidate if a third co-sim oracle is ever added for GBA-specific
  questions (review-c D4).

### ares — accuracy-purist, our co-sim oracle

- **Source:** https://ares-emu.net/ , https://github.com/ares-emulator/ares.
- **Finding:** the most accuracy-purist multi-system project (higan/bsnes lineage),
  cooperative-multithreaded via `libco` cothreads — the literal implementation of
  "cycle accuracy," at large per-thread context-switch cost. But its *GBA core
  specifically* is a laggard within its own family: community consensus places it
  behind NanoBoyAdvance/SkyEmu/mGBA on accuracy/compatibility (interpreter-based,
  under-invested), though it does use a dot-based renderer, ahead of VBA-M.
- **Role for Puck:** **our co-sim oracle** — our timer-overflow→IRQ boundary
  matches it exactly. No contradiction between "behind on accuracy" and "useful as
  an *independent* oracle."
- **Calibration (review-b §b1, review-c D4):** the digest-2 claim "ares has no GBA
  support" is **stale/wrong** — it came from Wikipedia/Grokipedia summaries and
  contradicts our live `ares-cosim.exe` + `--lockstep` oracle. Trust the
  implementation. Separately, ares's GBA core being behind the specialists does not
  weaken its value as an independent oracle — the two statements are not in
  conflict.

### SkyEmu (Saleh) — the per-pixel PPU

- **Source:** https://github.com/skylersaleh/SkyEmu ,
  https://github.com/skylersaleh/SkyEmu/blob/main/docs/Accuracy.md.
- **Finding:** genuinely per-pixel PPU (mid-scanline effects) and per-sample audio
  (called out as needed for Pikachu's voice synthesis in Pokémon Yellow/Pinball);
  2020/2020 timing with the official BIOS. Also emulates GB/GBC/DS in the same
  codebase, with GBA explicitly the most mature core — an internal admission of how
  costly per-pixel accuracy is even for a team that's solved it once.
- **Role for Puck:** the proof-of-cost reference for the per-dot PPU arc (survey
  #21, [ppu-rendering-models.md](ppu-rendering-models.md)).

### Hades — modern C, balance over purism

- **Source:** https://github.com/hades-emu/Hades , https://hades-emu.org/.
- **Finding:** explicitly targets "a decent balance between usability, speed and
  accuracy"; third software emulator to pass AGS. The "fast follower" architecture
  — adopt the specialists' published findings in a clean modern C codebase (Meson/
  Ninja, SDL3, GPIO device emulation), optimize for ergonomics rather than pushing
  the frontier. A case study in how hardware-behavior research becomes reusable
  community knowledge.

### VBA-M — the anti-pattern baseline

- **Source:** https://github.com/visualboyadvance-m/visualboyadvance-m ;
  Emulation Wiki, https://emulation.gametechwiki.com/index.php/Game_Boy_Advance_emulators.
- **Finding:** the community continuation of the aged 2004 VisualBoyAdvance core;
  fails a large fraction of the mGBA timing suite (680/1260 vs mGBA's 1098/1260).
  Its canonical warning is *accuracy debt*: a nontrivial population of ROM hacks
  were authored *against* VBA-M's specific timing bugs, so some content now depends
  on the inaccuracy. Once an inaccurate reference is popular long enough, its bugs
  become de facto "spec" later emulators must consciously choose whether to
  reproduce.

### DSHBA (DenSinH) — the GPU-side PPU experiment

- **Source:** https://github.com/DenSinH/DSHBA.
- **Finding:** the only project attempting GPU-accelerated PPU rendering (OpenGL
  3.3, 4× native). Documents a real architectural wall: GBA alpha blending blends
  specific top/bottom layer pairs, which does not map onto standard GPU blend
  hardware, so DSHBA renders the frame twice and composites in an extra pass. Worth
  knowing before any hardware-accelerated PPU path is considered here.

### The long tail

- **Source:** DenSinH GBAC-, https://github.com/DenSinH/GBAC- ;
  RustBoyAdvance-NG, https://github.com/michelhe/rustboyadvance-ng ; gdkGBA,
  https://github.com/gdkchan/gdkGBA ; shonumi GBE+ / *Edge of Emulation*,
  https://shonumi.github.io/ , https://github.com/shonumi/gbe-plus.
- **Finding:** RustBoyAdvance-NG and gdkGBA are language-choice / frontend data
  points. **shonumi's GBE+ and the "Edge of Emulation" series are the field's
  authority on GBA/GBC *peripheral* emulation** (Advance Movie Adapter, Battle Chip
  Gate, IR Adapter, tilt/gyro/solar sensors, Mobile Adapter GB, Play-Yan) — the
  only project treating those as first-class. The method: watch RCNT reads to
  intercept what the game expects, reverse-engineer the protocol from software
  behavior since no public hardware spec exists. Directly relevant if link-cable or
  GPIO peripheral emulation enters scope
  ([cartridge-saves-rtc-peripherals.md](cartridge-saves-rtc-peripherals.md)).

### Reference documentation status

- **Source:** GBATEK, https://www.gbadev.org/docs.php?showinfo=5 ; CoWBite;
  Tonc, https://gbadev.net/tonc/hardware.html.
- **Finding:** GBATEK is the de facto canonical hardware reference every project
  cites — but explicitly *not* infallible: the WAITCNT bit-15 read-only misstatement
  caused an identical bug across NO$GBA and other independently-written emulators,
  the field's standing example that GBATEK errata must be cross-checked against
  actual hardware tests (AGS, gba-suite, ARMWrestler, FuzzARM), not trusted blindly.
  CoWBite is a complementary secondary reference; Tonc is the standard *programming*
  tutorial (software-visible behavior), not a hardware-timing source.
- **Puck status:** the reason Puck anchors on two live oracles rather than GBATEK
  prose — see [test-roms-and-evidence.md](test-roms-and-evidence.md).

### The BIOS pre-flight profile (Puck-side)

- **Source:** implementation (`AgbBiosProfile.cs`); Cult-of-GBA/BIOS,
  https://github.com/Cult-of-GBA/BIOS.
- **Finding:** the core has no HLE SWI implementation — accuracy depends on running
  a real (or replacement) BIOS. The replacement `ReplacementBios` (open-source
  Cult-of-GBA MIT 16 KiB image) is legally clean for Tier-A only; a real dumped
  BIOS is supplied via `PUCK_GBA_BIOS` (never committed). Using the replacement for
  parity work once caused a session of phantom "cycle drift."
- **Puck status: BIOS SHA-1 classification landed this arc.** `AgbBiosProfile`
  classifies a loaded image by SHA-1 (retail vs replacement stub vs unknown), and
  co-sim diagnostics refuse a non-retail BIOS without `--allow-replacement-bios` —
  see [determinism-savestate-replay.md](determinism-savestate-replay.md).

---

**Synthesis.** The two accuracy leaders (NanoBoyAdvance, SkyEmu) anchor their
credibility on the same public, checkable artifact — AGS + the community timing
suites — rather than a self-reported score; that is the credible external
benchmark to co-simulate against, matching Puck's existing ares/mGBA posture. The
"Holy Grail Bugs" and WAITCNT stories are the strongest evidence in the survey
that GBATEK cannot be trusted uncritically and that independently-authored
emulators inherit identical documentation-sourced bugs — a standing argument for
validating against hardware-derived suites and multiple oracles, which Puck
already does.

**Dubious / marketing claims flagged (not laundered), across all three reviews:**
"ares has no GBA support" (stale/wrong — we run it as a live oracle); AGS
"first/only to pass" (stale — NBA, MiSTer, Nintendo's official emulator, SkyEmu,
Hades, GameBeanAdvance all pass; we're in a small club, not alone); cross-emulator
suite-score tables (non-comparable across emulators *and* suite versions — our
itemized scores are the trustworthy figure); the SkyEmu/Hades "2nd vs 3rd to pass
AGS" and "only released emulator with mid-scanline" self-claims (internally
inconsistent — use the mid-scanline game list as *candidate* proving ROMs, not
requirements); and NanoBoyAdvance issue #37's content (title-only, the body 404'd
— do not encode its exact prefetch penalty as ground truth).
