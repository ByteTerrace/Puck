using System.Diagnostics;
using System.Globalization;

namespace Puck.Abstractions.Waiting;

/// <summary>One deadline wait <see cref="HostWaits"/> saw block: a standing wait still in progress, or one whose deadline
/// passed.</summary>
/// <param name="Reason">Why the thread waited.</param>
/// <param name="ThreadId">The waiting thread's managed id.</param>
/// <param name="ThreadName">The waiting thread's name, or <see langword="null"/>.</param>
/// <param name="StartTimestamp">When the wait began blocking, as a <see cref="System.Diagnostics.Stopwatch"/>
/// timestamp.</param>
/// <param name="BudgetTicks">The <see cref="System.Diagnostics.Stopwatch"/> ticks the wait was allowed, after the pump
/// allowance clipped it.</param>
/// <param name="OnPump">Whether the wait ran on the host loop's pump thread.</param>
public readonly record struct WaitRecord(WaitReason Reason, int ThreadId, string? ThreadName, long StartTimestamp, long BudgetTicks, bool OnPump) {
    /// <summary>Returns the wait as text: its reason, its thread (the pump, its name, or its managed id), and the
    /// milliseconds it has blocked by <paramref name="nowTimestamp"/> of its budget, such as
    /// <c>machines: the worker's step on thread 12 for 4.2 of 16.7 ms</c>.</summary>
    /// <param name="nowTimestamp">The <see cref="Stopwatch"/> timestamp the blocked time is read at.</param>
    /// <returns>The wait's text.</returns>
    public string Describe(long nowTimestamp) {
        var thread = (OnPump
            ? "the pump"
            : ((ThreadName is { Length: > 0 } name) ? $"'{name}'" : string.Create(provider: CultureInfo.InvariantCulture, handler: $"thread {ThreadId}")));

        return string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"{Reason} on {thread} for {Stopwatch.GetElapsedTime(startingTimestamp: StartTimestamp, endingTimestamp: nowTimestamp).TotalMilliseconds:0.#} of {Stopwatch.GetElapsedTime(startingTimestamp: 0L, endingTimestamp: BudgetTicks).TotalMilliseconds:0.#} ms"
        );
    }
}
