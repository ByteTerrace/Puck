namespace Puck.SdfVm;

/// <summary>The residency-owned indirect producer's detail rows, separate from the shared view and sky rows.</summary>
public static class SdfWorldWorkDetails {
    /// <summary>The indirect kernels' detail indices: lattice levels, then hit launches and feedback proofs.</summary>
    public static IReadOnlyList<string> Indirect { get; } = ["near", "room", "world", "launch", "proof"];
}
