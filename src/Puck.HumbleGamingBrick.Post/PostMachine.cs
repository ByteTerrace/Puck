namespace Puck.HumbleGamingBrick.Post;

/// <summary>Shared helpers for the POST stages: builds a fully-composed machine (no boot ROM, so it starts at the
/// synthesized post-boot state) and advances it a whole number of frames. One dot-accurate frame is 70&#160;224 CPU
/// T-cycles (154 scanlines × 456 dots) at normal speed.</summary>
internal static class PostMachine {
    /// <summary>The number of CPU T-cycles in one video frame at normal speed.</summary>
    public const int TCyclesPerFrame = 70_224;

    /// <summary>The hardware refresh rate in frames per second (~59.73&#160;Hz).</summary>
    public const double HardwareFps = (4_194_304.0 / TCyclesPerFrame);

    /// <summary>Builds an isolated machine for a model and ROM image, with the standard component set and (by default) no
    /// boot ROM — starting at the model's seeded post-boot handoff state.</summary>
    /// <param name="model">The console model to emulate.</param>
    /// <param name="rom">The cartridge ROM image.</param>
    /// <param name="bootRom">An optional boot ROM to execute from reset instead of the seeded post-boot state — a
    /// diagnostic lever for isolating whether a seeded-state gap (rather than the emulator proper) explains a boot
    /// difference; <see langword="null"/> keeps the seeded handoff the battery normally uses.</param>
    /// <returns>The assembled machine instance. The caller owns it and must dispose it.</returns>
    public static MachineInstance Build(ConsoleModel model, byte[] rom, byte[]? bootRom = null) =>
        MachineFactory.Create(
            configuration: new MachineConfiguration(model: model, cartridgeRom: rom, bootRom: bootRom),
            compose: static services => services.AddHumbleGamingBrickComponents()
        );

    /// <summary>Advances a machine by a whole number of frames.</summary>
    /// <param name="instance">The machine to advance.</param>
    /// <param name="frames">The number of frames to run.</param>
    public static void RunFrames(MachineInstance instance, int frames) {
        for (var frame = 0; (frame < frames); ++frame) {
            instance.Machine.Run(tCycles: (ulong)TCyclesPerFrame);
        }
    }

    /// <summary>Advances a forked machine by a whole number of frames.</summary>
    /// <param name="fork">The fork rental to advance.</param>
    /// <param name="frames">The number of frames to run.</param>
    public static void RunFrames(MachineFork fork, int frames) {
        for (var frame = 0; (frame < frames); ++frame) {
            fork.Machine.Run(tCycles: (ulong)TCyclesPerFrame);
        }
    }
}
