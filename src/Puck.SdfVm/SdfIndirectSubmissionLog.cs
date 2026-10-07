namespace Puck.SdfVm;

/// <summary>Synchronous diagnostics emitted before an indirect dispatch enters its GPU command buffer.</summary>
public static class SdfIndirectSubmissionLog {
    /// <summary>Writes the admitted field-work estimate when the host enables submission diagnostics.</summary>
    /// <param name="write">The host's diagnostic sink, or null when disabled.</param>
    /// <param name="residency">The world residency submitting the work.</param>
    /// <param name="frame">The produced frame ordinal.</param>
    /// <param name="kind">The indirect pass or receiver kind.</param>
    /// <param name="queries">The maximum full-field evaluations, including the traversal step cap.</param>
    /// <param name="instructionCount">The submitting field program's instruction count.</param>
    /// <param name="fixedCost">Bounded cache-record traversal work independent of the field interpreter.</param>
    /// <param name="cacheFrame">The transport and receiver submission generation.</param>
    /// <param name="frameCost">The aggregate reserved cost in the produced frame.</param>
    public static void Write(Action<string>? write, string residency, long frame, string kind, long queries, int instructionCount, long fixedCost = 0, uint cacheFrame = 0, long frameCost = 0) {
        if (write is null) { return; }
        write(System.FormattableString.Invariant(formattable: $"[sdf-indirect] residency={residency} frame={frame} cache-frame={cacheFrame} kind={kind} queries={queries} instructions={instructionCount} estimated-cost={checked((SdfIndirectCost.EstimateCost(instructionCount: instructionCount, queries: queries) + fixedCost))} frame-cost={frameCost} frame-budget={SdfIndirectFrameBudget.CostLimit}"));
    }
}
