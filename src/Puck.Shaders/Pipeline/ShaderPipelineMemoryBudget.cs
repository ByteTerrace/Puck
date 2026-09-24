using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>
/// The device memory a shader pipeline instance may hold while it replaces its graph. A replacement is refused, before
/// anything is allocated for it, when its <see cref="ShaderPipelineMemoryAccount.PeakBytes"/> would exceed the budget;
/// the installed graph keeps running and is never freed to make room. The budget is a <see cref="DeviceLocalShare"/>
/// share of the device-local memory the device's <see cref="GpuMemoryProfile"/> reports, so it scales with the device,
/// and <see cref="UnreportedBytes"/> on a device whose profile reports none.
/// </summary>
public static class ShaderPipelineMemoryBudget {
    /// <summary>The divisor of the device-local memory a pipeline instance may hold: a quarter of it.</summary>
    public const ulong DeviceLocalShare = 4UL;
    /// <summary>The budget on a device whose profile reports no device-local memory: 512 MiB.</summary>
    public const ulong UnreportedBytes = ((512UL * 1024UL) * 1024UL);

    /// <summary>Gets the budget for a device.</summary>
    /// <param name="profile">The device's memory profile.</param>
    /// <returns>The device-local bytes divided by <see cref="DeviceLocalShare"/>; <see cref="UnreportedBytes"/> when
    /// that share is zero.</returns>
    public static ulong For(GpuMemoryProfile profile) {
        var share = (profile.DeviceLocalBytes / DeviceLocalShare);

        return ((share == 0UL)
            ? UnreportedBytes
            : share
        );
    }
}
/// <summary>
/// What installing a graph costs a shader pipeline instance, counted from the plan before anything is allocated. Bytes
/// are the storage images, storage buffers, render targets, vertex buffers and float-preview targets the graph creates;
/// descriptor pools, samplers, command pools, pipelines and shader modules are not counted.
/// </summary>
/// <param name="SteadyBytes">The bytes the graph owns once it runs: every frame slot's resources, retained history
/// included, and the float preview its selected output needs.</param>
/// <param name="PeakBytes">The bytes the instance owns at the replacement's peak: everything it owns now (the installed
/// graph with its preview, replaced objects still waiting for the GPU, published images held from them, and the capture
/// readback's staging buffer) plus the
/// candidate's <paramref name="SteadyBytes"/>, all of which exist together while the candidate allocates, less the history
/// the candidate carries from the installed graph, whose instances it takes over rather than allocates.</param>
/// <param name="BudgetBytes">The budget the peak is held to.</param>
public readonly record struct ShaderPipelineMemoryAccount(ulong SteadyBytes, ulong PeakBytes, ulong BudgetBytes) {
    /// <summary>The refusal code a candidate over the budget carries.</summary>
    public const string RefusalCode = "SHADERPIPE_BUDGET";

    /// <summary>Gets whether the peak fits the budget.</summary>
    public bool Fits => (PeakBytes <= BudgetBytes);

    /// <summary>Creates the refusal of a candidate whose peak does not fit, naming the budget and both counts.</summary>
    /// <returns>The refusal.</returns>
    public InvalidDataException Refusal() => new(message: $"[{RefusalCode}] The candidate's replacement peak is {PeakBytes} bytes ({SteadyBytes} bytes steady-state), over the pipeline budget of {BudgetBytes} bytes; the installed graph keeps running.");
}
