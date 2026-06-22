namespace Puck.Input.Output;

/// <summary>The kind of output effect a <see cref="GamepadOutputCommand"/> carries.</summary>
public enum GamepadOutputKind : byte {
    Rumble = 0,
    TriggerRumble = 1,
    Led = 2,
    Raw = 3,
    TriggerEffect = 4,
}


/// <summary>
/// A single queued output request handed from a caller's thread to the device's I/O loop. One tagged shape
/// keeps the per-device queue homogeneous; only the field selected by <see cref="Kind"/> is meaningful.
/// </summary>
/// <param name="Kind">Selects which payload field applies.</param>
/// <param name="Rumble">The dual-motor rumble payload (for <see cref="GamepadOutputKind.Rumble"/>).</param>
/// <param name="TriggerRumble">The trigger rumble payload (for <see cref="GamepadOutputKind.TriggerRumble"/>).</param>
/// <param name="Led">The LED payload (for <see cref="GamepadOutputKind.Led"/>).</param>
/// <param name="Raw">The raw report payload (for <see cref="GamepadOutputKind.Raw"/>).</param>
/// <param name="TriggerEffectLeft">The left trigger's adaptive effect (for <see cref="GamepadOutputKind.TriggerEffect"/>).</param>
/// <param name="TriggerEffectRight">The right trigger's adaptive effect (for <see cref="GamepadOutputKind.TriggerEffect"/>).</param>
/// <param name="ScheduleTick">
/// The capture-clock (<see cref="Puck.Commands.IInputClock"/>) engine tick at which a <see cref="GamepadOutputKind.TriggerEffect"/>
/// should be applied; <c>0</c> applies it immediately. Lets a caller schedule rhythm-grade haptics ahead of time.
/// </param>
public readonly record struct GamepadOutputCommand(
    GamepadOutputKind Kind,
    RumbleEffect Rumble,
    TriggerRumbleEffect TriggerRumble,
    LedColor Led,
    byte[]? Raw,
    TriggerEffectSpec TriggerEffectLeft = default,
    TriggerEffectSpec TriggerEffectRight = default,
    ulong ScheduleTick = 0UL
);
