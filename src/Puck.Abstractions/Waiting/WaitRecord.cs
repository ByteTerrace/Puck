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
public readonly record struct WaitRecord(WaitReason Reason, int ThreadId, string? ThreadName, long StartTimestamp, long BudgetTicks, bool OnPump);
