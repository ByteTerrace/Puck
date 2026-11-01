namespace Puck.Hosting.Jobs;

/// <summary>
/// The number of threads that run the engine's parallel partitions, the frame thread included: the one worker-count
/// setting a host's composition owns and the job system reads (<c>Puck.World --workers</c>). One runs every partition
/// on the frame thread; a request of zero takes the host's logical processor count. A partition's elements never
/// depend on this count, so every count gives bit-identical state, which <c>puck test --workers</c> checks.
/// </summary>
public sealed class JobWorkers {
    /// <summary>Initializes a new instance of the <see cref="JobWorkers"/> class from the count the host was asked
    /// for.</summary>
    /// <param name="requested">The requested thread count; zero takes the host's logical processor count.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="requested"/> is negative.</exception>
    public JobWorkers(int requested) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: requested);
        Requested = requested;
        Count = ((requested == 0) ? Math.Max(val1: 1, val2: Environment.ProcessorCount) : requested);
    }

    /// <summary>Gets the count the host was asked for; zero when it was asked for its logical processor count.</summary>
    public int Requested { get; }
    /// <summary>Gets the threads that run partitions, the frame thread included; at least one.</summary>
    public int Count { get; }
    /// <summary>Gets whether every partition runs on the frame thread.</summary>
    public bool Serial => (Count == 1);
}
