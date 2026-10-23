using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// The SDF engine's mesh pass pipelines: the graphics pipeline that rasterizes a frame's mesh draws
/// (<see cref="SdfFrame.MeshDraws"/>) into the mesh visibility target before primary traversal, and the shadow pipeline
/// that rasterizes the mesh draws into a shadow cascade's depth alone, one entry each a device in the composition's
/// <see cref="GpuPassPipelineCache"/>. The two share the layout; the shadow pipeline has a vertex stage of its own, the
/// light's orthographic projection, which hands its fragment stage only the position. Both fragment stages force the
/// depth test first. The engine's holder leases them beside its pipeline set, as it leases the region copy, and the
/// engine takes the built pipelines and their render passes at construction.
/// <para>
/// The pipelines draw with the mesh interface's layout (<see cref="SdfWorldInterfaces.Mesh"/>): one set per ring slot
/// binding the viewport table and the mesh region, and the view and draw pushed per draw call. They read no vertex
/// buffer: the vertex stage pulls each draw's triangles from the mesh region. The mesh render pass
/// (<see cref="RenderPass"/>) clears the target and a reversed-Z depth attachment to zero and tests
/// <see cref="GpuDepthCompare.Greater"/>, so the nearest surface of every covered pixel stays; the target ends in its
/// attachment layout, and the engine moves it on to be read. The shadow render pass (<see cref="ShadowRenderPass"/>) has
/// the depth attachment alone, cleared the same way and stored, which the surface pass samples.
/// </para>
/// </summary>
public sealed class SdfMeshRasterPass {
    /// <summary>The file stem of the pass's two stages: <c>sdf-mesh.vert</c> and <c>sdf-mesh.frag</c>.</summary>
    public const string ShaderStem = "sdf-mesh";
    /// <summary>The file stem of the shadow pipeline's two stages: <c>sdf-mesh-shadow.vert</c>, the light's orthographic
    /// projection, and <c>sdf-mesh-shadow.frag</c>, which writes no colour and counts its fragments.</summary>
    public const string ShadowShaderStem = "sdf-mesh-shadow";
    /// <summary>The file stem of the glass pipeline's fragment stage, <c>sdf-mesh-glass.frag</c>, drawn with the mesh
    /// pass's vertex stage: every glass face accumulates its signed ray distance into the glass sums where it lies before
    /// the opaque hit, and the nearest front face writes the glass target through the pass's own depth.</summary>
    public const string GlassShaderStem = "sdf-mesh-glass";
    /// <summary>The file stem of the mesh pass's occlusion kernel, <c>sdf-mesh-occlusion.comp</c>, which tests each
    /// draw's box against the depth of the pass's first phase and writes both phases' counted indirect draws.</summary>
    public const string OcclusionShaderStem = "sdf-mesh-occlusion";
    /// <summary>The bytes a pixel of the target and the depth attachment hold together.</summary>
    public const uint BytesPerPixel = (16u + 4u);

    /// <summary>Initializes a new instance of the <see cref="SdfMeshRasterPass"/> class from the deployed stages.</summary>
    /// <param name="pipelines">The composition's pass pipelines.</param>
    /// <param name="bytecodeExtension">The backend's compiled-shader extension (<c>".spv"</c> or <c>".dxil"</c>).</param>
    public SdfMeshRasterPass(GpuPassPipelineCache pipelines, string bytecodeExtension) : this(
        fragment: Load(
            bytecodeExtension: bytecodeExtension,
            directory: SdfKernelSet.DefaultDirectory,
            stage: "frag"
        ),
        glassFragment: Load(
            bytecodeExtension: bytecodeExtension,
            directory: SdfKernelSet.DefaultDirectory,
            stage: "frag",
            stem: GlassShaderStem
        ),
        occlusion: Load(
            bytecodeExtension: bytecodeExtension,
            directory: SdfKernelSet.DefaultDirectory,
            stage: "comp",
            stem: OcclusionShaderStem
        ),
        pipelines: pipelines,
        shadowFragment: Load(
            bytecodeExtension: bytecodeExtension,
            directory: SdfKernelSet.DefaultDirectory,
            stage: "frag",
            stem: ShadowShaderStem
        ),
        shadowVertex: Load(
            bytecodeExtension: bytecodeExtension,
            directory: SdfKernelSet.DefaultDirectory,
            stage: "vert",
            stem: ShadowShaderStem
        ),
        vertex: Load(
            bytecodeExtension: bytecodeExtension,
            directory: SdfKernelSet.DefaultDirectory,
            stage: "vert"
        )
    ) {
    }
    /// <summary>Initializes a new instance of the <see cref="SdfMeshRasterPass"/> class from given stages.</summary>
    /// <param name="pipelines">The composition's pass pipelines.</param>
    /// <param name="vertex">The vertex stage's bytecode.</param>
    /// <param name="fragment">The fragment stage's bytecode.</param>

    /// <param name="shadowVertex">The shadow pipeline's vertex stage's bytecode.</param>
    /// <param name="shadowFragment">The shadow pipeline's fragment stage's bytecode.</param>
    /// <param name="glassFragment">The glass pipeline's fragment stage's bytecode, drawn with <paramref name="vertex"/>.</param>
    /// <param name="occlusion">The mesh pass occlusion kernel's bytecode (<see cref="OcclusionShaderStem"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="pipelines"/> is <see langword="null"/>.</exception>
    public SdfMeshRasterPass(GpuPassPipelineCache pipelines, ReadOnlyMemory<byte> vertex, ReadOnlyMemory<byte> fragment, ReadOnlyMemory<byte> shadowVertex, ReadOnlyMemory<byte> shadowFragment, ReadOnlyMemory<byte> glassFragment, ReadOnlyMemory<byte> occlusion) {
        ArgumentNullException.ThrowIfNull(argument: pipelines);

        Pipelines = pipelines;
        Key = KeyOf(
            fragment: fragment,
            vertex: vertex
        );

        ShadowKey = ShadowKeyOf(
            fragment: shadowFragment,
            vertex: shadowVertex
        );
        OcclusionKey = OcclusionKeyOf(occlusion: occlusion);
        GlassKey = GlassKeyOf(
            fragment: glassFragment,
            vertex: vertex
        );
    }

    /// <summary>Gets the pipeline's description: the mesh interface's layout, no vertex input, the reversed-Z depth test
    /// and back faces culled, a face being back by the bake's winding (outward faces counter-clockwise on the target,
    /// <see cref="GpuGraphicsPipelineDescription"/>). The shadow and glass pipelines cull nothing: a cascade's winding
    /// follows the light, and a glass mesh's back faces carry its thickness.</summary>
    public static GpuGraphicsPipelineDescription Description { get; } = new(
        Cull: GpuCullMode.Back,
        DepthCompare: GpuDepthCompare.Greater,
        Layout: SdfWorldTables.PipelineLayouts.Mesh,
        Name: ShaderStem,
        VertexInput: new GpuVertexInputLayout(
            Attributes: [],
            StrideBytes: 0
        )
    );

    /// <summary>Gets the shadow pipeline's description: the mesh pass's without culling, named for the shadow stages.</summary>
    public static GpuGraphicsPipelineDescription ShadowDescription { get; } = Description with { Cull = GpuCullMode.None, Name = ShadowShaderStem };
    /// <summary>Gets the glass pipeline's description: the mesh pass's without culling, named for the glass stage. It draws in the mesh
    /// pass's render pass (<see cref="RenderPass"/>), into the glass target and the glass pass's own depth.</summary>
    public static GpuGraphicsPipelineDescription GlassDescription { get; } = Description with { Cull = GpuCullMode.None, Name = GlassShaderStem };
    /// <summary>Gets the render pass the pipeline draws in: the target (<see cref="SdfWorldPackage.MeshTargetFormat"/>)
    /// cleared and stored, left in its attachment layout, and the depth attachment
    /// (<see cref="SdfWorldPackage.MeshDepthAttachment"/>) cleared to its reversed-Z far plane and stored, which the
    /// occlusion's second phase draws on (<see cref="LateRenderPass"/>).</summary>
    public static GpuRenderPassDescription RenderPass { get; } = new(
        Colors: [new GpuColorAttachment(
            FinalLayout: GpuImageLayout.RenderTarget,
            Format: SdfWorldPackage.MeshTargetFormat,
            Load: GpuAttachmentLoad.Clear,
            Store: GpuAttachmentStore.Store
        )],
        Depth: SdfWorldPackage.MeshDepthAttachment
    );
    /// <summary>Gets the render pass the mesh pass's second occlusion phase draws in: <see cref="RenderPass"/>'s
    /// attachments loaded as the first phase stored them, so the draws the occlusion test finds newly visible join the
    /// first phase's under the same depth test. Pipelines created for <see cref="RenderPass"/> draw in it: only the load
    /// operations differ.</summary>
    public static GpuRenderPassDescription LateRenderPass { get; } = new(
        Colors: [RenderPass.Colors[0] with { Load = GpuAttachmentLoad.Load }],
        Depth: SdfWorldPackage.MeshDepthAttachment with { Load = GpuAttachmentLoad.Load }
    );
    /// <summary>Gets the occlusion kernel's description: the mesh interface's layout for the compute stage, so the
    /// kernel's sets bind what the mesh pass's bind, at the same bindings.</summary>
    public static GpuComputePipelineDescription OcclusionDescription { get; } = new(
        Bindings: [],
        Layout: SdfWorldTables.PipelineLayouts.MeshOcclusion,
        Name: OcclusionShaderStem,
        PushConstantBinding: null
    );
    /// <summary>Gets the render pass a shadow cascade draws in: no color attachment, and the cascade's depth
    /// (<see cref="SdfWorldPackage.MeshShadowDepthAttachment"/>) cleared to its reversed-Z far plane and stored for the
    /// surface pass to sample.</summary>
    public static GpuRenderPassDescription ShadowRenderPass { get; } = new(
        Colors: [],
        Depth: SdfWorldPackage.MeshShadowDepthAttachment
    );

    /// <summary>Gets the pipeline's key in <see cref="Pipelines"/>.</summary>
    public GpuPassPipelineKey Key { get; }

    /// <summary>Gets the shadow pipeline's key in <see cref="Pipelines"/>.</summary>
    public GpuPassPipelineKey ShadowKey { get; }
    /// <summary>Gets the glass pipeline's key in <see cref="Pipelines"/>.</summary>
    public GpuPassPipelineKey GlassKey { get; }
    /// <summary>Gets the occlusion kernel's key in <see cref="Pipelines"/>.</summary>
    public GpuPassPipelineKey OcclusionKey { get; }
    /// <summary>Gets the composition's pass pipelines.</summary>
    public GpuPassPipelineCache Pipelines { get; }

    /// <summary>Returns the key of the pipeline built from given stages.</summary>
    /// <param name="vertex">The vertex stage's bytecode.</param>
    /// <param name="fragment">The fragment stage's bytecode.</param>
    /// <returns>The key.</returns>
    public static GpuPassPipelineKey KeyOf(ReadOnlyMemory<byte> vertex, ReadOnlyMemory<byte> fragment) =>
        GpuPassPipelineKey.OfGraphics(
            description: Description,
            fragment: fragment,
            renderPass: RenderPass,
            vertex: vertex
        );

    /// <summary>Returns the key of the shadow pipeline built from given stages.</summary>
    /// <param name="vertex">The shadow vertex stage's bytecode.</param>
    /// <param name="fragment">The shadow fragment stage's bytecode.</param>
    /// <returns>The key.</returns>
    public static GpuPassPipelineKey ShadowKeyOf(ReadOnlyMemory<byte> vertex, ReadOnlyMemory<byte> fragment) =>
        GpuPassPipelineKey.OfGraphics(
            description: ShadowDescription,
            fragment: fragment,
            renderPass: ShadowRenderPass,
            vertex: vertex
        );
    /// <summary>Returns the key of the glass pipeline built from given stages.</summary>
    /// <param name="vertex">The mesh pass's vertex stage's bytecode.</param>
    /// <param name="fragment">The glass fragment stage's bytecode.</param>
    /// <returns>The key.</returns>
    public static GpuPassPipelineKey GlassKeyOf(ReadOnlyMemory<byte> vertex, ReadOnlyMemory<byte> fragment) =>
        GpuPassPipelineKey.OfGraphics(
            description: GlassDescription,
            fragment: fragment,
            renderPass: RenderPass,
            vertex: vertex
        );
    /// <summary>Returns the key of the occlusion kernel built from given bytecode.</summary>
    /// <param name="occlusion">The kernel's bytecode.</param>
    /// <returns>The key.</returns>
    public static GpuPassPipelineKey OcclusionKeyOf(ReadOnlyMemory<byte> occlusion) =>
        GpuPassPipelineKey.OfCompute(
            bytecode: occlusion,
            description: OcclusionDescription
        );
    /// <summary>Takes a lease on the occlusion kernel for a device, joining its build or starting it on the thread
    /// pool.</summary>
    /// <param name="device">The device.</param>
    /// <returns>The lease; its <see cref="GpuPassPipeline"/> carries the compute pipeline.</returns>
    public GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> AcquireOcclusion(IGpuDeviceContext device) {
        ArgumentNullException.ThrowIfNull(argument: device);

        return Pipelines.Acquire(
            device: device,
            key: OcclusionKey
        );
    }
    /// <summary>Reads one deployed stage.</summary>
    /// <param name="bytecodeExtension">The backend's compiled-shader extension.</param>
    /// <param name="directory">The directory the stages are deployed in.</param>
    /// <param name="stage">The stage's file suffix, <c>vert</c> or <c>frag</c>.</param>
    /// <param name="stem">The stage's file stem; the mesh pass's by default.</param>
    /// <returns>The stage's bytecode.</returns>
    public static ReadOnlyMemory<byte> Load(string bytecodeExtension, string directory, string stage, string stem = ShaderStem) {
        ArgumentException.ThrowIfNullOrEmpty(argument: bytecodeExtension);
        ArgumentException.ThrowIfNullOrEmpty(argument: directory);
        ArgumentException.ThrowIfNullOrEmpty(argument: stage);

        return File.ReadAllBytes(path: Path.Combine(
            path1: directory,
            path2: $"{stem}.{stage}{bytecodeExtension}"
        ));
    }
    /// <summary>Takes a lease on the pipeline for a device, joining its build or starting it on the thread pool.</summary>
    /// <param name="device">The device.</param>
    /// <returns>The lease; its <see cref="GpuPassPipeline"/> carries the pipeline and the render pass it was created
    /// for.</returns>
    public GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> Acquire(IGpuDeviceContext device) {
        ArgumentNullException.ThrowIfNull(argument: device);

        return Pipelines.Acquire(
            device: device,
            key: Key
        );
    }

    /// <summary>Takes a lease on the shadow pipeline for a device, joining its build or starting it on the thread
    /// pool.</summary>
    /// <param name="device">The device.</param>
    /// <returns>The lease; its <see cref="GpuPassPipeline"/> carries the pipeline and the shadow render pass.</returns>
    public GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> AcquireShadow(IGpuDeviceContext device) {
        ArgumentNullException.ThrowIfNull(argument: device);

        return Pipelines.Acquire(
            device: device,
            key: ShadowKey
        );
    }
    /// <summary>Takes a lease on the glass pipeline for a device, joining its build or starting it on the thread
    /// pool.</summary>
    /// <param name="device">The device.</param>
    /// <returns>The lease; its <see cref="GpuPassPipeline"/> carries the pipeline and the mesh render pass.</returns>
    public GpuBuildLease<GpuPassPipelineKey, GpuPassPipeline> AcquireGlass(IGpuDeviceContext device) {
        ArgumentNullException.ThrowIfNull(argument: device);

        return Pipelines.Acquire(
            device: device,
            key: GlassKey
        );
    }
}
