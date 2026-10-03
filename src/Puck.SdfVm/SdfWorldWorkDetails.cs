using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>The layer indices counted by shade/sdf-sky.hlsli, and the composite's atmosphere row after them, where
/// <c>passes/sdf-composite.comp.hlsl</c> counts one <c>gpu.sky.evaluations</c> for each atmosphere kind it evaluates at a
/// pixel (<c>SdfCompositeAtmosphereDetail</c>). The shadow pass names no detail rows: its per-slot march steps are the
/// <see cref="Puck.Abstractions.Gpu.GpuWork.ShadowSteps"/> kinds of its own row.</summary>
public static class SdfWorldWorkDetails {
    /// <summary>The composite's detail row the atmosphere's evaluations count in.</summary>
    public const string Atmosphere = "atmosphere";

    private static readonly string[] Sky = ["gradient", "disc", "stars", "clouds"];
    private static readonly string[] Composite = [.. Sky, Atmosphere];

    /// <summary>Returns the labels a world package part counts into, in kernel index order.</summary>
    /// <param name="part">The package part.</param>
    /// <returns>Its named rows, excluding the ledger's plain remainder.</returns>
    public static IReadOnlyList<string> Of(string part) => part switch {
        SdfWorldPackage.Parts.Sky => Sky,
        SdfWorldPackage.Parts.Composite => Composite,
        _ => [],
    };
}
