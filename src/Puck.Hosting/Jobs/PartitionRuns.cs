namespace Puck.Hosting.Jobs;

/// <summary>Cuts a list of independent items into runs of a fixed length, one run a partition of a <see cref="JobPool"/>
/// fork: partition <c>p</c> holds items <c>[p * run, min(count, (p + 1) * run))</c>, so which items a partition holds
/// never depends on the thread that takes it.</summary>
public static class PartitionRuns {
    /// <summary>Returns how many partitions cover <paramref name="count"/> items in runs of <paramref name="run"/>.</summary>
    /// <param name="count">The item count; zero or less needs no partition.</param>
    /// <param name="run">The items a run holds; at least one.</param>
    /// <returns>The partition count.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="run"/> is below one.</exception>
    public static int Count(int count, int run) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: run, other: 1);

        return ((count <= 0) ? 0 : Puck.Maths.BinaryIntegerFunctions.CeilingDivide(value: count, divisor: run));
    }
    /// <summary>Returns the half-open range of items one partition holds.</summary>
    /// <param name="partition">The partition's index.</param>
    /// <param name="count">The item count.</param>
    /// <param name="run">The items a run holds; at least one.</param>
    /// <returns>The first item and one past the last, equal when the partition holds none.</returns>
    public static (int Start, int End) Of(int partition, int count, int run) {
        var start = (int)Math.Min(val1: count, val2: ((long)partition * run));

        return (start, (int)Math.Min(val1: count, val2: ((long)start + run)));
    }
}
