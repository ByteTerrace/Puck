using Puck.Input;
using Puck.World.Client;

namespace Puck.World;

/// <summary>The windowed World's cursor policy, the one <see cref="IWindowCursorPolicy"/> it contributes: the pointer is
/// locked while a local seat's camera drag (<c>player.orbit</c> or <c>player.steer</c> held) reads its motion, the
/// system arrow shows while the pointer is a passthrough source's window's, and the cursor moves where a local pad drives
/// the pointer over an engaged window.</summary>
/// <param name="steering">Whether a 0-based local seat's pointer motion steers its camera; an empty seat steers
/// nothing.</param>
/// <param name="windowHoldsPointer">Whether a passthrough source's window holds the pointer
/// (<see cref="WorldSourcePassthrough.HoldsPointer"/>).</param>
/// <param name="warp">The client point a pad moves the cursor to this frame, or <see langword="null"/> when it moves
/// none (<see cref="WorldSourcePassthrough.TakeWarp"/>).</param>
public sealed class WorldCursorPolicy(Func<int, bool> steering, Func<bool> windowHoldsPointer, Func<(int X, int Y)?> warp) : IWindowCursorPolicy {
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

    /// <inheritdoc/>
    public bool TryTakeWarp(out int x, out int y) {
        var to = warp();

        (x, y) = to.GetValueOrDefault();

        return to.HasValue;
    }
}
