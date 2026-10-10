namespace Puck.Shaders;

/// <summary>
/// Grants a shader build the processor cores it compiles on. Under MSBuild it is the build engine's own core budget
/// (<c>IBuildEngine9.RequestCores</c> and <c>ReleaseCores</c>), which every project node shares, so concurrent shader
/// projects divide the machine between them rather than each starting a fixed number of compilers.
/// </summary>
/// <remarks>A build holds at most one request at a time. The first request is answered at once with at least one core,
/// the core the build already runs on; a later one may wait until another holder releases a core, and the build keeps
/// compiling and releasing on the cores it holds meanwhile. A build that finishes while a request is outstanding
/// cancels it, which must end the request promptly, and releases every core it holds before it returns; a broker that
/// answers a cancelled request later releases that grant itself.</remarks>
public interface IShaderCoreBroker {
    /// <summary>Requests up to <paramref name="count"/> more cores.</summary>
    /// <param name="count">The most cores the build can use now, at least one.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <returns>The cores granted: at least one, at most <paramref name="count"/>.</returns>
    Task<int> RequestAsync(int count, CancellationToken cancellationToken);
    /// <summary>Returns cores the build no longer needs.</summary>
    /// <param name="count">The cores returned, at least one.</param>
    void Release(int count);
}
/// <summary>A broker with a fixed number of cores and no other holder: the first request is granted up to the limit,
/// and a later request waits for a release. For a build run outside MSBuild, and for laws.</summary>
public sealed class FixedShaderCoreBroker : IShaderCoreBroker {
    private readonly Lock m_gate = new();
    private TaskCompletionSource m_released = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

    private int m_free;

    /// <summary>Initializes a broker over <paramref name="cores"/> cores.</summary>
    /// <param name="cores">The cores, at least one.</param>
    public FixedShaderCoreBroker(int cores) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: cores, other: 1);
        m_free = cores;
        Cores = cores;
    }

    /// <summary>Gets the cores the broker grants in all.</summary>
    public int Cores { get; }
    /// <summary>Gets the most cores the build held at once.</summary>
    public int PeakHeld { get; private set; }
    /// <summary>Gets the cores the build holds now.</summary>
    public int Held {
        get {
            lock (m_gate) {
                return (Cores - m_free);
            }
        }
    }

    /// <inheritdoc/>
    public async Task<int> RequestAsync(int count, CancellationToken cancellationToken) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: count, other: 1);
        cancellationToken.ThrowIfCancellationRequested();

        while (true) {
            Task released;

            lock (m_gate) {
                if (m_free > 0) {
                    var granted = Math.Min(val1: count, val2: m_free);

                    m_free -= granted;
                    PeakHeld = Math.Max(val1: PeakHeld, val2: (Cores - m_free));

                    return granted;
                }
                released = m_released.Task;
            }
            await released.WaitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
    }
    /// <inheritdoc/>
    public void Release(int count) {
        ArgumentOutOfRangeException.ThrowIfLessThan(value: count, other: 1);
        TaskCompletionSource released;

        lock (m_gate) {
            if ((m_free + count) > Cores) {
                throw new InvalidOperationException(message: $"Released {count} core(s) with only {(Cores - m_free)} held.");
            }
            m_free += count;
            released = m_released;
            m_released = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        }
        released.SetResult();
    }
}
