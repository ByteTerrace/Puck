using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// Owns the seat-assignment policy for the process-wide pointer. The mouse has no device identity of its own, so it
/// follows whichever seat currently owns the keyboard and falls back to slot 0 if that device is unmapped.
/// </summary>
internal static class WorldPointerSlot {
    /// <summary>Resolves the seat the pointer currently rides.</summary>
    /// <param name="roster">The live local-player roster.</param>
    /// <returns>The keyboard's assigned slot, or slot 0 while it is unmapped.</returns>
    public static int Resolve(PlayerRoster roster) {
        ArgumentNullException.ThrowIfNull(argument: roster);

        return (roster.DeviceSlot(device: PlayerRoster.KeyboardDevice) ?? 0);
    }
}
