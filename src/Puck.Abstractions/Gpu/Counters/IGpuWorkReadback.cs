namespace Puck.Abstractions.Gpu;

/// <summary>Counts a submission's kernels wrote on the GPU, which a <see cref="GpuWorkLedger"/> reads once the
/// submission has completed (<see cref="GpuWorkLedger.ReadOnCompletion"/>) and adds to the submission's pass rows before
/// it publishes them. <see cref="GpuKernelCounters"/> is the one implementation.</summary>
public interface IGpuWorkReadback {
    /// <summary>Adds the counts a completed submission left in one readback slot to its pass rows.</summary>
    /// <param name="slot">The readback slot the submission copied its counts into.</param>
    /// <param name="counts">The submission's counts: the work outside every pass, then one row per pass, each row
    /// <see cref="GpuWork.SubmissionKinds"/> long in its column order.</param>
    /// <param name="passCount">The passes the submission was recorded under; counts for a later row are
    /// dropped.</param>
    void AddTo(int slot, Span<long> counts, int passCount);
}
