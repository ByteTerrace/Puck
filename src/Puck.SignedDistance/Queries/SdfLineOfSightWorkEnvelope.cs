namespace Puck.SignedDistance.Queries;

/// <summary>A structural upper bound for one line-of-sight march over a compiled SDF evaluator.</summary>
/// <param name="ProgramInstructionCount">The compiled instructions one exact field evaluation may visit.</param>
/// <param name="ExactSampleBudget">The exact samples the march may accept before exhaustion.</param>
/// <param name="BoundSampleBudget">The grid-bound samples the march may accept before exhaustion.</param>
/// <param name="MaximumSamples">The sampler calls, including the terminal call that observes exhaustion.</param>
/// <param name="MaximumProgramEvaluations">The exact program evaluations, including lazy grid-corner evaluation.</param>
/// <param name="MaximumInstructionVisits">The resulting compiled-instruction visit bound.</param>
/// <remarks>The counts carry no elapsed-time or reference-cycle price.</remarks>
public readonly record struct SdfLineOfSightWorkEnvelope(
    int ProgramInstructionCount,
    int ExactSampleBudget,
    int BoundSampleBudget,
    long MaximumSamples,
    long MaximumProgramEvaluations,
    UInt128 MaximumInstructionVisits
);
