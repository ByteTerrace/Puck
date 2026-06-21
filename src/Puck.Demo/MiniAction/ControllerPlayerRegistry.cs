using Puck.Commands;

namespace Puck.Demo.MiniAction;

/// <summary>
/// The controller↔player binding: maps a stable <see cref="InputDeviceId"/> to the external player identity the demo
/// mints for it on join. A controller binds to a player when it connects and unbinds when it disconnects; the minted
/// <see cref="Guid"/> is recorded in the roster event, so a replay reuses the exact identity.
/// </summary>
public sealed class ControllerPlayerRegistry {
    private readonly Dictionary<InputDeviceId, Guid> m_playerByDevice = [];

    /// <summary>Resolves the player bound to a device.</summary>
    public bool TryGetPlayer(InputDeviceId device, out Guid player) {
        return m_playerByDevice.TryGetValue(key: device, value: out player);
    }

    /// <summary>Binds a device to a freshly minted player identity (idempotent — returns the existing binding if any).</summary>
    public Guid Bind(InputDeviceId device) {
        if (m_playerByDevice.TryGetValue(key: device, value: out var existing)) {
            return existing;
        }

        var player = Guid.NewGuid();

        m_playerByDevice[device] = player;

        return player;
    }

    /// <summary>Removes a device's binding, yielding the player it was bound to.</summary>
    public bool Unbind(InputDeviceId device, out Guid player) {
        return (m_playerByDevice.Remove(key: device, value: out player));
    }

    /// <summary>The current device→player bindings.</summary>
    public IReadOnlyDictionary<InputDeviceId, Guid> Bindings => m_playerByDevice;
}
