using Microsoft.Extensions.DependencyInjection;

namespace Puck.AdvancedGamingBrick;

/// <summary>
/// A live <see cref="AdvancedGamingBrickMachine"/> together with the dependency-injection container that backs it. Each
/// instance owns its own container, so two instances never share a stateful component — the foundation of running many
/// machines side by side and of forking one into divergent copies. <see cref="Fork"/> spins up a sibling from the same
/// configuration and loads this instance's current state into it, producing an independent machine that can then
/// diverge instruction for instruction. Dispose to release the container.
/// <para>
/// Fork is pooled: building a container costs a DI stand-up, but a fork only needs the sibling's <em>state</em> replaced,
/// which a full <see cref="AdvancedGamingBrickMachine.Restore"/> does exactly. So each instance keeps a bounded pool of
/// parked siblings — a disposed fork returns to the pool instead of tearing down its container, and the next fork rents
/// it and restores into it. Fork also skips the intermediate snapshot image: it serializes this machine into a retained
/// scratch writer and reads the sibling straight back from it, so a warm fork allocates only the reader over that buffer.
/// </para>
/// </summary>
public sealed class AgbMachineInstance : IDisposable {
    private readonly Action<IServiceCollection>? m_compose;
    private readonly ServiceProvider m_provider;
    private readonly IServiceScope m_scope;
    private readonly StateWriter m_forkWriter = new(capacity: 4096);
    private AgbMachineInstancePool? m_forkPool;
    private AgbMachineInstancePool? m_returnPool;
    private int m_rentalGeneration;
    private bool m_rented;
    private bool m_disposed;

    internal AgbMachineInstance(
        ServiceProvider provider,
        IServiceScope scope,
        AdvancedGamingBrickMachine machine,
        AgbMachineConfiguration configuration,
        Action<IServiceCollection>? compose
    ) {
        Configuration = configuration;
        Machine = machine;
        m_compose = compose;
        m_provider = provider;
        m_scope = scope;
    }

    /// <summary>Gets the machine driver.</summary>
    public AdvancedGamingBrickMachine Machine { get; }
    /// <summary>Gets the configuration this instance was built from.</summary>
    public AgbMachineConfiguration Configuration { get; }

    /// <summary>Resolves a service from this machine's container — the seam a host or diagnostic uses to reach into the
    /// machine for, say, its concrete bus or an exotic decorator it composed in.</summary>
    /// <typeparam name="TService">The service to resolve.</typeparam>
    /// <returns>The resolved service.</returns>
    /// <exception cref="InvalidOperationException">No service of type <typeparamref name="TService"/> is
    /// registered.</exception>
    public TService GetRequiredService<TService>() where TService : notnull =>
        m_scope.ServiceProvider.GetRequiredService<TService>();

    /// <summary>Builds (or rents from this instance's pool) an independent sibling machine from the same configuration
    /// and loads this instance's current state into it. The two machines share nothing mutable afterward, so stepping or
    /// running either one leaves the other untouched — divergent branches from a common point. The sibling's snapshot
    /// identity matches (same BIOS and ROM). Call from one thread per subject (the machine is single-producer); the fork
    /// pool itself is thread-safe.</summary>
    /// <returns>The fork's owner handle — a fresh per-rental <see cref="AgbMachineFork"/> bound to this rental's
    /// generation. The caller owns it and must dispose it (dispose returns the underlying sibling to the pool). The pooled
    /// machine is never handed out directly, so a stale handle over an earlier rental can never reach a later renter's
    /// machine.</returns>
    public AgbMachineFork Fork() {
        m_forkPool ??= new AgbMachineInstancePool(configuration: Configuration, compose: m_compose);

        var fork = m_forkPool.Rent();

        // Serialize this machine into the retained scratch writer and restore the sibling straight from that buffer —
        // no snapshot image, no identity re-check (the pooled sibling is the same configuration by construction, so it
        // computes the same identity). Warm cost: a restore plus the reader alias.
        m_forkWriter.Reset();
        Machine.SerializeState(writer: m_forkWriter);
        fork.Machine.RestoreState(reader: m_forkWriter.OpenReader());

        return fork;
    }

    /// <summary>Releases the ROOT (non-pooled) instance — the one the factory handed out — tearing down its container and
    /// any nested fork pool. Idempotent. A pooled fork is never disposed through here: its <see cref="AgbMachineFork"/>
    /// owner routes a generation-gated dispose to the pool, so a stale handle cannot park or tear down a re-rented
    /// sibling.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        DisposeResources();
    }

    // Arms this pooled instance as a fresh rental under a NEW generation and returns the owner handle bound to it. The
    // generation a prior handle captured can never be presented again, so a stale AgbMachineFork over an earlier rental
    // resolves as not-current and stays inert — the ABA guard. Called under the pool gate (parked path) or on a
    // not-yet-shared fresh instance.
    internal AgbMachineFork Arm() {
        m_rented = true;

        return new AgbMachineFork(instance: this, generation: ++m_rentalGeneration);
    }
    // A AgbMachineFork is the CURRENT rental iff this instance is still alive, still rented, and rented under that exact
    // generation. A handle over a returned or superseded rental resolves false and can touch nothing.
    internal bool IsRentalCurrent(int generation) =>
        (!m_disposed && m_rented && (m_rentalGeneration == generation));
    // Routes the current rental handle's dispose to its pool, which resolves the generation under its gate.
    internal void ReturnRental(int generation) =>
        (m_returnPool ?? throw new InvalidOperationException(message: "A pooled fork has no return pool.")).Return(instance: this, generation: generation);
    // Marks this belonging to a fork pool: its handle's Dispose parks it there instead of tearing down its container.
    // Set once, when the pool builds the instance.
    internal void SetReturnPool(AgbMachineInstancePool pool) =>
        m_returnPool = pool;
    // The pool resolved a return (rented -> parked); any later stale dispose of ANY generation now reads not-current.
    internal void MarkReturned() =>
        m_rented = false;
    // The pool tears the instance down exactly once (on a full-pool return or pool disposal), flipping the disposed flag
    // so any later stray dispose stays a no-op.
    internal void ReleaseFromPool() {
        m_disposed = true;
        DisposeResources();
    }
    // Actually releases the container (and any nested fork pool of parked siblings). A non-pooled instance calls it from
    // Dispose; a pooled instance reaches it through ReleaseFromPool.
    internal void DisposeResources() {
        m_forkPool?.Dispose();
        m_forkPool = null;
        m_scope.Dispose();
        m_provider.Dispose();
    }
}
