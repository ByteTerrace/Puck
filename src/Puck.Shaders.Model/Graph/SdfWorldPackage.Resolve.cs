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
    /// <summary>The reactivity buffer the views pass writes in a temporal view.</summary>
    public const string ReactivityWritten = "reactivityRW";
    /// <summary>The preceding frame's history color the temporal resolve reprojects: its RGB the resolved lit color, its
    /// alpha the resolved coverage, both premultiplied as the lit image is.</summary>
    public const string HistoryColor = "historyColor";
    /// <summary>The history color the resolve writes for the next frame.</summary>
    public const string HistoryColorWritten = "historyColorRW";
    /// <summary>The preceding frame's history surface: per output pixel <see cref="HistorySurfaceWords"/> words, the
    /// visibility identity of the nearest render-extent sample the resolve read, then its ray distance and the sample
    /// weight the pixel has gathered as two half floats, the distance low, then the accumulated surface transport as
    /// <see cref="Parts.Transport"/> packs it.</summary>
    public const string HistorySurface = "historySurface";
    /// <summary>The history surface the resolve writes for the next frame.</summary>
    public const string HistorySurfaceWritten = "historySurfaceRW";
    /// <summary>The words one output pixel holds in the history surface: its identity, its ray distance with its gathered
    /// weight, and its transport. KEEP IN SYNC with <c>SdfHistorySurfaceWords</c> in
    /// <c>passes/sdf-resolve.comp.hlsl</c>.</summary>
    public const uint HistorySurfaceWords = 3;

    /// <summary>The resolve interface: the common frame values, the render-grid color, the resolved lit image and surface
    /// transport it writes, the visibility records and the dispatch box it reads the render grid through, and what the
    /// temporal mode reads and writes beside them: the reactivity, the history color and surface of the preceding frame
    /// and of this one, and the World group's tables <c>sdfReprojection</c> reads poses from. A spatial resolve binds a
    /// filler at each temporal member. It adds no bindings to native passes.</summary>
    public static IReadOnlyList<ShaderInterfaceMember> ResolveMembers => ResolveDeclaration.Members;

    private static class ResolveDeclaration {
        internal static readonly IReadOnlyList<ShaderInterfaceMember> Members = [
            .. Values,
            Value(name: UpscaleSharpness, type: ShaderValueType.Float),
            ShaderInterfaceMember.SampledImage(group: ShaderInterfaceGroup.Pass, name: CurrentColor, type: ShaderValueType.Float4),
            ShaderInterfaceMember.StorageImage(format: RenderGraphPackageCatalog.WorkingFormat, group: ShaderInterfaceGroup.Pass, name: Output, type: ShaderValueType.Float4),
            Read(element: ShaderValueType.Uint, name: VisibilityRecords),
            Read(element: ShaderValueType.Uint, name: CullBounds),
            Written(element: ShaderValueType.Uint, name: TransportWritten),
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
    /// views pass shades, which the resolve pass reads within the frame: one retained allocation every frame slot
    /// shares. The resolve writes the lit image and each output pixel's surface transport at the output extent, from the
    /// render grid's samples inside the dispatch box, and the sky and the composite run at the output extent over them;
    /// only the published color the composite writes has one image per frame slot. Native views use
    /// <see cref="NativeFragment"/> and allocate no resolve resources; a view that reconstructs over time uses
    /// <see cref="TemporalFragment"/>.</summary>
    public static RenderGraphPackageFragment Fragment => ResolveFragment.Value;
    /// <summary>The temporal fragment: <see cref="Fragment"/>'s passes at the view's render ceiling, native or reduced,
    /// with the reactivity buffer views writes at the render extent, and the history the resolve keeps at the output
    /// extent: the history color and the history surface, each one allocation a frame slot that the next frame reads
    /// (<see cref="ResourceReference.PreviousFrame"/>), zero until the resolve first writes it. The resolve reads the
    /// visibility records and the dispatch box to reproject each pixel through <c>sdfReprojection</c>, and writes the lit
    /// image, the surface transport, the history color and the history surface. The sky never enters the history: it is
    /// evaluated and composited after the resolve.</summary>
    public static RenderGraphPackageFragment TemporalFragment => TemporalDeclaration.Value;

    // The resolve's inputs in either mode, in port order: the render-grid color, then the visibility records and the
    // dispatch box the samples are read through.
    private static string[] ResolveInputs => [CurrentColor, Parts.ShadowVisibility, Parts.CullBounds];
    // The resolve's outputs in either mode, in port order: the lit image and the surface transport.
    private static string[] ResolveOutputs => [Parts.Lit, Parts.Transport];
    // The sky's inputs in either mode, in port order: the render-grid color views writes, whose coverage says where the sky
    // is seen, and the dispatch box it is current inside. The sky evaluates its field runs on the render grid, and the
    // composite reads them at the output extent.
    private static string[] SkyInputs => [CurrentColor, Parts.CullBounds];

    private static class ResolveFragment {
        internal static readonly RenderGraphPackageFragment Value = new(
            InputVersions: [],
            OutputVersions: [Color],
            Resources: [
                .. NativeFragment.Resources
                    .Where(predicate: static resource => !IsOutputExtent(name: resource.Name))
                    .Select(selector: static resource => resource with {
                        Dimensions = ((resource.Dimensions is null) ? null : ShaderPipelineDimensions.Render()),
                        Count = ((resource.Count is null) ? null : [.. resource.Count.Select(selector: static term => term with {
                            Per = [.. term.Per.Select(selector: static basis => ((basis == ShaderPipelineCountBasis.Extent) ? ShaderPipelineCountBasis.RenderExtent : basis))],
                        })]),
                    }),
                Image(format: RenderGraphPackageCatalog.WorkingFormat, from: null, name: CurrentColor, retained: true) with { Dimensions = ShaderPipelineDimensions.Render() },
                Image(format: RenderGraphPackageCatalog.WorkingFormat, from: null, name: Parts.Lit, retained: true),
                Buffer(count: [Term(1, ShaderPipelineCountBasis.Extent, ShaderPipelineCountBasis.Viewports)], name: Parts.Transport, sizeBytes: null, strideBytes: sizeof(uint)),
                .. SkyResources.Select(selector: static resource => resource with { Dimensions = ShaderPipelineDimensions.Render() }),
                Image(format: RenderGraphPackageCatalog.WorkingFormat, from: null, name: Color, retained: false),
            ],
            Passes: [
                .. NativeFragment.Passes.TakeWhile(predicate: static pass => (pass.Name != Parts.Sky)).Select(selector: static pass => ((pass.Name == Parts.Views)
                    ? pass with { Outputs = [new ResourceReference(Name: CurrentColor)] }
                    : pass)),
                Pass(inputs: ResolveInputs, name: Resolve, outputs: ResolveOutputs) with { Members = ResolveMembers },
                Pass(inputs: SkyInputs, name: Parts.Sky, outputs: SkyRuns) with { Members = SkyMembers },
                Pass(inputs: [Parts.Lit, Parts.Transport, .. SkyRuns], name: Parts.Composite, outputs: [Color]) with { Members = SkyMembers },
            ]
        );

        // The native fragment's resources this fragment declares again: the lit image, which the resolve writes at the
        // output extent and views writes as the current color instead, the sky's runs, which it holds at the render
        // extent, and the color.
        private static bool IsOutputExtent(string name) =>
            ((name == Parts.Lit) || (name == Color) || SkyRuns.Contains(value: name));
    }
    private static class TemporalDeclaration {
        internal static readonly RenderGraphPackageFragment Value = new(
            InputVersions: [],
            OutputVersions: [Color],
            Resources: [
                .. ResolveFragment.Value.Resources,
                Buffer(count: ReactivityCount, name: Parts.Reactivity, sizeBytes: null, strideBytes: sizeof(float)),
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
                    Parts.Views => pass with {
                        OutputAccesses = [RenderGraphPortAccess.ComputeWrite, RenderGraphPortAccess.ComputeWrite],
                        Outputs = [new ResourceReference(Name: CurrentColor), new ResourceReference(Name: Parts.Reactivity)],
                    },
                    Resolve => pass with {
                        InputAccesses = [.. Enumerable.Repeat(count: 6, element: RenderGraphPortAccess.ComputeRead)],
                        Inputs = [
                            .. ResolveInputs.Select(selector: static input => new ResourceReference(Name: input)),
                            new ResourceReference(Name: Parts.Reactivity),
                            new ResourceReference(Name: Parts.HistoryColor, PreviousFrame: true),
                            new ResourceReference(Name: Parts.HistorySurface, PreviousFrame: true),
                        ],
                        OutputAccesses = [.. Enumerable.Repeat(count: 4, element: RenderGraphPortAccess.ComputeWrite)],
                        Outputs = [
                            .. ResolveOutputs.Select(selector: static output => new ResourceReference(Name: output)),
                            new ResourceReference(Name: Parts.HistoryColor),
                            new ResourceReference(Name: Parts.HistorySurface),
                        ],
                    },
                    _ => pass,
                }),
            ]
        );

        // One float a render-extent pixel.
        private static IReadOnlyList<ShaderPipelineCountTerm> ReactivityCount => [Term(1, ShaderPipelineCountBasis.RenderExtent, ShaderPipelineCountBasis.Viewports)];
    }
}
