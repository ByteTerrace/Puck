using Puck.Shaders;

namespace Puck.World;

/// <summary>
/// The probe kinds this build ships: the probe kinds found by their <c>puck.probe.v1</c> manifests under the
/// deploy's <c>Assets/Probes</c> tree. A world document's <c>probes[].kind</c> is a kind's id (its
/// manifest's file stem); shipping a kind is exactly shipping its manifest (and, for a kernel-class kind, its HLSL
/// source) beside it. Read by both composition roots' pre-container <see cref="WorldProbeVocabularyHook"/> wiring
/// (the validator checks a declared kind against it).
/// </summary>
/// <remarks>Consumed from a <see cref="System.Runtime.CompilerServices.ModuleInitializerAttribute"/> method, before
/// the DI container exists and before any device is opened — the scan reads file names only; a manifest is parsed
/// and validated once an probe starts.</remarks>
public static class WorldProbeKinds {
    /// <summary>Gets the shipped probe kinds, found under <c>Assets/Probes</c> beside the executable.</summary>
    public static ProbeKindCatalog Shipped { get; } = ProbeKindCatalog.Scan(rootDirectory: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Probes"));

    /// <summary>Determines whether a kind id names a shipped probe kind.</summary>
    /// <param name="kindId">The candidate id.</param>
    /// <returns><see langword="true"/> when a manifest with that id is shipped.</returns>
    public static bool IsShipped(string kindId) => Shipped.Contains(id: kindId);
}
