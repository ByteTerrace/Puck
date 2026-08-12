namespace Puck.Commands;

/// <summary>
/// Binds an input source to a command, as resolved by <see cref="IInputBindings"/> for the
/// <see cref="InputRouter"/>'s per-tick fold.
/// </summary>
/// <remarks>
/// When <see cref="Value"/> is <see langword="null"/>, the originating <see cref="InputSignal"/>'s own
/// value and text pass through (for example, a mouse delta driving <c>look</c>, or typed text driving
/// <c>console.insert</c>). When it is set, the constant value is used instead — for example, a digital
/// arrow key driving a two-dimensional <c>move</c> axis. <see cref="ChannelScale"/> is a third, distinct mode
/// reserved for a channel destination (see <see cref="BindingProfile.ChannelCommandPrefix"/>): unlike
/// <see cref="Value"/>'s unconditional override, it is applied differently by the live signal's own
/// <see cref="CommandValueKind"/> (see <see cref="InputRouter"/>).
/// </remarks>
/// <param name="Command">The name of the command to activate.</param>
/// <param name="Value">The constant value to send, or <see langword="null"/> to pass the input's value through.</param>
/// <param name="ActivateOn">The phase the input must be in for this binding to fire. <see langword="null"/>
/// (the default) fires an ordinary command on a press or continuous update (<see cref="CommandPhase.Started"/> or
/// <see cref="CommandPhase.Active"/>) and ignores releases. A channel destination instead receives its matching
/// <see cref="CommandPhase.Completed"/>/<see cref="CommandPhase.Canceled"/> edge automatically because a channel
/// row describes one complete physical hold, not merely its opening edge. Set this member to a specific phase to
/// author an edge-selective binding explicitly.</param>
/// <param name="ChannelScale">The declared scale for a channel destination, or <see langword="null"/> for an
/// ordinary command destination. Applied by the live signal's value kind, never guessed from nullability: a
/// <see cref="CommandValueKind.Digital"/> source (a key has no magnitude) contributes this constant; a
/// <see cref="CommandValueKind.Axis1D"/> source contributes its own sample times this scale (raw
/// <see cref="Puck.Maths.FixedQ4816"/> multiply, nearest, ties to even) — never this scale replacing the
/// sample; an <see cref="AxisComponent"/>-bearing source contributes the named component of its Axis2D sample
/// times this scale, the same multiply, so a stick's live magnitude feeds the channel instead of the Axis1D
/// fallback's constant.</param>
/// <param name="Component">The axis component an Axis2D source's live sample is decomposed to before
/// <paramref name="ChannelScale"/> applies (see <see cref="BindingSourceComponent"/>), or <see langword="null"/>
/// for an ordinary (non-component) source.</param>
/// <param name="Mode">Whether a held digital destination reads the physical control's live hold or an
/// input-side toggle latch (see <see cref="BindingEntryMode"/>). Defaults to <see cref="BindingEntryMode.Hold"/>,
/// today's behavior.</param>
public readonly record struct CommandBinding(string Command, CommandValue? Value = null, CommandPhase? ActivateOn = null, float? ChannelScale = null, AxisComponent? Component = null, BindingEntryMode Mode = BindingEntryMode.Hold);
