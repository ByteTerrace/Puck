namespace Puck.State;

/// <summary>What the evaluator read for an arm's declared sources, in the order the arm declared them: each an
/// <see cref="Outcome"/> that is a number or an absence, since a faulted source refuses the firing before the arm
/// runs.</summary>
/// <remarks>A view over the evaluator's buffer, valid for the one call it is handed to. An arm reads it by the position
/// it declared the source at.</remarks>
public readonly struct EffectValues {
    private readonly Outcome[]? m_values;
    private readonly int m_start;
    private readonly int m_count;

    /// <summary>Initializes a new instance of the <see cref="EffectValues"/> struct over a run of an evaluator's
    /// buffer.</summary>
    /// <param name="values">The buffer.</param>
    /// <param name="start">The index of the first value.</param>
    /// <param name="count">How many values the arm declared.</param>
    public EffectValues(Outcome[] values, int start, int count) {
        ArgumentNullException.ThrowIfNull(argument: values);

        m_values = values;
        m_start = start;
        m_count = count;
    }

    /// <summary>Gets how many values the arm declared.</summary>
    public int Count => m_count;

    /// <summary>Gets what the evaluator read for the source at a declared position.</summary>
    /// <param name="index">The position the arm declared the source at.</param>
    /// <exception cref="InvalidOperationException">The arm read a position it did not declare, or ran without the
    /// evaluator reading its sources.</exception>
    public Outcome this[int index] {
        get {
            if ((m_values is null) || (((uint)index) >= ((uint)m_count))) {
                throw new InvalidOperationException(message: "An arm read a source it did not declare, or ran before the evaluator read its sources.");
            }

            return m_values[(m_start + index)];
        }
    }
}
