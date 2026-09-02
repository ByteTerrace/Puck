namespace Puck.Commands;

/// <summary>
/// One prompt of a guided binding session: the command being bound and the physical source the session suggests
/// for it (the default the player is walked toward). The display metadata is opaque to the engine — a host
/// renders the prompt however it likes (console line, diegetic tutorial, on-screen overlay).
/// </summary>
/// <param name="Command">The name of the command this step binds (e.g. <c>overworld.jump</c>). For a
/// <paramref name="Channel"/> step this is the channel's engine-internal command name
/// (<see cref="BindingProfile.ChannelCommandName"/>) — the identity the capture matches an existing entry by, never
/// a name to write back into a document.</param>
/// <param name="SuggestedSource">The provider-neutral input source id suggested as the default (an <c>InputSources</c> control, e.g. <c>gamepad.buttonSouth</c>).</param>
/// <param name="ActivateOn">The phase the resulting binding fires on, or <see langword="null"/> for the default (press/continuous, not release).</param>
/// <param name="Channel">The channel destination this step binds, or <see langword="null"/> when the step binds a
/// plain command. Carried so <see cref="BindingSessionResult.Apply"/> can write a CHANNEL entry back rather than a
/// command entry named after the engine's internal channel command.</param>
/// <param name="Label">An optional display label for the UI layer; opaque to the engine.</param>
public sealed record BindingSessionStep(
    string Command,
    string SuggestedSource,
    CommandPhase? ActivateOn = null,
    ChannelRef? Channel = null,
    string? Label = null
);
