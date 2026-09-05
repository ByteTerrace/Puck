using System.Diagnostics;

namespace Puck.HumbleGamingBrick.Post;

/// <summary>The wall-clock budget one case may spend: real time for its frame cap (a machine slower than the hardware
/// it emulates is a defect the throughput stage owns, not something a probe waits for) plus a fixed allowance for
/// building the machine and reading the ROM. A probe checks the budget between frames and a case that exceeds it
/// ends in <see cref="CaseBudgetExceededException"/>, which the evaluator records as an error row rather than letting
/// one hung core hold the whole battery.</summary>
/// <param name="DeadlineTimestamp">The <see cref="Stopwatch"/> timestamp past which the case is over budget.</param>
internal readonly record struct CaseBudget(long DeadlineTimestamp) {
    private static readonly TimeSpan Allowance = TimeSpan.FromSeconds(value: 5);

    /// <summary>Gets a budget that never expires, for stages whose duration is their own measurement.</summary>
    public static CaseBudget None =>
        new(DeadlineTimestamp: long.MaxValue);

    /// <summary>Creates a budget for a case that runs at most <paramref name="frames"/> frames, starting now.</summary>
    /// <param name="frames">The case's frame cap.</param>
    /// <returns>The budget.</returns>
    public static CaseBudget ForFrames(int frames) =>
        new(DeadlineTimestamp: (Stopwatch.GetTimestamp() + ((long)((Allowance.TotalSeconds + (frames / PostMachine.HardwareFps)) * Stopwatch.Frequency))));
    /// <summary>Throws when the budget has been exceeded.</summary>
    /// <param name="framesRun">The frames run so far, for the exception's message.</param>
    /// <exception cref="CaseBudgetExceededException">The deadline has passed.</exception>
    public void ThrowIfExceeded(int framesRun) {
        if (Stopwatch.GetTimestamp() > DeadlineTimestamp) {
            throw new CaseBudgetExceededException(message: $"wall-clock budget exceeded after {framesRun} frames");
        }
    }
}
/// <summary>Thrown by a probe whose case ran past its <see cref="CaseBudget"/>.</summary>
internal sealed class CaseBudgetExceededException(string message) : Exception(message: message);
