namespace Puck.World.Client;

/// <summary>
/// The named refusal a world's composed render scene raises when its construction-time capacity probe does not fit an
/// engine ceiling. The composition root reports it as an ordinary boot refusal and exits, so an over-budget world
/// names what it could not fit instead of surfacing as an unhandled exception from a service factory.
/// </summary>
public sealed class WorldRenderCapacityRefusedException : InvalidOperationException {
    /// <summary>Initializes a new instance of the <see cref="WorldRenderCapacityRefusedException"/> class.</summary>
    /// <param name="message">The refusal text, naming the ceiling and the composed contributors.</param>
    /// <param name="innerException">The engine ceiling that refused.</param>
    public WorldRenderCapacityRefusedException(string message, Exception? innerException = null)
        : base(
            innerException: innerException,
            message: message
        ) {
    }
}
