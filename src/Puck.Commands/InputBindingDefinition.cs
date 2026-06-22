namespace Puck.Commands;

/// <summary>
/// A single data-driven binding: one physical source mapped to one command. A flat, serializable shape (no
/// packed value or delegates) so a binding profile loads straight from JSON/config and feeds
/// <see cref="InputBindingTable.Build"/>. Constant-valued bindings (a control driving a fixed value rather than
/// passing its own through) are a future extension; today every definition is pass-through.
/// </summary>
/// <param name="Source">The provider-neutral input source id (an <c>InputSources</c> control, e.g. <c>gamepad.buttonSouth</c>).</param>
/// <param name="Command">The name of the command this source activates.</param>
/// <param name="RequiredModifiers">The modifiers the input must carry (the chord); defaults to <see cref="InputModifiers.None"/>.</param>
/// <param name="ActivateOn">The phase the binding fires on, or <see langword="null"/> for the default (press/continuous, not release).</param>
public readonly record struct InputBindingDefinition(
    string Source,
    string Command,
    InputModifiers RequiredModifiers = InputModifiers.None,
    CommandPhase? ActivateOn = null
);
