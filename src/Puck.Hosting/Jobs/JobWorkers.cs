namespace Puck.Hosting.Jobs;

/// <summary>
/// The number of threads that run the engine's parallel partitions, a forking thread included: the one worker-count
/// setting a host's composition owns and the job system reads (<c>Puck.World --workers</c>). One runs every partition
/// on the forking thread. A request of zero sizes the pool from the machine: a worker for every logical processor but
/// the three threads that work beside the workers without waiting on them, the frame thread, the thread that steps the
/// simulation and the audio render thread, and never fewer than two, plus the forking thread. The frame thread and the
/// step thread each run their own forks' partitions or their own serial work, so with both busy the process keeps at
/// most one active thread per logical processor. A partition's elements never depend on this count, so every count
/// gives bit-identical state, which <c>puck test --workers</c> checks.
/// </summary>
public sealed class JobWorkers {
    /// <summary>Initializes a new instance of the <see cref="JobWorkers"/> class from the count the host was asked
    /// for.</summary>
    /// <param name="requested">The requested thread count; zero sizes it from the host's logical processor
    /// count.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="requested"/> is negative.</exception>
    public JobWorkers(int requested) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: requested);
        Requested = requested;
        Count = ((requested == 0) ? (MachineWorkers + 1) : requested);
    }

    /// <summary>Gets the worker threads a request of zero starts: every logical processor less the frame thread, the
    /// simulation step thread and the audio render thread, and at least two.</summary>
    public static int MachineWorkers => Math.Max(val1: 2, val2: (Environment.ProcessorCount - 3));

    /// <summary>Gets the count the host was asked for; zero when it was asked to size from the machine.</summary>
    public int Requested { get; }
    /// <summary>Gets the threads that run partitions, a forking thread included; at least one.</summary>
    public int Count { get; }
    /// <summary>Gets whether every partition runs on the forking thread.</summary>
    public bool Serial => (Count == 1);
}
