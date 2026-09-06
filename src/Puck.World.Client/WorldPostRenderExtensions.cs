using Puck.Shaders;

namespace Puck.World;

/// <summary>
/// The post-render extensions this build ships: the shader sets found by their <c>puck.shader.v1</c> manifests
/// under the deploy's <c>Assets/Shaders</c> tree. A world document's <c>render.extensions[].id</c> is a set's id
/// (its manifest's file stem); shipping a set is exactly shipping its manifest beside its bytecode. Read by both
/// composition roots' pre-container <see cref="WorldExtensionVocabularyHook"/> wiring (the validator checks a
/// declared id against it) and, on the desktop once composed, by <c>WorldBootComposition</c>'s Decorate closure, so
/// a document-declared id and the composable set can never disagree.
/// </summary>
/// <remarks>Consumed from a <see cref="System.Runtime.CompilerServices.ModuleInitializerAttribute"/> method, before
/// the DI container exists and before any device is opened — the scan reads file names only; a manifest is parsed
/// and validated when a set is composed.</remarks>
public static class WorldPostRenderExtensions {
    /// <summary>Gets the shipped shader sets, found under <c>Assets/Shaders</c> beside the executable.</summary>
    public static ShaderSetCatalog Shipped { get; } = ShaderSetCatalog.Scan(rootDirectory: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders"));

    /// <summary>Determines whether an extension id names a shipped shader set.</summary>
    /// <param name="extensionId">The candidate id.</param>
    /// <returns><see langword="true"/> when a manifest with that id is shipped.</returns>
    public static bool IsShipped(string extensionId) => Shipped.Contains(id: extensionId);
}
