namespace Puck.GameBoy;

/// <summary>
/// Temporary APU sub-cycle calibration knobs, read once from the environment. Each defaults to zero (no behavioral
/// change), so production builds are unaffected; a calibration sweep sets them per process to search for the phase
/// constants that align channel timing with hardware. Once a constant is found it is baked into the channel and its
/// knob removed.
/// </summary>
internal static class ApuTuning {
    /// <summary>Additive adjustment to the pulse channel's per-trigger start delay (2 MHz ticks).</summary>
    public static readonly int PulseDelay = Read(name: "PUCK_PULSE_DELAY");
    /// <summary>Additive adjustment to the pulse channel's duty index at trigger.</summary>
    public static readonly int PulseIndex = Read(name: "PUCK_PULSE_INDEX");
    /// <summary>Additive adjustment to the noise channel's alignment phase used for the start-delay calculation.</summary>
    public static readonly int NoisePhase = Read(name: "PUCK_NOISE_PHASE");

    private static int Read(string name) =>
        (int.TryParse(System.Environment.GetEnvironmentVariable(name), out var value) ? value : 0);
}
