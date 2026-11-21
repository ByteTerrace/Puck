namespace Puck.SignedDistance;

/// <summary>
/// How much irradiance work each presented frame grants the volume, and how stale its indirect light may grow before the
/// volume reports it is behind. A light change costs a relight of the probes' cached hits and a geometry change costs a
/// trace; overload lengthens the lighting's age, never the frame. The grant is a count a frame, never a rate of wall
/// time, so a frame that runs long owes no more probe work than a short one, and a device that holds fewer frames a
/// second does less work a second. The device's own table authors it, and the GPU time the work costs comes from the one
/// background budget. Zero in any field leaves that part unbounded. A lockstep frame finishes its window whatever the
/// budget.
/// </summary>
/// <param name="RelightPerFrame">The probes relit each presented frame; zero relights as fast as the volume's workers
/// run.</param>
/// <param name="TracePerFrame">The probes whose hits are traced each presented frame; zero traces as fast as the workers
/// run.</param>
/// <param name="Age">The lighting age, in seconds, past which the volume reports it is behind; zero reports nothing.</param>
public readonly record struct SdfIndirectBudget(int RelightPerFrame, int TracePerFrame, float Age) {
    /// <summary>Gets the budget that bounds nothing.</summary>
    public static SdfIndirectBudget Unbounded => default;

    /// <summary>Refuses a negative count or a negative or non-finite age.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative, or the age is negative or not finite.</exception>
    public void Validate() {
        ArgumentOutOfRangeException.ThrowIfNegative(RelightPerFrame);
        ArgumentOutOfRangeException.ThrowIfNegative(TracePerFrame);
        if (!float.IsFinite(f: Age) || (Age < 0f)) { throw new ArgumentOutOfRangeException(actualValue: Age, message: "An indirect age target is a finite, non-negative number of seconds.", paramName: nameof(Age)); }
    }
}
