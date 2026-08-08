namespace Puck.Post;

/// <summary>Resolves the engine compute kernels the GPU stages dispatch. The <c>Puck.SdfVm</c> reference deploys its
/// committed bytecode (<c>Assets/Shaders/**</c>) next to the POST, exactly as it does for the demo — SPIR-V for the
/// Vulkan host tiers, DXIL for the cross-backend tier's bespoke Direct3D 12 device.</summary>
internal static class PostShaders {
    /// <summary>Reads a compiled kernel deployed under <c>Assets/Shaders</c>.</summary>
    /// <param name="folder">The kernel's folder (e.g. <c>Sdf</c>, <c>Resample</c>, <c>Viewport</c>).</param>
    /// <param name="file">The kernel file name including its bytecode extension (e.g. <c>sdf-child.comp.spv</c>).</param>
    /// <returns>The kernel bytecode.</returns>
    public static byte[] Read(string folder, string file) =>
        File.ReadAllBytes(path: Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", folder, file));
}
