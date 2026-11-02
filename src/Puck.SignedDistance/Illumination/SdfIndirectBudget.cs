namespace Puck.SignedDistance;

/// <summary>
/// How much irradiance work each rendered frame grants the volume, and how stale its indirect light may grow before
/// the volume reports it is behind. A light change costs a relight of the probes' cached hits and a geometry change
/// costs a trace; overload lengthens the lighting's age, never the frame. Zero in any field leaves that part
/// unbounded. A lockstep frame finishes its window whatever the budget.
/// </summary>
/// <param name="Relight">The probes relit per frame; zero relights as fast as the volume's workers run.</param>
/// <param name="Trace">The probes whose hits are traced per frame; zero traces as fast as the workers run.</param>
/// <param name="Age">The lighting age, in seconds, past which the volume reports it is behind; zero reports nothing.</param>
public readonly record struct SdfIndirectBudget(int Relight, int Trace, float Age) {
    /// <summary>Gets the budget that bounds nothing.</summary>
    public static SdfIndirectBudget Unbounded => default;

    /// <summary>Refuses a negative count or a negative or non-finite age.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative, or the age is negative or not finite.</exception>
    public void Validate() {
        ArgumentOutOfRangeException.ThrowIfNegative(Relight);
        ArgumentOutOfRangeException.ThrowIfNegative(Trace);
        if (!float.IsFinite(f: Age) || (Age < 0f)) { throw new ArgumentOutOfRangeException(actualValue: Age, message: "An indirect age target is a finite, non-negative number of seconds.", paramName: nameof(Age)); }
    }
}
