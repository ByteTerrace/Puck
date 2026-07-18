namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Shared helper for the POST stages: a thin wrapper over a core <see cref="AgbMachineInstance"/> that builds a
/// fully-composed, isolated machine (its own dependency scope, so two machines never share a
/// component) direct-booted to the cartridge entry point, and advances it a whole number of frames. One frame is
/// <see cref="AdvancedGamingBrickMachine.CyclesPerFrame"/> master cycles (228 scanlines &#215; 1232 dots) at the
/// 16.777216&#160;MHz system clock. The instance owns its container and must be disposed.
/// </summary>
internal sealed class PostMachine : IDisposable {
    /// <summary>The number of master clock cycles in one video frame.</summary>
    public const int CyclesPerFrame = AdvancedGamingBrickMachine.CyclesPerFrame;

    /// <summary>The AGB system clock in Hz (16.777216&#160;MHz).</summary>
    public const double SystemClockHz = 16_777_216.0;

    /// <summary>The hardware refresh rate in frames per second (~59.73&#160;Hz).</summary>
    public const double HardwareFps = (SystemClockHz / CyclesPerFrame);

    private readonly AgbMachineInstance? m_instance;
    private readonly AgbMachineFork? m_fork;

    private PostMachine(AgbMachineInstance instance) {
        m_instance = instance;
        Machine = instance.Machine;
    }

    private PostMachine(AgbMachineFork fork) {
        m_fork = fork;
        Machine = fork.Machine;
    }

    /// <summary>The composed machine. The caller drives it; the instance (or fork rental) owns its lifetime.</summary>
    public AdvancedGamingBrickMachine Machine { get; }

    /// <summary>Builds an isolated, direct-booted machine for a BIOS image and cartridge ROM, with the standard
    /// component set.</summary>
    /// <param name="bios">The 16&#160;KiB BIOS image (a zeroed stub is valid; only the stages that need a real BIOS check for one).</param>
    /// <param name="rom">The cartridge ROM image.</param>
    /// <returns>The assembled, direct-booted machine instance. The caller owns it and must dispose it.</returns>
    public static PostMachine Build(ReadOnlyMemory<byte> bios, byte[] rom) {
        var instance = AgbMachineFactory.Create(configuration: new AgbMachineConfiguration(bios: bios, rom: rom));

        instance.Machine.DirectBoot();

        return new PostMachine(instance: instance);
    }

    /// <summary>Forks this machine: builds an independent sibling from the same configuration and loads this machine's
    /// current state into it, so the two run in lock-step from a common point until they are driven differently. Only a
    /// root machine is forked (a fork is never re-forked in the batteries).</summary>
    /// <returns>The forked machine's owner handle wrapper. The caller owns it and must dispose it.</returns>
    public PostMachine Fork() =>
        new(fork: (m_instance ?? throw new InvalidOperationException(message: "Only a root PostMachine can be forked.")).Fork());

    /// <summary>Advances the machine by a whole number of frames.</summary>
    /// <param name="frames">The number of frames to run.</param>
    public void RunFrames(int frames) {
        for (var frame = 0; (frame < frames); ++frame) {
            _ = Machine.RunFrame();
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        m_instance?.Dispose();
        m_fork?.Dispose();
    }
}
