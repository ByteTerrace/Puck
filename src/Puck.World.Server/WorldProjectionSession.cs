using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>How one attempt to serve a segment of a traveler's observation stream came out.</summary>
public enum WorldProjectionEnd : byte {
    /// <summary>The segment did not start and nothing was written: the credential, the admission or the route
    /// refuses it.</summary>
    Refused,
    /// <summary>The segment did not start and nothing was written, because the traveler stands nowhere the route
    /// reaches yet: its crossing is in flight. Asking again is the answer.</summary>
    InFlight,
    /// <summary>The segment ran and its traveler moved on from the authority that served it. The stream stands at a
    /// record boundary, and its next segment goes on wherever the traveler stands now.</summary>
    Moved,
    /// <summary>The stream is over: its last record, an invalidation or a refusal, was written.</summary>
    Ended,
}

/// <summary>The outcome of one attempt to serve a segment of a traveler's observation stream.</summary>
/// <param name="End">How the attempt came out.</param>
/// <param name="Reason">The named refusal for <see cref="WorldProjectionEnd.Refused"/> and
/// <see cref="WorldProjectionEnd.InFlight"/>; otherwise <see langword="null"/>.</param>
public readonly record struct WorldProjectionOutcome(WorldProjectionEnd End, string? Reason = null) {
    /// <summary>Gets the outcome of a segment whose traveler moved on.</summary>
    public static WorldProjectionOutcome Moved => new(End: WorldProjectionEnd.Moved);
    /// <summary>Gets the outcome of a stream whose last record was written.</summary>
    public static WorldProjectionOutcome Ended => new(End: WorldProjectionEnd.Ended);

    /// <summary>Returns the outcome of a segment that was refused.</summary>
    /// <param name="reason">The named refusal.</param>
    /// <returns>The outcome.</returns>
    public static WorldProjectionOutcome Refused(string reason) => new(End: WorldProjectionEnd.Refused, Reason: reason);
    /// <summary>Returns the outcome of a segment whose traveler is between authorities.</summary>
    /// <param name="reason">What the route lacks.</param>
    /// <returns>The outcome.</returns>
    public static WorldProjectionOutcome InFlight(string reason) => new(End: WorldProjectionEnd.InFlight, Reason: reason);
}

/// <summary>One recipient's traveler observation stream as the authorities that serve it share it: the connection its
/// records are written to, the lineage every segment stamps from, and the one place the recipient's acknowledgements
/// are handed to whichever segment is serving.</summary>
/// <remarks>The authority that holds the recipient's connection creates the session and reads the connection's
/// acknowledgements for as long as the stream stands; a segment never reads the connection itself, so a handoff
/// between segments loses no acknowledgement to a reader that was ending. Each authority on a forwarding path writes
/// through its own view of the connection (<see cref="WithOutput"/>), which lets it hold the stream to its live policy
/// at every record.</remarks>
public sealed class WorldProjectionSession {
    private sealed class Receiver {
        public Func<WorldStreamStamp, ReadOnlyMemory<byte>, CancellationToken, ValueTask>? Acknowledge;
    }

    private readonly Receiver m_receiver;

    /// <summary>Initializes a new instance of the <see cref="WorldProjectionSession"/> class, over a connection and
    /// the stream its records belong to.</summary>
    /// <param name="output">The recipient's connection, which the caller owns.</param>
    /// <param name="lineage">The stream's identity and counters.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldProjectionSession(Stream output, WorldStreamLineage lineage) : this(lineage: lineage, output: output, receiver: new Receiver()) { }
    private WorldProjectionSession(Stream output, WorldStreamLineage lineage, Receiver receiver) {
        ArgumentNullException.ThrowIfNull(argument: output);
        ArgumentNullException.ThrowIfNull(argument: lineage);
        Output = output;
        Lineage = lineage;
        m_receiver = receiver;
    }

    /// <summary>Gets the stream's identity and counters, which every segment of the stream stamps from.</summary>
    public WorldStreamLineage Lineage { get; }
    /// <summary>Gets the connection this authority writes the stream's records to.</summary>
    public Stream Output { get; }

    /// <summary>Returns the same session writing through another view of the connection.</summary>
    /// <param name="output">The view, which the caller owns.</param>
    /// <returns>The session over <paramref name="output"/>.</returns>
    public WorldProjectionSession WithOutput(Stream output) => new(lineage: Lineage, output: output, receiver: m_receiver);
    /// <summary>Makes <paramref name="acknowledge"/> the segment the recipient's acknowledgements are handed to, until
    /// the returned lease is disposed or another segment takes its place.</summary>
    /// <param name="acknowledge">Receives each acknowledged stamp and the bytes it crossed as.</param>
    /// <returns>The lease.</returns>
    public IDisposable Receive(Func<WorldStreamStamp, ReadOnlyMemory<byte>, CancellationToken, ValueTask> acknowledge) {
        ArgumentNullException.ThrowIfNull(argument: acknowledge);
        Volatile.Write(location: ref m_receiver.Acknowledge, value: acknowledge);

        return new Lease(acknowledge: acknowledge, receiver: m_receiver);
    }
    /// <summary>Hands one acknowledgement read from the connection to the segment that is serving. One that arrives
    /// between segments is dropped: it names a record of a segment that has ended, which nothing is composed
    /// against.</summary>
    /// <param name="stamp">The acknowledged stamp.</param>
    /// <param name="bytes">The bytes the acknowledgement crossed as.</param>
    /// <param name="ct">The stream's lifetime.</param>
    /// <returns>A task that completes when the segment has taken it.</returns>
    public ValueTask AcknowledgeAsync(WorldStreamStamp stamp, ReadOnlyMemory<byte> bytes, CancellationToken ct) =>
        (Volatile.Read(location: ref m_receiver.Acknowledge)?.Invoke(arg1: stamp, arg2: bytes, arg3: ct) ?? ValueTask.CompletedTask);

    private sealed class Lease(Receiver receiver, Func<WorldStreamStamp, ReadOnlyMemory<byte>, CancellationToken, ValueTask> acknowledge) : IDisposable {
        public void Dispose() => _ = Interlocked.CompareExchange(location1: ref receiver.Acknowledge, value: null, comparand: acknowledge);
    }
}
