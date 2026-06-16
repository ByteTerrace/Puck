namespace Puck.Demo.Rendering;

/// <summary>Configuration for the <see cref="SdfViewRenderer"/>: where its compiled SPIR-V lives.</summary>
internal sealed class SdfViewRendererOptions {
    /// <summary>The directory containing the compiled shader modules.</summary>
    public required string ShaderDirectory { get; init; }

    /// <summary>The composite fragment shader file name (samples a view target onto the swapchain).</summary>
    public string CompositeFragmentShaderFileName { get; init; } = "composite.frag.spv";

    /// <summary>The SDF raymarch fragment shader file name (relative to <see cref="ShaderDirectory"/>).</summary>
    public string FragmentShaderFileName { get; init; } = "sdf-view.frag.spv";

    /// <summary>The fullscreen-triangle vertex shader file name (relative to <see cref="ShaderDirectory"/>).</summary>
    public string VertexShaderFileName { get; init; } = "fullscreen.vert.spv";
}
