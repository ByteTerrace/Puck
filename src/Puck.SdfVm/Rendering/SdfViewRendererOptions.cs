namespace Puck.SdfVm.Rendering;

public sealed class SdfViewRendererOptions {
    public string CompositeFragmentShaderFileName { get; init; } = "composite.frag.spv";
    public string FragmentShaderFileName { get; init; } = "sdf-view.frag.spv";
    public required string ShaderDirectory { get; init; }
    public string VertexShaderFileName { get; init; } = "fullscreen.vert.spv";
}
