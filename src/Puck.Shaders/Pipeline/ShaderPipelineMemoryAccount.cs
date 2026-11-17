namespace Puck.Shaders;

/// <summary>
/// What installing a graph costs a shader pipeline instance, counted from the plan before anything is allocated. Bytes
/// are the storage images, storage buffers, render targets, vertex buffers and preview targets the graph creates;
/// descriptor pools, samplers, command pools, pipelines and shader modules are not counted.
/// </summary>
/// <param name="SteadyBytes">The bytes the graph owns once it runs: every frame slot's resources, retained history
/// included, and the preview its selected output needs.</param>
/// <param name="PeakBytes">The bytes the instance owns at the replacement's peak: everything it owns now (the installed
/// graph with its preview, replaced objects still waiting for the GPU, published images held from them, and the capture
/// readback's staging buffer) plus the
/// candidate's <paramref name="SteadyBytes"/>, all of which exist together while the candidate allocates, less the history
/// the candidate carries from the installed graph, whose instances it takes over rather than allocates.</param>
/// <param name="BudgetBytes">The budget the peak is held to: the peak the instance has reserved in the process's memory
/// account and the room the account has left for it to raise, lowered to the instance's own cap.</param>
public readonly record struct ShaderPipelineMemoryAccount(ulong SteadyBytes, ulong PeakBytes, ulong BudgetBytes) {
    /// <summary>The refusal code a candidate over the budget carries.</summary>
    public const string RefusalCode = "SHADERPIPE_BUDGET";

    /// <summary>Gets whether the peak fits the budget.</summary>
    public bool Fits => (PeakBytes <= BudgetBytes);

    /// <summary>Creates the refusal of a candidate whose peak does not fit, naming the budget and both counts.</summary>
    /// <returns>The refusal.</returns>
    public InvalidDataException Refusal() => new(message: $"[{RefusalCode}] The candidate's replacement peak is {PeakBytes} bytes ({SteadyBytes} bytes steady-state), over the pipeline budget of {BudgetBytes} bytes; the installed graph keeps running.");
}
