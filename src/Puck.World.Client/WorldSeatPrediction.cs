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
public readonly record struct WorldPredictedBody(WorldAuthorityEndpoint Endpoint, int Index, ulong ContinuationTick, ulong Tick, FixedVector3 Position, FixedVector3 Velocity, FixedQuaternion Orientation) {
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

/// <summary>The input one seat sent its authority, each batch with the client tick it was sent at: what a prediction
/// replays past the authority's continuation. Holds the most recent <see cref="Capacity"/> batches.</summary>
public sealed class WorldSeatSentInput {
    /// <summary>The batches held: two seconds at the highest simulation rate a delivery's round trip is predicted
    /// across.</summary>
    public const int Capacity = 256;

    /// <summary>One sent batch.</summary>
    /// <param name="ClientTick">The seat clock, in ticks of the authority's rate, the batch was sent at.</param>
    /// <param name="Sequence">The highest event sequence the batch carries, or zero for a batch with none.</param>
    /// <param name="Submission">The batch as sent.</param>
    public readonly record struct Sent(ulong ClientTick, uint Sequence, IntentSubmission Submission);

    private readonly Sent[] m_sent = new Sent[Capacity];
    private int m_count;
    private int m_next;

    /// <summary>Gets the batches held.</summary>
    public int Count => m_count;

    /// <summary>Records a batch as it is sent.</summary>
    /// <param name="clientTick">The seat clock, in ticks of the authority's rate, it is sent at.</param>
    /// <param name="submission">The batch.</param>
    public void Record(ulong clientTick, in IntentSubmission submission) {
        var sequence = 0U;

        foreach (ref readonly var input in submission.Input.Events.Span) {
            sequence = Math.Max(val1: sequence, val2: input.Sequence);
        }
        m_sent[m_next] = new Sent(ClientTick: clientTick, Sequence: sequence, Submission: submission);
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
    /// <summary>Finds the client tick the batch carrying an answered event was sent at.</summary>
    /// <param name="epoch">The answered input epoch.</param>
    /// <param name="sequence">The answered event sequence.</param>
    /// <param name="clientTick">The send tick, when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a held batch of that epoch carries the sequence.</returns>
    public bool TryFindSent(ulong epoch, uint sequence, out ulong clientTick) {
        for (var position = (m_count - 1); (position >= 0); position--) {
            ref readonly var sent = ref At(position: position);

            if ((sent.Submission.Input.Epoch == epoch) && (sent.Sequence == sequence) && (sequence != 0U)) {
                clientTick = sent.ClientTick;
                return true;
            }
        }

        clientTick = 0UL;
        return false;
    }
}
