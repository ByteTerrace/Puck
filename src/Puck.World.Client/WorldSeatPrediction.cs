using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Client;

/// <summary>One seat's own body as its client predicts it: the authority's exact continuation of the body after a
/// delivered tick, stepped on by the authority's own body step through the input the seat sent that the authority had
/// not yet taken, to the tick at which an input the seat sends now would land.</summary>
/// <param name="Endpoint">The authority the prediction is of.</param>
/// <param name="Index">The body's 0-based index at that authority.</param>
/// <param name="ContinuationTick">The authority tick of the continuation the prediction started from.</param>
/// <param name="Tick">The authority tick the prediction reaches.</param>
/// <param name="Position">The predicted foot point.</param>
/// <param name="Velocity">The predicted velocity over the last predicted step, in metres per second.</param>
/// <param name="Orientation">The predicted attitude.</param>
/// <param name="Error">The distance, in metres, by which the latest continuation missed what an earlier prediction
/// reached for its tick; <see cref="FixedQ4816.MaxValue"/> until the authority has answered one.</param>
public readonly record struct WorldPredictedBody(WorldAuthorityEndpoint Endpoint, int Index, ulong ContinuationTick, ulong Tick, FixedVector3 Position, FixedVector3 Velocity, FixedQuaternion Orientation, FixedQ4816 Error) {
    /// <summary>Gets the authority ticks the prediction runs ahead of its continuation.</summary>
    public ulong Steps => (Tick - ContinuationTick);
}

/// <summary>Predicts a remote seat's own body. Implemented where the authority's body step is reachable (the
/// composition root), so a client predicts with the one step every authority runs and never a second.</summary>
public interface IWorldSeatPrediction {
    /// <summary>Predicts a seat's own body at the client's seat clock (<see cref="WorldClient.SeatClockSeconds"/>).</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <param name="body">The prediction, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the seat is routed to an authority in another process that delivered its
    /// body's continuation and answered an input it can time; otherwise the seat steers from what was delivered.</returns>
    bool TryPredict(int slot, out WorldPredictedBody body);
    /// <summary>Describes a seat's latest prediction: the ticks it stepped past its continuation, or why it predicted
    /// nothing.</summary>
    /// <param name="slot">The 0-based seat slot.</param>
    /// <returns>A one-line reading.</returns>
    string Describe(int slot);
}

/// <summary>The input one seat sent its authority, each batch stamped with the authority tick it was sent at: what a
/// prediction replays past the authority's continuation. Holds the most recent <see cref="Capacity"/> batches, and the
/// seat clock's mapping onto that authority's ticks (<see cref="Clock"/>).</summary>
public sealed class WorldSeatSentInput {
    /// <summary>The batches held: two seconds at the highest simulation rate a delivery's round trip is predicted
    /// across.</summary>
    public const int Capacity = 256;

    /// <summary>One sent batch.</summary>
    /// <param name="Tick">The authority tick the batch was sent at, as the seat's clock mapped it then
    /// (<see cref="WorldAuthorityClock.TickAt"/>).</param>
    /// <param name="Sequence">The highest event sequence the batch carries, or zero for a batch with none.</param>
    /// <param name="Submission">The batch as sent.</param>
    public readonly record struct Sent(long Tick, uint Sequence, IntentSubmission Submission);

    private readonly Sent[] m_sent = new Sent[Capacity];
    private int m_count;
    private int m_next;

    /// <summary>Gets the batches held.</summary>
    public int Count => m_count;

    /// <summary>Gets the authority the held batches were sent to.</summary>
    public WorldAuthorityEndpoint? Endpoint { get; private set; }
    /// <summary>Gets the seat clock's mapping onto the ticks of the authority the batches were sent to.</summary>
    public WorldAuthorityClock Clock { get; } = new();
    // The owner incarnation (WorldSeatInput.OwnerIncarnation) the held batches were sent under.
    private long m_incarnation;

    /// <summary>Records a batch as it is sent, after noting the authority's latest delivered tick on the seat's clock. A
    /// batch sent to another authority than the held ones forgets them and the clock's anchors, since every authority
    /// numbers its ticks itself; one sent under another owner incarnation at the same authority forgets the batches,
    /// since every owner numbers its input epochs and event sequences itself.</summary>
    /// <param name="clientTick">The seat clock, in ticks of the authority's rate, it is sent at.</param>
    /// <param name="deliveredTick">The authority's latest delivered tick as the batch is sent.</param>
    /// <param name="submission">The batch.</param>
    /// <param name="endpoint">The authority it is sent to.</param>
    /// <param name="incarnation">The seat input's <see cref="WorldSeatInput.OwnerIncarnation"/> the batch was produced
    /// under.</param>
    public void Record(ulong clientTick, ulong deliveredTick, in IntentSubmission submission, WorldAuthorityEndpoint endpoint, long incarnation) {
        var sequence = 0U;

        if (!ReferenceEquals(objA: Endpoint, objB: endpoint)) {
            Clock.Reset();
        }
        if (!ReferenceEquals(objA: Endpoint, objB: endpoint) || (m_incarnation != incarnation)) {
            Clear();
            Endpoint = endpoint;
            m_incarnation = incarnation;
        }

        Clock.Observe(clientTick: clientTick, deliveredTick: deliveredTick);
        foreach (ref readonly var input in submission.Input.Events.Span) {
            sequence = Math.Max(val1: sequence, val2: input.Sequence);
        }
        m_sent[m_next] = new Sent(Sequence: sequence, Submission: submission, Tick: Clock.TickAt(clientTick: clientTick));
        m_next = ((m_next + 1) % Capacity);
        m_count = Math.Min(val1: (m_count + 1), val2: Capacity);
    }
    /// <summary>Forgets every batch, as a new route or input epoch starts.</summary>
    public void Clear() {
        m_count = 0;
        m_next = 0;
    }
    /// <summary>Reads a held batch, oldest first.</summary>
    /// <param name="position">The 0-based position, below <see cref="Count"/>.</param>
    /// <returns>The batch.</returns>
    public ref readonly Sent At(int position) => ref m_sent[(((m_next - m_count) + position + Capacity) % Capacity)];
    /// <summary>Finds the authority tick the batch carrying an answered event was sent at (<see cref="Sent.Tick"/>).</summary>
    /// <param name="epoch">The answered input epoch.</param>
    /// <param name="sequence">The answered event sequence.</param>
    /// <param name="tick">The send tick, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a held batch of that epoch carries the sequence.</returns>
    public bool TryFindSent(ulong epoch, uint sequence, out long tick) {
        for (var position = (m_count - 1); (position >= 0); position--) {
            ref readonly var sent = ref At(position: position);

            if ((sent.Submission.Input.Epoch == epoch) && (sent.Sequence == sequence) && (sequence != 0U)) {
                tick = sent.Tick;
                return true;
            }
        }

        tick = 0L;
        return false;
    }
}
