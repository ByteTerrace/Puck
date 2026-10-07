namespace Puck.SdfVm;

/// <summary>The residency-owned indirect producer's detail rows, separate from the shared view and sky rows.</summary>
public static class SdfWorldWorkDetails {
    /// <summary>The indirect kernels' detail indices: lattice levels, hit launches, feedback proofs and light visibility.</summary>
    public static IReadOnlyList<string> Indirect { get; } = ["near", "room", "world", "launch", "proof", "light-map", "light-fallback"];
}
