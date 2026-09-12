# Post Harness & Conformance Testing

To ensure that the Humble Gaming Brick maintains cycle-exact parity with physical hardware and across all supported console revisions, Puck includes a specialized verification engine: `Puck.HumbleGamingBrick.Post`.

---

## The Post Test Architecture

The Post system executes automated test corpora, compares frame hash signatures against known reference implementations (e.g. Mooneye, Gambatte), and analyzes execution ledgers:

```mermaid
flowchart LR
    Corpus[Test ROM Corpora<br/>Mooneye, Blargg, Gambatte] --> PostRunner[Post Harness (ProbeRunner)]
    PostRunner --> LaneCheck{Selected Lane}
    LaneCheck -->|gate| FastGate[Fast Signature Gate<br/>Exits on breakpoint / signature]
    LaneCheck -->|conformance| DeepGate[Full Conformance Matrix<br/>Frame-by-frame hash evaluation]
    FastGate --> Evaluator[Ledger Evaluator]
    DeepGate --> Evaluator
    Evaluator --> Verdict[Pass / Divergence / Unrunnable Verdict]
```

### Execution Lanes

Command line invocation:
```pwsh
# Fetch external test corpora
dotnet run --project src/Puck.HumbleGamingBrick.Post -c Release -- --fetch-corpora

# Run the fast pre-commit regression gate
dotnet run --project src/Puck.HumbleGamingBrick.Post -c Release -- --lane gate

# Run full multi-suite conformance
dotnet run --project src/Puck.HumbleGamingBrick.Post -c Release -- --lane conformance
```

| Lane | Target Scope | Stop Condition | Target Environment |
|---|---|---|---|
| **`gate`** | High-signal micro-ROMs covering CPU, interrupts, and timer glitches | Exits immediately when the ROM writes its pass signature to registers or memory | Every CI commit and `dotnet test` suite |
| **`conformance`** | Broad multi-thousand test suite across all hardware revisions | Executes specified frame counts, validating complete memory and framebuffer hashes | Pre-release verification and core refactoring |

---

## Test ROM Corpora

Puck's conformance harness evaluates against industry-standard hardware test suites:

### 1. Mooneye GB (Gekkio)
The definitive hardware test suite for the Sharp SM83 and DMG/CGB hardware:
- **`acceptance/`**: CPU instruction execution, DAA correction table, branch timings, and the hardware HALT bug.
- **`timer/`**: Sub-cycle `DIV` increments, `TIMA` reload delays, and write-during-reload cycle quirks.
- **`ppu/`**: STAT interrupt blocking, mode switching timings, and VRAM lockout edge cases.
- **`mbc/`**: Bank switching behaviors and RAM protection logic for MBC1, MBC2, and MBC5.

### 2. Blargg's Test Suites (Shay Green)
- **`cpu_instrs`**: Individual instruction sanity checks.
- **`instr_timing`**: Precise cycle counts for every SM83 opcode.
- **`mem_timing`**: Read/write cycle timing during PPU access conflicts.
- **`dmg_sound`**: APU length counter, volume envelope, and frequency sweep clocks.

---

## The Bess Save State Standard

Puck implements the **Bess (Best Effort Save State)** specification (`BessScope.cs`). Bess is an open, cross-emulator save state format designed to allow save states to be shared seamlessly between independent Game Boy emulators:

- **Chunked Metadata**: Stores CPU registers (`PC`, `SP`, `AF`, `BC`, `DE`, `HL`, `IME`), PPU state (`LY`, `LCDC`, `STAT`), timer latches, and active mapper banks in named binary footers.
- **Cross-Emulator Interoperability**: A game saved inside Puck's 3D arcade cabinet can be exported and resumed inside external emulators like BGB, SameBoy, or Gambatte.

---

## Hash Divergence Probing

When tracking down elusive emulation timing bugs, `HashDivergenceProbe` compares execution traces frame-by-frame:
1. **Trace State**: Computes rolling 64-bit hashes over CPU registers, memory bus accesses, and PPU scanlines.
2. **Binary Search**: When a divergence is detected between Puck and a reference trace, the probe executes a binary search over instruction cycles to identify the exact instruction and cycle where execution diverged.
3. **Ledger Evaluation**: Results are recorded in `LedgerEvaluator`, outputting structured JSON summaries suitable for automated regression analysis.
