namespace Puck.Commands;

/// <summary>
/// One entry of a binding page: a physical source mapped to a command while that page is active. Mirrors
/// <see cref="InputBindingDefinition"/> (flat, serializable, pass-through value) and adds the display metadata
/// an on-screen binding UI presents — both opaque strings the engine never interprets.
/// </summary>
/// <param name="Source">The provider-neutral input source id (an <c>InputSources</c> control, e.g. <c>gamepad.buttonSouth</c>).</param>
/// <param name="Command">The name of the command this source activates while the page is active.</param>
/// <param name="ActivateOn">The phase the binding fires on, or <see langword="null"/> for the default (press/continuous, not release).</param>
/// <param name="Label">An optional display label for the UI layer; opaque to the engine.</param>
/// <param name="Icon">An optional display icon id for the UI layer; opaque to the engine.</param>
public sealed record BindingPageEntryDefinition(
    string Source,
    string Command,
    CommandPhase? ActivateOn = null,
    string? Label = null,
    string? Icon = null
);
