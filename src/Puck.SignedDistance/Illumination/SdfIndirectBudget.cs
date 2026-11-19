namespace Puck.SignedDistance;

/// <summary>
/// How much irradiance work each second of wall time grants the volume, and how stale its indirect light may grow
/// before the volume reports it is behind. A light change costs a relight of the probes' cached hits and a geometry
/// change costs a trace; overload lengthens the lighting's age, never the frame. A rate of wall time means the same
/// lighting at every frame rate and on every device, and the GPU time it costs comes from the one background budget.
/// Zero in any field leaves that part unbounded. A lockstep frame finishes its window whatever the budget.
/// </summary>
/// <param name="RelightPerSecond">The probes relit each second; zero relights as fast as the volume's workers
/// run.</param>
/// <param name="TracePerSecond">The probes whose hits are traced each second; zero traces as fast as the workers
/// run.</param>
/// <param name="Age">The lighting age, in seconds, past which the volume reports it is behind; zero reports nothing.</param>
public readonly record struct SdfIndirectBudget(int RelightPerSecond, int TracePerSecond, float Age) {
    /// <summary>Gets the budget that bounds nothing.</summary>
    public static SdfIndirectBudget Unbounded => default;

    /// <summary>Refuses a negative rate or a negative or non-finite age.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A rate is negative, or the age is negative or not finite.</exception>
    public void Validate() {
        ArgumentOutOfRangeException.ThrowIfNegative(RelightPerSecond);
        ArgumentOutOfRangeException.ThrowIfNegative(TracePerSecond);
        if (!float.IsFinite(f: Age) || (Age < 0f)) { throw new ArgumentOutOfRangeException(actualValue: Age, message: "An indirect age target is a finite, non-negative number of seconds.", paramName: nameof(Age)); }
    }
}
