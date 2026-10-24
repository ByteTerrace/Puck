namespace Puck.SignedDistance;

/// <summary>The growable lists an <see cref="SdfProgramBuilder"/> emits into, held by a caller that builds many programs
/// one after another so each builder reuses the arrays the last one grew. One builder uses a storage at a time.</summary>
public sealed class SdfProgramBuilderStorage {
    internal List<SdfInstruction> Instructions { get; } = [];
    internal List<SdfInstanceRange> Instances { get; } = [];
    internal List<SdfMaterial> Materials { get; } = [];

    /// <summary>Empties every list, keeping its capacity; a held storage then pins no stale program beyond its arrays.</summary>
    public void Clear() {
        Instructions.Clear();
        Instances.Clear();
        Materials.Clear();
    }
}
