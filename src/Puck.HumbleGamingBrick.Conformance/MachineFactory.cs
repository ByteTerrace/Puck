using Microsoft.Extensions.DependencyInjection;
using Puck.HumbleGamingBrick.Interfaces;

namespace Puck.HumbleGamingBrick.Conformance;

/// <summary>A constructed machine and the DI container backing it; dispose to release both.</summary>
public sealed class MachineHandle : IDisposable {
    private readonly ServiceProvider m_provider;
    private readonly IServiceScope m_scope;

    internal MachineHandle(ServiceProvider provider, IServiceScope scope, Sm83Machine machine) {
        m_provider = provider;
        m_scope = scope;
        Machine = machine;
    }

    /// <summary>Gets the assembled machine.</summary>
    public Sm83Machine Machine { get; }

    /// <summary>Releases the per-machine DI scope and provider.</summary>
    public void Dispose() {
        m_scope.Dispose();
        m_provider.Dispose();
    }
}

/// <summary>Builds a fully wired <see cref="Sm83Machine"/> for a given ROM and model. Each call gets its own DI
/// provider and scope, so machines never share a stateful component instance — the
/// <see cref="Sm83ServiceRegistration"/> contract (a per-machine scope with its own
/// <see cref="MachineConfiguration"/> and <see cref="ICartridge"/>).</summary>
public static class MachineFactory {
    /// <summary>Creates a machine running the supplied ROM image, started at the model's post-boot state
    /// (no boot ROM needed).</summary>
    /// <param name="rom">The full cartridge ROM image.</param>
    /// <param name="model">The console model to emulate.</param>
    /// <returns>A handle owning the machine and its DI container.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rom"/> is <see langword="null"/>.</exception>
    public static MachineHandle Create(byte[] rom, ConsoleModel model) {
        ArgumentNullException.ThrowIfNull(rom);

        var services = new ServiceCollection();

        services.AddHumbleGamingBrick();
        services.AddSingleton(implementationInstance: new MachineConfiguration(model: model, bootRom: null));
        services.AddSingleton<ICartridge>(implementationInstance: Cartridge.Load(rom: rom));

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var machine = scope.ServiceProvider.GetRequiredService<Sm83Machine>();

        return new(provider: provider, scope: scope, machine: machine);
    }
}
