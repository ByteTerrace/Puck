using Microsoft.Extensions.DependencyInjection;

namespace Puck.HumbleGamingBrick;

/// <summary>
/// A bounded, thread-safe pool of parked <see cref="MachineInstance"/> siblings for a single fork lineage — all built
/// from one configuration and composition. A fork rents a parked instance (or builds one when the pool is empty) and
/// restores fresh state into it, sidestepping the ~3.4 ms container build; a disposed fork returns here rather than
/// tearing its container down. A parked instance is indistinguishable from a fresh one because a full
/// <see cref="Machine.Restore"/> replaces every byte of emulated state, and each instance owns its own DI container, so
/// no component ever aliases another machine or carries a subscription across a reuse.
/// </summary>
internal sealed class MachineInstancePool : IDisposable {
    // The park cap: high-churn fork workloads (rewind/runahead, fleet dormancy) reuse a handful of siblings; beyond this
    // a returned fork is disposed for real rather than hoarding containers.
    private const int Capacity = 8;

    private readonly Action<IServiceCollection> m_compose;
    private readonly MachineConfiguration m_configuration;
    private readonly Lock m_gate = new();
    private readonly Stack<MachineInstance> m_parked = new();
    private bool m_disposed;

    internal MachineInstancePool(MachineConfiguration configuration, Action<IServiceCollection> compose) {
        m_configuration = configuration;
        m_compose = compose;
    }

    /// <summary>Rents a parked sibling, or builds a fresh one when the pool is empty, arming it as a new rental under a
    /// fresh generation. Either way the returned handle carries this pool as its return target, so disposing it parks the
    /// sibling here. The caller must restore state into it.</summary>
    /// <returns>The per-rental owner handle, ready to have state restored into it.</returns>
    internal MachineFork Rent() {
        lock (m_gate) {
            if (m_parked.TryPop(result: out var parked)) {
                return parked.Arm();
            }
        }

        // Build outside the lock; a fresh sibling is tagged to return here on dispose, then armed as its first rental.
        var instance = MachineFactory.Create(configuration: m_configuration, compose: m_compose);

        instance.SetReturnPool(pool: this);

        return instance.Arm();
    }

    /// <summary>Returns a fork to the pool. Under the gate: resolve the disposing handle's generation against the current
    /// rental — a stale handle over a superseded rental resolves not-current and is inert (the ABA guard: it never parks
    /// or releases a sibling a later owner now holds) — then park the sibling for reuse when there is room and the pool is
    /// live, otherwise release its container.</summary>
    /// <param name="instance">The pooled sibling whose handle is disposing.</param>
    /// <param name="generation">The disposing handle's rental generation.</param>
    internal void Return(MachineInstance instance, int generation) {
        var release = false;

        lock (m_gate) {
            if (!instance.IsRentalCurrent(generation: generation)) {
                return;
            }

            instance.MarkReturned();

            if (!m_disposed && (m_parked.Count < Capacity)) {
                m_parked.Push(item: instance);

                return;
            }

            release = true;
        }

        if (release) {
            instance.ReleaseFromPool();
        }
    }

    /// <summary>Releases every parked sibling. Called when the lineage's owning instance is disposed.</summary>
    public void Dispose() {
        MachineInstance[] drained;

        lock (m_gate) {
            m_disposed = true;
            drained = m_parked.ToArray();
            m_parked.Clear();
        }

        foreach (var instance in drained) {
            instance.ReleaseFromPool();
        }
    }
}
