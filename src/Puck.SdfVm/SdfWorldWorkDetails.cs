using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>The layer indices counted by shade/sdf-sky.hlsli. The shadow pass names no detail rows: its per-slot march steps
/// are the <see cref="Puck.Abstractions.Gpu.GpuWork.ShadowSteps"/> kinds of its own row.</summary>
public static class SdfWorldWorkDetails {
    private static readonly string[] Sky = ["gradient", "disc", "stars", "clouds"];
    private static readonly string[] Views = ["indirect"];

    /// <summary>The indirect kernels' detail indices: lattice levels, then hit launches and feedback proofs.</summary>
    public static IReadOnlyList<string> Indirect { get; } = ["near", "room", "world", "launch", "proof"];

    /// <summary>Returns the labels a world package part counts into, in kernel index order.</summary>
    /// <param name="part">The package part.</param>
    /// <returns>Its named rows, excluding the ledger's plain remainder.</returns>
    public static IReadOnlyList<string> Of(string part) => part switch {
        SdfWorldPackage.Parts.Sky or SdfWorldPackage.Parts.Composite => Sky,
        SdfWorldPackage.Parts.Views => Views,
        _ => [],
    };
}
