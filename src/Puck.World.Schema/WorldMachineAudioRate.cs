namespace Puck.World;

/// <summary>The single-sourced machine-audio output rate — every booted <c>IScreenMachine</c> synthesizes at this
/// rate from boot (ALWAYS-ON machine audio), so a speaker binds (or a mutation adds one) at any time with no machine
/// reboot. Shared by <c>Puck.World.Server.WorldMachineHost</c> (which boots every machine and so must pass this to
/// <c>IScreenMachineEngine.Create</c>) and <c>Puck.World.Audio.WorldAudioMixer</c> (the presentation-side mixer that
/// drains the machines' rings at the SAME rate it was created with) — two projects on opposite sides of the
/// presentation firewall that must never disagree on this number.</summary>
public static class WorldMachineAudioRate {
    /// <summary>The machine-audio output rate in frames per emulated second.</summary>
    public const int SampleRate = 48_000;
}
