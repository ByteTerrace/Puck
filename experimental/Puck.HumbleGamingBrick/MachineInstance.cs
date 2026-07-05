using Microsoft.Extensions.DependencyInjection;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// A live machine together with the dependency-injection container that backs it. Each instance owns its own container,
/// so two instances never share a stateful component — the foundation of running many configurations side by side.
/// <see cref="Fork"/> spins up a sibling from the same configuration and loads this instance's current state into it,
/// producing an independent machine that can then diverge tick for tick. Dispose to release the container.
/// </summary>
public sealed class MachineInstance : IDisposable {
    private readonly Action<IServiceCollection> m_compose;
    private readonly ServiceProvider m_provider;
    private readonly IServiceScope m_scope;

    internal MachineInstance(
        ServiceProvider provider,
        IServiceScope scope,
        Machine machine,
        MachineConfiguration configuration,
        Action<IServiceCollection> compose
    ) {
        Configuration = configuration;
        Machine = machine;
        m_compose = compose;
        m_provider = provider;
        m_scope = scope;
    }

    /// <summary>Gets the machine driver.</summary>
    public Machine Machine { get; }
    /// <summary>Gets the configuration this instance was built from.</summary>
    public MachineConfiguration Configuration { get; }

    /// <summary>Resolves a service from this machine's container — the seam a host uses to reach into the machine for,
    /// say, its framebuffer or audio buffer.</summary>
    /// <typeparam name="TService">The service to resolve.</typeparam>
    /// <returns>The resolved service.</returns>
    /// <exception cref="InvalidOperationException">No service of type <typeparamref name="TService"/> is registered.</exception>
    public TService GetRequiredService<TService>() where TService : notnull =>
        m_scope.ServiceProvider.GetRequiredService<TService>();

    /// <summary>The LIVE device swap seam (the boot shim): retargets the running machine's emulated model without a
    /// reboot, keeping all progress. See <see cref="Machine.SwitchModel"/>.</summary>
    /// <param name="model">The model to switch to.</param>
    /// <param name="pokes">The per-ROM hardware-detection pokes that flip a GB-compatible game onto the target model's
    /// code path (empty = a bare capability flip with no code-path change).</param>
    public void SwitchModel(ConsoleModel model, ReadOnlySpan<ModePoke> pokes) =>
        Machine.SwitchModel(model: model, pokes: pokes);

    /// <summary>Builds an independent sibling machine from the same configuration and loads this instance's current
    /// state into it. The two machines share nothing afterward, so stepping or running either one leaves the other
    /// untouched — divergent branches from a common point.</summary>
    /// <returns>The forked instance. The caller owns it and must dispose it.</returns>
    public MachineInstance Fork() {
        var fork = MachineFactory.Create(configuration: Configuration, compose: m_compose);

        fork.Machine.Restore(snapshot: Machine.Snapshot());

        return fork;
    }
    /// <summary>Releases the instance's dependency-injection container.</summary>
    public void Dispose() {
        m_scope.Dispose();
        m_provider.Dispose();
    }
}
