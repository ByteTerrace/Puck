namespace Puck.Input.Output;

/// <summary>The output features a controller supports. Callers branch on these before issuing an effect.</summary>
[Flags]
public enum GamepadOutputCapabilities {
    /// <summary>No output features.</summary>
    None = 0,
    /// <summary>Dual-motor (low/high frequency) rumble.</summary>
    Rumble = (1 << 0),
    /// <summary>Trigger (impulse) rumble, as on Xbox One/Series controllers.</summary>
    TriggerRumble = (1 << 1),
    /// <summary>A settable RGB indicator (DualSense light bar, etc.).</summary>
    Led = (1 << 2),
    /// <summary>A raw device-specific effect channel (HD rumble waveforms, and other effects without a typed shape).</summary>
    RawEffect = (1 << 3),
    /// <summary>Typed adaptive-trigger effects (DualSense), via <see cref="IGamepadOutput.SetTriggerEffect"/>.</summary>
    TriggerEffect = (1 << 4),
    /// <summary>HD rumble with per-band frequency and amplitude (Switch Pro), via <see cref="IGamepadOutput.RumbleHd"/>.</summary>
    HdRumble = (1 << 5),
    /// <summary>A Home LED brightness pattern the controller plays itself (Switch Pro), via
    /// <see cref="IGamepadOutput.SetHomeLedPattern"/>.</summary>
    HomeLedPattern = (1 << 6),
    /// <summary>Trackpad haptics (Steam controllers), via <see cref="IGamepadOutput.PlayTrackpadHaptic"/>; which
    /// forms play is <see cref="IGamepadOutput.TrackpadHaptics"/>.</summary>
    TrackpadHaptics = (1 << 7),
}
