namespace Puck.SdfVm;

public sealed partial class SdfWorldResidency {
    /// <summary>The host's optional synchronous diagnostic sink for admitted indirect submissions.</summary>
    public Action<string>? IndirectSubmissionLog { get; set; }

    /// <summary>Reports an indirect dispatch before recording it, so a stalled device leaves its admitted work in the log.</summary>
    /// <param name="kind">The indirect pass or receiver kind.</param>
    /// <param name="queries">The maximum full-field evaluations, including the traversal step cap.</param>
    /// <param name="instructionCount">The submitting field program's instruction count.</param>
    /// <param name="frame">The indirect cache generation, separate from the produced frame.</param>
    /// <param name="fixedCost">Bounded cache-record traversal work independent of the field interpreter.</param>
    public void LogIndirectSubmission(string kind, long queries, int instructionCount, uint frame, long fixedCost = 0) =>
        SdfIndirectSubmissionLog.Write(IndirectSubmissionLog, Name, IndirectFrameBudget.Frame, kind, queries, instructionCount, fixedCost, frame, IndirectFrameBudget.Cost,
            (IndirectFrameBudget.DeviceLimit + SdfIndirectFrameBudget.AuxiliaryLimit));
}
