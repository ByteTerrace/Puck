using System.Text.Json;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws for graphics attachments in a planned pipeline. A graphics pass writes one color image and, for a geometry pass,
/// at most one depth version; a version that forwards its predecessor is loaded, one that starts from discarded contents
/// is cleared, and what nothing uses afterwards is discarded. The render pass leaves every attachment in its attachment
/// layout, so a planned barrier transitions it for a sampling consumer. The planner refuses, by name, every
/// attachment or geometry declaration no backend executes.
/// </summary>
public sealed class ShaderPipelineAttachmentLawTests {
    private const uint Extent = 16;

    private static ShaderPipelineResource Color(string name, string? from = null, uint extent = Extent) => new(
        Dimensions: ShaderPipelineDimensions.Absolute(
            height: extent,
            width: extent
        ),
        Format: "R8G8B8A8Unorm",
        From: from,
        Name: name
    );
    private static ShaderPipelineResource Depth(string name, string? from = null) => new(
        Dimensions: ShaderPipelineDimensions.Absolute(
            height: Extent,
            width: Extent
        ),
        Format: "D32Float",
        From: from,
        Kind: ShaderPipelineResourceKind.Depth,
        Name: name
    );
    // Two triangles over a quad whose second triangle is listed first, with a shared edge named in both directions.
    private static ShaderPipelineGeometry Quad() => new(
        Attributes: [new ShaderPipelineVertexAttribute(
            Format: "R32G32B32Float",
            Location: 0
        )],
        IndexFormat: ShaderPipelineIndexFormat.UInt16,
        Indices: [2, 1, 3, 0, 1, 2],
        StrideBytes: 12,
        VertexEntryPoint: "vs",
        Vertices: [-1f, -1f, 0.5f, 1f, -1f, 0.5f, -1f, 1f, 0.5f, 1f, 1f, 0.5f]
    );
    private static ShaderPipelinePass Geometry(string name, string[] outputs, ShaderPipelineGeometry? geometry = null, ShaderPipelineDepthCompare? compare = null) => new(
        DepthCompare: compare,
        EntryPoint: "ps",
        Geometry: (geometry ?? Quad()),
        Inputs: [],
        Kind: ShaderPipelineDocumentPassKind.Geometry,
        Name: name,
        Outputs: [.. outputs.Select(selector: static output => new ResourceReference(Name: output))],
        Source: $"{name}.hlsl"
    );
    private static ShaderPipelinePass Sample(string input, string output) => new(
        EntryPoint: "main",
        Inputs: [new ResourceReference(
            Binding: 1,
            Name: input
        )],
        Kind: ShaderPipelineDocumentPassKind.Compute,
        Name: "sample",
        Outputs: [new ResourceReference(
            Binding: 0,
            Name: output
        )],
        Source: "sample.hlsl"
    );
    // near clears c0 and d0 and draws; far continues both, testing against what near left; a compute pass samples c1.
    // The passes are declared in reverse, so the order is the planner's.
    private static ShaderPipelineDefinition Layers(Func<ShaderPipelineResource[], ShaderPipelineResource[]>? resources = null, Func<ShaderPipelinePass[], ShaderPipelinePass[]>? passes = null, string[]? outputs = null) {
        ShaderPipelineResource[] declared = [
            Color(name: "c0"),
            Color(
                from: "c0",
                name: "c1"
            ),
            Depth(name: "d0"),
            Depth(
                from: "d0",
                name: "d1"
            ),
            Color(name: "image"),
        ];
        ShaderPipelinePass[] written = [
            Sample(
                input: "c1",
                output: "image"
            ),
            Geometry(
                compare: ShaderPipelineDepthCompare.Less,
                name: "far",
                outputs: ["c1", "d1"]
            ),
            Geometry(
                compare: ShaderPipelineDepthCompare.Less,
                name: "near",
                outputs: ["d0", "c0"]
            ),
        ];

        return new ShaderPipelineDefinition(
            name: "layers",
            outputs: (outputs ?? ["image"]),
            passes: (passes?.Invoke(arg: written) ?? written),
            resources: (resources?.Invoke(arg: declared) ?? declared)
        );
    }
    // seed writes an image in a compute pass, and a fullscreen pass continues it, drawing over what seed left.
    private static ShaderPipelineDefinition Tint() => new(
        name: "tint",
        outputs: ["tinted"],
        passes: [
            new ShaderPipelinePass(
                EntryPoint: "main",
                Kind: ShaderPipelineDocumentPassKind.Fullscreen,
                Name: "tint",
                Outputs: [new ResourceReference(Name: "tinted")],
                Source: "tint.hlsl"
            ),
            new ShaderPipelinePass(
                EntryPoint: "main",
                Kind: ShaderPipelineDocumentPassKind.Compute,
                Name: "seed",
                Outputs: [new ResourceReference(Name: "seeded")],
                Source: "seed.hlsl"
            ),
        ],
        resources: [
            Color(name: "seeded"),
            Color(
                from: "seeded",
                name: "tinted"
            ),
        ]
    );
    private static ShaderPipelinePlan Plan(ShaderPipelineDefinition definition) => new ShaderPipelineCompiler().Compile(definition: definition);
    private static CompiledShaderPipeline Compiled(ShaderPipelinePlan plan) {
        ReadOnlyMemory<byte> bytecode = new byte[] { 0x03, 0x02, 0x23, 0x07 };

        Dictionary<ShaderStage, ReadOnlyMemory<byte>> Stages(ShaderPipelineDocumentPassKind kind) => ((kind == ShaderPipelineDocumentPassKind.Compute)
            ? new() { [ShaderStage.Compute] = bytecode }
            : new() { [ShaderStage.Vertex] = bytecode, [ShaderStage.Fragment] = bytecode });

        return new CompiledShaderPipeline(
            plan: plan,
            shaders: plan.Passes.ToDictionary(
                elementSelector: pass => new CompiledShader(
                    diagnostics: [],
                    dxil: Stages(kind: pass.Declaration.Kind),
                    name: pass.Name,
                    sourceHash: pass.Name,
                    sourcePath: $"{pass.Name}.hlsl",
                    spirv: Stages(kind: pass.Declaration.Kind)
                ),
                keySelector: static pass => pass.Name
            )
        );
    }
    private static ShaderPipelineRenderNode InstalledNode(FakePipelineGpu gpu, ShaderPipelinePlan plan) {
        const uint InFlight = 3;
        var node = new ShaderPipelineRenderNode(
            deviceContext: gpu,
            gpu: gpu,
            graphics: gpu,
            height: Extent,
            hostsOnDirectX: false,
            inFlightFrames: InFlight,
            name: "attachments",
            width: Extent
        );

        node.Swap(pipeline: Compiled(plan: plan));
        _ = node.ProduceUntilInstalled();
        for (var frame = 0; (frame < ((int)(InFlight * 3))); frame++) {
            _ = node.ProduceFrame(context: default);
        }
        Assert.True(condition: node.IsReady);

        return node;
    }
    private static IReadOnlyList<ShaderPipelineDiagnostic> Refusal(ShaderPipelineDefinition definition) =>
        Assert.Throws<ShaderPipelineCompilationException>(testCode: () => Plan(definition: definition)).Diagnostics;
    private static ShaderPipelineResource[] Replace(ShaderPipelineResource[] resources, string name, Func<ShaderPipelineResource, ShaderPipelineResource> change) =>
        [.. resources.Select(selector: resource => ((resource.Name == name)
            ? change(arg: resource)
            : resource))];
    private static ShaderPipelinePass[] ReplacePass(ShaderPipelinePass[] passes, string name, Func<ShaderPipelinePass, ShaderPipelinePass> change) =>
        [.. passes.Select(selector: pass => ((pass.Name == name)
            ? change(arg: pass)
            : pass))];
    private static ShaderPipelineDefinition WithGeometry(Func<ShaderPipelineGeometry, ShaderPipelineGeometry> change) =>
        Layers(passes: passes => ReplacePass(
            change: pass => (pass with { Geometry = change(arg: pass.Geometry!) }),
            name: "near",
            passes: passes
        ));

    [Fact]
    public void TwoGraphicsUsesOfPreservedAttachmentContentThenASamplingConsumer() {
        var plan = Plan(definition: Layers());
        var near = plan.Passes.Single(predicate: static pass => (pass.Name == "near"));
        var far = plan.Passes.Single(predicate: static pass => (pass.Name == "far"));
        var accesses = plan.Passes.SelectMany(selector: static pass => pass.Accesses.Select(selector: access => (Pass: pass.Name, access.Version, access.Use.Access, access.Barrier.Kind, access.Barrier.OldLayout, access.Barrier.NewLayout))).Where(predicate: static access => (access.Version is "c0" or "c1" or "d0" or "d1")).ToArray();

        Assert.Equal(
            actual: plan.PassOrder,
            expected: ["near", "far", "sample"]
        );
        // The color attachment leads, whatever order the pass names its outputs in. near starts both attachments from
        // discarded contents and keeps them for far, which continues them; far's depth is used by nothing after it.
        Assert.Equal(
            actual: near.Attachments.Select(selector: static attachment => (attachment.Version, attachment.Depth, attachment.Load, attachment.Store)),
            expected: [
                ("c0", false, GpuAttachmentLoad.Clear, GpuAttachmentStore.Store),
                ("d0", true, GpuAttachmentLoad.Clear, GpuAttachmentStore.Store),
            ]
        );
        Assert.Equal(
            actual: far.Attachments.Select(selector: static attachment => (attachment.Version, attachment.Depth, attachment.Load, attachment.Store)),
            expected: [
                ("c1", false, GpuAttachmentLoad.Load, GpuAttachmentStore.Store),
                ("d1", true, GpuAttachmentLoad.Load, GpuAttachmentStore.Discard),
            ]
        );
        // Each chain is one storage. The color's first use transitions into the attachment layout; the second continues
        // it there, reading what the first wrote, behind a memory barrier; the consumer's own barrier makes the color
        // shader-readable.
        // In the steady state the depth chain starts where the last frame left it, so even its clearing use needs only a
        // memory barrier; a new instance's first use starts from the node's override instead.
        Assert.Equal(
            expected: [plan.FindResource(name: "c0")!.Storage, plan.FindResource(name: "d0")!.Storage],
            actual: [plan.FindResource(name: "c1")!.Storage, plan.FindResource(name: "d1")!.Storage]
        );
        Assert.Equal(
            actual: accesses,
            expected: [
                ("near", "d0", GpuAccess.DepthAttachmentRead | GpuAccess.DepthAttachmentWrite, ShaderPipelineBarrierKind.Memory, GpuImageLayout.DepthAttachment, GpuImageLayout.DepthAttachment),
                ("near", "c0", GpuAccess.ColorAttachmentWrite, ShaderPipelineBarrierKind.Image, GpuImageLayout.ShaderReadOnly, GpuImageLayout.RenderTarget),
                ("far", "c1", GpuAccess.ColorAttachmentRead | GpuAccess.ColorAttachmentWrite, ShaderPipelineBarrierKind.Memory, GpuImageLayout.RenderTarget, GpuImageLayout.RenderTarget),
                ("far", "d1", GpuAccess.DepthAttachmentRead | GpuAccess.DepthAttachmentWrite, ShaderPipelineBarrierKind.Memory, GpuImageLayout.DepthAttachment, GpuImageLayout.DepthAttachment),
                ("sample", "c1", GpuAccess.ShaderRead, ShaderPipelineBarrierKind.Image, GpuImageLayout.RenderTarget, GpuImageLayout.ShaderReadOnly),
            ]
        );
    }
    [Fact]
    public void AFullscreenPassContinuesTheColorItForwards() {
        var plan = Plan(definition: Tint());
        var tint = plan.Passes.Single(predicate: static pass => (pass.Name == "tint"));

        Assert.Equal(
            actual: plan.PassOrder,
            expected: ["seed", "tint"]
        );
        Assert.Equal(
            actual: tint.Attachments.Single(),
            expected: new ShaderPipelineAttachment(
                Depth: false,
                Load: GpuAttachmentLoad.Load,
                Storage: 0,
                Store: GpuAttachmentStore.Store,
                Version: "tinted"
            )
        );
        Assert.Equal(
            actual: (tint.Accesses.Single().Barrier.Kind, tint.Accesses.Single().Barrier.OldLayout, tint.Accesses.Single().Barrier.NewLayout),
            expected: (ShaderPipelineBarrierKind.Image, GpuImageLayout.General, GpuImageLayout.RenderTarget)
        );
    }
    [Fact]
    public void TheNodeBeginsAForwardingFullscreenPassByLoadingTheImageItsPredecessorWrote() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(
            gpu: gpu,
            plan: Plan(definition: Tint())
        );

        gpu.Recording = true;
        _ = node.ProduceFrame(context: default);

        var begun = Assert.Single(collection: gpu.RenderPasses);
        var color = Assert.Single(collection: begun.Colors);

        // The render pass loads and keeps the image, and leaves it a render target for the next planned barrier.
        Assert.Equal(
            actual: Assert.Single(collection: begun.Pass.Colors),
            expected: new GpuColorAttachment(
                FinalLayout: GpuImageLayout.RenderTarget,
                Format: GpuPixelFormat.R8G8B8A8Unorm,
                Load: GpuAttachmentLoad.Load,
                Store: GpuAttachmentStore.Store
            )
        );
        Assert.Null(@object: begun.Pass.Depth);
        // The image it loads is the one seed wrote in general layout, which the planned barrier moves into the attachment
        // layout; publication moves it back.
        Assert.Equal(
            actual: gpu.Barriers.Where(predicate: static recorded => (recorded.Barrier.Kind == ShaderPipelineBarrierKind.Image)).Select(selector: static recorded => (recorded.Barrier.OldLayout, recorded.Barrier.NewLayout, recorded.Handle)),
            expected: [
                (GpuImageLayout.General, GpuImageLayout.RenderTarget, color),
                (GpuImageLayout.RenderTarget, GpuImageLayout.General, color),
            ]
        );
    }

    public static TheoryData<string, string> Refusals => new() {
        { "blend", "SHADERPIPE_UNSUPPORTED_BLEND" },
        { "alpha-test", "SHADERPIPE_UNSUPPORTED_ALPHA_TEST" },
        { "multisampled-attachment", "SHADERPIPE_UNSUPPORTED_SAMPLES" },
        { "attachment-extent", "SHADERPIPE_ATTACHMENT_EXTENT" },
        { "two-colors", "SHADERPIPE_UNSUPPORTED_MRT" },
        { "two-depths", "SHADERPIPE_DEPTH_OUTPUTS" },
        { "depth-format", "SHADERPIPE_DEPTH_FORMAT" },
        { "depth-sampled", "SHADERPIPE_DEPTH_SAMPLED" },
        { "depth-public", "SHADERPIPE_DEPTH_PUBLIC" },
        { "depth-history", "SHADERPIPE_DEPTH_HISTORY" },
        { "depth-initialized", "SHADERPIPE_DEPTH_INITIALIZATION" },
        { "depth-written-by-compute", "SHADERPIPE_DEPTH_WRITER" },
        { "depth-written-by-fullscreen", "SHADERPIPE_DEPTH_WRITER" },
        { "compare-without-depth", "SHADERPIPE_DEPTH_STATE" },
        { "geometry-on-fullscreen", "SHADERPIPE_GRAPHICS_FIELDS" },
        { "graphics-fields-on-compute", "SHADERPIPE_GRAPHICS_FIELDS" },
        { "no-geometry", "SHADERPIPE_GEOMETRY_SHAPE" },
        { "vertex-on-geometry", "SHADERPIPE_VERTEX_INPUT" },
        { "stride", "SHADERPIPE_VERTEX_LAYOUT" },
        { "location", "SHADERPIPE_VERTEX_LAYOUT" },
        { "attribute-format", "SHADERPIPE_VERTEX_LAYOUT" },
        { "attribute-past-stride", "SHADERPIPE_VERTEX_LAYOUT" },
        { "partial-vertex", "SHADERPIPE_GEOMETRY_VERTICES" },
        { "non-finite-vertex", "SHADERPIPE_GEOMETRY_VERTICES" },
        { "index-count", "SHADERPIPE_INDEX_COUNT" },
        { "index-range", "SHADERPIPE_INDEX_RANGE" },
        { "wide-sixteen-bit-index", "SHADERPIPE_INDEX_FORMAT" },
        { "geometry-limit", "SHADERPIPE_LIMIT_GEOMETRY" },
    };

    [MemberData(memberName: nameof(Refusals))]
    [Theory]
    public void EachAttachmentOrGeometryNoBackendExecutesIsRefusedByName(string refusal, string code) {
        var definition = refusal switch {
            "blend" => Layers(passes: static passes => ReplacePass(
                change: static pass => (pass with { Blend = ShaderPipelineBlend.AlphaOver }),
                name: "far",
                passes: passes
            )),
            "alpha-test" => Layers(passes: static passes => ReplacePass(
                change: static pass => (pass with { AlphaTest = 0.5 }),
                name: "far",
                passes: passes
            )),
            "multisampled-attachment" => Layers(resources: static resources => Replace(
                change: static resource => (resource with { Samples = 4 }),
                name: "c0",
                resources: resources
            )),
            "attachment-extent" => Layers(resources: static resources => Replace(
                change: static resource => (resource with {
                    Dimensions = ShaderPipelineDimensions.Absolute(
                        height: (Extent * 2),
                        width: (Extent * 2)
                    ),
                }),
                name: "c0",
                resources: resources
            )),
            "two-colors" => Layers(
                passes: static passes => ReplacePass(
                    change: static pass => (pass with { Outputs = [.. pass.OutputReferences, new ResourceReference(Name: "extra")] }),
                    name: "near",
                    passes: passes
                ),
                resources: static resources => [.. resources, Color(name: "extra")],
                outputs: ["image", "extra"]
            ),
            "two-depths" => Layers(
                passes: static passes => ReplacePass(
                    change: static pass => (pass with { Outputs = [.. pass.OutputReferences, new ResourceReference(Name: "extra")] }),
                    name: "near",
                    passes: passes
                ),
                resources: static resources => [.. resources, Depth(name: "extra")]
            ),
            "depth-format" => Layers(resources: static resources => Replace(
                change: static resource => (resource with { Format = "R32G32B32A32Float" }),
                name: "d0",
                resources: resources
            )),
            "depth-sampled" => Layers(passes: static passes => ReplacePass(
                change: static pass => (pass with { Inputs = [.. pass.InputReferences, new ResourceReference(Binding: 2, Name: "d1")] }),
                name: "sample",
                passes: passes
            )),
            "depth-public" => Layers(outputs: ["image", "d1"]),
            "depth-history" => Layers(resources: static resources => Replace(
                change: static resource => (resource with { History = true }),
                name: "d1",
                resources: resources
            )),
            "depth-initialized" => Layers(resources: static resources => Replace(
                change: static resource => (resource with { Initialization = ShaderPipelineInitialization.Zero }),
                name: "d0",
                resources: resources
            )),
            "depth-written-by-compute" => Layers(
                passes: static passes => [.. passes, new ShaderPipelinePass(
                    EntryPoint: "main",
                    Kind: ShaderPipelineDocumentPassKind.Compute,
                    Name: "stamp",
                    Outputs: [new ResourceReference(Binding: 0, Name: "stamped")],
                    Source: "stamp.hlsl"
                )],
                resources: static resources => [.. resources, Depth(name: "stamped")]
            ),
            "depth-written-by-fullscreen" => Layers(
                passes: static passes => [.. passes, new ShaderPipelinePass(
                    EntryPoint: "main",
                    Kind: ShaderPipelineDocumentPassKind.Fullscreen,
                    Name: "stamp",
                    Outputs: [new ResourceReference(Name: "stamped"), new ResourceReference(Name: "painted")],
                    Source: "stamp.hlsl"
                )],
                resources: static resources => [.. resources, Depth(name: "stamped"), Color(name: "painted")],
                outputs: ["image", "painted"]
            ),
            "compare-without-depth" => Layers(passes: static passes => ReplacePass(
                change: static pass => (pass with { Outputs = [new ResourceReference(Name: "c1")] }),
                name: "far",
                passes: passes
            )),
            "geometry-on-fullscreen" => Layers(
                passes: static passes => [.. passes, new ShaderPipelinePass(
                    EntryPoint: "main",
                    Geometry: Quad(),
                    Kind: ShaderPipelineDocumentPassKind.Fullscreen,
                    Name: "cover",
                    Outputs: [new ResourceReference(Name: "covered")],
                    Source: "cover.hlsl"
                )],
                resources: static resources => [.. resources, Color(name: "covered")],
                outputs: ["image", "covered"]
            ),
            "graphics-fields-on-compute" => Layers(passes: static passes => ReplacePass(
                change: static pass => (pass with { Blend = ShaderPipelineBlend.Opaque }),
                name: "sample",
                passes: passes
            )),
            "no-geometry" => Layers(passes: static passes => ReplacePass(
                change: static pass => (pass with { Geometry = null }),
                name: "near",
                passes: passes
            )),
            "vertex-on-geometry" => Layers(passes: static passes => ReplacePass(
                change: static pass => (pass with { Vertex = ShaderPipelineVertexInput.Position }),
                name: "near",
                passes: passes
            )),
            "stride" => WithGeometry(change: static geometry => (geometry with { StrideBytes = 14 })),
            "location" => WithGeometry(change: static geometry => (geometry with {
                Attributes = [new ShaderPipelineVertexAttribute(
                    Format: "R32G32B32Float",
                    Location: 1
                )],
            })),
            "attribute-format" => WithGeometry(change: static geometry => (geometry with {
                Attributes = [new ShaderPipelineVertexAttribute(
                    Format: "R8G8B8A8Unorm",
                    Location: 0
                )],
            })),
            "attribute-past-stride" => WithGeometry(change: static geometry => (geometry with {
                Attributes = [new ShaderPipelineVertexAttribute(
                    Format: "R32G32B32Float",
                    Location: 0,
                    OffsetBytes: 4
                )],
            })),
            "partial-vertex" => WithGeometry(change: static geometry => (geometry with { Vertices = [.. geometry.Vertices, 0f] })),
            "non-finite-vertex" => WithGeometry(change: static geometry => (geometry with { Vertices = [float.NaN, .. geometry.Vertices.Skip(count: 1)] })),
            "index-count" => WithGeometry(change: static geometry => (geometry with { Indices = [0, 1, 2, 3] })),
            "index-range" => WithGeometry(change: static geometry => (geometry with { Indices = [0, 1, 4] })),
            "wide-sixteen-bit-index" => WithGeometry(change: static geometry => (geometry with { Indices = [0, 1, 65536] })),
            "geometry-limit" => WithGeometry(change: static geometry => (geometry with { Vertices = new float[(3 * 90_000)] })),
            _ => throw new ArgumentOutOfRangeException(paramName: nameof(refusal)),
        };

        Assert.Contains(
            collection: Refusal(definition: definition),
            filter: diagnostic => (diagnostic.Code == code)
        );
    }
    [Fact]
    public void TheNodeDrawsEachGeometryPassIndexedInDeclaredOrderFromOneBuffer() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(
            gpu: gpu,
            plan: Plan(definition: Layers())
        );
        var quad = Quad();

        // One buffer per geometry pass holds its vertices, then its indices exactly as declared: the second triangle
        // first, with the shared edge named in both directions.
        Assert.Equal(
            actual: gpu.GeometryBuffers.Select(selector: static buffer => (buffer.Usage, buffer.Data.Length)),
            expected: [(GpuBufferUsage.Vertex | GpuBufferUsage.Index, 60), (GpuBufferUsage.Vertex | GpuBufferUsage.Index, 60)]
        );
        Assert.All(
            action: buffer => Assert.Equal(
                actual: buffer.Data,
                expected: quad.BufferData()
            ),
            collection: gpu.GeometryBuffers
        );
        Assert.Equal(
            actual: Enumerable.Range(count: 6, start: 0).Select(selector: position => BitConverter.ToUInt16(startIndex: (48 + (position * 2)), value: gpu.GeometryBuffers[0].Data)),
            expected: [((ushort)2), 1, 3, 0, 1, 2]
        );
        Assert.Equal(
            actual: BitConverter.ToSingle(startIndex: (3 * 12), value: gpu.GeometryBuffers[0].Data),
            expected: 1f
        );
        // Each pass's pipeline reads the declared layout and tests depth by the declared comparison, in a render pass
        // with a depth attachment.
        Assert.All(
            action: static pipeline => Assert.Equal(
                actual: (pipeline.Description.DepthCompare, pipeline.Description.VertexInput.StrideBytes, Attribute: pipeline.Description.VertexInput.Attributes.Single(), pipeline.Pass.Depth?.Format),
                expected: (GpuDepthCompare.Less, 12U, Attribute: new GpuVertexAttribute(Format: GpuVertexFormat.R32G32B32Float, Location: 0, OffsetBytes: 0), GpuPixelFormat.D32Float)
            ),
            collection: gpu.GraphicsPipelines
        );

        // A geometry pass with no input binds no descriptor, so only the sampling consumer has a pool in each slot.
        Assert.Equal(
            actual: gpu.CreatedObjects.Count(predicate: static created => (created.Kind == "descriptor pool")),
            expected: 3
        );

        gpu.Recording = true;
        _ = node.ProduceFrame(context: default);

        var near = gpu.GeometryBuffers[0].Handle;
        var far = gpu.GeometryBuffers[1].Handle;

        Assert.Equal(
            actual: gpu.GraphicsCommands,
            expected: [
                ("vertices", near, 0UL, 48UL, 12U),
                ("indices16", near, 48UL, 12UL, 0U),
                ("draw-indexed", 0, 0UL, 0UL, 6U),
                ("vertices", far, 0UL, 48UL, 12U),
                ("indices16", far, 48UL, 12UL, 0U),
                ("draw-indexed", 0, 0UL, 0UL, 6U),
            ]
        );
    }
    [Fact]
    public void TheNodeCarriesColorAndDepthThroughTwoGeometryPassesToTheSamplingConsumer() {
        var gpu = new FakePipelineGpu();
        var plan = Plan(definition: Layers());
        using var node = InstalledNode(
            gpu: gpu,
            plan: plan
        );

        gpu.Recording = true;
        _ = node.ProduceFrame(context: default);

        var near = gpu.RenderPasses[0];
        var far = gpu.RenderPasses[1];
        var color = Assert.Single(collection: near.Colors);

        // near clears both attachments and keeps them; far loads both into the same images, keeps the color and
        // discards the depth, which nothing reads after it.
        Assert.Equal(
            actual: gpu.RenderPasses.Select(selector: static begun => (Color: begun.Pass.Colors.Single().Load, ColorStore: begun.Pass.Colors.Single().Store, Depth: begun.Pass.Depth!.Value.Load, DepthStore: begun.Pass.Depth!.Value.Store)),
            expected: [
                (Color: GpuAttachmentLoad.Clear, ColorStore: GpuAttachmentStore.Store, Depth: GpuAttachmentLoad.Clear, DepthStore: GpuAttachmentStore.Store),
                (Color: GpuAttachmentLoad.Load, ColorStore: GpuAttachmentStore.Store, Depth: GpuAttachmentLoad.Load, DepthStore: GpuAttachmentStore.Discard),
            ]
        );
        Assert.Equal(
            actual: (Color: Assert.Single(collection: far.Colors), far.Depth),
            expected: (Color: color, near.Depth)
        );
        Assert.NotEqual(
            actual: near.Depth,
            expected: 0
        );
        // The depth storage is a ring of depth attachments that is never sampled, and the consumer's own barrier makes
        // the color shader-readable after the second pass.
        Assert.Equal(
            actual: gpu.CreatedObjects.Count(predicate: static created => (created.Kind == "D32Float image")),
            expected: 3
        );
        Assert.Contains(
            collection: gpu.Barriers,
            expected: (ShaderPipelineBarrier.Between(
                kind: ShaderPipelineResourceKind.Image,
                prior: plan.Passes.Single(predicate: static pass => (pass.Name == "far")).Accesses.First(predicate: static access => (access.Version == "c1")).Use,
                use: plan.Passes.Single(predicate: static pass => (pass.Name == "sample")).Accesses.First(predicate: static access => (access.Version == "c1")).Use
            ), color)
        );
    }
    [Fact]
    public void TheNodeCountsDepthAttachmentsAndGeometryBuffersInWhatItOwns() {
        var gpu = new FakePipelineGpu();
        using var node = InstalledNode(
            gpu: gpu,
            plan: Plan(definition: Layers())
        );

        // Three slots of three 16x16 four-byte storages (the color chain, the depth chain, the image), and two 60-byte
        // geometry buffers, exactly as the fake holds them.
        Assert.Equal(
            actual: (Owned: node.OwnedBytes, Steady: node.InstalledAccount.SteadyBytes),
            expected: (Owned: gpu.LiveBytes, Steady: gpu.LiveBytes)
        );
        Assert.Equal(
            actual: gpu.LiveBytes,
            expected: (((((3UL * 3UL) * 16UL) * 16UL) * 4UL) + (2UL * 60UL))
        );
    }
    [Fact]
    public void TheDocumentSpellsAGeometryPassAndItsDepthAttachment() {
        const string Document = """
            {
              "$schema": "puck.shader.pipeline.v1",
              "name": "spelled",
              "resources": [
                { "name": "color", "kind": "Image", "format": "R8G8B8A8Unorm", "dimensions": { "mode": "Absolute", "width": 16, "height": 16 } },
                { "name": "depth", "kind": "Depth", "format": "D32Float", "dimensions": { "mode": "Absolute", "width": 16, "height": 16 } }
              ],
              "passes": [
                {
                  "name": "draw",
                  "source": "draw.hlsl",
                  "entryPoint": "ps",
                  "kind": "Geometry",
                  "outputs": [ { "name": "color" }, { "name": "depth" } ],
                  "depthCompare": "LessOrEqual",
                  "blend": "Opaque",
                  "geometry": {
                    "vertexEntryPoint": "vs",
                    "strideBytes": 12,
                    "attributes": [ { "location": 0, "format": "R32G32B32Float", "offsetBytes": 0 } ],
                    "vertices": [ -1, -1, 0.5, 1, -1, 0.5, -1, 1, 0.5 ],
                    "indices": [ 2, 0, 1 ],
                    "indexFormat": "UInt32"
                  }
                }
              ],
              "outputs": [ "color" ]
            }
            """;
        var definition = JsonSerializer.Deserialize(
            json: Document,
            jsonTypeInfo: ShaderPipelineJsonContext.Default.ShaderPipelineDefinition
        )!;
        var pass = Plan(definition: definition).Passes.Single();

        Assert.Equal(
            actual: (pass.Declaration.Kind, pass.Declaration.DepthCompare, pass.Declaration.Geometry!.VertexEntryPoint, pass.Declaration.Geometry.IndexFormat, pass.Declaration.Geometry.VertexCount, pass.Declaration.Geometry.SizeBytes),
            expected: (ShaderPipelineDocumentPassKind.Geometry, ((ShaderPipelineDepthCompare?)ShaderPipelineDepthCompare.LessOrEqual), "vs", ShaderPipelineIndexFormat.UInt32, 3U, ((9UL * 4UL) + (3UL * 4UL)))
        );
        Assert.Equal(
            actual: pass.Attachments.Select(selector: static attachment => (attachment.Version, attachment.Depth, attachment.Load, attachment.Store)),
            expected: [("color", false, GpuAttachmentLoad.Clear, GpuAttachmentStore.Store), ("depth", true, GpuAttachmentLoad.Clear, GpuAttachmentStore.Discard)]
        );
    }
    [Fact]
    public void TheLayersDocumentThatEveryRefusalChangesCompiles() =>
        Assert.Equal(
            actual: Plan(definition: Layers()).Passes.Count,
            expected: 3
        );
}
