namespace Puck.Commands;

/// <summary>
/// Represents a single command activation emitted by an <see cref="ICommandSource"/> and pushed into
/// an <see cref="ICommandSink"/>.
/// </summary>
/// <remarks>
/// The receiving sink records <paramref name="Value"/> as the command's value for the current frame
/// and runs the command's handler, subject to command-map gating.
/// </remarks>
/// <param name="Name">The name of the command being activated.</param>
/// <param name="Value">The value carried by the activation for the current frame.</param>
/// <param name="Phase">The transition the activation represents.</param>
/// <param name="Text">
/// An optional text payload for text-bearing commands (for example, a console insertion) that the
/// numeric <paramref name="Value"/> cannot represent.
/// </param>
/// <param name="DeviceId">
/// The device that produced the activation, carried through from the originating <see cref="InputSignal"/> so
/// handlers can act on the specific source (for example, rumbling the controller that pressed the button).
/// <see langword="default"/> for activations with no device (text/console).
/// </param>
/// <param name="Dispatch">
/// Whether the sink should run the command's handler for this activation. <see langword="true"/> by default;
/// a binding sets it to <see langword="false"/> for an edge it tracks for state (a held value) but does not
/// fire on — for example a key-release, which updates the polled value without re-running a press handler.
/// </param>
public readonly record struct CommandSignal(string Name, CommandValue Value, CommandPhase Phase, string? Text = null, InputDeviceId DeviceId = default, bool Dispatch = true);
