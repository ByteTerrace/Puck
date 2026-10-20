using Puck.Input;
using Puck.World.Client;

namespace Puck.World;

/// <summary>The windowed World's cursor policy, the one <see cref="IWindowCursorPolicy"/> it contributes: the pointer is
/// locked while a local seat's camera drag (<c>player.orbit</c> or <c>player.steer</c> held) reads its motion, and the
/// system arrow shows while the pointer is a passthrough source's window's.</summary>
/// <param name="steering">Whether a 0-based local seat's pointer motion steers its camera; an empty seat steers
/// nothing.</param>
/// <param name="windowHoldsPointer">Whether a passthrough source's window holds the pointer
/// (<see cref="WorldSourcePassthrough.HoldsPointer"/>).</param>
public sealed class WorldCursorPolicy(Func<int, bool> steering, Func<bool> windowHoldsPointer) : IWindowCursorPolicy {
    /// <inheritdoc/>
    public bool LocksPointer {
        get {
            for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
                if (steering(arg: slot)) {
                    return true;
                }
            }

            return false;
        }
    }
    /// <inheritdoc/>
    public bool ShowsSystemCursor => windowHoldsPointer();
}
