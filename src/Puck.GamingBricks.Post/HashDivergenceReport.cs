using Puck.Maths;

namespace Puck.GamingBricks.Post;

/// <summary>The core of the hash-divergence localizer shared by both bricks' probes: snapshot-hash two machines with
/// FNV-1a, and on a mismatch print the one-line component/offset localization plus a short hex window of both sides.
/// Generic over any snapshot deriving from <see cref="MachineSnapshot{TSnapshot, TIdentity, TClock}"/>, so each
/// brick's probe supplies only its own machine-stepping loop and its own <c>DescribeDivergence</c>.</summary>
public static class HashDivergenceReport {
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
    /// <summary>Prints the one-line component/offset localization, then a short hex window of both sides around the
    /// first differing byte.</summary>
    /// <param name="a">The first snapshot.</param>
    /// <param name="b">The second snapshot.</param>
    /// <param name="describeDivergence">Renders the brick-specific one-line component/offset description.</param>
    public static void PrintDivergenceReport<TSnapshot, TIdentity, TClock>(TSnapshot a, TSnapshot b, Func<TSnapshot, TSnapshot, string> describeDivergence)
        where TSnapshot : MachineSnapshot<TSnapshot, TIdentity, TClock>
        where TIdentity : IEquatable<TIdentity>
        where TClock : IEquatable<TClock> {
        Console.WriteLine(value: $"  {describeDivergence(arg1: a, arg2: b)}");

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
}
