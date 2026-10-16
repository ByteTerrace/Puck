namespace Puck.Input.Output;

/// <summary>Which trackpad actuator a <see cref="TrackpadHapticEffect"/> drives.</summary>
public enum TrackpadSide : byte {
    /// <summary>The left trackpad.</summary>
    Left = 0,
    /// <summary>The right trackpad.</summary>
    Right = 1,
    /// <summary>Both trackpads.</summary>
    Both = 2,
}
/// <summary>The trackpad haptic forms a controller plays; a <see cref="TrackpadHapticEffect"/> is one of them.</summary>
[Flags]
public enum TrackpadHapticKinds : byte {
    /// <summary>No trackpad haptics.</summary>
    None = 0,
    /// <summary>A pulse train: on and off times and a repeat count.</summary>
    Pulse = (1 << 0),
    /// <summary>One of the firmware's built-in effects (<see cref="TrackpadHapticCommand"/>).</summary>
    Command = (1 << 1),
    /// <summary>A tone at a frequency, optionally modulated by a low-frequency oscillator.</summary>
    Tone = (1 << 2),
    /// <summary>A logarithmic sine sweep between two frequencies.</summary>
    Sweep = (1 << 3),
    /// <summary>One of the firmware's stored haptic scripts.</summary>
    Script = (1 << 4),
}
/// <summary>The firmware's built-in trackpad effects (Valve <c>MsgTriggerHaptic</c> commands).</summary>
public enum TrackpadHapticCommand : byte {
    /// <summary>Stops the actuator.</summary>
    Off = 0,
    /// <summary>A short tick.</summary>
    Tick = 1,
    /// <summary>A click.</summary>
    Click = 2,
    /// <summary>A tone.</summary>
    Tone = 3,
    /// <summary>A rumble.</summary>
    Rumble = 4,
    /// <summary>A noise-modulated rumble.</summary>
    Noise = 5,
    /// <summary>A scripted effect.</summary>
    Script = 6,
    /// <summary>A sweep.</summary>
    Sweep = 7,
}
/// <summary>
/// A trackpad haptic: a pulse train, a built-in effect, a tone, a sweep or a stored script, played on one or both
/// trackpad actuators of a Steam controller (the classic Steam Controller plays pulses; the Triton plays every
/// kind). Only the fields of <see cref="Kind"/> carry meaning; build one with the factory methods.
/// </summary>
/// <param name="Kind">The haptic form, one flag.</param>
/// <param name="Side">The actuator or actuators to drive.</param>
/// <param name="GainDecibels">The gain, in dB; the firmware limits it (about -24 to +6). The Triton's pulse report
/// has no gain field, so a pulse plays at its default level there.</param>
/// <param name="OnMicroseconds">A pulse's on-time, in µs.</param>
/// <param name="OffMicroseconds">A pulse's off-time, in µs.</param>
/// <param name="Repeat">A pulse train's pulse count.</param>
/// <param name="Command">A built-in effect.</param>
/// <param name="FrequencyHertz">A tone's frequency, or a sweep's start frequency, in hertz.</param>
/// <param name="EndFrequencyHertz">A sweep's end frequency, in hertz.</param>
/// <param name="DurationMilliseconds">A tone's or sweep's duration, in milliseconds.</param>
/// <param name="LfoFrequencyHertz">A tone's low-frequency-oscillator frequency, in hertz; zero for none.</param>
/// <param name="LfoDepthPercent">A tone's oscillator depth, 0..100.</param>
/// <param name="ScriptId">A stored script's id.</param>
public readonly record struct TrackpadHapticEffect(
    TrackpadHapticKinds Kind,
    TrackpadSide Side,
    sbyte GainDecibels,
    ushort OnMicroseconds = 0,
    ushort OffMicroseconds = 0,
    ushort Repeat = 0,
    TrackpadHapticCommand Command = TrackpadHapticCommand.Off,
    ushort FrequencyHertz = 0,
    ushort EndFrequencyHertz = 0,
    ushort DurationMilliseconds = 0,
    ushort LfoFrequencyHertz = 0,
    byte LfoDepthPercent = 0,
    byte ScriptId = 0
) {
    /// <summary>Creates a built-in effect.</summary>
    /// <param name="side">The actuator or actuators to drive.</param>
    /// <param name="command">The effect.</param>
    /// <param name="gainDecibels">The gain, in dB.</param>
    /// <returns>The effect.</returns>
    public static TrackpadHapticEffect Builtin(TrackpadSide side, TrackpadHapticCommand command, sbyte gainDecibels = 0) =>
        new(Command: command, GainDecibels: gainDecibels, Kind: TrackpadHapticKinds.Command, Side: side);
    /// <summary>Creates a pulse train.</summary>
    /// <param name="side">The actuator or actuators to drive.</param>
    /// <param name="onMicroseconds">Each pulse's on-time, in µs.</param>
    /// <param name="offMicroseconds">The gap after each pulse, in µs.</param>
    /// <param name="repeat">The number of pulses.</param>
    /// <param name="gainDecibels">The gain, in dB; the classic Steam Controller applies it, the Triton's pulse report
    /// has no gain field.</param>
    /// <returns>The effect.</returns>
    public static TrackpadHapticEffect Pulse(TrackpadSide side, ushort onMicroseconds, ushort offMicroseconds, ushort repeat, sbyte gainDecibels = 0) =>
        new(GainDecibels: gainDecibels, Kind: TrackpadHapticKinds.Pulse, OffMicroseconds: offMicroseconds, OnMicroseconds: onMicroseconds, Repeat: repeat, Side: side);
    /// <summary>Creates a stored script.</summary>
    /// <param name="side">The actuator or actuators to drive.</param>
    /// <param name="scriptId">The script's id.</param>
    /// <param name="gainDecibels">The gain, in dB.</param>
    /// <returns>The effect.</returns>
    public static TrackpadHapticEffect StoredScript(TrackpadSide side, byte scriptId, sbyte gainDecibels = 0) =>
        new(GainDecibels: gainDecibels, Kind: TrackpadHapticKinds.Script, ScriptId: scriptId, Side: side);
    /// <summary>Creates a logarithmic sine sweep.</summary>
    /// <param name="side">The actuator or actuators to drive.</param>
    /// <param name="startHertz">The start frequency, in hertz.</param>
    /// <param name="endHertz">The end frequency, in hertz.</param>
    /// <param name="durationMilliseconds">The sweep's duration, in milliseconds.</param>
    /// <param name="gainDecibels">The gain, in dB.</param>
    /// <returns>The effect.</returns>
    public static TrackpadHapticEffect LogSweep(TrackpadSide side, ushort startHertz, ushort endHertz, ushort durationMilliseconds, sbyte gainDecibels = 0) =>
        new(DurationMilliseconds: durationMilliseconds, EndFrequencyHertz: endHertz, FrequencyHertz: startHertz, GainDecibels: gainDecibels, Kind: TrackpadHapticKinds.Sweep, Side: side);
    /// <summary>Creates a tone, optionally modulated by a low-frequency oscillator.</summary>
    /// <param name="side">The actuator or actuators to drive.</param>
    /// <param name="frequencyHertz">The tone's frequency, in hertz.</param>
    /// <param name="durationMilliseconds">The tone's duration, in milliseconds.</param>
    /// <param name="lfoFrequencyHertz">The oscillator's frequency, in hertz; zero for none.</param>
    /// <param name="lfoDepthPercent">The oscillator's depth, 0..100.</param>
    /// <param name="gainDecibels">The gain, in dB.</param>
    /// <returns>The effect.</returns>
    public static TrackpadHapticEffect LfoTone(TrackpadSide side, ushort frequencyHertz, ushort durationMilliseconds, ushort lfoFrequencyHertz = 0, byte lfoDepthPercent = 0, sbyte gainDecibels = 0) =>
        new(DurationMilliseconds: durationMilliseconds, FrequencyHertz: frequencyHertz, GainDecibels: gainDecibels, Kind: TrackpadHapticKinds.Tone, LfoDepthPercent: lfoDepthPercent, LfoFrequencyHertz: lfoFrequencyHertz, Side: side);
}
