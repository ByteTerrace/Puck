using Puck.Snapshots;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>
/// The per-tick hash-divergence localizer — the fine half of the two-stage determinism story ("hashing is the coarse
/// detector, full-state diff the fine localizer, used in sequence"), mirroring the GBA machine's probe. Two
/// independently-built machines are stepped in lockstep and, every frame (or every scanline with <c>--fine</c>),
/// snapshot-hashed with FNV-1a — the repo's standard fingerprint — and compared. The coarse hash is cheap enough to run
/// every tick; on the first mismatch the localizer switches to the fine tool, walking the snapshot's section table
/// (<see cref="MachineSnapshot.Sections"/>) to name the first diverging component and byte offset, with a short hex
/// window of both sides — turning "somewhere diverged" into "the SystemMemory component, byte 32776".
/// </summary>
/// <remarks>
/// Self-check mode (no second ROM, no perturbation) boots two machines from the same ROM and asserts they never
/// diverge — the core's own claim to determinism, the same claim <see cref="DeterminismStage"/> makes at coarser
/// (whole-snapshot, frame-boundary) granularity. Supplying a second ROM path, or a perturbation frame, intentionally
/// diverges machine B — which exercises the localizer itself: it must name the right component at the right frame.
/// </remarks>
internal static class HashDivergenceProbe {
    // One dot-accurate frame is 154 scanlines of 456 CPU T-cycles at normal speed (PostMachine.TCyclesPerFrame = their
    // product). The fine mode compares at each scanline boundary; the coarse mode at each frame boundary.
    private const int ScanlineCycles = 456;
    private const int ScanlinesPerFrame = 154;

    // SystemMemory's fixed save order (SystemMemory.SaveState): the two 4-byte bank selects, then video RAM
    // (2 * 0x2000), then work RAM (8 * 0x1000), then OAM, then high RAM. The deliberate perturbation targets a work-RAM
    // byte in a high bank the synthetic ROM's WRAM-fill loop (which touches only bank 0, 0xC000-0xC0FF) never rewrites,
    // so the injected divergence persists to the next compare instead of reconverging. Offset within the SystemMemory
    // section: 8 (bank selects) + 0x4000 (video RAM) + 0x4000 (into work-RAM bank 4). Annotation only for the report.
    private const string PerturbSectionName = "SystemMemory";
    private const int PerturbSectionOffset = ((8 + 0x4000) + 0x4000);

    /// <summary>
    /// Runs the lockstep self-check (or, with <paramref name="romBPath"/> / <paramref name="perturbAtFrame"/>, the
    /// deliberate-divergence proof). Prints progress and the divergence report to stdout — the tool discipline. A
    /// <see langword="null"/> <paramref name="romAPath"/> falls back to the built-in synthetic cartridge, so the
    /// self-check runs anywhere with no ROM corpus.
    /// </summary>
    /// <param name="romAPath">The ROM machine A always boots, or <see langword="null"/> for the synthetic cartridge.</param>
    /// <param name="romBPath">When supplied, the ROM machine B boots instead of <paramref name="romAPath"/> — a
    /// deliberate way to diverge two machines for testing the localizer. <see langword="null"/> for the self-check
    /// (both machines boot the same ROM).</param>
    /// <param name="frames">The number of frames to step.</param>
    /// <param name="fine">When <see langword="true"/>, hashes every scanline instead of only every frame boundary.</param>
    /// <param name="perturbAtFrame">When supplied, machine B has one work-RAM byte flipped (via a snapshot/restore poke —
    /// no bus cycle cost, so it never itself shifts the master clock) immediately before this frame runs — a self-test
    /// of the tool, not of the core.</param>
    /// <returns><c>0</c> when no divergence was found in the self-check, <c>1</c> when a divergence was found and
    /// localized, <c>2</c> when a ROM path was missing.</returns>
    public static int Run(string? romAPath, string? romBPath, int frames, bool fine, int? perturbAtFrame) {
        byte[] romA;
        string romALabel;

        if (romAPath is null) {
            romA = SyntheticRom.Create();
            romALabel = "synthetic";
        } else if (File.Exists(path: romAPath)) {
            romA = File.ReadAllBytes(path: romAPath);
            romALabel = Path.GetFileName(path: romAPath);
        } else {
            Console.WriteLine(value: $"  [SKIP] --hash-divergence: rom not found at {romAPath}");

            return 2;
        }

        byte[]? romB;
        string? romBLabel;

        if (romBPath is null) {
            romB = null;
            romBLabel = null;
        } else if (File.Exists(path: romBPath)) {
            romB = File.ReadAllBytes(path: romBPath);
            romBLabel = Path.GetFileName(path: romBPath);
        } else {
            Console.WriteLine(value: $"  [SKIP] --hash-divergence: rom B not found at {romBPath}");

            return 2;
        }

        return Run(romA: romA, romALabel: romALabel, romB: romB, romBLabel: romBLabel, frames: frames, fine: fine, perturbAtFrame: perturbAtFrame);
    }

    /// <summary>The in-memory counterpart of <see cref="Run(string, string, int, bool, int?)"/>, for callers that
    /// already hold ROM bytes rather than a disk path. Both machines emulate the model the ROM A header asks for, so a
    /// Color ROM self-checks in color.</summary>
    /// <param name="romA">The ROM machine A boots (and machine B too, when <paramref name="romB"/> is null).</param>
    /// <param name="romALabel">A short label for machine A's ROM, for the report.</param>
    /// <param name="romB">The ROM machine B boots instead, or <see langword="null"/> to boot the same as A.</param>
    /// <param name="romBLabel">A short label for machine B's ROM, or <see langword="null"/> when it matches A.</param>
    /// <param name="frames">The number of frames to step.</param>
    /// <param name="fine">When <see langword="true"/>, hashes every scanline instead of only every frame boundary.</param>
    /// <param name="perturbAtFrame">When supplied, machine B has one work-RAM byte flipped immediately before that frame.</param>
    /// <returns><c>0</c> for no divergence, <c>1</c> for a localized divergence.</returns>
    public static int Run(byte[] romA, string romALabel, byte[]? romB, string? romBLabel, int frames, bool fine, int? perturbAtFrame) {
        var model = ModelFromHeader(rom: romA);

        using var hostA = PostMachine.Build(model: model, rom: romA);
        using var hostB = PostMachine.Build(model: model, rom: (romB ?? romA));
        var machineA = hostA.Machine;
        var machineB = hostB.Machine;

        var mode = ((romBLabel is not null)
            ? $"romA={romALabel} romB={romBLabel}"
            : ((perturbAtFrame is not null)
                ? $"rom={romALabel} (self-check + deliberate perturbation @frame {perturbAtFrame})"
                : $"rom={romALabel} (self-check)"));

        Console.WriteLine(value: $"== hash-divergence localizer: {mode} ({model}), {frames} frames{(fine ? ", --fine (per-scanline)" : "")} ==");

        // Drive both machines against one shared, absolute cumulative cycle target rather than each machine's private
        // pacing accumulator, so a perturbation's snapshot/restore (which reanchors that accumulator) can never desync
        // the two clocks — both are always told to reach the identical instant.
        var target = 0L;

        for (var frame = 0; (frame < frames); ++frame) {
            if (fine) {
                for (var scanline = 0; (scanline < ScanlinesPerFrame); ++scanline) {
                    if ((perturbAtFrame == frame) && (scanline == 0)) {
                        Perturb(machine: machineB);
                    }

                    target += ScanlineCycles;
                    RunTo(machine: machineA, target: target);
                    RunTo(machine: machineB, target: target);

                    if (!TryCompare(machineA: machineA, machineB: machineB, frame: frame, scanline: scanline)) {
                        return 1;
                    }
                }
            } else {
                if (perturbAtFrame == frame) {
                    Perturb(machine: machineB);
                }

                target += (ScanlineCycles * ScanlinesPerFrame);
                RunTo(machine: machineA, target: target);
                RunTo(machine: machineB, target: target);

                if (!TryCompare(machineA: machineA, machineB: machineB, frame: frame, scanline: null)) {
                    return 1;
                }
            }
        }

        Console.WriteLine(value: $"== hash-divergence: NO divergence across {frames} frames ==");

        return 0;
    }

    /// <summary>
    /// Describes the first byte-level difference between two snapshots as a one-line, component-localized detail —
    /// "component 'SystemMemory', byte offset 32776 within component (absolute 32784)" rather than a bare "mismatch".
    /// Shared by the CLI report and by a POST stage's failure detail (a plain string, no console output).
    /// </summary>
    /// <param name="a">The first snapshot.</param>
    /// <param name="b">The second snapshot.</param>
    /// <returns>The one-line localization detail.</returns>
    public static string DescribeDivergence(MachineSnapshot a, MachineSnapshot b) {
        var diff = SnapshotDivergence.FindFirstDifference(a: a.Data, b: b.Data, sections: a.Sections);

        if (diff is null) {
            return ((a.Identity != b.Identity)
                ? $"snapshots hold no byte difference, but their machine identity differs (format version / model / ROM) — {a.Identity} vs {b.Identity}"
                : $"snapshots hold no byte difference, but their captured instant differs — takenAt {a.TakenAt} vs {b.TakenAt}");
        }

        var (section, offsetInSection, absoluteOffset) = diff.Value;

        return $"component '{section}', byte offset {offsetInSection} within component (absolute {absoluteOffset})";
    }

    // Snapshot-hashes both machines and, on a mismatch, prints the full localization report. Returns false (and has
    // already printed the report) on divergence, so the caller can stop the lockstep immediately.
    private static bool TryCompare(Machine machineA, Machine machineB, int frame, int? scanline) {
        var snapshotA = machineA.Snapshot();
        var snapshotB = machineB.Snapshot();
        var hashA = StateFingerprint.Compute(data: snapshotA.Data);
        var hashB = StateFingerprint.Compute(data: snapshotB.Data);

        if (hashA == hashB) {
            return true;
        }

        var where = ((scanline is not null) ? $"frame {frame} scanline {scanline}" : $"frame {frame}");

        Console.WriteLine(value: $"== HASH DIVERGENCE at {where}: A=0x{hashA:X16}  B=0x{hashB:X16} ==");
        PrintDivergenceReport(a: snapshotA, b: snapshotB);

        return false;
    }

    // The fine localizer's console form: prints the one-line component/offset detail, then a short hex window of both
    // sides around the first differing byte.
    private static void PrintDivergenceReport(MachineSnapshot a, MachineSnapshot b) {
        Console.WriteLine(value: $"  {DescribeDivergence(a: a, b: b)}");

        var diff = SnapshotDivergence.FindFirstDifference(a: a.Data, b: b.Data, sections: a.Sections);

        if (diff is null) {
            return;
        }

        var (_, _, absoluteOffset) = diff.Value;

        Console.WriteLine(value: SnapshotDivergence.FormatHexWindow(label: "A", data: a.Data, offset: absoluteOffset));
        Console.WriteLine(value: SnapshotDivergence.FormatHexWindow(label: "B", data: b.Data, offset: absoluteOffset));
    }

    // Corrupts one work-RAM byte in `machine` without spending any bus cycle: snapshot it, flip a byte inside the
    // SystemMemory section's high work-RAM banks (which the synthetic ROM never touches), then restore the poked
    // snapshot into the same machine. Restore repositions every component to exactly this instant, so the only
    // observable change is the one flipped byte — a surgical, deterministic divergence for testing the localizer.
    private static void Perturb(Machine machine) {
        var snapshot = machine.Snapshot();
        var section = FindSectionByName(sections: snapshot.Sections, name: PerturbSectionName);
        var absoluteOffset = (section.Offset + PerturbSectionOffset);
        var current = snapshot.Data[absoluteOffset];
        var poked = snapshot.WithPokedByte(offset: absoluteOffset, value: (byte)(current ^ 0xFF));

        machine.Restore(snapshot: poked);
    }
    private static SnapshotSection FindSectionByName(IReadOnlyList<SnapshotSection> sections, string name) {
        foreach (var section in sections) {
            if (section.Name == name) {
                return section;
            }
        }

        throw new InvalidOperationException(message: $"snapshot has no '{name}' section");
    }

    // Advances a machine until its master clock reaches an absolute cumulative cycle target (a no-op when already past
    // it). It steps atomic instructions (or bare T-cycles with no bus master) rather than Machine.Run, because Run's
    // pacing accumulator is reanchored by a snapshot restore — so an absolute target reached through Run would desync a
    // perturbed machine's clock by the last instruction's overshoot. Stepping to the absolute cycle keeps two machines
    // bit-for-bit cycle-aligned even after one is perturbed via snapshot/restore.
    private static void RunTo(Machine machine, long target) {
        if (machine.HasBusMaster) {
            while (machine.Clock.CycleCount < (ulong)target) {
                machine.StepInstruction();
            }
        } else {
            while (machine.Clock.CycleCount < (ulong)target) {
                machine.StepTick();
            }
        }
    }
    private static ConsoleModel ModelFromHeader(byte[] rom) =>
        (((rom.Length > 0x0143) && (0 != (rom[0x0143] & 0x80))) ? ConsoleModel.Cgb : ConsoleModel.Dmg);
}
