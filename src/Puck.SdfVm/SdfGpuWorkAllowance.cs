namespace Puck.SdfVm;

/// <summary>
/// What one job of background GPU work may take (<see cref="ISdfGpuWorkClient.TryTake"/>): the GPU time left before the
/// frame's deadline, and whether the job must fit it: other background work is queued ahead of it, or the frame is over its
/// period (<see cref="SdfGpuWorkBudget.FrameOver"/>). The queue runs every job submitted
/// before a frame whole before that frame, so the jobs queued ahead of one frame together fit one frame allowance
/// (<see cref="SdfGpuWorkBudget.FrameAllowanceSeconds"/>): <see cref="Seconds"/> is that allowance less the priced length
/// of the jobs already queued.
/// <para>Invariant: while work is queued (<see cref="Queued"/>) a client hands over no job priced past
/// <see cref="Seconds"/>. A client prices its job through <see cref="UnitsWithin"/> or <see cref="Admits"/> before it takes
/// anything, since a job once taken cannot always be handed back unchanged (a probe query handed back is cancelled). With
/// nothing queued and room in the frame the job is admitted whatever its price, cut to the allowance as far as its units
/// allow, so a unit costlier than the whole allowance (a bake lattice, which is never split) still runs, alone; a frame
/// over its period leaves no such room until the share has paid for the job (<see cref="SdfGpuWorkBudget.Hold"/>), which
/// then runs alone as with room. The service counts a job
/// that breaks the invariant (<see cref="SdfGpuWork.Describe"/>'s <c>over-deadline</c>).</para>
/// </summary>
/// <param name="Seconds">The GPU time, in seconds, the job is to fit: what is left of the frame allowance after the jobs
/// already queued, or <see cref="double.PositiveInfinity"/> for a held frame's own work, which queues no frame behind
/// it.</param>
/// <param name="Queued">Whether the job must fit <see cref="Seconds"/>: other background work is queued ahead of it, or the
/// frame is over its period, so a job past <see cref="Seconds"/> must wait rather than run.</param>
public readonly record struct SdfGpuWorkAllowance(double Seconds, bool Queued) {
    /// <summary>Gets the allowance of a held frame's own work: no bound, since no frame is queued behind it.</summary>
    public static SdfGpuWorkAllowance Unbounded => new(Queued: false, Seconds: double.PositiveInfinity);

    /// <summary>Returns whether a job priced at <paramref name="seconds"/> may be taken: always with nothing queued, and
    /// otherwise when it fits <see cref="Seconds"/>.</summary>
    /// <param name="seconds">The job's priced GPU time, in seconds.</param>
    /// <returns><see langword="true"/> when the job may be taken.</returns>
    public bool Admits(double seconds) => (!Queued || (seconds <= Seconds));
    /// <summary>Returns the most units of a kind a job may hold within the allowance at the kind's measured rate
    /// (<see cref="SdfGpuWorkRate.UnitsWithin"/>): at least one with nothing queued, and none while work is queued and one
    /// unit does not fit, in which case the client takes no job.</summary>
    /// <param name="rate">The kind's rate.</param>
    /// <param name="most">The most units a job holds whatever its price, at least one.</param>
    /// <returns>The units, zero when no job of the kind may be taken.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rate"/> is <see langword="null"/>.</exception>
    public long UnitsWithin(SdfGpuWorkRate rate, long most) {
        ArgumentNullException.ThrowIfNull(argument: rate);

        return (Admits(seconds: rate.SecondsOf(units: 1L)) ? rate.UnitsWithin(most: most, seconds: Seconds) : 0L);
    }
}
