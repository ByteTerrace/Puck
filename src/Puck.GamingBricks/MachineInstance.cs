using Microsoft.Extensions.DependencyInjection;

namespace Puck.GamingBricks;

/// <summary>
/// A live machine together with the dependency-injection container that backs it. Each instance owns its own container,
/// so two instances never share a stateful component — the foundation of running many configurations side by side.
/// <see cref="Fork"/> spins up a sibling from the same configuration and loads this instance's current state into it,
/// producing an independent machine that can then diverge tick for tick. Dispose to release the container.
/// <para>
/// Fork is pooled: building a container costs a DI stand-up, but a fork only needs the sibling's <em>state</em> replaced,
/// which a full restore does exactly. So each instance keeps a bounded pool of parked siblings — a disposed fork returns
/// to the pool instead of tearing down its container, and the next fork rents it and restores into it. After warm-up a
/// fork costs a restore, not a container build. Fork also skips the intermediate snapshot image: it serializes this
/// machine into a retained scratch writer and reads the sibling straight back from it, so a warm fork allocates only the
/// reader over that buffer.
/// </para>
/// </summary>
/// <typeparam name="TMachine">The whole-machine driver type.</typeparam>
/// <typeparam name="TConfiguration">The per-machine configuration type.</typeparam>
public sealed class MachineInstance<TMachine, TConfiguration> : IDisposable
    where TMachine : ISnapshotableMachine
    where TConfiguration : notnull {
    private readonly Action<IServiceCollection>? m_compose;
    private readonly Func<TConfiguration, Action<IServiceCollection>?, MachineInstance<TMachine, TConfiguration>> m_create;
    private readonly ServiceProvider m_provider;
    private readonly IServiceScope m_scope;
    private readonly StateWriter m_forkWriter;

    private MachineInstancePool<TMachine, TConfiguration>? m_forkPool;
    private MachineInstancePool<TMachine, TConfiguration>? m_returnPool;
    private int m_rentalGeneration;
    private bool m_rented;
    private bool m_disposed;

    /// <summary>Creates a machine instance over an already-resolved container.</summary>
    /// <param name="provider">The instance's own service provider.</param>
    /// <param name="scope">The instance's own DI scope.</param>
    /// <param name="machine">The resolved machine driver.</param>
    /// <param name="configuration">The configuration this instance was built from.</param>
    /// <param name="compose">The composition callback this instance was built with — re-invoked on <see cref="Fork"/> to
    /// build each pooled sibling identically.</param>
    /// <param name="create">Builds a fresh sibling instance from a configuration and composition callback — the pool's
    /// fill delegate, typically the factory method that constructed this instance.</param>
    /// <param name="forkWriterCapacity">The scratch fork-serialization buffer's initial capacity in bytes.</param>
    public MachineInstance(
        ServiceProvider provider,
        IServiceScope scope,
        TMachine machine,
        TConfiguration configuration,
        Action<IServiceCollection>? compose,
        Func<TConfiguration, Action<IServiceCollection>?, MachineInstance<TMachine, TConfiguration>> create,
        int forkWriterCapacity = 256
    ) {
        Configuration = configuration;
        Machine = machine;
        m_compose = compose;
        m_create = create;
        m_provider = provider;
        m_scope = scope;
        m_forkWriter = new StateWriter(capacity: forkWriterCapacity);
    }

    /// <summary>Gets the machine driver.</summary>
    public TMachine Machine { get; }
    /// <summary>Gets the configuration this instance was built from.</summary>
    public TConfiguration Configuration { get; }

    /// <summary>Resolves a service from this machine's container — the seam a host uses to reach into the machine for,
    /// say, its framebuffer or audio buffer.</summary>
    /// <typeparam name="TService">The service to resolve.</typeparam>
    /// <returns>The resolved service.</returns>
    /// <exception cref="InvalidOperationException">No service of type <typeparamref name="TService"/> is registered.</exception>
    public TService GetRequiredService<TService>() where TService : notnull =>
        m_scope.ServiceProvider.GetRequiredService<TService>();
    /// <summary>Builds (or rents from this instance's pool) an independent sibling machine from the same configuration
    /// and loads this instance's current state into it. The two machines share nothing afterward, so stepping or running
    /// either one leaves the other untouched — divergent branches from a common point. Call from one thread per subject
    /// (the machine is single-producer by contract); the fork pool itself is thread-safe.</summary>
    /// <returns>The fork's owner handle — a fresh per-rental <see cref="MachineFork{TMachine, TConfiguration}"/> bound to
    /// this rental's generation. The caller owns it and must dispose it (dispose returns the underlying sibling to the
    /// pool). The pooled machine is never handed out directly, so a stale handle over an earlier rental can never reach a
    /// later renter's machine.</returns>
    public MachineFork<TMachine, TConfiguration> Fork() {
        m_forkPool ??= new MachineInstancePool<TMachine, TConfiguration>(
            compose: m_compose,
            configuration: Configuration,
            create: m_create
        );

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
    /// any nested fork pool. Idempotent. A pooled fork is never disposed through here: its
    /// <see cref="MachineFork{TMachine, TConfiguration}"/> owner routes a generation-gated dispose to the pool, so a stale
    /// handle cannot park or tear down a re-rented sibling.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        DisposeResources();
    }

    // Arms this pooled instance as a fresh rental under a NEW generation and returns the owner handle bound to it. The
    // generation a prior handle captured can never be presented again, so a stale MachineFork over an earlier rental
    // resolves as not-current and stays inert — the ABA guard. Called under the pool gate (parked path) or on a
    // not-yet-shared fresh instance.
    internal MachineFork<TMachine, TConfiguration> Arm() {
        m_rented = true;

        return new MachineFork<TMachine, TConfiguration>(
            instance: this,
            generation: ++m_rentalGeneration
        );
    }
    // A MachineFork is the CURRENT rental iff this instance is still alive, still rented, and rented under that exact
    // generation. A handle over a returned or superseded rental resolves false and can touch nothing.
    internal bool IsRentalCurrent(int generation) =>
        (!m_disposed && m_rented && (m_rentalGeneration == generation));
    // Routes the current rental handle's dispose to its pool, which resolves the generation under its gate.
    internal void ReturnRental(int generation) =>
        (m_returnPool ?? throw new InvalidOperationException(message: "A pooled fork has no return pool.")).Return(
            generation: generation,
            instance: this
        );
    // Marks this belonging to a fork pool: its handle's Dispose parks it there instead of tearing down its container.
    // Set once, when the pool builds the instance.
    internal void SetReturnPool(MachineInstancePool<TMachine, TConfiguration> pool) =>
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
