using Microsoft.Extensions.DependencyInjection;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Shared helper for the POST stages: builds a fully-composed, isolated Game Boy Advance machine (its own dependency
/// scope, so two machines never share a component) that direct-boots to the cartridge entry point, and advances it a
/// whole number of frames. One frame is <see cref="AdvancedGamingBrickMachine.CyclesPerFrame"/> master cycles (228 scanlines
/// &#215; 1232 dots) at the 16.777216&#160;MHz system clock. The instance owns its <see cref="ServiceProvider"/> and must
/// be disposed.
/// </summary>
internal sealed class PostMachine : IDisposable {
    /// <summary>The number of master clock cycles in one video frame.</summary>
    public const int CyclesPerFrame = AdvancedGamingBrickMachine.CyclesPerFrame;

    /// <summary>The GBA system clock in Hz (16.777216&#160;MHz).</summary>
    public const double SystemClockHz = 16_777_216.0;

    /// <summary>The hardware refresh rate in frames per second (~59.73&#160;Hz).</summary>
    public const double HardwareFps = (SystemClockHz / CyclesPerFrame);

    private readonly ServiceProvider m_provider;
    private readonly IServiceScope m_scope;

    private PostMachine(ServiceProvider provider, IServiceScope scope, AdvancedGamingBrickMachine machine) {
        m_provider = provider;
        m_scope = scope;
        Machine = machine;
    }

    /// <summary>The composed machine. The caller drives it; the instance owns its lifetime.</summary>
    public AdvancedGamingBrickMachine Machine { get; }

    /// <summary>Builds an isolated, direct-booted machine for a BIOS image and cartridge ROM, with the standard
    /// component set.</summary>
    /// <param name="bios">The 16&#160;KiB BIOS image (a zeroed stub is valid; only the stages that need a real BIOS check for one).</param>
    /// <param name="rom">The cartridge ROM image.</param>
    /// <returns>The assembled, direct-booted machine instance. The caller owns it and must dispose it.</returns>
    public static PostMachine Build(ReadOnlyMemory<byte> bios, byte[] rom) {
        var services = new ServiceCollection();

        _ = services.AddAdvancedGamingBrick();
        _ = services.AddReplacementBios(image: bios);
        _ = services.AddScoped<AgbCartridge>(implementationFactory: _ => new AgbCartridge(rom: rom));

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var machine = scope.ServiceProvider.GetRequiredService<AdvancedGamingBrickMachine>();

        machine.DirectBoot();

        return new PostMachine(provider: provider, scope: scope, machine: machine);
    }

    /// <summary>Resolves a scoped component from the machine's dependency scope.</summary>
    /// <typeparam name="T">The service type.</typeparam>
    /// <returns>The resolved component.</returns>
    public T GetRequiredService<T>() where T : notnull =>
        m_scope.ServiceProvider.GetRequiredService<T>();

    /// <summary>Advances the machine by a whole number of frames.</summary>
    /// <param name="frames">The number of frames to run.</param>
    public void RunFrames(int frames) {
        for (var frame = 0; (frame < frames); ++frame) {
            _ = Machine.RunFrame();
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        m_scope.Dispose();
        m_provider.Dispose();
    }
}
