namespace Puck.State.Rules;

/// <summary>The evaluation in flight, handed by reference to every entry that evaluates authored tokens and to every
/// operand read: the reader the operands read through, and whose scratch holds the fault scope the evaluation raises
/// into, with the arena and the clocks that reader answers, each asked of it once. A frame lives on the machine stack
/// and cannot be captured, so an evaluation is always a visible call with a frame in hand.</summary>
/// <remarks>
/// <para><c>default(EvaluationFrame)</c> names no reader and is refused when evaluated.</para>
/// <para>The arena and the clocks are taken from the reader at the first read that needs each and kept for the
/// frame's life, so a host's guard on its arena (a tick still pending) runs once per evaluation, and an evaluation
/// that reads no arena (an action gate) never asks for one. A frame is passed by <see langword="ref"/>, so what it
/// took is kept; one copied by value takes it again.</para>
/// </remarks>
public ref struct EvaluationFrame {
    private readonly IStateReader? m_reader;
    private StateArena? m_arena;
    private ArenaTime m_time;
    private bool m_timed;

    /// <summary>Initializes a new instance of the <see cref="EvaluationFrame"/> struct.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is <see langword="null"/>.</exception>
    public EvaluationFrame(IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        m_reader = reader;
    }

    // Whether the frame names a reader: the constant folder evaluates with none, and every live read refuses then.
    internal readonly bool IsLive => (m_reader is not null);
    /// <summary>Gets the evaluation in flight.</summary>
    /// <exception cref="InvalidOperationException">The frame is <see langword="default"/>.</exception>
    public readonly IStateReader Reader => (m_reader ?? throw new InvalidOperationException(message: "A default evaluation frame names no reader."));
    /// <summary>Gets the arena the reader answers (<see cref="IStateReader.Arena"/>), asked of the reader at the
    /// frame's first arena read.</summary>
    /// <exception cref="InvalidOperationException">The frame is <see langword="default"/>, or the reader refuses its
    /// arena.</exception>
    public StateArena Arena => (m_arena ??= Reader.Arena);
    /// <summary>Gets the clocks the reader answers (<see cref="IStateReader.Time"/>), asked of the reader at the
    /// frame's first timed read.</summary>
    /// <exception cref="InvalidOperationException">The frame is <see langword="default"/>.</exception>
    public ArenaTime Time {
        get {
            if (!m_timed) {
                m_time = Reader.Time;
                m_timed = true;
            }

            return m_time;
        }
    }
}
