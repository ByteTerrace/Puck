using System.Runtime.InteropServices;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Verifies the whole-machine savestate (Snapshot/Restore) round-trip: boot a ROM, run past its setup, then prove that
/// restoring a snapshot reproduces the machine bit-for-bit. Three independent checks per ROM, each fresh-booted so it
/// starts from a deterministic state:
/// <list type="number">
/// <item>a frame-boundary snapshot: record the framebuffer + register fingerprint of the next K frames, restore, re-run
/// K frames, and assert the two recordings are identical;</item>
/// <item>a mid-frame snapshot taken at a mid-scanline, between two instructions — the same record/restore/re-run
/// comparison, which only passes if the partial-frame PPU state (scanline position, affine references, and the
/// partially-rendered framebuffer) rode along in the snapshot;</item>
/// <item>a double-restore invariant: restoring a snapshot and immediately re-snapshotting reproduces the original
/// image byte-for-byte, and doing it twice yields the same image — snapshot∘restore is the identity on the image.</item>
/// </list>
/// The core is fully deterministic, so any mismatch is a genuine hole in the state coverage, not noise.
/// </summary>
internal static class StateRoundTripProbe {
    private const ulong FnvOffsetBasis = 0xCBF29CE484222325ul;
    private const ulong FnvPrime = 0x100000001B3ul;
    private const int RecordFrames = 6;
    private const int WarmupFrames = 8;

    /// <summary>Runs the three round-trip checks against one ROM image.</summary>
    /// <param name="rom">The cartridge ROM bytes.</param>
    /// <param name="label">A display label for the ROM.</param>
    /// <param name="bios">The BIOS image to boot with.</param>
    /// <returns><see langword="true"/> when every check passed, paired with a one-line detail.</returns>
    public static (bool Pass, string Detail) Run(byte[] rom, string label, ReadOnlyMemory<byte> bios) {
        var (frameOk, frameDetail, size) = FrameBoundaryCheck(rom: rom, bios: bios);
        var (midOk, midDetail) = MidFrameCheck(rom: rom, bios: bios);
        var (doubleOk, doubleDetail) = DoubleRestoreCheck(rom: rom, bios: bios);

        var pass = (frameOk && midOk && doubleOk);
        var status = (pass ? "PASS" : "FAIL");

        Console.WriteLine(value: $"  [{status}] {label}  (image {size} bytes)");
        Console.WriteLine(value: $"           frame-boundary: {frameDetail}");
        Console.WriteLine(value: $"           mid-frame:      {midDetail}");
        Console.WriteLine(value: $"           double-restore: {doubleDetail}");

        return (pass, $"{label}: {status}");
    }

    // Frame-boundary snapshot: record K frames, restore, re-run K frames, compare.
    private static (bool Ok, string Detail, int Size) FrameBoundaryCheck(byte[] rom, ReadOnlyMemory<byte> bios) {
        using var host = PostMachine.Build(bios: bios, rom: rom);
        var machine = host.Machine;

        RunFrames(machine: machine, frames: WarmupFrames);

        var snapshot = machine.Snapshot();
        var baseline = RecordFramesInto(machine: machine, frames: RecordFrames);

        machine.Restore(snapshot: snapshot);

        var replay = RecordFramesInto(machine: machine, frames: RecordFrames);

        var (ok, detail) = Compare(baseline: baseline, replay: replay);

        return (ok, $"{detail} (snapshot at cycle {snapshot.TakenAt})", snapshot.Size);
    }

    // Mid-frame snapshot: advance to a mid-scanline point at an instruction boundary, then the same compare.
    private static (bool Ok, string Detail) MidFrameCheck(byte[] rom, ReadOnlyMemory<byte> bios) {
        using var host = PostMachine.Build(bios: bios, rom: rom);
        var machine = host.Machine;

        RunFrames(machine: machine, frames: WarmupFrames);

        // Step whole instructions until the master clock sits roughly half-way through a frame — a mid-scanline
        // instant, and (because Step is instruction-atomic) an instruction boundary. This is exactly the snapshot
        // point that fails if the partial-frame PPU/scheduler state is not captured.
        var target = (machine.Cycles + (AdvancedGamingBrickMachine.CyclesPerFrame / 2));

        while (machine.Cycles < target) {
            machine.Step();
        }

        var atCycle = machine.Cycles;
        var intoFrame = (atCycle % AdvancedGamingBrickMachine.CyclesPerFrame);
        var snapshot = machine.Snapshot();
        var baseline = RecordFramesInto(machine: machine, frames: RecordFrames);

        machine.Restore(snapshot: snapshot);

        var replay = RecordFramesInto(machine: machine, frames: RecordFrames);

        var (ok, detail) = Compare(baseline: baseline, replay: replay);

        return (ok, $"{detail} (snapshot {intoFrame} cycles into frame)");
    }

    // Double-restore invariant: snapshot∘restore reproduces the image, and is idempotent.
    private static (bool Ok, string Detail) DoubleRestoreCheck(byte[] rom, ReadOnlyMemory<byte> bios) {
        using var host = PostMachine.Build(bios: bios, rom: rom);
        var machine = host.Machine;

        RunFrames(machine: machine, frames: WarmupFrames);

        var original = machine.Snapshot();

        RunFrames(machine: machine, frames: RecordFrames);

        machine.Restore(snapshot: original);
        var afterFirst = machine.Snapshot();

        machine.Restore(snapshot: original);
        var afterSecond = machine.Snapshot();

        var reproducesOriginal = afterFirst.ContentEquals(other: original);
        var idempotent = afterFirst.ContentEquals(other: afterSecond);
        var ok = (reproducesOriginal && idempotent);

        var detail = (ok
            ? "restore reproduces the original image, and is idempotent"
            : $"reproduces-original={reproducesOriginal}, idempotent={idempotent}");

        return (ok, detail);
    }
    private static void RunFrames(AdvancedGamingBrickMachine machine, int frames) {
        for (var i = 0; (i < frames); ++i) {
            _ = machine.RunFrame();
        }
    }

    // Per-frame fingerprints: the framebuffer hash and the CPU register+CPSR hash after each of `frames` whole frames.
    private static (ulong Frame, ulong Registers)[] RecordFramesInto(AdvancedGamingBrickMachine machine, int frames) {
        var record = new (ulong Frame, ulong Registers)[frames];

        for (var i = 0; (i < frames); ++i) {
            _ = machine.RunFrame();
            record[i] = (FramebufferHash(machine: machine), RegisterHash(machine: machine));
        }

        return record;
    }
    private static (bool Ok, string Detail) Compare((ulong Frame, ulong Registers)[] baseline, (ulong Frame, ulong Registers)[] replay) {
        for (var i = 0; (i < baseline.Length); ++i) {
            if (baseline[i] != replay[i]) {
                return (false, $"frame {i} diverged (fb {baseline[i].Frame:X16}/{replay[i].Frame:X16}, reg {baseline[i].Registers:X16}/{replay[i].Registers:X16})");
            }
        }

        return (true, $"{baseline.Length} frames identical after restore (final fb 0x{baseline[^1].Frame:X16})");
    }
    private static ulong FramebufferHash(AdvancedGamingBrickMachine machine) =>
        Fnv(data: MemoryMarshal.AsBytes(span: machine.Framebuffer));
    private static ulong RegisterHash(AdvancedGamingBrickMachine machine) {
        Span<uint> registers = stackalloc uint[17];

        for (var r = 0; (r < 16); ++r) {
            registers[r] = machine.Cpu.GetRegister(index: r);
        }

        registers[16] = machine.Cpu.Cpsr;

        return Fnv(data: MemoryMarshal.AsBytes(span: registers));
    }
    private static ulong Fnv(ReadOnlySpan<byte> data) {
        var hash = FnvOffsetBasis;

        foreach (var value in data) {
            hash = ((hash ^ value) * FnvPrime);
        }

        return hash;
    }
}
