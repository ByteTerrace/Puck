using System.Diagnostics;

namespace Puck.Abstractions.Gpu;

/// <summary>The time the calling thread has spent blocked in submission-fence waits (<see cref="IGpuSubmissionFence.Wait"/>,
/// through <see cref="GpuBatchedSubmissionFence"/>, the one door every backend's fence waits pass), so a reading of the
/// thread's frame can tell work it did from time it spent waiting for the device to finish earlier work. Per thread:
/// a reader takes two readings on the thread whose waits it attributes and differences them. Presenter waits for the
/// swap chain are not submission fences and are not counted here.</summary>
public static class GpuFenceBlocking {
    [ThreadStatic] private static long BlockedTicks;

    /// <summary>Gets the calling thread's total blocked time so far, in <see cref="Stopwatch"/> timestamp ticks.</summary>
    public static long Blocked => BlockedTicks;

    internal static void Add(long ticks) => BlockedTicks += ticks;
}
