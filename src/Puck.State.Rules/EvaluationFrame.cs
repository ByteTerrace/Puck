namespace Puck.State.Rules;

/// <summary>The evaluation in flight, handed by reference to every entry that evaluates authored tokens: the reader the
/// operands read through, and whose scratch holds the fault scope the evaluation raises into. A frame lives on the
/// machine stack and cannot be captured, so an evaluation is always a visible call with a frame in hand.</summary>
/// <remarks><c>default(EvaluationFrame)</c> names no reader and is refused when evaluated.</remarks>
public readonly ref struct EvaluationFrame {
    private readonly IStateReader? m_reader;

    /// <summary>Initializes a new instance of the <see cref="EvaluationFrame"/> struct.</summary>
    /// <param name="reader">The evaluation in flight.</param>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is <see langword="null"/>.</exception>
    public EvaluationFrame(IStateReader reader) {
        ArgumentNullException.ThrowIfNull(argument: reader);

        m_reader = reader;
    }

    /// <summary>Gets the evaluation in flight.</summary>
    /// <exception cref="InvalidOperationException">The frame is <see langword="default"/>.</exception>
    public IStateReader Reader => (m_reader ?? throw new InvalidOperationException(message: "A default evaluation frame names no reader."));
}
