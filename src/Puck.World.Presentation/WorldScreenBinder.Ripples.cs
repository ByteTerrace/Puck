using System.Diagnostics;
using Puck.SdfVm;
using Puck.World.Client;

namespace Puck.World;

public sealed partial class WorldScreenBinder {
    // The contacts the rippling openings show, and the presentation clock they and the idle waves run on.
    private readonly WorldOpeningRipples m_ripples = new();
    private readonly Stopwatch m_rippleClock = Stopwatch.StartNew();
    private readonly List<int> m_rippleBodies = [];

    /// <inheritdoc/>
    /// <remarks>An opening ripples while it shows a session whose source authors <c>ripple</c>: its contacts are the bodies
    /// of the world that come within reach of its glass (<see cref="WorldOpeningRipples"/>).</remarks>
    public bool TryRippleOf(int screen, out SdfScreenRipple ripple) {
        var row = RowOf(screen: screen);

        if (
            (row is null) ||
            (ShownOf(screen: screen) is not WorldScreenSource.Session { Ripple: true })
        ) {
            ripple = default;

            return false;
        }

        // The world's active bodies, never every index of its capacity.
        // A source that names no active list offers no bodies to touch the glass.
        m_rippleBodies.Clear();
        var capacity = 0;

        if (m_anchors is IWorldStampSource source) {
            source.CopyActiveBodies(bodies: m_rippleBodies);
            capacity = source.EntityCapacity;
        }

        ripple = m_ripples.Advance(
            active: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: m_rippleBodies),
            bodies: m_anchors,
            capacity: capacity,
            halfHeight: row.HalfHeight,
            halfWidth: row.HalfWidth,
            origin: row.Origin,
            right: row.Right,
            screen: screen,
            seconds: m_rippleClock.Elapsed.TotalSeconds,
            up: row.Up
        );

        return true;
    }
}
