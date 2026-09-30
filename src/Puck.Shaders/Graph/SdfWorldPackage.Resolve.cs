namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The current render-grid color sampled by reconstruction.</summary>
    public const string CurrentColor = "currentColor";
    /// <summary>The spatial reconstruction sharpness in the resolve-only pass block.</summary>
    public const string UpscaleSharpness = "upscaleSharpness";
    /// <summary>The final spatial or temporal reconstruction part.</summary>
    public const string Resolve = "resolve";

    /// <summary>The resolve interface: the common frame values, the render-grid color and the output color. It adds no
    /// bindings to native passes.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> ResolveMembers => ResolveDeclaration.Members;

    private static class ResolveDeclaration {
        internal static readonly IReadOnlyList<ShaderInterfaceMember> Members = [
            .. Values,
            Value(name: UpscaleSharpness, type: ShaderValueType.Float),
            ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: CurrentColor, type: ShaderValueType.Float4),
            ShaderInterfaceMember.StorageImage(format: RenderGraphPackageCatalog.WorkingFormat, group: ShaderInterfaceGroup.Pass, name: Output, type: ShaderValueType.Float4),
            ShaderWorkCounters.BufferMember,
        ];
    }

    /// <summary>The reduced render-grid fragment. Traversal and shading use the render extent, and so does the color the
    /// views pass shades, which the sky pass starts and the resolve pass reads within the frame: one transient
    /// allocation every frame slot shares. Only the published color, which the resolve pass writes, has the output
    /// extent and one image per frame slot. Native views use <see cref="NativeFragment"/> and allocate no resolve
    /// resources.</summary>
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
                Transient = (resource.Transient || (resource.Name == Parts.SkyImage)),
            }),
            new ShaderPipelineResource(Name: Color, Dimensions: ShaderPipelineDimensions.Relative(), Format: RenderGraphPackageCatalog.WorkingFormat.ToString()),
            ],
            Passes: [
                .. NativeFragment.Passes.Select(selector: static pass => ((pass.Name == Parts.Views)
                ? pass with { Outputs = [new ResourceReference(Name: CurrentColor)] }
                : pass)),
            Pass(inputs: [CurrentColor], name: Resolve, outputs: [Color]) with { Members = ResolveMembers },
            ]
        );
    }
}
