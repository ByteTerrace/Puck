namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The current render-grid color sampled by reconstruction.</summary>
    public const string CurrentColor = "currentColor";
    /// <summary>The nearest surface at each output pixel: ray-distance bits and exact visibility identity.</summary>
    public const string ResolvedSurface = "resolvedSurface";
    /// <summary>The spatial reconstruction sharpness in the resolve-only pass block.</summary>
    public const string UpscaleSharpness = "upscaleSharpness";
    /// <summary>The final spatial or temporal reconstruction part.</summary>
    public const string Resolve = "resolve";

    /// <summary>The resolve interface: the common frame values, current color and visibility, and output color plus
    /// nearest surface. It adds no bindings to native passes.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> ResolveMembers => ResolveDeclaration.Members;

    private static class ResolveDeclaration {
        internal static readonly IReadOnlyList<ShaderInterfaceMember> Members = [
            .. Values,
            Value(name: UpscaleSharpness, type: ShaderValueType.Float),
            ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: CurrentColor, type: ShaderValueType.Float4),
            Read(element: ShaderValueType.Uint, name: VisibilityRecords),
            Read(element: ShaderValueType.Uint, name: CullBounds),
            Written(element: ShaderValueType.Uint2, name: ResolvedSurface),
            ShaderInterfaceMember.StorageImage(format: RenderGraphPackageCatalog.WorkingFormat, group: ShaderInterfaceGroup.Pass, name: Output, type: ShaderValueType.Float4),
            ShaderWorkCounters.BufferMember,
        ];
    }

    /// <summary>The reduced or variable render-grid fragment. Its published color and nearest-surface storage have the
    /// output extent, while traversal and shading use the render extent. Native fixed views use <see cref="NativeFragment"/>
    /// and allocate no resolve resources.</summary>
    public static RenderGraphPackageFragment Fragment => ResolveFragment.Value;

    private static class ResolveFragment {
        internal static readonly RenderGraphPackageFragment Value = new(
            InputVersions: [],
            OutputVersions: [Color],
            Resources: [
                .. NativeFragment.Resources.Select(selector: static resource => resource with {
                Name = ((resource.Name == Color) ? CurrentColor : resource.Name),
                Dimensions = ((resource.Dimensions is null) ? null : ShaderPipelineDimensions.Render()),
                Count = ((resource.Count is null) ? null : [.. resource.Count.Select(selector: static term => term with {
                    Per = [.. term.Per.Select(selector: static basis => ((basis == ShaderPipelineCountBasis.Extent) ? ShaderPipelineCountBasis.RenderExtent : basis))],
                })]),
            }),
            new ShaderPipelineResource(Name: Color, Dimensions: ShaderPipelineDimensions.Relative(), Format: RenderGraphPackageCatalog.WorkingFormat.ToString()),
            Buffer(name: ResolvedSurface, strideBytes: (2 * sizeof(uint)), sizeBytes: null, count: [Term(1, ShaderPipelineCountBasis.Extent)]),
            ],
            Passes: [
                .. NativeFragment.Passes.Select(selector: static pass => ((pass.Name == Parts.Views)
                ? pass with { Outputs = [new ResourceReference(Name: CurrentColor)] }
                : pass)),
            Pass(inputs: [CurrentColor, Parts.ShadowVisibility, Parts.CullBounds], name: Resolve, outputs: [Color, ResolvedSurface]) with { Members = ResolveMembers },
            ]
        );
    }
}
