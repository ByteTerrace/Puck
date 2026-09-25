using Puck.Shaders;

namespace Puck.World;

/// <summary>
/// The post-render extensions this build ships: the <c>post.&lt;id&gt;</c> packages of the shipped package catalog
/// (<see cref="RenderGraphPackageCatalog.Shipped"/>), one per shader set whose <c>puck.shader.manifest.v1</c> manifest
/// ships under the deploy's <c>Assets/Shaders</c> tree. A world document's <c>render.extensions[].id</c> is a set's id
/// (its manifest's file stem); shipping a set is exactly shipping its manifest beside its bytecode. Read by both
/// composition roots' pre-container <see cref="WorldExtensionVocabularyHook"/> wiring, where the validator checks a
/// declared id against it, so a document-declared id and the package a graph names can never disagree.
/// </summary>
/// <remarks>Consumed from a <see cref="System.Runtime.CompilerServices.ModuleInitializerAttribute"/> method, before
/// the DI container exists and before any device is opened: the catalog reads each manifest's declaration and none of
/// its bytecode.</remarks>
public static class WorldPostRenderExtensions {
    /// <summary>Determines whether an extension id names a shipped post-process package.</summary>
    /// <param name="extensionId">The candidate id.</param>
    /// <returns><see langword="true"/> when the shipped catalog offers <c>post.&lt;id&gt;</c>.</returns>
    public static bool IsShipped(string extensionId) => RenderGraphPackageCatalog.Shipped.TryGet(
        id: (RenderGraphPackageCatalog.PostProcessPrefix + extensionId),
        package: out _
    );
}
