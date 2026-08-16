namespace Puck.GamingBricks;

/// <summary>
/// The per-rental owner handle a <see cref="MachineInstance{TMachine, TConfiguration}.Fork"/> hands back — a fresh
/// object bound to one pooled sibling under one rental generation. Every member resolves the pooled instance through
/// that generation, so a stale handle (its rental long since returned and the sibling re-rented to a later owner) can
/// reach nothing: access throws and disposal is inert. The pooled instance is never exposed directly, which is what
/// closes the ABA hole — two owners can never alias one emulated machine through a lingering reference.
/// </summary>
/// <typeparam name="TMachine">The whole-machine driver type.</typeparam>
/// <typeparam name="TConfiguration">The per-machine configuration type.</typeparam>
public sealed class MachineFork<TMachine, TConfiguration> : IDisposable
    where TMachine : ISnapshotableMachine
    where TConfiguration : notnull {
    private readonly MachineInstance<TMachine, TConfiguration> m_instance;
    private readonly int m_generation;

    internal MachineFork(MachineInstance<TMachine, TConfiguration> instance, int generation) {
        m_instance = instance;
        m_generation = generation;
    }

    /// <summary>Gets the forked machine driver — valid only while this handle is the current rental.</summary>
    /// <exception cref="ObjectDisposedException">This rental has been disposed and its sibling re-rented.</exception>
    public TMachine Machine =>
        Current.Machine;

    /// <summary>Resolves a service from the fork's container — valid only while this handle is the current rental.</summary>
    /// <typeparam name="TService">The service to resolve.</typeparam>
    /// <returns>The resolved service.</returns>
    /// <exception cref="ObjectDisposedException">This rental has been disposed and its sibling re-rented.</exception>
    public TService GetRequiredService<TService>() where TService : notnull =>
        Current.GetRequiredService<TService>();
    /// <summary>Returns the underlying sibling to the pool. Idempotent, and inert once superseded: a second call, or a
    /// call after the sibling has been re-rented to a later owner, does nothing (it never parks or tears down a machine
    /// another owner now holds).</summary>
    public void Dispose() =>
        m_instance.ReturnRental(generation: m_generation);

    // Resolves to the pooled instance only while this handle is the live rental; a stale handle throws rather than
    // reaching through to a machine it no longer owns.
    private MachineInstance<TMachine, TConfiguration> Current =>
        (m_instance.IsRentalCurrent(generation: m_generation)
            ? m_instance
            : throw new ObjectDisposedException(
                objectName: nameof(MachineFork<TMachine, TConfiguration>),
                message: "This fork rental was disposed and its machine re-rented; a stale fork handle cannot reach it."
            )
        );
}
