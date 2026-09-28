using System.Text.Json;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.World.Client;

/// <summary>Backend-independent march budgets derived from the committed floor-device counter rows. The larger
/// backend reading sets the budget; output area and the quantized ceiling scale it for each view. This controls
/// counted work and makes no elapsed-time guarantee. The source documents are embedded at build time, so a floor
/// recording updates shipped budgets without another set of numeric constants.</summary>
public sealed class WorldDynamicResolutionBudget {
    private readonly double m_stepsPerNativePixel;

    /// <summary>Gets the budget derived once from the committed counter ceilings and their workload's low preset.</summary>
    public static WorldDynamicResolutionBudget Recorded { get; } = ReadRecorded();

    /// <summary>Derives one common budget from recorded backend rows.</summary>
    /// <param name="ceilings">The committed source rows, or an injected recording for a law.</param>
    /// <param name="recordedScale">The named render scale the source workload's low preset selected.</param>
    /// <exception cref="ArgumentException">The recording contains no positive march work or a non-positive extent.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="ceilings"/> is null.</exception>
    public WorldDynamicResolutionBudget(WorldCountersCeilings ceilings, WorldRenderScaleTier recordedScale) {
        ArgumentNullException.ThrowIfNull(argument: ceilings);
        var scale = RenderGraphExtent.Quantize(fraction: WorldRenderScaleTiers.Scale(tier: recordedScale));
        foreach (var run in ceilings.Runs) {
            if ((run.Width <= 0) || (run.Height <= 0)) {
                throw new ArgumentException(message: "A march-budget recording must have a positive output extent.", paramName: nameof(ceilings));
            }
            long steps = 0;
            foreach (var row in run.Ceilings) {
                if (string.Equals(a: row.Kind, b: GpuWork.MarchSteps.Name, comparisonType: StringComparison.Ordinal)) {
                    steps = checked(steps + row.Ceiling);
                }
            }
            m_stepsPerNativePixel = Math.Max(val1: m_stepsPerNativePixel,
                val2: (steps / (((double)run.Width) * run.Height * scale * scale)));
        }
        if (!(m_stepsPerNativePixel > 0)) {
            throw new ArgumentException(message: "A march-budget recording must contain positive gpu.march.steps rows.", paramName: nameof(ceilings));
        }
    }
    /// <summary>Returns a view's step budget at its output area and quantized ceiling, shared by both backends.</summary>
    /// <param name="width">The output width in pixels.</param>
    /// <param name="height">The output height in pixels.</param>
    /// <param name="ceiling">The authored render-scale ceiling in (0, 1].</param>
    /// <returns>A positive march-step budget.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The extent is zero or the ceiling is outside (0, 1].</exception>
    public double For(uint width, uint height, float ceiling) {
        ArgumentOutOfRangeException.ThrowIfZero(value: width);
        ArgumentOutOfRangeException.ThrowIfZero(value: height);
        if (!float.IsFinite(f: ceiling) || (ceiling <= 0) || (ceiling > 1)) {
            throw new ArgumentOutOfRangeException(paramName: nameof(ceiling));
        }
        var scale = RenderGraphExtent.Quantize(fraction: ceiling);
        return Math.Max(val1: 1, val2: (m_stepsPerNativePixel * width * height * scale * scale));
    }
    private static WorldDynamicResolutionBudget ReadRecorded() {
        var assembly = typeof(WorldDynamicResolutionBudget).Assembly;
        using var ceilingSource = assembly.GetManifestResourceStream(name: "Puck.World.Client.DynamicResolution.Ceilings.json")!;
        using var workloadSource = assembly.GetManifestResourceStream(name: "Puck.World.Client.DynamicResolution.Workload.json")!;
        var ceilings = JsonSerializer.Deserialize(utf8Json: ceilingSource, jsonTypeInfo: WorldJsonContext.Default.WorldCountersCeilings)!;
        var workload = JsonSerializer.Deserialize(utf8Json: workloadSource, jsonTypeInfo: WorldJsonContext.Default.WorldDefinition)!;
        return new WorldDynamicResolutionBudget(ceilings: ceilings,
            recordedScale: workload.Render!.LowRaw!.Value.RenderScale);
    }
}
