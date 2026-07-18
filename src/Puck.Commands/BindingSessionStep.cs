namespace Puck.Commands;

/// <summary>
/// One prompt of a guided binding session: the command being bound and the physical source the session suggests
/// for it (the default the player is walked toward). The display metadata is opaque to the engine — a host
/// renders the prompt however it likes (console line, diegetic tutorial, on-screen overlay).
/// </summary>
/// <param name="Command">The name of the command this step binds (e.g. <c>overworld.jump</c>).</param>
/// <param name="SuggestedSource">The provider-neutral input source id suggested as the default (an <c>InputSources</c> control, e.g. <c>gamepad.buttonSouth</c>).</param>
/// <param name="ActivateOn">The phase the resulting binding fires on, or <see langword="null"/> for the default (press/continuous, not release).</param>
/// <param name="Label">An optional display label for the UI layer; opaque to the engine.</param>
/// <param name="Icon">An optional display icon id for the UI layer; opaque to the engine.</param>
public sealed record BindingSessionStep(
    string Command,
    string SuggestedSource,
    CommandPhase? ActivateOn = null,
    string? Label = null,
    string? Icon = null
);
