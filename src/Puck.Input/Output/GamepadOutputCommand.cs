namespace Puck.Input.Output;

/// <summary>The kind of output effect a <see cref="GamepadOutputCommand"/> carries.</summary>
public enum GamepadOutputKind : byte {
    /// <summary>Dual-motor body rumble.</summary>
    Rumble = 0,
    /// <summary>Independent left and right trigger rumble.</summary>
    TriggerRumble = 1,
    /// <summary>Controller LED color.</summary>
    Led = 2,
    /// <summary>A device-specific raw output report.</summary>
    Raw = 3,
    /// <summary>Adaptive trigger effects.</summary>
    TriggerEffect = 4,
    /// <summary>HD rumble with per-band frequency and amplitude.</summary>
    HdRumble = 5,
    /// <summary>A Home LED brightness pattern.</summary>
    HomeLedPattern = 6,
    /// <summary>A trackpad haptic.</summary>
    TrackpadHaptic = 7,
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
/// <param name="HdRumble">The HD-rumble payload (for <see cref="GamepadOutputKind.HdRumble"/>).</param>
/// <param name="HomeLedPattern">The Home LED pattern (for <see cref="GamepadOutputKind.HomeLedPattern"/>).</param>
/// <param name="TrackpadHaptic">The trackpad haptic (for <see cref="GamepadOutputKind.TrackpadHaptic"/>).</param>
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
    ulong ScheduleTick = 0UL,
    HdRumbleEffect HdRumble = default,
    HomeLedPattern HomeLedPattern = default,
    TrackpadHapticEffect TrackpadHaptic = default
);
