using Puck.Abstractions.Counting;

namespace Puck.SignedDistance.Queries;

/// <summary>Observational work of the shared CPU field-query algorithms. Reading these totals never changes a query.</summary>
public static class SdfQueryWork {
    /// <summary>Calls to the shared march, including empty or invalid rays.</summary>
    public static WorkKind Marches { get; } = new(name: "sdf.queries.marches", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Sampler calls actually attempted, including a failed or over-budget final sample.</summary>
    public static WorkKind Samples { get; } = new(name: "sdf.queries.samples", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Successful sampler calls returning exact distances.</summary>
    public static WorkKind ExactSamples { get; } = new(name: "sdf.queries.samples.exact", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Successful sampler calls returning certified bounds.</summary>
    public static WorkKind BoundSamples { get; } = new(name: "sdf.queries.samples.bounds", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Sampler calls that could not answer.</summary>
    public static WorkKind FailedSamples { get; } = new(name: "sdf.queries.samples.failed", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Finite-difference gradient requests.</summary>
    public static WorkKind Gradients { get; } = new(name: "sdf.queries.gradients", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Distance taps actually attempted by finite-difference gradients, including failed taps.</summary>
    public static WorkKind GradientTaps { get; } = new(name: "sdf.queries.gradient-taps", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Gradient requests whose taps or normalization could not produce a gradient.</summary>
    public static WorkKind FailedGradients { get; } = new(name: "sdf.queries.gradients.failed", unit: "count", workClass: WorkClass.Pacing);
    /// <summary>Process totals; read twice to measure a window. March samples are accumulated locally and published once per march.</summary>
    public static WorkCounterSet ProcessWork { get; } = new(name: "sdf.queries", kinds: [Marches, Samples, ExactSamples, BoundSamples, FailedSamples, Gradients, GradientTaps, FailedGradients]);
}
