namespace Puck.World.Client;

/// <summary>The runtime accounting for <see cref="WorldScreenProjection.Window"/> sessions' true, always-paid render
/// cost — one live count <c>world.faces</c> reads and echoes, since a window renders on every produced frame. It refuses
/// nothing: a window's cost is priced like any view instance's, in the render graph's schedule and
/// <c>world.budget</c>.</summary>
/// <remarks>A process-wide static counter, deliberately: exactly one <c>WorldScreenBinder</c> (the boot
/// world's own presentation) is ever live in one running <c>Puck.World</c> process — the same "no instance-addressed
/// form" fact <c>world.faces</c>' own description already states (screens are the boot instance's presentation
/// state, and a spawned instance carries neither a client nor a machine host to bind one from) — so there is no
/// second binder whose leases this counter could conflate with.</remarks>
public static class WorldSessionWindowLeases {
    private static int LiveLeaseCount;

    /// <summary>How many window sessions are live right now, process-wide.</summary>
    public static int LiveCount => LiveLeaseCount;

    /// <summary>Acquires one window lease, incrementing <see cref="LiveCount"/> until disposed.</summary>
    /// <param name="width">The window's resolved render width, pixels — carried on the lease purely for a future
    /// per-lease cost breakdown; today's read-back sums leases rather than reading this per-entry.</param>
    /// <param name="height">The window's resolved render height, pixels.</param>
    /// <returns>A disposable that releases the lease exactly once (idempotent past the first <c>Dispose</c>).</returns>
    public static IDisposable Acquire(int width, int height) {
        LiveLeaseCount++;

        return new Lease(
            height: height,
            width: width
        );
    }

    private sealed class Lease(int width, int height) : IDisposable {
        private bool m_disposed;

        public int Width { get; } = width;
        public int Height { get; } = height;

        public void Dispose() {
            if (m_disposed) {
                return;
            }

            m_disposed = true;
            LiveLeaseCount--;
        }
    }
}
