namespace Puck.SdfVm;

/// <summary>
/// Generates <see cref="FileName"/>, the renderer's declarations beside the field's instruction set
/// (<see cref="SdfIsaHlsl"/>), which it includes first: the indirect-light sources, the renderer's capacities, the
/// photometry and exposure constants, the grid flags, the light kinds, the visibility record, the mesh region's draw
/// record, the irradiance probe, the temporal resolve, the glass sums, the sky's environment map, the volumes, the
/// surface transport, the atmosphere and the debug view modes. Every value is read from the C# model, as
/// <see cref="SdfIsaHlsl"/>'s are. A pass, stage or shading module that reads any of them includes this file; the field
/// interpreter and the mesh bake read none of them and include <see cref="SdfIsaHlsl.FileName"/> alone, so a constant
/// added here moves no stored mesh's key (<c>SdfMeshBakeIdentity</c>).
/// </summary>
public static class SdfRenderHlsl {
    /// <summary>The file name of the generated include, beside <see cref="SdfIsaHlsl.FileName"/> in
    /// <c>Assets/Shaders/Sdf/isa</c>.</summary>
    public const string FileName = "sdf-render.hlsli";

    internal const string Guard = "SDF_RENDER_HLSLI";

    /// <summary>Generates the include.</summary>
    /// <returns>The HLSL text.</returns>
    /// <exception cref="InvalidOperationException">Two declarations, here or in the field's include, would share one HLSL
    /// name.</exception>
    public static string Generate() => SdfIsaHlsl.DeclareRender();
}
