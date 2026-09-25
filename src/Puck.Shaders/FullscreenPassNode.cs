using System.Buffers.Binary;

using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>Adapts a shipped <see cref="ShaderSetManifest"/> fullscreen effect to the canonical shader pipeline executor.</summary>
/// <remarks>The inner node produces the source surface; the adapter preserves its capture and device-lifetime semantics.</remarks>
public sealed class FullscreenPassNode : IRenderNode, ICaptureRequestTarget {
    /// <summary>The name a counters report heads <see cref="LoadWork"/>'s section with.</summary>
    public const string LoadWorkSourceName = "shaders.fullscreen-pass";

    private readonly CaptureRequestSlot m_capture = new();

    private readonly NodeDescriptor m_descriptor;
    private readonly IGpuDeviceContext m_deviceContext;
    private readonly uint m_height;
    private readonly bool m_hostsOnDirectX;
    private readonly IRenderNode m_inner;
    private readonly WorkCounterSet m_loadWork;
    private readonly ShaderSetManifest m_manifest;

    private readonly List<RetiringExecutor> m_retiring = [];

    private readonly uint m_width;

    private ShaderConfigValues m_config;
    // Bumped by every config change; each executor records the version its pass holds, so a change reaches an executor
    // that was still building when it was made.
    private uint m_configVersion;
    private bool m_disposed;
    private ShaderPipelineRenderNode? m_executor;
    private uint m_executorConfigVersion;
    private ShaderPipelineRenderNode? m_nextExecutor;
    private uint m_nextExecutorConfigVersion;
    private Surface m_presented;
    private GpuPixelFormat? m_inputFormat;
    private uint m_inputHeight;
    private uint m_inputWidth;
    private Dictionary<string, ShaderConfigValue>? m_liveConfig;
    private Dictionary<string, byte[]>? m_liveConfigBytes;

    /// <summary>Initializes a new instance of the <see cref="FullscreenPassNode"/> class over an inner node.</summary>
    /// <param name="inner">The node whose output the pass reads; the pass owns and disposes it.</param>
    /// <param name="manifest">The graphics shader set the pass runs.</param>
    /// <param name="config">The set's initial configuration values.</param>
    /// <param name="deviceContext">The device the pass renders on, the one the inner node renders on; the pass records
    /// through its services.</param>
    /// <param name="hostsOnDirectX">Whether the device is Direct3D 12 (else Vulkan).</param>
    /// <param name="width">The output width, in pixels.</param>
    /// <param name="height">The output height, in pixels.</param>
    /// <param name="loadWork">The counts each executor's bytecode load adds to; <see langword="null"/> counts into the
    /// process's <see cref="LoadWork"/>. It must count <see cref="Loads"/> and <see cref="BytecodeBytes"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/>, <paramref name="manifest"/>,
    /// <paramref name="config"/>, or <paramref name="deviceContext"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="loadWork"/> does not count <see cref="Loads"/> and
    /// <see cref="BytecodeBytes"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> or <paramref name="height"/> is zero.</exception>
    /// <exception cref="InvalidDataException"><paramref name="manifest"/> is a compute set, or declares anything but
    /// one sampled image.</exception>
    public FullscreenPassNode(IRenderNode inner, ShaderSetManifest manifest, ShaderConfigValues config,
        IGpuDeviceContext deviceContext, bool hostsOnDirectX, uint width, uint height, WorkCounterSet? loadWork = null) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(deviceContext);
        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);
        if (!manifest.IsGraphics) {
            throw new InvalidDataException(message: $"'{manifest.Name}' is a compute set; a fullscreen pass needs vertex and fragment stages.");
        }

        if (
            (manifest.Bindings.Count != 1) ||
            (manifest.Bindings[0].Kind != ShaderSetManifestBindingKind.SampledImage) ||
            (manifest.Bindings[0].Count != 1)
        ) {
            throw new InvalidDataException(message: $"'{manifest.Name}' must declare exactly one sampledImage binding (the inner surface) and nothing else to run as a fullscreen pass.");
        }

        m_descriptor = new NodeDescriptor(
            Name: manifest.Name,
            SurfaceId: SurfaceId.New()
        );
        m_inner = inner; m_manifest = manifest; m_config = config;
        m_width = width; m_height = height; m_hostsOnDirectX = hostsOnDirectX; m_deviceContext = deviceContext;
        m_loadWork = (loadWork ?? LoadWork);

        if (
            !m_loadWork.TryRead(kind: Loads, value: out _) ||
            !m_loadWork.TryRead(kind: BytecodeBytes, value: out _)
        ) {
            throw new ArgumentException(
                message: $"Work source '{m_loadWork.Name}' does not count {Loads.Name} and {BytecodeBytes.Name}.",
                paramName: nameof(loadWork)
            );
        }
    }

    /// <summary>Gets the kind counting executor bytecode loads: one per executor the pass builds for a new input
    /// extent or format, each reading the vertex and fragment stages for both backends.</summary>
    public static WorkKind Loads { get; } = new(name: "shaders.fullscreen-pass.loads", unit: "count", workClass: WorkClass.PerBackendDeterministic);
    /// <summary>Gets the kind counting the bytecode bytes those loads read.</summary>
    public static WorkKind BytecodeBytes { get; } = new(name: "shaders.fullscreen-pass.bytecode-bytes", unit: "bytes", workClass: WorkClass.PerBackendDeterministic);

    /// <summary>Gets the process's fullscreen-pass load counts, which a pass built without its own counts adds to and
    /// a host registers as its <see cref="LoadWorkSourceName"/> source. Counts only go up.</summary>
    public static WorkCounterSet LoadWork =>
        LoadCounts.Process;
    /// <summary>Gets the live configuration values the pass's frame block carries.</summary>
    public ShaderConfigValues Config => m_config;
    /// <summary>Gets this adapter node descriptor.</summary>
    public NodeDescriptor Descriptor => m_descriptor;
    public string? PendingCapturePath => (m_capture.PendingPath ?? (m_nextExecutor?.PendingCapturePath ?? (m_executor?.PendingCapturePath ?? (m_inner as ICaptureRequestTarget)?.PendingCapturePath)));

    private ShaderPipelineRenderNode CreateExecutor(GpuPixelFormat inputFormat, uint inputWidth, uint inputHeight) {
        var input = new ShaderPipelineResource(
            "input",
            ShaderPipelineResourceKind.Image,
            inputFormat.ToString(),
            ShaderPipelineDimensions.Absolute(
                height: inputHeight,
                width: inputWidth
            ),
            Initialization: ShaderPipelineInitialization.External
        );
        var output = new ShaderPipelineResource(
            "output",
            ShaderPipelineResourceKind.Image,
            "R8G8B8A8Unorm",
            ShaderPipelineDimensions.Relative()
        );
        var pass = new ShaderPipelinePass(
            Name: m_manifest.Name,
            Source: Path.Combine(
                path1: m_manifest.Directory,
                path2: (m_manifest.Stages.Fragment! + ".hlsl")
            ),
            EntryPoint: "PSMain",
            Kind: ShaderPipelinePassKind.Fullscreen,
            Inputs: [new ResourceReference(
                    "input",
                    Binding: m_manifest.Bindings[0].VulkanBinding
                )],
            Outputs: [new ResourceReference("output")],
            Vertex: ShaderPipelineVertexInput.Position,
            Config: ConfigDefaultingTo(values: m_config)
        );
        var definition = new ShaderPipelineDefinition(
            m_manifest.Name,
            [input, output],
            [pass],
            ["output"]
        );
        var plan = ShaderPipelineCompiler.Plan(definition: definition);

        m_loadWork.Count(kind: Loads);

        var spirv = new Dictionary<ShaderStage, ReadOnlyMemory<byte>> {
            [ShaderStage.Vertex] = ReadBytecode(
                bytecodeExtension: ".spv",
                stem: m_manifest.Stages.Vertex!
            ),
            [ShaderStage.Fragment] = ReadBytecode(
                bytecodeExtension: ".spv",
                stem: m_manifest.Stages.Fragment!
            ),
        };
        var dxil = new Dictionary<ShaderStage, ReadOnlyMemory<byte>> {
            [ShaderStage.Vertex] = ReadBytecode(
                bytecodeExtension: ".dxil",
                stem: m_manifest.Stages.Vertex!
            ),
            [ShaderStage.Fragment] = ReadBytecode(
                bytecodeExtension: ".dxil",
                stem: m_manifest.Stages.Fragment!
            ),
        };
        var compiled = new CompiledShader(
            m_manifest.Name,
            Path.Combine(
                path1: m_manifest.Directory,
                path2: (m_manifest.Name + ShaderSetManifest.FileSuffix)
            ),
            "manifest",
            spirv,
            dxil,
            []
        );
        var candidate = new CompiledShaderPipeline(
            plan: plan,
            shaders: new Dictionary<string, CompiledShader> { [m_manifest.Name] = compiled }
        );

        return new ShaderPipelineRenderNode(
            candidate,
            m_deviceContext,
            m_hostsOnDirectX,
            m_width,
            m_height,
            outputLayout: GpuImageLayout.ShaderReadOnly
        );
    }
    // Reads the ShaderSetManifest.Load-validated bytes rather than the file again — that load already read, counted,
    // and format-checked the same bytecode.
    private ReadOnlyMemory<byte> ReadBytecode(string stem, string bytecodeExtension) {
        var key = $"{stem}{bytecodeExtension}";

        if (!m_manifest.Bytecode.TryGetValue(
            key: key,
            value: out var bytecode
        )) {
            throw new FileNotFoundException(
                fileName: m_manifest.BytecodePath(
                    bytecodeExtension: bytecodeExtension,
                    stem: stem
                ),
                message: $"'{m_manifest.Name}' manifest carries no validated bytecode for '{key}'; ShaderSetManifest.Load did not read it."
            );
        }

        m_loadWork.Add(
            amount: bytecode.Length,
            kind: BytecodeBytes
        );

        return bytecode;
    }
    // The manifest's config schema with each field defaulting to its live value, so an executor's pass starts from the
    // live config on its first frame.
    private IReadOnlyDictionary<string, ShaderConfigField>? ConfigDefaultingTo(ShaderConfigValues values) {
        if (m_manifest.Config is not { } schema) {
            return null;
        }

        var json = values.ToJson();

        return schema.ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: pair => (pair.Value with { Default = json.GetProperty(propertyName: pair.Key) }),
            keySelector: static pair => pair.Key
        );
    }
    // Rebinds an installed executor's pass to the live config when a change has not reached it yet.
    private void ApplyConfig(ShaderPipelineRenderNode executor, ref uint appliedVersion) {
        if (
            (appliedVersion == m_configVersion) ||
            !executor.IsReady
        ) {
            return;
        }

        if (executor.TrySetConfig(
            config: m_config.ToJson(),
            passName: m_manifest.Name,
            reason: out _
        )) {
            appliedVersion = m_configVersion;
        }
    }
    private static GpuPixelFormat ToGpuFormat(SurfaceFormat format) {
        return format switch {
            SurfaceFormat.R8G8B8A8Unorm => GpuPixelFormat.R8G8B8A8Unorm,
            SurfaceFormat.B8G8R8A8Unorm => GpuPixelFormat.B8G8R8A8Unorm,
            _ => throw new InvalidDataException(message: $"Fullscreen input surface format '{format}' is unsupported."),
        };
    }

    /// <summary>Releases the active executor and inner node.</summary>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_capture.Refuse(error: new ObjectDisposedException(objectName: nameof(FullscreenPassNode)));
        try {
            m_nextExecutor?.Dispose();
            m_executor?.Dispose();
            foreach (var retiring in m_retiring) {
                retiring.Node.Dispose();
            }
            m_retiring.Clear();
        } finally { m_inner.Dispose(); }
    }
    /// <summary>Forwards device loss to the active executor, any executor building for a new input, and the inner
    /// node; a replaced executor still waiting to retire is released at once, since the lost device's work is gone, and
    /// a capture armed here is refused (<see cref="CaptureRequestSlot.RefuseForDeviceLoss"/>).</summary>
    public void OnDeviceLost() {
        m_nextExecutor?.OnDeviceLost();
        m_executor?.OnDeviceLost();
        foreach (var retiring in m_retiring) {
            retiring.Node.DisposeRetired();
        }
        m_retiring.Clear();
        m_capture.RefuseForDeviceLoss();
        m_inner.OnDeviceLost();
    }
    /// <summary>Produces one adapted frame from the inner node.</summary>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        var surface = m_inner.ProduceFrame(context: context);

        if (
            surface.IsEmpty ||
            (surface.ImageViewHandle == 0)
        ) {
            m_capture.Forward(target: (m_inner as ICaptureRequestTarget));
            return surface;
        }
        var format = ToGpuFormat(format: surface.Format);

        if (
            ((m_executor is null) && (m_nextExecutor is null)) ||
            (m_inputFormat != format) ||
            (m_inputWidth != surface.Width) ||
            (m_inputHeight != surface.Height)
        ) {
            m_nextExecutor?.Dispose();
            m_nextExecutor = CreateExecutor(
                format,
                surface.Width,
                surface.Height
            );
            m_nextExecutorConfigVersion = m_configVersion;
            m_inputFormat = format;
            m_inputWidth = surface.Width;
            m_inputHeight = surface.Height;
        }

        var input = new ShaderPipelineExternalImage(
            surface.ImageHandle,
            surface.ImageViewHandle,
            surface.Width,
            surface.Height,
            format,
            GpuImageLayout.ShaderReadOnly
        );

        // An executor for a new input builds its pipelines off the frame thread; until it produces, the installed
        // executor's last frame stays published.
        if (m_nextExecutor is { } next) {
            next.BindImage(
                image: input,
                name: "input"
            );
            m_capture.Forward(target: next);
            ApplyConfig(
                appliedVersion: ref m_nextExecutorConfigVersion,
                executor: next
            );

            var produced = next.ProduceFrame(context: context);

            if (produced.IsEmpty) {
                return ((m_executor is null)
                    ? produced
                    : m_presented
                );
            }

            // The replaced executor is not drained here: a downstream reader may still sample its last image, so it
            // retires once its successor's RetirementLag-th submission after this one completes.
            if (m_executor is { } replaced) {
                m_retiring.Add(item: new RetiringExecutor(
                    node: replaced,
                    retiresAfter: (next.SubmissionCount + ShaderPipelineRenderNode.RetirementLag)
                ));
            }
            m_executor = next;
            m_executorConfigVersion = m_nextExecutorConfigVersion;
            m_nextExecutor = null;

            return (m_presented = produced);
        }

        var executor = m_executor!;

        executor.BindImage(
            image: input,
            name: "input"
        );
        m_capture.Forward(target: executor);
        ApplyConfig(
            appliedVersion: ref m_executorConfigVersion,
            executor: executor
        );
        m_presented = executor.ProduceFrame(context: context);
        RetireCompleted(successor: executor);
        return m_presented;
    }
    /// <summary>Arms a capture that is forwarded to the current executor when available.</summary>
    public void RequestCapture(FrameCaptureRequest request) {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        m_capture.Arm(
            request,
            PendingCapturePath
        );
    }
    /// <summary>Updates a declared floating-point manifest parameter; the pass's frame block carries it from the next
    /// frame its executor renders.</summary>
    /// <param name="field">The config field's name.</param>
    /// <param name="value">The value, which must be finite and inside the field's range.</param>
    /// <returns><see langword="true"/> when the field is a float field and the value is admitted.</returns>
    public bool TrySetConfig(string field, float value) {
        if (
            (m_manifest.Config is not { } schema) ||
            !schema.TryGetValue(
            key: field,
            value: out var declared
        ) ||
            (declared.Type != ShaderValueType.Float) ||
            !float.IsFinite(f: value) ||
            !ShaderConfigBinding.InRange(
            field: declared,
            value: value
        )
        ) {
            return false;
        }

        if (m_liveConfig is not { } live) {
            live = new Dictionary<string, ShaderConfigValue>(comparer: StringComparer.Ordinal);
            foreach (var name in m_config.Names) {
                live[name] = m_config[name];
            }

            m_liveConfig = live; m_liveConfigBytes = new Dictionary<string, byte[]>(comparer: StringComparer.Ordinal); m_config = new ShaderConfigValues(values: live);
        }
        if (!m_liveConfigBytes!.TryGetValue(
            key: field,
            value: out var bytes
        )) {
            bytes = new byte[ShaderValueTypes.ComponentBytes]; m_liveConfigBytes[field] = bytes; live[field] = new ShaderConfigValue(
                Bytes: bytes,
                Type: ShaderValueType.Float
            );
        }
        BinaryPrimitives.WriteSingleLittleEndian(
            destination: bytes,
            value: value
        );
        m_configVersion++;

        return true;
    }

    // A nested holder initializes after every kind above, whatever order the members are declared in.
    private static class LoadCounts {
        internal static readonly WorkCounterSet Process = new(
            kinds: [Loads, BytecodeBytes],
            name: LoadWorkSourceName
        );
    }

    // Releases every replaced executor whose retiring submission, its successor's, has completed.
    private void RetireCompleted(ShaderPipelineRenderNode successor) {
        for (var index = (m_retiring.Count - 1); (index >= 0); index--) {
            var retiring = m_retiring[index];

            if (
                (retiring.Fence is null) &&
                (successor.SubmissionCount >= retiring.RetiresAfter)
            ) {
                retiring.Fence = successor.LatestSubmission;
            }
            if (retiring.Fence is { IsSignaled: true }) {
                retiring.Node.DisposeRetired();
                m_retiring.RemoveAt(index: index);
            }
        }
    }

    // An executor a new input replaced, waiting for the successor submission whose completion retires it.
    private sealed class RetiringExecutor(ShaderPipelineRenderNode node, long retiresAfter) {
        public IGpuSubmissionFence? Fence { get; set; }

        public ShaderPipelineRenderNode Node { get; } = node;
        public long RetiresAfter { get; } = retiresAfter;
    }
}
