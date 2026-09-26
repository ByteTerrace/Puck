using Puck.Abstractions.Gpu;
using Puck.Hosting;
using Puck.Shaders;

namespace Puck.SdfVm;

/// <summary>
/// The SDF engine's mesh pass pipeline: the graphics pipeline that rasterizes a frame's mesh draws
/// (<see cref="SdfFrame.MeshDraws"/>) into the mesh visibility target before primary traversal, one entry a device in
/// the composition's <see cref="GpuPassPipelineCache"/>. The engine's holder leases it beside its pipeline set, as it
/// leases the region copy, and the engine takes the built pipeline and its render pass at construction.
/// <para>
/// The pipeline draws with the mesh interface's layout (<see cref="SdfWorldInterfaces.Mesh"/>): one set per ring slot
/// binding the viewport table and the mesh region, and the view and draw pushed per draw call. It reads no vertex
/// buffer: its vertex stage pulls each draw's triangles from the mesh region. Its render pass (<see cref="RenderPass"/>) clears the target and a
/// reversed-Z depth attachment to zero and tests <see cref="GpuDepthCompare.Greater"/>, so the nearest surface of every
/// covered pixel stays; the target ends in its attachment layout, and the engine moves it on to be read.
/// </para>
/// </summary>
public sealed class SdfMeshRasterPass {
    /// <summary>The file stem of the pass's two stages: <c>sdf-mesh.vert</c> and <c>sdf-mesh.frag</c>.</summary>
    public const string ShaderStem = "sdf-mesh";
    /// <summary>The format of the mesh visibility target: per pixel the ray parameter, the draw index plus one and the
    /// octahedral normal.</summary>
    public const GpuPixelFormat TargetFormat = GpuPixelFormat.R32G32B32A32Float;
    /// <summary>The format of the pass's reversed-Z depth attachment.</summary>
    public const GpuPixelFormat DepthFormat = GpuPixelFormat.D32Float;
    /// <summary>The depth the pass clears its depth attachment to: zero, the reversed-Z far plane at infinity.</summary>
    public const float ClearDepth = 0f;
    /// <summary>The bytes a pixel of the target and the depth attachment hold together.</summary>
    public const uint BytesPerPixel = (16u + 4u);

    /// <summary>Initializes a new instance of the <see cref="SdfMeshRasterPass"/> class from the deployed stages.</summary>
    /// <param name="pipelines">The composition's pass pipelines.</param>
    /// <param name="bytecodeExtension">The backend's compiled-shader extension (<c>".spv"</c> or <c>".dxil"</c>).</param>
    public SdfMeshRasterPass(GpuPassPipelineCache pipelines, string bytecodeExtension) : this(
        fragment: Load(
            bytecodeExtension: bytecodeExtension,
            directory: SdfWorldKernels.DefaultDirectory,
            stage: "frag"
        ),
        pipelines: pipelines,
        vertex: Load(
            bytecodeExtension: bytecodeExtension,
            directory: SdfWorldKernels.DefaultDirectory,
            stage: "vert"
        )
    ) {
    }
    /// <summary>Initializes a new instance of the <see cref="SdfMeshRasterPass"/> class from given stages.</summary>
    /// <param name="pipelines">The composition's pass pipelines.</param>
    /// <param name="vertex">The vertex stage's bytecode.</param>
    /// <param name="fragment">The fragment stage's bytecode.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pipelines"/> is <see langword="null"/>.</exception>
    public SdfMeshRasterPass(GpuPassPipelineCache pipelines, ReadOnlyMemory<byte> vertex, ReadOnlyMemory<byte> fragment) {
        ArgumentNullException.ThrowIfNull(argument: pipelines);

        Pipelines = pipelines;
        Key = KeyOf(
            fragment: fragment,
            vertex: vertex
        );
    }

    /// <summary>Gets the pipeline's description: the mesh interface's layout, no vertex input and the reversed-Z depth
    /// test.</summary>
    public static GpuGraphicsPipelineDescription Description { get; } = new(
        DepthCompare: GpuDepthCompare.Greater,
        Layout: SdfWorldEngine.PipelineLayouts.Mesh,
        Name: ShaderStem,
        VertexInput: new GpuVertexInputLayout(
            Attributes: [],
            StrideBytes: 0
        )
    );
    /// <summary>Gets the render pass the pipeline draws in: the target cleared and stored, left in its attachment
    /// layout, and the depth attachment cleared to <see cref="ClearDepth"/> and discarded.</summary>
    public static GpuRenderPassDescription RenderPass { get; } = new(
        Colors: [new GpuColorAttachment(
            FinalLayout: GpuImageLayout.RenderTarget,
            Format: TargetFormat,
            Load: GpuAttachmentLoad.Clear,
            Store: GpuAttachmentStore.Store
        )],
        Depth: new GpuDepthAttachment(
            ClearDepth: ClearDepth,
            Format: DepthFormat,
            Load: GpuAttachmentLoad.Clear,
            Store: GpuAttachmentStore.Discard
        )
    );
    /// <summary>Gets the pipeline's key in <see cref="Pipelines"/>.</summary>
    public GpuPassPipelineKey Key { get; }
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
    /// <summary>Reads one deployed stage.</summary>
    /// <param name="bytecodeExtension">The backend's compiled-shader extension.</param>
    /// <param name="directory">The directory the stages are deployed in.</param>
    /// <param name="stage">The stage's file suffix, <c>vert</c> or <c>frag</c>.</param>
    /// <returns>The stage's bytecode.</returns>
    public static ReadOnlyMemory<byte> Load(string bytecodeExtension, string directory, string stage) {
        ArgumentException.ThrowIfNullOrEmpty(argument: bytecodeExtension);
        ArgumentException.ThrowIfNullOrEmpty(argument: directory);
        ArgumentException.ThrowIfNullOrEmpty(argument: stage);

        return File.ReadAllBytes(path: Path.Combine(
            path1: directory,
            path2: $"{ShaderStem}.{stage}{bytecodeExtension}"
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
}
