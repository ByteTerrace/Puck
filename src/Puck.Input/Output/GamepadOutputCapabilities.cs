namespace Puck.Input.Output;

/// <summary>The output features a controller supports. Callers branch on these before issuing an effect.</summary>
[Flags]
public enum GamepadOutputCapabilities
{
    /// <summary>No output features.</summary>
    None = 0,
    /// <summary>Dual-motor (low/high frequency) rumble.</summary>
    Rumble = 1 << 0,
    /// <summary>Trigger (impulse) rumble, as on Xbox One/Series controllers.</summary>
    TriggerRumble = 1 << 1,
    /// <summary>A settable RGB indicator (DualSense light bar, etc.).</summary>
    Led = 1 << 2,
    /// <summary>A raw device-specific effect channel (adaptive triggers, HD rumble waveforms).</summary>
    RawEffect = 1 << 3,
}
