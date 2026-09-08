using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The fallback seat for a pointer event whose device the roster cannot yet resolve to a mapped seat — every mouse
/// carries its own <see cref="Puck.Commands.InputDeviceId"/> now (<see cref="WorldPointerSink"/> resolves each
/// event through its OWN device), so this is reached only for a genuinely unclassified device (e.g. a platform that
/// has not yet stamped one, or the brief window before a fresh mouse's first report is observed).
/// </summary>
internal static class WorldPointerSlot {
    /// <summary>Resolves the fallback seat for an unresolvable pointer event.</summary>
    /// <param name="roster">The live local-player roster.</param>
    /// <returns>Slot 0.</returns>
    public static int Resolve(PlayerRoster roster) {
        ArgumentNullException.ThrowIfNull(argument: roster);

        return 0;
    }
}
