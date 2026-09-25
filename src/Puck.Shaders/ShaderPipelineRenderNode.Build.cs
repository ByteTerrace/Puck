using System.Runtime.ExceptionServices;
using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

// A candidate graph in two halves, the way SdfWorldPipelineSource builds the SDF engine's pipelines. Its pipeline and
// module set — every shader module, compute pipeline, graphics pipeline and the render pass it is created for, and the
// float preview's — is built on the thread pool through BackgroundBuild, so a cold driver cache delays the install
// instead of freezing the pump. Its resources — images and buffers, the geometry buffer, framebuffers, descriptor pools,
// sets and samplers, command pools and fences — are allocated on the frame thread when the finished set is taken, and
// the graph installs there. Until then every frame presents the installed graph.
//
// A build starts at a frame boundary, never in Swap, Resize or SelectOutput: like SdfWorldPipelineSource.Poll, the
// produced frame starts it, so every request made between two frames — a compiled candidate, the host's resize of the
// slot showing it, a selection — is built once, together. One build runs at a time. A request that changes what should
// be built while a build runs does not cancel it: the finished set no longer matches, so it is discarded when it is
// taken and the next build starts. A device loss or disposal waits a build out and discards it, because it creates
// objects on the device being released. The replaced graph is never drained on the frame thread; see
// ShaderPipelineRenderNode.Retirement.cs.
public sealed partial class ShaderPipelineRenderNode {
    // The stages every pass's frame block is pushed to, a package's included.
    internal const GpuShaderStage FrameBlockStages = GpuShaderStage.Compute | GpuShaderStage.Fragment;

    private readonly BackgroundBuild<GraphBuild> m_build = new();

    private BuildKey m_buildKey;

    /// <summary>Gets whether a candidate's pipelines and shader modules are being built on the thread pool. Frames
    /// produced meanwhile present the installed graph; the build installs at the first frame boundary after it finishes,
    /// paused or running.</summary>
    public bool IsBuildingCandidate => (m_build.IsPending && !m_build.IsCompleted);

    private static GpuPushConstantBinding? PushConstantBinding(uint sizeBytes, GpuShaderStage stages) =>
        ((sizeBytes == 0)
            ? null
            : new GpuPushConstantBinding(
                data: new byte[sizeBytes],
                offset: 0,
                stageFlags: stages
            )
        );
    // Every version name mapped to the declaration of the storage that holds it, which fixes its kind, format and extent.
    private static Dictionary<string, ShaderPipelineResource> VersionSpecs(ShaderPipelinePlan plan) {
        var specs = new Dictionary<string, ShaderPipelineResource>(comparer: StringComparer.Ordinal);

        foreach (var storage in plan.Storages) {
            foreach (var version in storage.Versions) {
                specs.Add(
                    key: version,
                    value: storage.Declaration
                );
            }
        }

        return specs;
    }
    // Starts building what the node should install next, unless a build is already running. A paused instance builds
    // too, since a replacement is not a step. A candidate whose replacement peak exceeds the budget, or whose selected
    // output cannot be resolved, is refused here, before anything is created. The installed pipeline rebuilt after a
    // device loss is not a replacement and is never refused by the budget: nothing else is owned then, and the graph it
    // restores was accepted when it installed.
    private void EnsureBuild() {
        if (
            m_disposed ||
            m_build.IsPending ||
            !TryDesiredCandidate(
                candidate: out var candidate,
                extent: out var extent,
                pipeline: out var pipeline
            )
        ) {
            return;
        }

        BuildKey key;

        try {
            key = KeyFor(
                candidate: candidate,
                extent: extent,
                pipeline: pipeline
            );
            if (
                candidate &&
                (Account(
                    extent: extent,
                    plan: pipeline.Plan,
                    preview: key.Preview
                ) is { Fits: false } account)
            ) {
                throw account.Refusal();
            }
        } catch (Exception error) {
            Refuse(
                candidate: candidate,
                error: error
            );

            return;
        }

        StartBuild(key: key);
    }
    // Takes a finished build and installs it, refuses it, or discards it when what should be built has changed since it
    // started. A failed rebuild of the installed pipeline after a device loss rethrows here, on the frame thread, so the
    // loss reaches the host's recovery; the next frame starts a fresh build.
    private void InstallPending() {
        if (!m_build.TryTake(
            error: out var error,
            result: out var built
        )) {
            return;
        }

        var key = m_buildKey;

        m_buildKey = default;

        if (
            !TryDesiredKey(key: out var desired) ||
            !key.Matches(other: desired)
        ) {
            built?.Dispose();
            EnsureBuild();

            return;
        }

        if (error is not null) {
            if (!key.Candidate) {
                ExceptionDispatchInfo.Throw(source: error);
            }

            Refuse(
                candidate: true,
                error: error
            );

            return;
        }

        Install(
            built: built!,
            key: key
        );
    }

    // The selection the next graph installs with: one still waiting for its own float preview, else the published one.
    // A graph build carries the preview for it, and its install supersedes the waiting preview build.
    private string? DesiredSelection => (m_previewRequest?.Name ?? m_selectedOutput);

    // The float preview extent the selected output of a graph planned at this extent needs, or null for an RGBA8 output.
    private (uint Width, uint Height)? PreviewFor(ShaderPipelinePlan plan, (uint Width, uint Height) extent) {
        var desired = DesiredSelection;
        var selected = (IsDeclaredImageOutput(
            plan: plan,
            resourceName: desired
        )
            ? desired!
            : plan.DefaultOutput
        );
        var version = (plan.FindResource(name: selected) ?? throw new InvalidDataException(message: $"The shader pipeline publishes no output named '{selected}'."));
        var spec = plan.Storages[version.Storage].Declaration;

        return (NeedsPreview(spec: spec)
            ? (spec.Dimensions?.Resolve(
                frameHeight: extent.Height,
                frameWidth: extent.Width
            ) ?? extent)
            : null
        );
    }
    private void Refuse(bool candidate, Exception error) {
        if (candidate) {
            m_pending = null;
            // A refused resize is not retried until a different extent is requested.
            m_resizePending = false;
        }

        m_lastSwapError = error;
    }
    // Kept apart from EnsureBuild so the build's closure is allocated only when a build starts, never on a polled frame.
    private void StartBuild(BuildKey key) {
        var request = new BuildRequest(
            Device: m_device,
            DirectX: m_directX,
            Gpu: m_gpu,
            InFlight: m_inFlight,
            Instance: m_descriptor.Name,
            Key: key,
            Packages: m_packages
        );

        m_buildKey = key;
        m_build.Start(build: token => GraphBuild.Create(
            cancellationToken: token,
            request: request
        ));
    }
    // What the node should install next: a queued candidate, or the installed pipeline again at a requested extent, both
    // at the requested extent; or, after a device loss released the installed graph, the installed pipeline at its own.
    private bool TryDesiredCandidate(out CompiledShaderPipeline pipeline, out (uint Width, uint Height) extent, out bool candidate) {
        if (m_pending is { } pending) {
            (pipeline, extent, candidate) = (pending, (m_requestedWidth, m_requestedHeight), true);
        } else if (
            m_resizePending &&
            (m_pipeline is { } resized)
        ) {
            (pipeline, extent, candidate) = (resized, (m_requestedWidth, m_requestedHeight), true);
        } else if (
            !m_ready &&
            (m_pipeline is { } lost)
        ) {
            (pipeline, extent, candidate) = (lost, (m_width, m_height), false);
        } else {
            (pipeline, extent, candidate) = (null!, default, false);

            return false;
        }

        return true;
    }
    private bool TryDesiredKey(out BuildKey key) {
        key = default;

        if (!TryDesiredCandidate(
            candidate: out var candidate,
            extent: out var extent,
            pipeline: out var pipeline
        )) {
            return false;
        }

        try {
            key = KeyFor(
                candidate: candidate,
                extent: extent,
                pipeline: pipeline
            );
        } catch (InvalidDataException) {
            return false;
        }

        return true;
    }
    // The build of a pipeline at an extent, with the float preview its selected output needs.
    private BuildKey KeyFor(CompiledShaderPipeline pipeline, (uint Width, uint Height) extent, bool candidate) =>
        new(
            Candidate: candidate,
            Height: extent.Height,
            Pipeline: pipeline,
            Preview: PreviewFor(
                extent: extent,
                plan: pipeline.Plan
            ),
            Width: extent.Width
        );

    // What one build makes: the pipeline, the extent it is planned at, whether it is a candidate (a queued pipeline or a
    // resize) rather than the installed pipeline rebuilt after a device loss, and the float preview it needs.
    private readonly record struct BuildKey(CompiledShaderPipeline? Pipeline, uint Width, uint Height, bool Candidate, (uint Width, uint Height)? Preview) {
        public bool Matches(BuildKey other) =>
            (
                ReferenceEquals(
                    objA: Pipeline,
                    objB: other.Pipeline
                ) &&
                (Width == other.Width) &&
                (Height == other.Height) &&
                (Candidate == other.Candidate) &&
                (Preview == other.Preview)
            );
    }
    // Everything a build reads, captured on the frame thread when it starts; a build never touches the node.
    private sealed record BuildRequest(BuildKey Key, GpuDeviceServices Gpu, IGpuDeviceContext Device, bool DirectX, uint InFlight, string Instance, RenderGraphPackageRecorders Packages);
    // The pipeline and module set of one candidate, built on the thread pool. The install takes each object into the
    // runtime graph and clears it here, so disposing a build releases exactly what was never taken.
    private sealed class GraphBuild : IDisposable {
        private GraphBuild(int passCount) =>
            Passes = new PassObjects?[passCount];

        public PassObjects?[] Passes { get; }
        public PreviewObjects? Preview { get; set; }

        // Builds every pass's modules and pipelines, then the preview's. Safe on any thread: it only creates objects on
        // the device, counted through the node's wrapped services. The token is checked before each pass and the preview,
        // and a build that fails or is canceled releases what it created.
        public static GraphBuild Create(BuildRequest request, CancellationToken cancellationToken) {
            var pipeline = request.Key.Pipeline!;
            var plan = pipeline.Plan;
            var specs = VersionSpecs(plan: plan);
            var build = new GraphBuild(passCount: plan.Passes.Count);

            try {
                foreach (var planned in plan.Passes) {
                    cancellationToken.ThrowIfCancellationRequested();

                    var objects = new PassObjects();

                    build.Passes[planned.Index] = objects;
                    objects.Create(
                        cancellationToken: cancellationToken,
                        compiled: pipeline.Shaders.GetValueOrDefault(key: planned.Name),
                        planned: planned,
                        request: request,
                        specs: specs
                    );
                }

                if (request.Key.Preview is { } preview) {
                    cancellationToken.ThrowIfCancellationRequested();
                    build.Preview = PreviewObjects.Create(
                        device: request.Device,
                        gpu: request.Gpu,
                        directX: request.DirectX,
                        height: preview.Height,
                        inFlight: request.InFlight,
                        width: preview.Width
                    );
                }
            } catch {
                build.Dispose();

                throw;
            }

            return build;
        }
        public void Dispose() {
            foreach (var pass in Passes) {
                pass?.Dispose();
            }

            Preview?.Dispose();
            Preview = null;
        }
        public PassObjects TakePass(int index) {
            var objects = (Passes[index] ?? throw new InvalidOperationException(message: $"The build has no objects for pass {index}."));

            Passes[index] = null;

            return objects;
        }
    }
    // One pass's pipeline and modules: for a compute pass its module and pipeline; for a fullscreen pass its two modules,
    // the render pass it draws in, and the graphics pipeline created for that render pass; for a package pass what its
    // package's factory builds. The images it draws into are the graph's, allocated on the frame thread.
    private sealed class PassObjects : IDisposable {
        public List<GpuComputeBinding> Bindings = [];
        public IGpuComputePipeline? Compute;
        public (uint Width, uint Height) Extent;
        public IGpuPipeline? Graphics;
        public IRenderGraphPackageFactory? PackageFactory;
        public RenderGraphPackageRecorderContext? PackageContext;
        public IDisposable? PackageBuilt;
        public IGpuShaderModule? Primary;
        public IGpuRenderPass? RenderPass;
        public IGpuShaderModule? Secondary;

        public void Create(ShaderPipelinePlannedPass planned, CompiledShader? compiled, BuildRequest request, IReadOnlyDictionary<string, ShaderPipelineResource> specs, CancellationToken cancellationToken) {
            // A package pass's objects are whatever its factory builds; its recorder binds its own descriptors.
            if (planned.Declaration is not { } declaration) {
                var step = planned.Package!;

                Extent = step.ResolveExtent(
                    frameHeight: request.Key.Height,
                    frameWidth: request.Key.Width
                );
                PackageFactory = request.Packages.FactoryFor(
                    instance: request.Instance,
                    package: step.Package,
                    pass: planned.Name
                );
                PackageContext = PackageContextOf(
                    extent: Extent,
                    planned: planned,
                    request: request,
                    specs: specs
                );
                PackageBuilt = PackageFactory.Build(
                    cancellationToken: cancellationToken,
                    context: PackageContext
                );

                return;
            }
            if (compiled is null) {
                throw new InvalidDataException(message: $"Pass '{declaration.Name}' has no compiled shader.");
            }

            var push = PushConstantBinding(
                sizeBytes: planned.Parameters.SizeBytes,
                stages: FrameBlockStages
            );
            var device = request.Device;
            var gpu = request.Gpu;
            var primary = (request.DirectX
                ? compiled.DxilByStage
                : compiled.SpirvByStage
            );

            Extent = ResolveExtent(
                frame: (request.Key.Width, request.Key.Height),
                pass: declaration,
                specs: specs
            );
            Bindings = Descriptors(
                pass: declaration,
                specs: specs
            );

            if (declaration.Kind == ShaderPipelineDocumentPassKind.Compute) {
                if (
                    !primary.TryGetValue(
                    key: ShaderStage.Compute,
                    value: out var bytes
                ) ||
                    bytes.IsEmpty
                ) {
                    throw new InvalidDataException(message: $"Pass '{declaration.Name}' has no compute bytecode.");
                }

                Primary = gpu.ShaderModuleFactory.Create(
                    bytecode: bytes,
                    stage: GpuShaderStage.Compute
                );
                Compute = gpu.PipelineFactory.Create(
                    computeShaderModule: Primary,
                    description: new GpuComputePipelineDescription(
                        declaration.Name,
                        Bindings,
                        push
                    )
                );

                return;
            }

            if (
                !primary.TryGetValue(
                key: ShaderStage.Vertex,
                value: out var vertex
            ) ||
                !primary.TryGetValue(
                key: ShaderStage.Fragment,
                value: out var fragment
            )
            ) {
                throw new InvalidDataException(message: $"Fullscreen pass '{declaration.Name}' needs vertex and fragment bytecode.");
            }

            Primary = gpu.ShaderModuleFactory.Create(
                bytecode: vertex,
                stage: GpuShaderStage.Vertex
            );
            Secondary = gpu.ShaderModuleFactory.Create(
                bytecode: fragment,
                stage: GpuShaderStage.Fragment
            );

            var vertexInput = ((declaration.Geometry is { } geometry)
                ? new GpuVertexInputLayout(
                    Attributes: [.. geometry.Attributes.Select(selector: static attribute => new GpuVertexAttribute(
                        Format: Enum.Parse<GpuVertexFormat>(
                            ignoreCase: true,
                            value: attribute.Format
                        ),
                        Location: attribute.Location,
                        OffsetBytes: attribute.OffsetBytes
                    ))],
                    StrideBytes: geometry.StrideBytes
                )
                : ((declaration.Vertex == ShaderPipelineVertexInput.Position)
                    ? new GpuVertexInputLayout(
                        FullscreenTriangle.StrideBytes,
                        [new GpuVertexAttribute(
                            Format: GpuVertexFormat.R32G32Float,
                            Location: 0,
                            OffsetBytes: 0
                        )]
                    )
                    : new GpuVertexInputLayout(
                        Attributes: [],
                        StrideBytes: 0
                    ))
            );
            var sampled = ((uint)Bindings.Count(predicate: static item => (item.Kind == GpuComputeBindingKind.SampledImage)));

            RenderPass = gpu.RenderPassFactory.Create(
                description: RenderPassOf(
                    planned: planned,
                    specs: specs
                )
            );
            Graphics = gpu.PipelineFactory.Create(
                RenderPass,
                Primary,
                Secondary,
                new GpuGraphicsPipelineDescription(
                    declaration.Name,
                    vertexInput,
                    sampled,
                    false,
                    push,
                    DepthCompareOf(pass: planned)
                )
            );
        }

        // A pass with a depth attachment tests by its declared comparison, less when it declares none; any other pass has
        // no depth test.
        private static GpuDepthCompare? DepthCompareOf(ShaderPipelinePlannedPass pass) {
            if (!pass.Attachments.Any(predicate: static attachment => attachment.Depth)) {
                return null;
            }

            return (pass.Declaration!.DepthCompare ?? ShaderPipelineDepthCompare.Less) switch {
                ShaderPipelineDepthCompare.Less => GpuDepthCompare.Less,
                ShaderPipelineDepthCompare.LessOrEqual => GpuDepthCompare.LessOrEqual,
                ShaderPipelineDepthCompare.Greater => GpuDepthCompare.Greater,
                ShaderPipelineDepthCompare.GreaterOrEqual => GpuDepthCompare.GreaterOrEqual,
                ShaderPipelineDepthCompare.Equal => GpuDepthCompare.Equal,
                ShaderPipelineDepthCompare.Always => GpuDepthCompare.Always,
                var compare => throw new InvalidDataException(message: $"Pass '{pass.Name}' has an unknown depth comparison '{compare}'."),
            };
        }
        // A graphics pass's render pass, exactly as its planned attachments declare it: each attachment cleared or loaded,
        // stored or discarded, and left in its attachment layout, which is the state the pass's planned access leaves.
        private static GpuRenderPassDescription RenderPassOf(ShaderPipelinePlannedPass planned, IReadOnlyDictionary<string, ShaderPipelineResource> specs) {
            var colors = new List<GpuColorAttachment>();
            GpuDepthAttachment? depth = null;

            foreach (var attachment in planned.Attachments) {
                var format = ParseFormat(format: specs[attachment.Version].Format);

                if (attachment.Depth) {
                    depth = new GpuDepthAttachment(
                        Format: format,
                        Load: attachment.Load,
                        Store: attachment.Store
                    );
                } else {
                    colors.Add(item: new GpuColorAttachment(
                        FinalLayout: GpuImageLayout.RenderTarget,
                        Format: format,
                        Load: attachment.Load,
                        Store: attachment.Store
                    ));
                }
            }

            return new GpuRenderPassDescription(
                Colors: colors,
                Depth: depth
            );
        }

        public void Dispose() {
            PackageBuilt?.Dispose();
            PackageBuilt = null;
            Compute?.Dispose();
            Compute = null;
            Graphics?.Dispose();
            Graphics = null;
            RenderPass?.Dispose();
            RenderPass = null;
            Primary?.Dispose();
            Primary = null;
            Secondary?.Dispose();
            Secondary = null;
        }
    }
}
