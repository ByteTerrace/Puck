namespace Puck.Testing;

/// <summary>Measures what a warmed body allocates on the calling thread.</summary>
/// <remarks>A body that allocates does so every time it runs. The runtime's own work on the calling thread — a
/// promoted method resolving its literals and handles on first execution, a collection another test provoked — lands
/// in whichever window happens to be open, so one window alone cannot tell the two apart.</remarks>
internal static class AllocationWindow {
    /// <summary>The most windows one measurement opens.</summary>
    public const int MaximumWindows = 16;

    /// <summary>Runs <paramref name="window"/> until one run allocates nothing, at most
    /// <see cref="MaximumWindows"/> times.</summary>
    /// <param name="window">The warmed body. It runs more than once, so it leaves its subject able to run again.</param>
    /// <returns>The fewest bytes any run allocated; zero when some run allocated nothing.</returns>
    public static long Least(Action window) {
        ArgumentNullException.ThrowIfNull(window);

        var least = long.MaxValue;

        for (var index = 0; ((index < MaximumWindows) && (least != 0L)); index++) {
            var before = GC.GetAllocatedBytesForCurrentThread();

            window();
            least = Math.Min(
                val1: least,
                val2: (GC.GetAllocatedBytesForCurrentThread() - before)
            );
        }

        return least;
    }
}
