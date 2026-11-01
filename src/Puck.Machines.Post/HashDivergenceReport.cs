using Puck.Maths;

namespace Puck.Machines.Post;

/// <summary>The core of the hash-divergence localizer shared by both bricks' probes: snapshot-hash two machines with
/// FNV-1a, and on a mismatch print the one-line component/offset localization plus a short hex window of both sides.
/// Generic over any snapshot deriving from <see cref="MachineSnapshot{TSnapshot, TIdentity, TClock}"/>, so each
/// probe supplies its machine operations and component descriptions.</summary>
public static class HashDivergenceReport {
    /// <summary>Compares two machines at shared absolute cycle targets, stopping at the first divergence.</summary>
    /// <typeparam name="TMachine">The machine type.</typeparam>
    /// <typeparam name="TSnapshot">The captured snapshot type.</typeparam>
    /// <typeparam name="TIdentity">The machine identity type.</typeparam>
    /// <typeparam name="TClock">The snapshot clock type.</typeparam>
    /// <param name="machineA">The first machine, owned by the caller.</param>
    /// <param name="machineB">The second machine, owned by the caller.</param>
    /// <param name="runTo">Advances a machine to an absolute cycle target without reanchoring its pacing.</param>
    /// <param name="snapshot">Captures a machine.</param>
    /// <param name="perturb">Injects a deliberate divergence without advancing the machine.</param>
    /// <param name="describeDivergence">Describes the first snapshot difference.</param>
    /// <param name="romALabel">The first cartridge's display label.</param>
    /// <param name="romBLabel">The second cartridge's display label, or null for the first cartridge.</param>
    /// <param name="modelLabel">The optional model suffix, including its leading space.</param>
    /// <param name="frames">The number of frames to compare.</param>
    /// <param name="fine">Whether to compare every scanline.</param>
    /// <param name="perturbAtFrame">The frame before which to perturb the second machine, or null.</param>
    /// <param name="scanlineCycles">The number of cycles per scanline.</param>
    /// <param name="scanlinesPerFrame">The number of scanlines per frame.</param>
    /// <returns>Zero when both machines agree, or one after reporting a divergence.</returns>
    public static int Run<TMachine, TSnapshot, TIdentity, TClock>(
        TMachine machineA, TMachine machineB, Action<TMachine, long> runTo,
        Func<TMachine, TSnapshot> snapshot, Action<TMachine> perturb,
        Func<TSnapshot, TSnapshot, string> describeDivergence,
        string romALabel, string? romBLabel, string modelLabel,
        int frames, bool fine, int? perturbAtFrame, int scanlineCycles, int scanlinesPerFrame)
        where TSnapshot : MachineSnapshot<TSnapshot, TIdentity, TClock>
        where TIdentity : IEquatable<TIdentity>
        where TClock : IEquatable<TClock> {
        var mode = ((romBLabel is not null)
            ? $"romA={romALabel} romB={romBLabel}"
            : ((perturbAtFrame is not null)
                ? $"rom={romALabel} (self-check + deliberate perturbation @frame {perturbAtFrame})"
                : $"rom={romALabel} (self-check)"));

        Console.WriteLine(value: $"== hash-divergence localizer: {mode}{modelLabel}, {frames} frames{(fine ? ", --fine (per-scanline)" : "")} ==");
        // Snapshot restore can reanchor a private pacing accumulator. Both machines must still reach one instant.
        var target = 0L;
        var stepsPerFrame = (fine ? scanlinesPerFrame : 1);
        var stepCycles = (fine ? scanlineCycles : (scanlineCycles * scanlinesPerFrame));

        for (var frame = 0; (frame < frames); ++frame) {
            for (var step = 0; (step < stepsPerFrame); ++step) {
                if ((perturbAtFrame == frame) && (step == 0)) {
                    perturb(obj: machineB);
                }
                target += stepCycles;
                runTo(arg1: machineA, arg2: target);
                runTo(arg1: machineB, arg2: target);
                var where = (fine ? $"frame {frame} scanline {step}" : $"frame {frame}");

                if (!TryCompare<TSnapshot, TIdentity, TClock>(
                    snapshotA: snapshot(arg: machineA), snapshotB: snapshot(arg: machineB),
                    where: where, describeDivergence: describeDivergence)) {
                    return 1;
                }
            }
        }
        Console.WriteLine(value: $"== hash-divergence: NO divergence across {frames} frames ==");
        return 0;
    }
    /// <summary>Prints the one-line component/offset localization, then a short hex window of both sides around the
    /// first differing byte.</summary>
    /// <param name="a">The first snapshot.</param>
    /// <param name="b">The second snapshot.</param>
    /// <param name="describeDivergence">Renders the brick-specific one-line component/offset description.</param>
    public static void PrintDivergenceReport<TSnapshot, TIdentity, TClock>(TSnapshot a, TSnapshot b, Func<TSnapshot, TSnapshot, string> describeDivergence)
        where TSnapshot : MachineSnapshot<TSnapshot, TIdentity, TClock>
        where TIdentity : IEquatable<TIdentity>
        where TClock : IEquatable<TClock> {
        Console.WriteLine(value: $"  {describeDivergence(
            arg1: a,
            arg2: b
        )}");

        var diff = SnapshotDivergence.FindFirstDifference(
            a: a.Data,
            b: b.Data,
            sections: a.Sections
        );

        if (diff is null) {
            return;
        }

        var (_, _, absoluteOffset) = diff.Value;

        Console.WriteLine(value: SnapshotDivergence.FormatHexWindow(
            label: "A",
            data: a.Data,
            offset: absoluteOffset
        ));
        Console.WriteLine(value: SnapshotDivergence.FormatHexWindow(
            label: "B",
            data: b.Data,
            offset: absoluteOffset
        ));
    }
    /// <summary>Snapshot-hashes both sides and, on a mismatch, prints the full localization report.</summary>
    /// <param name="snapshotA">Machine A's snapshot.</param>
    /// <param name="snapshotB">Machine B's snapshot.</param>
    /// <param name="where">A one-line label for the compared instant (e.g. <c>"frame 3"</c>), printed in the mismatch
    /// banner.</param>
    /// <param name="describeDivergence">Renders the brick-specific one-line component/offset description.</param>
    /// <returns><see langword="true"/> when the hashes match; <see langword="false"/> after printing the divergence
    /// report.</returns>
    public static bool TryCompare<TSnapshot, TIdentity, TClock>(TSnapshot snapshotA, TSnapshot snapshotB, string where, Func<TSnapshot, TSnapshot, string> describeDivergence)
        where TSnapshot : MachineSnapshot<TSnapshot, TIdentity, TClock>
        where TIdentity : IEquatable<TIdentity>
        where TClock : IEquatable<TClock> {
        var hashA = Fnv1aHash.Compute(values: snapshotA.Data);
        var hashB = Fnv1aHash.Compute(values: snapshotB.Data);

        if (hashA == hashB) {
            return true;
        }

        Console.WriteLine(value: $"== HASH DIVERGENCE at {where}: A=0x{hashA:X16}  B=0x{hashB:X16} ==");
        PrintDivergenceReport<TSnapshot, TIdentity, TClock>(
            a: snapshotA,
            b: snapshotB,
            describeDivergence: describeDivergence
        );

        return false;
    }
}
