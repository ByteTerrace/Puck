---
name: rom-forge
description: Working on the ROM forge — src/Puck.Demo/Forge/ (the SM83 game framework in Framework/, Brickfall/, the SDF→brick bake pipeline in Bake/, Sm83Emitter, RomForge, AvatarForge, HgbImage/HgbCartridge). Use whenever forging or hand-authoring SM83 game ROMs, touching the game framework (kernel, WRAM map, saves, PRNG, input, text, OAM), the bake pipeline (palette fit, tiles, PBAK blobs), or the cart/save/title-art seams. Carries the settled cartridge, framework, and bake contracts so they aren't re-derived or accidentally forked.
---

# The ROM forge: SDF worlds and hand-authored games onto real brick hardware

Factual and procedural only: settled contracts and how to verify. The user's
current instruction outranks this file — if it argues against a demanded
change, it is stale; update it in the same change. The forge lives under the
GREENFIELD demo: its verification story is the self-verify batteries below,
never a Post stage or gate.

## The map (src/Puck.Demo/Forge/)

- `Framework/` — the SM83 game framework, 19 modules in
  `Puck.Demo.Forge.Framework`, deliberately dependent only on `Sm83Emitter` +
  the `HgbImage` encoders (lift-ready for
  `experimental/Puck.HumbleGamingBrickRom` later): `FrameworkCartridge`,
  `FrameworkKernel`, `FrameworkMemoryMap` (the WRAM source of truth),
  `GameFramework` (the facade — wires every module, `BuildRom` assembles),
  `RomDataBuilder`, `Hw`, `InputModule` (edges + attract-script override),
  `BgModule` (queue + LCD-off paints), `OamManager` (shadow OAM +
  metasprites), `PrngModule`, `SaveModule`, `TextModule` (39-glyph font),
  `GameStateMachine` (pending-state dispatch),
  the LINKER LAYER: `PbakBundle` (the PBAK wire-form READER — raw bytes,
  no bake types), `AssetLinker` (on the facade as `GameFramework.Assets`:
  allocates the 256-tile bank + 8/8 palette slots in declaration order,
  relocates PBAK sections — map cells/OAM tiles rebased, palette bits
  shifted — seals the composed `tile-bank`/`bg-palette-table`/
  `obj-palette-table` blocks the boot spec consumes), `GameManifest`
  (declarative: tiles/font/palettes/screens/tables/records/texts/scripts/
  sprite art → `Link(framework)` → `LinkedManifest` name lookups;
  `FontTileBase` is known at declare time, before the framework ctor). Sprite
  sets link as relocated `(dy,dx,tile,attr)` frame rows + a 4-byte-stride
  runtime frame table (addr lo, addr hi, entry count, 0). A game's identity —
  rules, layouts, decks, art — is manifest DATA the card games SHARE, never
  copy. Full consumption pattern: Framework/README.md's linker section.
  And SOUND (landed, the resolved minimal-set fork): `ISoundDriver`
  (boot/tick/effect-by-id/`EmitLibrary` hooks), `NoOpSoundDriver`, the real
  `ApuSoundDriver` (three sequencer voices — pulse-1 SFX, noise SFX, pulse-2
  music loop; driver WRAM 0xC0A8..0xC0B2, `Scratch` now starts 0xC0B4;
  `Bind(linked)` resolves its streams after `Link`), and the `SoundTables`
  catalog (deal/flip/shuffle/win + cursor/thud/sweep/over effects,
  `MusicLoop`/`MusicStop` ids) whose `DefineIn(manifest)` declares every
  stream as an ordinary manifest table — the REUSABLE surface games trigger
  via `Sound.EmitEffect`. Host speakers live in `src/Puck.Demo/Audio/`
  (per-cabinet OS output streams draining the emulator's `IAudioSink`); all
  five game forges write an asserted `<out>.audio.wav` proof.
- `Cards/` — the shared card-game substrate BOTH card games consume (never
  copy): `CardDeck` (id = suit×13 + rank−1; `EmitInitDeck` +
  `EmitShuffleSubroutine` = in-place Fisher–Yates over the framework PRNG;
  `ShuffleOracle`/`NextState`/`StepBack` — 5⁻¹ mod 2¹⁶ = 52429 — give the
  bit-exact C# oracle and seed inversion), `CardTables` (52 4-byte card
  records; the 42-tile composed-face set — 52 literal 2×2 faces exceed the
  256-tile bank, so in-play faces compose from rank/suit corner tiles, a
  NARRATED budget trade), `CardArtBake` (budget-guarded felt/emblem/cursor
  bakes + the SDF scene vocabulary `BuildFeltScene`/`AddSuitSymbol`/
  `AddCardShape`), `CardMenu`, `CardInitialsPad`, `CardUndo` (fixed WRAM
  ring), `CardSfx` (ids ALIAS the `SoundTables` catalog). Cursor navigation
  = a manifest position-record table the verifier BFS-routes over the same
  C# builder, so game and oracle can't drift.
- `Brickfall/`, `Volley/`, `Chroma/`, `Solitaire/`, `Poker/` — the five
  five-star games ON the framework; every `<Game>Rom.Build()/Verify()`
  facade is preserved (zero call-site churn). Each is the same trio +
  manifest shape: a `<Game>Protocol` (state ids + game WRAM at 0xC200+),
  `<Game>Tables` (identity as DATA — pure providers the game's
  `BuildManifest` declares), `<Game>Game` (manifest + states + SM83
  emission), `<Game>Verify` (the battery), `<Game>TitleBake`/`<Game>Bake`
  (the SDF art). Seven states each (Title / Attract / HighScores / Play /
  Pause / GameOver / ScoreEntry); attract = scripted play that never writes
  SRAM; all five trigger the shared `SoundTables` catalog through
  `ApuSoundDriver`. Brickfall: piece + preview are 8 hardware sprites, locked
  cells ride the BG queue, a line clear is an LCD-off repaint. Volley: the
  original court physics verbatim, a match to 7 (+1 BCD per rally return,
  +100 per point), serve directions from the framework PRNG. Chroma: the
  original 6×12 drip/swap/cascade well verbatim (+1 BCD per cleared block),
  drip column/colour from the framework PRNG, and the well repaints through a
  per-frame grid-vs-screen DIFF over the BG queue (no LCD-off flash
  mid-cascade). Solitaire: draw-1 Klondike, combined stock+waste with a
  `WastePos` split (recycle = split reset, replays the same pass order),
  exact-snapshot undo, streak/best-streak battery save; board changes are
  LCD-off repaints spanning ~5–6 frames. Poker: five-card draw, player + 3
  AI whose personalities are data records; the decision seam
  (`m_subDecide`: DecisionSeat/Strength/Facing/Raises → DecisionAction, one
  PRNG draw per decision, table-side legality downgrades) is the LANDING
  POINT for link-fed multiplayer; the evaluator (packed shape byte → 64-entry
  category table + strength bases) is mirrored byte-for-byte by a C# oracle.
  Overworld cart types 5 / 4 / 6 / 7 / 8; battery saves at
  `%LOCALAPPDATA%\Puck\Demo\{brickfall,volley,chroma,solitaire,poker}.sav`
  (`PrepareDefaultSavePath`).
- `Bake/` — the SDF→brick bake pipeline (below).
- Top level: `Sm83Emitter` (the machine-code emitter everything shares),
  `RomForge` (the `--forge*` tool-mode entry points), `AvatarForge`,
  `HgbImage` (pure-C# RGBA8 → 2bpp/RGB555 encoders — every byte layout the
  INVERSE of the emulator's `Ppu.cs` decode; no external image library, on
  purpose), `HgbCartridge` (the overworld cart), `CameraRom`, `WorldLensRom`.
- The legacy `ArcadeKernel`/`ArcadeCartridge`/`ArcadeArt` machinery is
  DELETED (retired with the Volley/Chroma re-forge; git history has it).
  Every game goes through the framework.

## The framework cartridge (FrameworkCartridge)

- 32 KiB **MBC1+RAM+BATTERY**: header 0x0143=0xC0 (Color REQUIRED),
  0x0147=0x03, 0x0148=0x00, 0x0149=0x02 (8 KiB SRAM at 0xA000). Both 16 KiB
  banks are visible without a single bank-switch write (MBC1's primary bank
  resets to 1): code window 0x0150..0x3FFF (16,048 B), data window
  0x4000..0x7FFF (16,384 B) — `Build` throws past either.
- Vectors: 0x0040 = `jp 0x0153` (the VBlank handler address is FIXED by the
  prologue convention); the other four vectors are bare `reti`. The routine
  MUST open with the 3-byte `jp boot` at 0x0150 — `Build` rejects it
  otherwise.
- Puck's `--rom` path runs no boot ROM (A = 0x11 seeded at the post-boot
  handoff), so logo/checksums aren't needed to boot here; they are written
  anyway so the `.gbc` is valid on real hardware.

## Kernel + WRAM convention (FrameworkMemoryMap.cs is the source of truth)

Interrupt-driven `halt` main loop. The VBlank handler: push → `call 0xFF80`
(the 10-byte OAM-DMA trampoline copied to HRAM at boot) → drain the 24-entry
BG write queue → FrameCounter16++ → `reti`.

- 0xC000–0xC0FF is framework state: frame counter 0xC000/01, input pipeline
  0xC003–06 (held/pressed/previous/raw), PRNG 0xC007/08, state machine
  0xC009/0A, BG queue 0xC00B+ (count + 24 × 3-byte entries; a push past
  capacity is dropped), attract script 0xC054–59, save mirror 0xC060 (≤72 B),
  scratch 0xC0A8..0xC0FF.
- 0xC100 = the 160-byte shadow OAM page the HRAM trampoline DMA-copies every
  VBlank. **0xC200+ (`GameRam`) is game-owned — the framework never touches
  that page or above.** SP grows down from 0xFFFE.

## Doctrine: PRNG, saves, title art

- **PRNG seed = input entropy, nothing else.** 16-bit LCG ×5+1 (the high byte
  is the output); seed = FrameCounter16 XOR 0xA5C3, sampled at the
  title-screen START edge (`EmitSeedFromFrameCounter` at the moment of
  commitment). No RNG hardware, no wall clock: the same press frame is a
  bit-identical game — verified across machines.
- **NEVER trust SRAM.** Save block: `magic('P','F') | version | payload |
  sum16-of-payload LE`; the game only ever reads/writes the WRAM mirror at
  0xC060. Any magic/version/checksum mismatch loads ROM defaults — a fresh
  cartridge, corruption, and a version bump all land on defaults (bump the
  version byte to orphan old saves on a layout change). SRAM enable
  (0x0A → 0x0000)/disable only inside the load/store subroutines; attract
  never writes SRAM.
- **Title art installs ONLY through `<Game>Tables.SetTitleArt(art:
  PbakBackground?)`** — a PARSED PBAK background, which the game's manifest
  links as the art-backed title screen. The menu-text contract is the
  MANIFEST'S overlay contract (`ScreenText` overlays swap cells into the font
  AND zero their attributes on art-backed screens — art can never make the
  menu unreadable); the linker owns all relocation (tiles after the font,
  palettes into slots 1..7 — slot 0 stays gameplay's because the gameplay
  palette is declared first). Each game's `<Game>TitleBake` (the SDF emblem:
  ≤120-tile budget, ≤7 palettes) round-trips the bake through its own wire
  form (`ToBlob` → `PbakBundle.Parse`) and is the DEFAULT; the hand-authored
  banner is the narrated no-GPU fallback, and the verify battery runs OUTSIDE
  the GPU host so a failure is never masked by that fallback.

## The bake pipeline (Forge/Bake/)

- `BakePipeline` is split: GPU `Rasterize` (one view at a time) + pure-CPU
  `RunCpu` (grade → reduce → palette-fit → tile-assemble → preview compose) —
  the split lets the live creator preview rasterize on the render thread and
  quantize on a worker.
- `BakeStyle` is the per-cart style knob AS DATA: `classic` (no dither,
  contrast 1.15, outlines, supersample 2) and `bold` (Bayer4 ordered dither,
  contrast 1.3, saturation boost, supersample 4). Unknown names resolve to
  classic with a diagnostic.
- CGB palette assignment happens entirely in ROUNDED RGB555: per-tile
  median-cut ≤4 → sorted-tuple dedupe → ≤48 pre-merge guard → greedy pairwise
  merge to ≤8 (pooled median-cut candidates, lowest-index ties) → exactly 2
  Lloyd rounds. Sprites fit across ALL frames JOINTLY — no palette flicker.
  DMG: luma ramp, colour 0 transparent (3 usable shades = a reported
  diagnostic).
- `TileAssembler`: 2bpp dedupe including CGB X/Y-flip matching; DMG
  backgrounds get NO flip dedupe (no attribute map); the OAM per-scanline
  worst case is reported.
- **Bake determinism is asserted** (`--forge-bake` output is byte-identical
  across runs): keep every stage free of wall-clock, iteration-order, and
  float-ambient nondeterminism — the live preview debounces by PRODUCED
  FRAMES, never time.
- **Diagnostics are the product**: budget pressure/loss is REPORTED (console
  lines + preview overlays), never silently fixed. `BakeCalibration`
  (`--forge-bake-calibration <dir>`) is a report, never a failing gate.
- `BakedAssetBundle.ToBlob()` = the **PBAK** chunked blob (little-endian,
  version 1); chunk order is FIXED — the background's (TILE, MAPX, ATTR,
  PALB, DMGP) then each sprite set's (TILE, PALO, DMGP, META, ANIM) — and
  every payload derives from the bundle alone, so the same bundle always
  serializes identically. The framework READS this wire form natively
  (`Framework/PbakBundle.Parse` + `AssetLinker`); `--forge-avatar` writes
  `<out>.bake.bin` and PROVES it consumable by parsing + linking it back
  (the `PBAK round-trip linked` stderr line).
- `AvatarForge` runs ON the pipeline (12 views = 4 facings × 3 poses;
  timeline frames from a creation document become the walk poses, procedural
  sway/bob is the fallback), but the public `AvatarSheet` record is a
  COMPATIBILITY SEAM — `HgbCartridge.BuildOverworld` and
  VerifyOverworld/VerifyBoot consume it; change its shape only with them in
  the same change.

## Verify on a real machine

Every game/cart self-verifies by driving a REAL Humble machine and asserting
observable WRAM/framebuffer behavior BEFORE writing bytes — that discipline is
the forge's whole verification story. **Every verify Driver MUST call the
shared `VerifyMachineSettle.SettleOutOfOamDma` after stepping frames** (all
five game Drivers + `VerifyOverworld` do): a fixed-size `Run` can phase-lock
its boundary inside the VBlank handler's OAM DMA, where the emulated bus
conflict returns the transfer's in-flight bytes — a battery that skips the
settle passes or fails by code-layout luck (once misdiagnosed as a ROM wild
jump; the ROM was correct, the READS were gated). `BrickfallVerify` (runs
inside `--forge-brickfall`) is the model battery: boot→title, attract in/out,
seed entropy + same-press-frame replay determinism, gameplay, forced line
clear, pause freeze, score entry, SRAM round-trip validated by an INDEPENDENT
C# checksum, top-slot insertion, corruption → defaults. `VolleyVerify` and
`ChromaVerify` hold the same bar (their gameplay legs: chase-the-ball rally
scoring + dodge-to-concede; staged three-run clears + a matchless
checkerboard top-out). `SolitaireVerify` and `PokerVerify` extend it with
C# oracles: the deal is predicted from the INVERTED post-deal PRNG state (51
LCG steps back to the seed), Poker's evaluator and AI personalities are
mirrored byte-for-byte, chips are re-derived and conservation-checked. All
five forges also write an `<out>.emulated.png` boot proof and an asserted
`<out>.audio.wav` sound proof (card games add a dealt-board `.play.png`).
Verifier timing realities: press 8 frames / release 6 (edge-triggered, a long
hold acts once); LCD-off board repaints span multiple frames, so settle a few
frames after state flips before pressing. The full demo-side command canon
lives in the `verifying-puck-changes` skill.

## Composing under the analyzer ceilings

CA1506/CA1502 ceilings (`CodeMetricsConfig.txt`) actively guard
`OverworldRenderNode`, `EnsureResources`, `Program`'s Main, and the command
modules' `GetCommands`. Wiring a new forge/creator subsystem into the
overworld: compose it INSIDE `OverworldFrameSource` behind primitive-typed
forwarder members, extract helpers, split registration iterators into
sub-iterators — do NOT raise the ceilings. House style throughout: named
arguments, `m_` fields, parenthesized expressions, XML docs on publics.

## Arc-3 additions (settled)

- **Walker movement modes**: `MovementMode { FourWay, EightWay, Hex }`
  (`Forge/MovementModule.cs` + `HgbCartridge` emission) — FourWay default is
  byte-identical to the pre-mode walker; Hex = pointy-top (L/R pure ±2px,
  diagonals ±1,±2 carry Y, pure U/D is a deliberate NO-OP, diagonals FACE UP
  because the 1:2 step's vertical magnitude dominates the facing rule).
  `VerifyOverworld` is MODE-AWARE: hex proves its vertical axis through the
  diagonals and asserts the vertical-alone no-op. CLI: `--forge-avatar-movement-mode
  <four|eight|hex>` (string→enum mapping lives in `RomForge.RunAvatarAsync`'s
  string overload — never in Main).
- **The forge CLI surface lives in `Forge/ForgeCliSeams.cs`** (options as
  static properties + one `TryRunAsync` dispatch) — Main is at its CA1506+CA1505
  ceilings; add new tool options THERE, never in `Program.cs`.
- **Serial plumbing**: `Framework/LinkModule.cs` (stateless SB/SC helpers:
  start-internal-send / arm-external-receive with optional SB staging — one
  transfer is inherently full-duplex / bounded poll with a timeout label /
  read byte) + `LinkModuleVerify` (the SerialLinkSession loopback proof). No
  game consumes it yet; link-fed Poker is the follow-on arc (full design in
  the arc plan).
- **Flagship content determinism**: `--forge-flagships` asserts the three
  committed `docs/examples/creations/*.creation.json` regenerate byte-identical
  from their recipes (per-character classes, e.g. `FlagshipLanternFish`);
  `PUCK_FLAGSHIPS_REGENERATE=1` is the sanctioned content-update mode when the
  creation SCHEMA evolves (new nullable members change every recipe's canonical
  bytes — regenerate, review the diff, commit).
