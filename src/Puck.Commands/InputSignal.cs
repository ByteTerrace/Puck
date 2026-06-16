namespace Puck.Commands;

/// <summary>
/// A raw input activation identified by a provider-neutral source id, before it is bound to any command.
/// </summary>
/// <remarks>
/// Mirrors <see cref="CommandSignal"/> but is keyed by a physical input rather than a command name. A
/// <see cref="BindingCommandSource"/> rewrites it into one or more <see cref="CommandSignal"/>s using a
/// binding table.
/// </remarks>
/// <param name="Source">The provider-neutral identifier of the input that produced the activation.</param>
/// <param name="Value">The value carried by the activation (for example, a mouse delta or a digital press).</param>
/// <param name="Phase">The transition the activation represents.</param>
/// <param name="Text">An optional text payload, such as typed characters.</param>
public readonly record struct InputSignal(string Source, CommandValue Value, CommandPhase Phase, string? Text = null);
