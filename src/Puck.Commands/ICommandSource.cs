namespace Puck.Commands;

/// <summary>
/// Produces command activations. Keyboard, mouse, console text, AI, replay, and network input are all implemented as sources.
/// </summary>
/// <remarks>
/// Each frame, the registry pulls from every registered source by calling <see cref="Collect"/>.
/// </remarks>
public interface ICommandSource {
    /// <summary>Pushes the activations produced for the current frame into the supplied sink.</summary>
    /// <param name="sink">The sink that receives the produced activations.</param>
    void Collect(ICommandSink sink);
}
