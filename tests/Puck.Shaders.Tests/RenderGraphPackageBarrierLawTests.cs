using Puck.Abstractions.Gpu;

namespace Puck.Shaders.Tests;

/// <summary>
/// Laws of package-pass barriers: the planner plans each package port's barrier and layout from the stage and access
/// the port declares (<see cref="RenderGraphPortAccess"/>) exactly as it plans a shader pass's, so a graph of a compute
/// shader pass, a <c>post.&lt;id&gt;</c> pass and the <c>overlay</c> pass plans the hand-derived barrier table below,
/// layouts included, and a drawing package's target is created usable as a color attachment while a compute package's
/// is not.
/// </summary>
public sealed class RenderGraphPackageBarrierLawTests {
    private const GpuStage Shaders = GpuStage.ComputeShader | GpuStage.FragmentShader;

    // A compute read of an external image arriving shader-readable, and each port's use.
    private static readonly ShaderPipelineAccessState ComputeRead = new(
        Access: GpuAccess.ShaderRead,
        Layout: GpuImageLayout.ShaderReadOnly,
        Stage: GpuStage.ComputeShader
    );
    private static readonly ShaderPipelineAccessState ComputeWrite = new(
        Access: GpuAccess.ShaderWrite,
        Layout: GpuImageLayout.General,
        Stage: GpuStage.ComputeShader
    );
    private static readonly ShaderPipelineAccessState Sampled = new(
        Access: GpuAccess.ShaderRead,
        Layout: GpuImageLayout.ShaderReadOnly,
        Stage: GpuStage.FragmentShader
    );
    private static readonly ShaderPipelineAccessState Drawn = new(
        Access: GpuAccess.ColorAttachmentWrite,
        Layout: GpuImageLayout.RenderTarget,
        Stage: GpuStage.ColorAttachmentOutput
    );

    private static RenderGraphPackageCatalog Catalog() => RenderGraphPackageCatalog.WithPostProcess(postProcess: ShaderSetCatalog.Scan(rootDirectory: Path.Combine(
        path1: AppContext.BaseDirectory,
        path2: "Assets",
        path3: "Shaders"
    )));
    private static ShaderPipelineResource Image(string name, bool external = false) => new(
        Dimensions: ShaderPipelineDimensions.Relative(),
        Format: "R8G8B8A8Unorm",
        Initialization: (external
            ? ShaderPipelineInitialization.External
            : ShaderPipelineInitialization.Undefined),
        Name: name
    );
    // The host's world, toned by a compute shader pass, grained by a post pass, then drawn over by the overlay.
    private static ShaderPipelinePlan Plan() => new RenderGraphCompiler(packages: Catalog()).Compile(definition: new RenderGraphDefinition(
        Name: "chain",
        Outputs: ["composed"],
        Packages: [
            new RenderGraphPackagePass(
                Inputs: ["toned"],
                Name: "grain",
                Outputs: ["grained"],
                Package: "post.sdf-film-grain"
            ),
            new RenderGraphPackagePass(
                Inputs: ["grained"],
                Name: "overlay",
                Outputs: ["composed"],
                Package: RenderGraphPackageCatalog.Overlay
            ),
        ],
        Passes: [new ShaderPipelinePass(
            EntryPoint: "main",
            Inputs: [new ResourceReference(
                Name: "world"
            )],
            Kind: ShaderPipelineDocumentPassKind.Compute,
            Name: "tone",
            Outputs: [new ResourceReference(
                Name: "toned"
            )],
            Source: "tone.hlsl"
        )],
        Resources: [
            Image(name: "composed"),
            Image(name: "grained"),
            Image(name: "toned"),
            Image(
                external: true,
                name: "world"
            ),
        ],
        Schema: RenderGraphSchemas.Graph
    )).Pipeline;
    private static ShaderPipelineBarrier Barrier(ShaderPipelineBarrierKind kind, ShaderPipelineAccessState prior, ShaderPipelineAccessState use, GpuStage destinationStage) => new(
        DestinationAccess: use.Access,
        DestinationStage: destinationStage,
        Kind: kind,
        NewLayout: use.Layout,
        OldLayout: prior.Layout,
        SourceAccess: prior.Access,
        SourceStage: prior.Stage
    );
    private static ShaderPipelineAccess Access(int storage, string version, ShaderPipelinePriorKind priorKind, int priorPass, ShaderPipelineAccessState prior, ShaderPipelineAccessState use, ShaderPipelineBarrierKind kind, GpuStage destinationStage) => new(
        Barrier: Barrier(
            destinationStage: destinationStage,
            kind: kind,
            prior: prior,
            use: use
        ),
        PreviousFrame: false,
        Prior: prior,
        PriorKind: priorKind,
        PriorPass: priorPass,
        Storage: storage,
        Use: use,
        Version: version
    );

    [Fact]
    public void AGraphOfShaderPostAndOverlayPassesPlansTheHandDerivedBarrierTable() {
        var plan = Plan();

        // Storages in the ordinal order of their first versions.
        const int Composed = 0;
        const int Grained = 1;
        const int Toned = 2;
        const int World = 3;

        Assert.Equal(
            expected: ["composed", "grained", "toned", "world"],
            actual: plan.Storages.Select(selector: static storage => storage.Versions[0])
        );
        Assert.Equal(
            expected: [("tone", ShaderPipelinePassKind.Compute), ("grain", ShaderPipelinePassKind.Package), ("overlay", ShaderPipelinePassKind.Package)],
            actual: plan.Passes.Select(selector: static pass => (pass.Name, pass.Kind))
        );

        ShaderPipelineAccess[][] expected = [
            [
                // The host hands the world over shader-readable, so the compute read needs no barrier.
                Access(
                    destinationStage: Shaders,
                    kind: ShaderPipelineBarrierKind.None,
                    prior: ShaderPipelineAccessState.Handover(layout: GpuImageLayout.ShaderReadOnly),
                    priorKind: ShaderPipelinePriorKind.Host,
                    priorPass: -1,
                    storage: World,
                    use: ComputeRead,
                    version: "world"
                ),
                // Last frame's grain sampled the toned image; the compute write takes it back to General.
                Access(
                    destinationStage: GpuStage.ComputeShader,
                    kind: ShaderPipelineBarrierKind.Image,
                    prior: Sampled,
                    priorKind: ShaderPipelinePriorKind.CrossFrame,
                    priorPass: -1,
                    storage: Toned,
                    use: ComputeWrite,
                    version: "toned"
                ),
            ],
            [
                // The post pass samples what the compute pass wrote, in the fragment stage.
                Access(
                    destinationStage: Shaders,
                    kind: ShaderPipelineBarrierKind.Image,
                    prior: ComputeWrite,
                    priorKind: ShaderPipelinePriorKind.Pass,
                    priorPass: 0,
                    storage: Toned,
                    use: Sampled,
                    version: "toned"
                ),
                // Its target moves from last frame's overlay sampling into render-target layout.
                Access(
                    destinationStage: GpuStage.ColorAttachmentOutput,
                    kind: ShaderPipelineBarrierKind.Image,
                    prior: Sampled,
                    priorKind: ShaderPipelinePriorKind.CrossFrame,
                    priorPass: -1,
                    storage: Grained,
                    use: Drawn,
                    version: "grained"
                ),
            ],
            [
                // The overlay samples the drawn target, which the render pass left in render-target layout.
                Access(
                    destinationStage: Shaders,
                    kind: ShaderPipelineBarrierKind.Image,
                    prior: Drawn,
                    priorKind: ShaderPipelinePriorKind.Pass,
                    priorPass: 1,
                    storage: Grained,
                    use: Sampled,
                    version: "grained"
                ),
                // Its own target stays in render-target layout from frame to frame, a write after a write.
                Access(
                    destinationStage: GpuStage.ColorAttachmentOutput,
                    kind: ShaderPipelineBarrierKind.Memory,
                    prior: Drawn,
                    priorKind: ShaderPipelinePriorKind.CrossFrame,
                    priorPass: -1,
                    storage: Composed,
                    use: Drawn,
                    version: "composed"
                ),
            ],
        ];

        for (var index = 0; (index < expected.Length); index++) {
            Assert.Equal(
                expected: expected[index],
                actual: plan.Passes[index].Accesses
            );
        }

        Assert.Equal(
            expected: Drawn,
            actual: plan.Storages[Composed].FrameEnd
        );
    }
    [Fact]
    public void ADrawnTargetIsAColorAttachmentAndAComputeTargetIsNot() {
        var plan = new RenderGraphCompiler(packages: RenderGraphPackageCatalog.Engine).Compile(definition: new RenderGraphDefinition(
            Name: "resampled",
            Outputs: ["composed"],
            Packages: [
                new RenderGraphPackagePass(
                    Inputs: ["world"],
                    Name: "resample",
                    Outputs: ["scaled"],
                    Package: RenderGraphPackageCatalog.Resample
                ),
                new RenderGraphPackagePass(
                    Inputs: ["scaled"],
                    Name: "overlay",
                    Outputs: ["composed"],
                    Package: RenderGraphPackageCatalog.Overlay
                ),
            ],
            Resources: [
                Image(name: "composed"),
                Image(name: "scaled"),
                Image(
                    external: true,
                    name: "world"
                ),
            ],
            Schema: RenderGraphSchemas.Graph
        )).Pipeline;

        // The resample package writes its output as a compute dispatch does, and the overlay samples it and draws.
        Assert.Equal(
            expected: [(ComputeRead, ComputeWrite), (Sampled, Drawn)],
            actual: plan.Passes.Select(selector: static pass => (pass.Accesses[0].Use, pass.Accesses[1].Use))
        );
        Assert.Equal(
            expected: [
                ("composed", GpuImageUsage.Sampled | GpuImageUsage.Storage | GpuImageUsage.ColorAttachment),
                ("scaled", GpuImageUsage.Sampled | GpuImageUsage.Storage),
            ],
            actual: plan.Storages.Where(predicate: static storage => !storage.Declaration.IsExternal).Select(selector: storage => (storage.Versions[0], ShaderPipelineRenderNode.UsageOf(
                plan: plan,
                storage: storage
            )))
        );
    }
}
