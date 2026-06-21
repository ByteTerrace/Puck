namespace Puck.Input.Devices;

/// <summary>
/// The surface a <see cref="IGamepadAcquisitionSource"/> uses to publish its connections into the
/// <c>GamepadManager</c>. The manager owns the connection set, the per-frame drain, and output resolution; an
/// external source (e.g. the Windows XInput poll loop in <c>Puck.Platform</c>) drives its own acquisition and
/// pushes connections here.
/// </summary>
public interface IGamepadConnectionRegistry
{
    /// <summary>
    /// Atomically allocates the lowest free player slot, builds the connection with
    /// <paramref name="connectionFactory"/> (passed the allocated slot), and tracks it — so the slot is never
    /// double-allocated against a connection that is mid-construction. The factory must only construct the
    /// connection; it must not call back into the registry or the manager.
    /// </summary>
    /// <param name="connectionFactory">Builds the connection from its allocated zero-based player index.</param>
    /// <returns>The registered connection.</returns>
    IGamepadConnection Register(Func<int, IGamepadConnection> connectionFactory);

    /// <summary>Removes a previously registered connection from the tracked set (the caller disposes it).</summary>
    /// <param name="connection">The connection to stop tracking.</param>
    void Unregister(IGamepadConnection connection);
}
