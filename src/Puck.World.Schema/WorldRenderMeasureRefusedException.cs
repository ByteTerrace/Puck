namespace Puck.World;

/// <summary>A render measurer's named refusal of a candidate definition it cannot measure: one that could never reach
/// its renderer as authored (a projection the candidate would not disclose). <see cref="WorldRenderEnvelope.TryFit"/>
/// answers it as the candidate's refusal, never as a candidate that costs nothing.</summary>
public sealed class WorldRenderMeasureRefusedException : InvalidOperationException {
    /// <summary>Initializes a new instance of the <see cref="WorldRenderMeasureRefusedException"/> class.</summary>
    /// <param name="message">Why the candidate cannot be measured.</param>
    public WorldRenderMeasureRefusedException(string message) : base(message: message) {
    }
    /// <summary>Initializes a new instance of the <see cref="WorldRenderMeasureRefusedException"/> class.</summary>
    /// <param name="message">Why the candidate cannot be measured.</param>
    /// <param name="innerException">The failure that made it unmeasurable.</param>
    public WorldRenderMeasureRefusedException(string message, Exception innerException) : base(
        innerException: innerException,
        message: message
    ) {
    }
}
