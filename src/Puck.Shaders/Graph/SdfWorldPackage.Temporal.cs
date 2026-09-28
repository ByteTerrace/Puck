using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The render-grid rejection mask consumed only by temporal reconstruction.</summary>
    public const string Reactivity = "reactivity";
    /// <summary>The previous output color, including premultiplied coverage.</summary>
    public const string HistoryColor = "historyColor";
    /// <summary>The previous output surface: exact distance bits and visibility identity.</summary>
    public const string HistorySurface = "historySurface";
    /// <summary>The temporal Views interface. Native Views has no reactivity binding or image.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> TemporalViewsMembers { get; } = [
        .. Members,
        ShaderInterfaceMember.StorageImage(format: GpuPixelFormat.R32Float, group: ShaderInterfaceGroup.Pass,
            name: Reactivity, type: ShaderValueType.Float),
    ];
    /// <summary>The temporal resolve adds history and reactivity reads plus the residency tables used for motion.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> TemporalResolveMembers { get; } = [
        .. ResolveMembers,
        ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: Reactivity, type: ShaderValueType.Float),
        ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: HistoryColor, type: ShaderValueType.Float4),
        Read(element: ShaderValueType.Uint2, name: HistorySurface),
        .. Tables,
    ];
    /// <summary>The temporal fragment. Output color and surface use the graph's ordinary history versions;
    /// the render-grid reactivity image exists only in this shape.</summary>
    public static RenderGraphPackageFragment TemporalFragment => TemporalDeclaration.Fragment;

    private static class TemporalDeclaration {
        internal static readonly RenderGraphPackageFragment Fragment = new(
            InputVersions: [], OutputVersions: [Color],
            Resources: [
                .. SdfWorldPackage.Fragment.Resources.Select(static resource => resource.Name is Color or ResolvedSurface
                    ? resource with { History = true, Transient = false, Initialization = ShaderPipelineInitialization.Zero }
                    : resource),
                new ShaderPipelineResource(Name: Reactivity, Dimensions: ShaderPipelineDimensions.Render(),
                    Format: nameof(GpuPixelFormat.R32Float), Transient: true),
            ],
            Passes: [.. SdfWorldPackage.Fragment.Passes.Select(static pass => pass.Name switch {
                Parts.Views => pass with {
                    Members = TemporalViewsMembers,
                    Outputs = [.. pass.Outputs, new ResourceReference(Name: Reactivity)],
                    OutputAccesses = [.. pass.OutputAccesses, RenderGraphPortAccess.ComputeWrite],
                },
                Resolve => pass with {
                    Members = TemporalResolveMembers,
                    Inputs = [.. pass.Inputs, new ResourceReference(Name: Reactivity),
                        new ResourceReference(Name: Color, PreviousFrame: true),
                        new ResourceReference(Name: ResolvedSurface, PreviousFrame: true)],
                    InputAccesses = [.. pass.InputAccesses, RenderGraphPortAccess.ComputeRead,
                        RenderGraphPortAccess.ComputeRead, RenderGraphPortAccess.ComputeRead],
                },
                _ => pass,
            })]
        );
    }
}
