namespace Puck.Commands;

/// <summary>
/// Binds an input source to a command for a <see cref="BindingCommandSource"/>.
/// </summary>
/// <remarks>
/// When <see cref="Value"/> is <see langword="null"/>, the originating <see cref="InputSignal"/>'s own
/// value and text pass through (for example, a mouse delta driving <c>look</c>, or typed text driving
/// <c>console.insert</c>). When it is set, the constant value is used instead — for example, a digital
/// arrow key driving a two-dimensional <c>move</c> axis.
/// </remarks>
/// <param name="Command">The name of the command to activate.</param>
/// <param name="Value">The constant value to send, or <see langword="null"/> to pass the input's value through.</param>
/// <param name="RequiredModifiers">The modifiers the input must carry for this binding to fire — the chord
/// it answers (e.g. <see cref="InputModifiers.Control"/> for <c>Ctrl+C</c>). Defaults to
/// <see cref="InputModifiers.None"/>, i.e. the unmodified key.</param>
/// <param name="ActivateOn">The phase the input must be in for this binding to fire. <see langword="null"/>
/// (the default) fires on a press or a continuous update (<see cref="CommandPhase.Started"/> or
/// <see cref="CommandPhase.Active"/>) and ignores releases, so a key-release never re-fires a press-bound
/// command; set it to a specific phase (such as <see cref="CommandPhase.Completed"/>) to bind that edge only.</param>
public readonly record struct CommandBinding(string Command, CommandValue? Value = null, InputModifiers RequiredModifiers = InputModifiers.None, CommandPhase? ActivateOn = null);
