using Puck.Snapshots;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// The per-tick hash-divergence localizer — the fine half of the two-stage story the determinism/savestate-replay
/// doctrine names: "hashing is the coarse detector, full-state diff the fine localizer, used in sequence." Two
/// independently-built machines are stepped in lockstep and, every frame (or every scanline with <c>--fine</c>),
/// snapshot-hashed with FNV-1a — the repo's standard fingerprint — and compared. The coarse hash is cheap enough to
/// run every tick; on the first mismatch the localizer switches to the fine tool, walking the snapshot's section
/// table (<see cref="AgbMachineSnapshot.Sections"/>) to name the first diverging component and byte offset, with a
/// short hex window of both sides — turning "somewhere diverged" into "the bus component, byte 32768 (EWRAM)".
/// </summary>
/// <remarks>
/// Self-check mode (no second ROM, no perturbation) boots two machines from the same ROM+BIOS and asserts they never
/// diverge — the core's own claim to determinism, the same claim <see cref="DeterminismStage"/> makes at
/// coarser (register+framebuffer) granularity. Supplying a second ROM path, or a perturbation frame, intentionally
/// diverges machine B — which exercises the localizer itself: it must name the right component at the right frame.
/// </remarks>
internal static class HashDivergenceProbe {
    private const int ScanlineCycles = 1232;
    private const int ScanlinesPerFrame = 228;

    // Sizes from AgbBus.State.cs's fixed save order (EWRAM, then IWRAM, then the I/O register backing) — used only
    // to annotate a "bus" section offset for a human-readable report; the localizer itself does not need to touch
    // AgbBus at all, since the section table already gives an exact byte range per component.
    private const int EwramSize = 0x40000;
    private const int IoSize = 0x400;
    private const int IwramSize = 0x8000;

    // Any offset inside the EWRAM sub-range of the "bus" section's data — mid-region, clear of anything a
    // synthetic/micro-ROM's own working set would touch. Used only by the deliberate-perturbation mode, to prove the
    // tool finds an injected divergence and names it correctly.
    private const int PerturbEwramOffset = 0x8000;

    /// <summary>
    /// Runs the lockstep self-check (or, with <paramref name="romBPath"/> / <paramref name="perturbAtFrame"/>, the
    /// deliberate-divergence proof). Prints progress and the divergence report to stdout — the tool discipline.
    /// </summary>
    /// <param name="romAPath">The ROM machine A always boots.</param>
    /// <param name="romBPath">When supplied, the ROM machine B boots instead of <paramref name="romAPath"/> — a
    /// deliberate way to diverge two machines for testing the localizer. <see langword="null"/> for the self-check
    /// (both machines boot the same ROM).</param>
    /// <param name="bios">The BIOS image both machines boot with.</param>
    /// <param name="frames">The number of frames to step.</param>
    /// <param name="fine">When <see langword="true"/>, hashes every scanline instead of only every frame boundary.</param>
    /// <param name="perturbAtFrame">When supplied, machine B has one EWRAM byte flipped (via a snapshot/restore poke —
    /// no bus cycle cost, so it never itself shifts the master clock) immediately before this frame runs — a
    /// self-test of the tool, not of the core.</param>
    /// <returns><c>0</c> when no divergence was found in the self-check, <c>1</c> when a divergence was found and
    /// localized, <c>2</c> when a ROM path was missing.</returns>
    public static int Run(string romAPath, string? romBPath, ReadOnlyMemory<byte> bios, int frames, bool fine, int? perturbAtFrame) {
        if (!File.Exists(path: romAPath)) {
            Console.WriteLine(value: $"  [SKIP] --hash-divergence: rom not found at {romAPath}");

            return 2;
        }

        var romA = File.ReadAllBytes(path: romAPath);
        byte[]? romB;

        if (romBPath is null) {
            romB = romA;
        } else if (File.Exists(path: romBPath)) {
            romB = File.ReadAllBytes(path: romBPath);
        } else {
            Console.WriteLine(value: $"  [SKIP] --hash-divergence: rom B not found at {romBPath}");

            return 2;
        }

        return Run(romA: romA, romALabel: Path.GetFileName(path: romAPath), romB: romB, romBLabel: ((romBPath is null) ? null : Path.GetFileName(path: romBPath)), bios: bios, frames: frames, fine: fine, perturbAtFrame: perturbAtFrame);
    }

    /// <summary>The in-memory counterpart of <see cref="Run(string, string?, ReadOnlyMemory{byte}, int, bool, int?)"/>,
    /// for callers (and the self-check-over-a-micro-ROM proof) that already hold ROM bytes rather than a disk path.</summary>
    public static int Run(byte[] romA, string romALabel, byte[] romB, string? romBLabel, ReadOnlyMemory<byte> bios, int frames, bool fine, int? perturbAtFrame) {
        using var hostA = PostMachine.Build(bios: bios, rom: romA);
        using var hostB = PostMachine.Build(bios: bios, rom: romB);
        var machineA = hostA.Machine;
        var machineB = hostB.Machine;

        var mode = ((romBLabel is not null)
            ? $"romA={romALabel} romB={romBLabel}"
            : ((perturbAtFrame is not null)
                ? $"rom={romALabel} (self-check + deliberate perturbation @frame {perturbAtFrame})"
                : $"rom={romALabel} (self-check)"));

        Console.WriteLine(value: $"== hash-divergence localizer: {mode}, {frames} frames{(fine ? ", --fine (per-scanline)" : "")} ==");

        for (var frame = 0; (frame < frames); ++frame) {
            if (fine) {
                for (var scanline = 0; (scanline < ScanlinesPerFrame); ++scanline) {
                    if ((perturbAtFrame == frame) && (scanline == 0)) {
                        Perturb(machine: machineB);
                    }

                    RunUntilCycle(machine: machineA, target: (machineA.Cycles + ScanlineCycles));
                    RunUntilCycle(machine: machineB, target: (machineB.Cycles + ScanlineCycles));

                    if (!TryCompare(machineA: machineA, machineB: machineB, frame: frame, scanline: scanline)) {
                        return 1;
                    }
                }
            } else {
                if (perturbAtFrame == frame) {
                    Perturb(machine: machineB);
                }

                _ = machineA.RunFrame();
                _ = machineB.RunFrame();

                if (!TryCompare(machineA: machineA, machineB: machineB, frame: frame, scanline: null)) {
                    return 1;
                }
            }
        }

        Console.WriteLine(value: $"== hash-divergence: NO divergence across {frames} frames ==");

        return 0;
    }

    // Snapshot-hashes both machines and, on a mismatch, prints the full localization report. Returns false (and has
    // already printed the report) on divergence, so the caller can stop the lockstep immediately.
    private static bool TryCompare(AdvancedGamingBrickMachine machineA, AdvancedGamingBrickMachine machineB, int frame, int? scanline) {
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

    /// <summary>
    /// The fine localizer: full-state diffs two snapshots and prints which component first differs, at what byte
    /// offset, with a short hex window of both sides. Shared by the CLI diagnostic above and by any POST stage that
    /// wants a precise failure detail instead of a bare "diverged" (see <see cref="DeterminismStage"/>).
    /// </summary>
    /// <param name="a">The first snapshot.</param>
    /// <param name="b">The second snapshot.</param>
    public static void PrintDivergenceReport(AgbMachineSnapshot a, AgbMachineSnapshot b) {
        Console.WriteLine(value: $"  {DescribeDivergence(a: a, b: b)}");

        var diff = SnapshotDivergence.FindFirstDifference(a: a.Data, b: b.Data, sections: a.Sections);

        if (diff is null) {
            return;
        }

        var (_, _, absoluteOffset) = diff.Value;

        Console.WriteLine(value: SnapshotDivergence.FormatHexWindow(label: "A", data: a.Data, offset: absoluteOffset));
        Console.WriteLine(value: SnapshotDivergence.FormatHexWindow(label: "B", data: b.Data, offset: absoluteOffset));
    }

    /// <summary>
    /// Describes the first byte-level difference between two snapshots as a one-line, component-localized detail —
    /// "component 'bus' (EWRAM), byte offset 32768 within component (absolute 32768)" rather than a bare "mismatch".
    /// Used both by the CLI report and by a stage's failure detail (a plain string, no console output).
    /// </summary>
    /// <param name="a">The first snapshot.</param>
    /// <param name="b">The second snapshot.</param>
    /// <returns>The one-line localization detail.</returns>
    public static string DescribeDivergence(AgbMachineSnapshot a, AgbMachineSnapshot b) {
        var diff = SnapshotDivergence.FindFirstDifference(a: a.Data, b: b.Data, sections: a.Sections);

        if (diff is null) {
            return ((a.Identity != b.Identity)
                ? $"snapshots hold no byte difference, but their machine identity differs (format version / BIOS / ROM) — {a.Identity} vs {b.Identity}"
                : $"snapshots hold no byte difference, but their captured instant differs — takenAt {a.TakenAt} vs {b.TakenAt}");
        }

        var (section, offsetInSection, absoluteOffset) = diff.Value;
        var annotation = ((section == "bus") ? $" ({DescribeBusSubRegion(offsetInSection: offsetInSection)})" : "");

        return $"component '{section}'{annotation}, byte offset {offsetInSection} within component (absolute {absoluteOffset})";
    }

    // The "bus" component's serialized layout (AgbBus.State.cs's fixed write order): EWRAM, then IWRAM, then the I/O
    // register backing, then a run of scalar latches (open bus, prefetch, wait-state table, ...). Annotation only —
    // never used to locate the difference, only to describe it.
    private static string DescribeBusSubRegion(int offsetInSection) {
        if (offsetInSection < EwramSize) {
            return "EWRAM";
        }

        if (offsetInSection < (EwramSize + IwramSize)) {
            return "IWRAM";
        }

        return ((offsetInSection < ((EwramSize + IwramSize) + IoSize))
            ? "I/O registers"
            : "bus latches (open-bus/prefetch/wait-state)");
    }
    // Corrupts one EWRAM byte in `machine` without spending any bus cycle: snapshot it, flip a byte inside the "bus"
    // section's EWRAM sub-range (offset 0 of that section, per AgbBus.State.cs), then restore the poked snapshot back
    // into the same machine. Restore repositions every component to exactly this instant, so the only observable
    // change is the one flipped byte — a surgical, deterministic divergence for testing the localizer itself.
    private static void Perturb(AdvancedGamingBrickMachine machine) {
        var snapshot = machine.Snapshot();
        var busSection = FindSectionByName(sections: snapshot.Sections, name: "bus");
        var absoluteOffset = (busSection.Offset + PerturbEwramOffset);
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
    private static void RunUntilCycle(AdvancedGamingBrickMachine machine, long target) {
        while (machine.Cycles < target) {
            machine.Step();
        }
    }
}
