using Puck.Hosting;

namespace Puck.Shaders;

public static partial class SdfWorldPackage {
    /// <summary>The current render-grid color sampled by reconstruction.</summary>
    public const string CurrentColor = "currentColor";
    /// <summary>The spatial reconstruction sharpness in the resolve-only pass block.</summary>
    public const string UpscaleSharpness = "upscaleSharpness";
    /// <summary>The final spatial or temporal reconstruction part.</summary>
    public const string Resolve = "resolve";
    /// <summary>The render-extent reactivity the resolve reads: one float a pixel, from zero, where history follows the
    /// pixel's motion, to one, where the current frame alone is trusted.</summary>
    public const string Reactivity = "reactivity";
    /// <summary>The reactivity buffer the sky and views passes write in a temporal view.</summary>
    public const string ReactivityWritten = "reactivityRW";
    /// <summary>The preceding frame's history color the temporal resolve reprojects: its RGB the resolved color, its
    /// alpha the sample weight the pixel has accumulated.</summary>
    public const string HistoryColor = "historyColor";
    /// <summary>The history color the resolve writes for the next frame.</summary>
    public const string HistoryColorWritten = "historyColorRW";
    /// <summary>The preceding frame's history surface: per output pixel <see cref="HistorySurfaceWords"/> words, the ray
    /// distance as a float's bits and the visibility identity, of the nearest render-extent sample the resolve read.</summary>
    public const string HistorySurface = "historySurface";
    /// <summary>The history surface the resolve writes for the next frame.</summary>
    public const string HistorySurfaceWritten = "historySurfaceRW";
    /// <summary>The words one output pixel holds in the history surface: its ray distance and its identity. KEEP IN
    /// SYNC with <c>SdfHistorySurfaceWords</c> in <c>passes/sdf-resolve.comp.hlsl</c>.</summary>
    public const uint HistorySurfaceWords = 2;

    /// <summary>The resolve interface: the common frame values, the render-grid color, the output color, and what the
    /// temporal mode reads and writes beside them: the visibility records and the dispatch box it reprojects from, the
    /// reactivity, the history color and surface of the preceding frame and of this one, and the World group's tables
    /// <c>sdfReprojection</c> reads poses from. A spatial resolve binds a filler at each temporal member. It adds no
    /// bindings to native passes.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> ResolveMembers => ResolveDeclaration.Members;

    private static class ResolveDeclaration {
        internal static readonly IReadOnlyList<ShaderInterfaceMember> Members = [
            .. Values,
            Value(name: UpscaleSharpness, type: ShaderValueType.Float),
            ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: CurrentColor, type: ShaderValueType.Float4),
            ShaderInterfaceMember.StorageImage(format: RenderGraphPackageCatalog.WorkingFormat, group: ShaderInterfaceGroup.Pass, name: Output, type: ShaderValueType.Float4),
            Read(element: ShaderValueType.Uint, name: VisibilityRecords),
            Read(element: ShaderValueType.Uint, name: CullBounds),
            Read(element: ShaderValueType.Float, name: Reactivity),
            ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: HistoryColor, type: ShaderValueType.Float4),
            Read(element: ShaderValueType.Uint, name: HistorySurface),
            ShaderInterfaceMember.StorageImage(format: RenderGraphPackageCatalog.WorkingFormat, group: ShaderInterfaceGroup.Pass, name: HistoryColorWritten, type: ShaderValueType.Float4),
            Written(element: ShaderValueType.Uint, name: HistorySurfaceWritten),
            ShaderWorkCounters.BufferMember,
            .. Tables,
        ];
    }

    /// <summary>The reduced render-grid fragment. Traversal and shading use the render extent, and so does the color the
    /// views pass shades, which the sky pass starts and the resolve pass reads within the frame: one transient
    /// allocation every frame slot shares. Only the published color, which the resolve pass writes, has the output
    /// extent and one image per frame slot. Native views use <see cref="NativeFragment"/> and allocate no resolve
    /// resources; a view that reconstructs over time uses <see cref="TemporalFragment"/>.</summary>
    public static RenderGraphPackageFragment Fragment => ResolveFragment.Value;
    /// <summary>The temporal fragment: <see cref="Fragment"/>'s passes at the view's render ceiling, native or reduced,
    /// with the reactivity buffer the sky starts and views writes at the render extent, and the history the resolve
    /// keeps at the output extent: the history color and the history surface, each one allocation a frame slot that the
    /// next frame reads (<see cref="ResourceReference.PreviousFrame"/>), zero until the resolve first writes it. The
    /// resolve reads the visibility records and the dispatch box to reproject each pixel through
    /// <c>sdfReprojection</c>, and writes the output, the history color and the history surface.</summary>
    public static RenderGraphPackageFragment TemporalFragment => TemporalDeclaration.Value;

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
    private static class TemporalDeclaration {
        internal static readonly RenderGraphPackageFragment Value = new(
            InputVersions: [],
            OutputVersions: [Color],
            Resources: [
                .. ResolveFragment.Value.Resources,
                Buffer(count: ReactivityCount, name: Parts.SkyReactivity, sizeBytes: null, strideBytes: sizeof(float)),
                new ShaderPipelineResource(Count: ReactivityCount, From: Parts.SkyReactivity, Kind: ShaderPipelineResourceKind.Buffer, Name: Parts.Reactivity, StrideBytes: sizeof(float)),
                new ShaderPipelineResource(
                    Dimensions: ShaderPipelineDimensions.Relative(),
                    Format: RenderGraphPackageCatalog.WorkingFormat.ToString(),
                    History: true,
                    Initialization: ShaderPipelineInitialization.Zero,
                    Name: Parts.HistoryColor
                ),
                new ShaderPipelineResource(
                    Count: [Term(HistorySurfaceWords, ShaderPipelineCountBasis.Extent, ShaderPipelineCountBasis.Viewports)],
                    History: true,
                    Initialization: ShaderPipelineInitialization.Zero,
                    Kind: ShaderPipelineResourceKind.Buffer,
                    Name: Parts.HistorySurface,
                    StrideBytes: sizeof(uint)
                ),
            ],
            Passes: [
                .. ResolveFragment.Value.Passes.Select(selector: static pass => pass.Name switch {
                    Parts.Sky => Pass(name: Parts.Sky, outputs: [Parts.SkyImage, Parts.SkyReactivity]),
                    Parts.Views => pass with {
                        OutputAccesses = [RenderGraphPortAccess.ComputeWrite, RenderGraphPortAccess.ComputeWrite],
                        Outputs = [new ResourceReference(Name: CurrentColor), new ResourceReference(Name: Parts.Reactivity)],
                    },
                    Resolve => pass with {
                        InputAccesses = [.. Enumerable.Repeat(count: 6, element: RenderGraphPortAccess.ComputeRead)],
                        Inputs = [
                            new ResourceReference(Name: CurrentColor),
                            new ResourceReference(Name: Parts.ShadowVisibility),
                            new ResourceReference(Name: Parts.CullBounds),
                            new ResourceReference(Name: Parts.Reactivity),
                            new ResourceReference(Name: Parts.HistoryColor, PreviousFrame: true),
                            new ResourceReference(Name: Parts.HistorySurface, PreviousFrame: true),
                        ],
                        OutputAccesses = [.. Enumerable.Repeat(count: 3, element: RenderGraphPortAccess.ComputeWrite)],
                        Outputs = [new ResourceReference(Name: Color), new ResourceReference(Name: Parts.HistoryColor), new ResourceReference(Name: Parts.HistorySurface)],
                    },
                    _ => pass,
                }),
            ]
        );

        // One float a render-extent pixel.
        private static IReadOnlyList<ShaderPipelineCountTerm> ReactivityCount => [Term(1, ShaderPipelineCountBasis.RenderExtent, ShaderPipelineCountBasis.Viewports)];
    }
}
