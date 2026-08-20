namespace Puck.SignedDistance;

/// <summary>
/// The refusal a program build raises when a declared count reaches one of the engine's frozen ceilings. Typed so a
/// composing host can name the overrun as its own refusal instead of letting an anonymous
/// <see cref="InvalidOperationException"/> escape; derived from it so existing handlers are unaffected.
/// </summary>
public sealed class SdfProgramCapacityException : InvalidOperationException {
    /// <summary>Initializes a new instance of the <see cref="SdfProgramCapacityException"/> class.</summary>
    /// <param name="message">The refusal text, naming the ceiling.</param>
    /// <param name="capacity">The ceiling's own name, e.g. <c>instances</c>.</param>
    /// <param name="limit">The ceiling's value.</param>
    public SdfProgramCapacityException(string message, string capacity, int limit)
        : base(message: message) {
        Capacity = capacity;
        Limit = limit;
    }

    /// <summary>Gets the ceiling's own name.</summary>
    public string Capacity { get; }
    /// <summary>Gets the ceiling's value.</summary>
    public int Limit { get; }
}
