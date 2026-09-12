using System.Text.Json;
using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>Describes a host-owned same-device image supplied to a pipeline.</summary>
public readonly record struct ShaderPipelineExternalImage(
    nint ImageHandle,
    nint ImageViewHandle,
    uint Width,
    uint Height,
    GpuPixelFormat Format,
    GpuImageLayout Layout = GpuImageLayout.ShaderReadOnly);

/// <summary>
/// One render node for an ordered multi-pass shader graph. All passes are recorded before one queue submission;
/// frame-slot fences protect command buffers and descriptors, and every inter-pass image transition is explicit.
/// A compiled candidate is installed only after all prior slots retire.
/// </summary>
public sealed class ShaderPipelineRenderNode : IRenderNode, ICaptureRequestTarget, IPassTimingSource {
    private const GpuComputeAccess PriorAccess = GpuComputeAccess.ShaderRead | GpuComputeAccess.ShaderWrite | GpuComputeAccess.TransferWrite | GpuComputeAccess.ColorAttachmentWrite;
    private const GpuComputeStage PriorStages = GpuComputeStage.ComputeShader | GpuComputeStage.FragmentShader | GpuComputeStage.Transfer | GpuComputeStage.ColorAttachmentOutput;
    private readonly IGpuComputeServices m_gpu;
    private readonly IFullscreenPassServices? m_graphics;
    private readonly IGpuDeviceContext m_device;
    private readonly bool m_directX;
    private readonly uint m_inFlight;
    private readonly ulong m_allocationBudgetBytes;
    private readonly IReadOnlyDictionary<string, IShaderPipelinePassConstants> m_passConstants;
    private readonly GpuImageLayout m_outputLayout;
    private readonly HashSet<string> m_positionVertexPasses;
    private readonly FrameSlot[] m_slots;
    private readonly NodeDescriptor m_descriptor;
    private readonly Dictionary<string, ShaderPipelineExternalImage> m_externalImages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IGpuBuffer> m_externalBuffers = new(StringComparer.Ordinal);

    private CompiledShaderPipeline? m_pipeline;
    private CompiledShaderPipeline? m_pending;
    private RuntimeResource[] m_resources = [];
    private IReadOnlyDictionary<string, RuntimeResource> m_resourceLookup = new Dictionary<string, RuntimeResource>(StringComparer.Ordinal);
    private RuntimePass[] m_passes = [];
    private string? m_selectedOutput;
    private Surface m_lastSurface;
    private uint m_width;
    private uint m_height;
    private ulong m_frame;
    private int m_steps;
    private bool m_disposed;
    private bool m_ready;
    private ulong m_allocationBytes;
    private Exception? m_lastSwapError;
    private readonly CaptureRequestSlot m_capture = new();
    private readonly CapturePngWriter m_capturePng = new();
    private IGpuSurfaceReadback? m_readback;
    private FloatPreviewPass? m_preview;
    private bool m_outputRefreshRequested;
    private bool m_initializationPending = true;
    private string[] m_passLabels = [];
    private readonly List<nint> m_commands = [];

    /// <summary>Creates an initially empty node. The first valid <see cref="Swap"/> installs a graph.</summary>
    public ShaderPipelineRenderNode(string name, IGpuComputeServices gpu, IGpuDeviceContext deviceContext, bool hostsOnDirectX, uint width, uint height, IFullscreenPassServices? graphics = null, uint inFlightFrames = 3, ulong allocationBudgetBytes = 512UL * 1024UL * 1024UL, IReadOnlyDictionary<string, IShaderPipelinePassConstants>? passConstants = null, GpuImageLayout outputLayout = GpuImageLayout.General, IReadOnlySet<string>? positionVertexPasses = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(deviceContext);
        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);
        ArgumentOutOfRangeException.ThrowIfZero(inFlightFrames);
        ArgumentOutOfRangeException.ThrowIfZero(allocationBudgetBytes);
        m_descriptor = new NodeDescriptor(name, SurfaceId.New());
        m_gpu = gpu;
        m_device = deviceContext;
        m_directX = hostsOnDirectX;
        m_graphics = graphics;
        m_inFlight = inFlightFrames;
        m_allocationBudgetBytes = allocationBudgetBytes;
        if (outputLayout is not GpuImageLayout.General and not GpuImageLayout.ShaderReadOnly) {
            throw new ArgumentOutOfRangeException(nameof(outputLayout), outputLayout, "Pipeline outputs must be General or ShaderReadOnly.");
        }
        m_outputLayout = outputLayout;
        m_positionVertexPasses = positionVertexPasses is null ? new(StringComparer.Ordinal) : new(positionVertexPasses, StringComparer.Ordinal);
        m_passConstants = passConstants is null ? new Dictionary<string, IShaderPipelinePassConstants>(StringComparer.Ordinal) : new Dictionary<string, IShaderPipelinePassConstants>(passConstants, StringComparer.Ordinal);
        m_slots = new FrameSlot[inFlightFrames];
        for (var i = 0; i < m_slots.Length; i++) { m_slots[i] = new FrameSlot(); }
        m_width = width;
        m_height = height;
    }

    /// <summary>Creates a node with an already compiled candidate.</summary>
    public ShaderPipelineRenderNode(CompiledShaderPipeline pipeline, IGpuComputeServices gpu, IGpuDeviceContext deviceContext, bool hostsOnDirectX, uint width, uint height, IFullscreenPassServices? graphics = null, uint inFlightFrames = 3, ulong allocationBudgetBytes = 512UL * 1024UL * 1024UL, IReadOnlyDictionary<string, IShaderPipelinePassConstants>? passConstants = null, GpuImageLayout outputLayout = GpuImageLayout.General, IReadOnlySet<string>? positionVertexPasses = null)
        : this(pipeline.Plan.Definition.Name, gpu, deviceContext, hostsOnDirectX, width, height, graphics, inFlightFrames, allocationBudgetBytes, passConstants, outputLayout, positionVertexPasses) => Swap(pipeline);

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;
    /// <summary>Gets or sets presentation values supplied to shader frame constants by the host.</summary>
    public ShaderFrameInput Input { get; set; }
    /// <summary>Gets or sets whether rendering is paused.</summary>
    public bool Paused { get; set; }
    /// <summary>Gets the submitted frame count.</summary>
    public ulong FrameCounter => m_frame;
    /// <summary>Gets the last deferred candidate creation failure, if any.</summary>
    public Exception? LastSwapError => m_lastSwapError;
    /// <summary>Gets whether a compiled graph has allocated all of its GPU resources.</summary>
    public bool IsReady => m_pipeline is not null && m_ready;
    /// <summary>Gets the active immutable execution plan.</summary>
    public ShaderPipelinePlan? Plan => m_pipeline?.Plan;
    /// <summary>Gets the current graph allocation in bytes.</summary>
    public ulong AllocationBytes => checked(m_allocationBytes + (m_preview is null ? 0 : (ulong)m_preview.Width * m_preview.Height * 4 * m_inFlight));
    /// <summary>Gets the configured graph allocation ceiling.</summary>
    public ulong AllocationBudgetBytes => m_allocationBudgetBytes;
    /// <summary>Gets resource allocation and extent information for the active graph.</summary>
    public IReadOnlyList<ShaderPipelineResourceStatus> ResourceStatus => m_resources.Select(resource => resource.Status(m_width, m_height)).ToArray();
    /// <summary>Gets pass binding information. GPU timings are currently unavailable.</summary>
    public IReadOnlyList<ShaderPipelinePassStatus> PassStatus => m_passes.Select(pass => new ShaderPipelinePassStatus(pass.Spec.Name, pass.Spec.Kind, (uint)pass.Bindings.Count, null)).ToArray();
    /// <inheritdoc/>
    public ReadOnlySpan<string> PassLabels => m_passLabels;
    /// <inheritdoc/>
    public int PassCount => m_passes.Length;
    /// <inheritdoc/>
    public bool TryReadPassTimings(Span<double> passMilliseconds, out int passCount, out double frameMilliseconds) {
        if (passMilliseconds.Length < m_passes.Length) {
            throw new ArgumentException("The timing destination is smaller than PassCount.", nameof(passMilliseconds));
        }
        passMilliseconds[..m_passes.Length].Clear();
        passCount = 0;
        frameMilliseconds = 0;
        return false;
    }
    /// <inheritdoc/>
    public string? PendingCapturePath => m_capture.PendingPath;

    /// <summary>Requests a capture of the next completed RGBA8 output frame.</summary>
    public void RequestCapture(FrameCaptureRequest request) {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        m_capture.Arm(request, PendingCapturePath);
    }

    /// <summary>Requests one render while <see cref="Paused"/>.</summary>
    public void Step() => m_steps = checked(m_steps + 1);
    /// <summary>Gets or sets whether a step is pending.</summary>
    public bool StepRequested { get => m_steps != 0; set => m_steps = value ? Math.Max(1, m_steps) : 0; }

    /// <summary>Binds a host-owned image for a named external resource. The node never disposes it.</summary>
    public void BindImage(string name, ShaderPipelineExternalImage image) {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfZero(image.ImageHandle);
        ArgumentOutOfRangeException.ThrowIfZero(image.ImageViewHandle);
        ValidateExternalBinding(name, ShaderPipelineResourceKind.Image);
        m_externalImages[name] = image;
    }
    /// <summary>Binds a host-owned buffer for a named external resource. The node never disposes it.</summary>
    public void BindBuffer(string name, IGpuBuffer buffer) {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(buffer);
        ValidateExternalBinding(name, ShaderPipelineResourceKind.Buffer);
        m_externalBuffers[name] = buffer;
    }

    private void ValidateExternalBinding(string name, ShaderPipelineResourceKind kind) {
        var plan = m_pending?.Plan ?? m_pipeline?.Plan;
        if (plan is not null && !plan.Resources.Any(resource => resource.Name == name && resource.Declaration.IsExternal && resource.Declaration.Kind == kind)) {
            throw new ArgumentException($"Resource '{name}' is not a declared external {kind} in the candidate graph.", nameof(name));
        }
    }
    /// <summary>Publishes a declared image output by name.</summary>
    public void SelectOutput(string name) {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var pipeline = m_pipeline ?? m_pending ?? throw new InvalidOperationException("No shader pipeline is installed.");
        var output = pipeline.Plan.Outputs.FirstOrDefault(item => item.Name == name);
        var resourceName = output?.Resource.Name ?? name;
        if (!pipeline.Plan.Resources.Any(resource => resource.Name == resourceName && resource.Declaration.Kind == ShaderPipelineResourceKind.Image)) {
            throw new ArgumentException($"Output {name} is not a declared image resource.", nameof(name));
        }
        m_selectedOutput = resourceName;
        m_outputRefreshRequested = true;
    }

    /// <summary>Rebinds a complete JSON object to one pass's authored parameter schema.</summary>
    public bool TrySetConfig(string passName, JsonElement? config, out string reason) {
        ArgumentException.ThrowIfNullOrWhiteSpace(passName);
        if (m_pipeline is null || !m_ready) {
            reason = "The shader pipeline has not allocated its GPU resources yet.";
            return false;
        }
        var pass = m_passes.FirstOrDefault(item => item.Spec.Name == passName);
        if (pass is null) {
            reason = $"Unknown shader pass '{passName}'.";
            return false;
        }
        if (pass.CustomConstants is not null) {
            reason = "This pass supplies a custom constants writer.";
            return false;
        }
        if (!pass.ParametersLayout.TryBind(config, out var values, out reason)) {
            return false;
        }
        pass.Parameters = values;
        return true;
    }
    /// <summary>Copies one pass's live packed parameter block for inspection or persistence.</summary>
    public bool TryGetConfigSnapshot(string passName, out byte[] bytes) {
        ArgumentException.ThrowIfNullOrWhiteSpace(passName);
        var pass = m_passes.FirstOrDefault(item => item.Spec.Name == passName);
        if (pass is null) {
            bytes = [];
            return false;
        }
        bytes = pass.Parameters.Bytes.ToArray();
        return true;
    }
    /// <summary>Queues an atomic compiled candidate for the next frame boundary.</summary>
    public void Swap(CompiledShaderPipeline pipeline) {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        ArgumentNullException.ThrowIfNull(pipeline);
        if (!pipeline.IsSuccess) {
            throw new InvalidDataException("A failed shader compilation cannot be installed.");
        }
        ValidatePlan(pipeline.Plan, m_graphics);
        if (m_inFlight < 2 && pipeline.Plan.Resources.Any(static resource => resource.Declaration.History)) {
            throw new InvalidDataException("History requires at least two frame slots.");
        }
        m_lastSwapError = null;
        m_pending = pipeline;
    }

    /// <summary>Resizes all graph images after the current frame slots retire.</summary>
    public void Resize(uint width, uint height) {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);
        if (width == m_width && height == m_height) {
            return;
        }
        m_width = width;
        m_height = height;
        Release(wait: true);
        m_lastSurface = default;
    }

    /// <summary>Clears all history state and resets the presentation counter.</summary>
    public void Reset() {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        WaitAll();
        foreach (var resource in m_resources) {
            if (resource.Layouts is not null) {
                Array.Fill(resource.Layouts, GpuImageLayout.Undefined);
            }
            if (resource.Initialized is not null) {
                Array.Fill(resource.Initialized, false);
            }
        }
        m_initializationPending = true;
        m_frame = 0;
        m_steps = 0;
        m_outputRefreshRequested = false;
        m_lastSurface = default;
    }

    /// <inheritdoc/>
    public void OnDeviceLost() => Release(wait: false);

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }
        if (!Paused || m_steps != 0 || !m_ready) { InstallPending(); }
        if (m_pipeline is null) {
            return default;
        }
        if (Paused && m_steps == 0 && m_ready && !m_lastSurface.IsEmpty && !m_outputRefreshRequested) {
            if (m_frame != 0) {
                CaptureIfPending((int)((m_frame - 1) % m_inFlight));
            }
            return m_lastSurface;
        }
        if (Paused && m_steps == 0 && m_ready && m_outputRefreshRequested && m_frame != 0) {
            PresentSelectedOutput();
            CaptureIfPending((int)((m_frame - 1) % m_inFlight));
            return m_lastSurface;
        }
        if (Paused && m_steps != 0) {
            m_steps--;
        }
        Ensure();
        ValidateExternalResources(m_pipeline.Plan);
        var selectedResource = m_resourceLookup[m_selectedOutput ?? m_pipeline.Plan.OutputResourceName];
        EnsurePreview(selectedResource);
        var slotIndex = (int)(m_frame % m_inFlight);
        var slot = m_slots[slotIndex];
        slot.Fence!.Wait();
        foreach (var resource in m_resources) {
            if (resource.Spec.IsExternal && resource.Spec.Kind == ShaderPipelineResourceKind.Image) { Array.Fill(resource.Layouts!, m_externalImages[resource.Spec.Name].Layout); }
        }
        var commands = m_commands;
        commands.Clear();
        foreach (var pass in m_passes) {
            Record(pass, slotIndex, context, commands);
        }
        if (NeedsPreview(selectedResource)) {
            m_preview!.Record(selectedResource, ResolveImage(selectedResource, selectedResource.Spec.Name, slotIndex), slotIndex, commands);
        }
        FinalizeOutputs(slotIndex, commands);
        if (commands.Count == 0) {
            return m_lastSurface;
        }
        m_gpu.QueueSubmitter.Submit(m_device, CollectionsMarshal.AsSpan(commands), slot.Fence!);
        m_lastSurface = Output(slotIndex);
        m_outputRefreshRequested = false;
        m_frame++;
        m_ready = true;
        CaptureIfPending(slotIndex);
        return m_lastSurface;
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }
        m_disposed = true;
        m_capture.Refuse(new ObjectDisposedException(nameof(ShaderPipelineRenderNode)));
        Release(wait: true);
    }
    private void InstallPending() {
        if (m_pending is not { } next) {
            return;
        }
        // Build the candidate beside the live graph. A malformed descriptor, unsupported format, or
        // backend pipeline failure must leave the last good graph and its feedback images intact.
        var previousPipeline = m_pipeline;
        var previousResources = m_resources;
        var previousLookup = m_resourceLookup;
        var previousAllocation = m_allocationBytes;
        var previousPasses = m_passes;
        var previousLabels = m_passLabels;
        var previousReady = m_ready;
        var previousSelectedOutput = m_selectedOutput;
        var hadFences = m_slots.Any(static slot => slot.Fence is not null);
        m_pipeline = next;
        m_resources = [];
        m_resourceLookup = new Dictionary<string, RuntimeResource>(StringComparer.Ordinal);
        m_passes = [];
        m_ready = false;
        try {
            Ensure();
            WaitAll();
            // A downstream compositor may still sample the old graph after its own submission fence.
            m_device.WaitIdle();
            PreserveCompatibleHistory(previousResources, m_resources, previousPasses);
            PreserveLiveParameters(previousPasses, m_passes);
            DisposeGraph(previousPasses, previousResources);
            m_pending = null;
            m_selectedOutput = IsDeclaredImageOutput(next.Plan, previousSelectedOutput)
                ? previousSelectedOutput
                : next.Plan.OutputResourceName;
        } catch (Exception error) {
            DisposeGraph(m_passes, m_resources);
            m_pipeline = previousPipeline;
            m_resources = previousResources;
            m_resourceLookup = previousLookup;
            m_passes = previousPasses;
            m_passLabels = previousLabels;
            m_ready = previousReady;
            m_allocationBytes = previousAllocation;
            m_pending = null;
            m_lastSwapError = error;
            if (!hadFences) {
                foreach (var slot in m_slots) { slot.Fence?.Dispose(); slot.Fence = null; }
            }
            return;
        }
    }
    private static bool IsDeclaredImageOutput(ShaderPipelinePlan plan, string? resourceName) {
        if (resourceName is null) {
            return false;
        }
        return plan.Resources.Any(resource => resource.Name == resourceName && resource.Declaration.Kind == ShaderPipelineResourceKind.Image);
    }
    private void PreserveCompatibleHistory(RuntimeResource[] previous, RuntimeResource[] next, RuntimePass[] previousPasses) {
        var oldByName = previous.ToDictionary(resource => resource.Spec.Name, StringComparer.Ordinal);
        foreach (var current in next) {
            if (!current.Spec.History || !oldByName.TryGetValue(current.Spec.Name, out var old) || !CompatibleHistory(old.Spec, current.Spec)) {
                continue;
            }
            if (current.Images is not null && old.Images is not null) {
                foreach (var image in current.Images) {
                    image?.Dispose();
                }
                current.Images = old.Images;
                current.Layouts = old.Layouts;
                current.Initialized = old.Initialized;
                old.Images = null;
                old.Buffers = null;
            } else if (current.Targets is not null && old.Targets is not null) {
                var nextPass = m_passes.First(pass => ReferenceEquals(pass.Targets, current.Targets));
                var oldPass = previousPasses.First(pass => ReferenceEquals(pass.Targets, old.Targets));
                foreach (var target in current.Targets) { target.Dispose(); }
                current.Targets = old.Targets;
                current.Layouts = old.Layouts;
                current.Initialized = old.Initialized;
                nextPass.Targets = old.Targets;
                oldPass.Targets = null;
                old.Targets = null;
            } else if (current.Buffers is not null && old.Buffers is not null) {
                foreach (var buffer in current.Buffers) {
                    buffer?.Dispose();
                }
                current.Buffers = old.Buffers;
                current.Initialized = old.Initialized;
                old.Buffers = null;
            }
        }
    }
    private static void PreserveLiveParameters(RuntimePass[] previous, RuntimePass[] next) {
        var oldByName = previous.ToDictionary(pass => pass.Spec.Name, StringComparer.Ordinal);
        foreach (var current in next) {
            if (!oldByName.TryGetValue(current.Spec.Name, out var old) || old.ParametersLayout.SizeBytes != current.ParametersLayout.SizeBytes || old.ParametersLayout.Slots.Count != current.ParametersLayout.Slots.Count) {
                continue;
            }
            var compatible = true;
            for (var i = 0; i < current.ParametersLayout.Slots.Count; i++) {
                var oldSlot = old.ParametersLayout.Slots[i];
                var currentSlot = current.ParametersLayout.Slots[i];
                if (oldSlot.Name != currentSlot.Name || oldSlot.Type != currentSlot.Type || oldSlot.Offset != currentSlot.Offset) {
                    compatible = false;
                    break;
                }
            }
            if (compatible && System.Text.Json.Nodes.JsonNode.DeepEquals(old.ParametersLayout.JsonSchema(), current.ParametersLayout.JsonSchema())) {
                current.Parameters = new ShaderPipelineParameterValues(old.Parameters.Config, old.Parameters.Bytes.ToArray());
            }
        }
    }
    private bool CompatibleHistory(ShaderPipelineResource old, ShaderPipelineResource current) {
        if (old.Kind != current.Kind || !string.Equals(old.Format, current.Format, StringComparison.OrdinalIgnoreCase) || old.History != current.History || old.Dimensions?.Resolve(m_width, m_height) != current.Dimensions?.Resolve(m_width, m_height) || old.SizeBytes != current.SizeBytes || old.ElementType != current.ElementType || old.StrideBytes != current.StrideBytes || old.Initialization != current.Initialization) {
            return false;
        }
        return true;
    }
    private void Ensure() {
        if (m_ready) {
            return;
        }
        var plan = m_pipeline!.Plan;
        var map = new Dictionary<string, RuntimeResource>(StringComparer.Ordinal);
        ulong allocationBytes = 0;
        foreach (var planned in plan.Resources) {
            allocationBytes = checked(allocationBytes + new RuntimeResource(planned.Declaration, (int)m_inFlight).EstimateBytes(m_width, m_height));
        }
        var largestPreviewBytes = plan.Resources.Where(static resource => resource.Declaration.Kind == ShaderPipelineResourceKind.Image && (resource.Declaration.IsExternal || IsFloatFormat(ParseFormat(resource.Declaration.Format))))
            .Select(resource => { var extent = resource.Declaration.Dimensions?.Resolve(m_width, m_height) ?? (m_width, m_height); return checked((ulong)extent.Width * extent.Height * 4 * m_inFlight); }).DefaultIfEmpty().Max();
        if (checked(allocationBytes + largestPreviewBytes) > m_allocationBudgetBytes) {
            throw new InvalidDataException($"Shader pipeline allocation exceeds the {m_allocationBudgetBytes} byte budget.");
        }
        allocationBytes = 0;
        try {
            foreach (var planned in plan.Resources) {
                var declaration = planned.Declaration;
                var resource = new RuntimeResource(declaration, (int)m_inFlight);
                map.Add(declaration.Name, resource);
                resource.Layouts = new GpuImageLayout[m_inFlight];
                Array.Fill(resource.Layouts, GpuImageLayout.Undefined);
                var writtenByFullscreen = declaration.Kind == ShaderPipelineResourceKind.Image &&
                    plan.Passes.Any(pass => pass.Declaration.Kind == ShaderPipelinePassKind.Fullscreen &&
                        pass.Declaration.OutputReferences.Any(output => output.Name == declaration.Name));
                if (declaration.Kind == ShaderPipelineResourceKind.Image && !declaration.IsExternal && !writtenByFullscreen) {
                    resource.Images = new IGpuStorageImage[m_inFlight];
                    var extent = declaration.Dimensions?.Resolve(m_width, m_height) ?? (m_width, m_height);
                    for (var i = 0; i < m_inFlight; i++) {
                        resource.Images[i] = m_gpu.StorageImageFactory.Create(m_device, ParseFormat(declaration.Format), extent.Width, extent.Height);
                    }
                } else if (declaration.Kind == ShaderPipelineResourceKind.Buffer && !declaration.IsExternal) {
                    if (!declaration.SizeBytes.HasValue || declaration.SizeBytes.Value == 0) {
                        throw new InvalidDataException($"Buffer '{declaration.Name}' has no positive size.");
                    }
                    var sizeBytes = declaration.SizeBytes.Value;
                    resource.Buffers = new IGpuBuffer[m_inFlight];
                    for (var i = 0; i < m_inFlight; i++) {
                        resource.Buffers[i] = m_gpu.StorageBufferFactory.CreateDeviceLocal(m_device, sizeBytes);
                    }
                }
                allocationBytes = checked(allocationBytes + resource.EstimateBytes(m_width, m_height));
                if (allocationBytes > m_allocationBudgetBytes) {
                    throw new InvalidDataException($"Shader pipeline allocation exceeds the {m_allocationBudgetBytes} byte budget.");
                }
            }
            m_allocationBytes = allocationBytes;
            m_resourceLookup = map;
            m_resources = [.. map.Values];
            m_passLabels = plan.Passes.Select(static pass => pass.Name).ToArray();
            m_passes = new RuntimePass[plan.Passes.Count];
            for (var i = 0; i < plan.Passes.Count; i++) {
                m_passes[i] = BuildPass(plan.Passes[i], map);
            }
            for (var i = 0; i < m_inFlight; i++) {
                if (m_slots[i].Fence is null) {
                    m_slots[i].Fence = m_gpu.QueueSubmitter.CreateSubmissionFence(m_device);
                }
            }
            m_initializationPending = true;
            m_ready = true;
        } catch {
            DisposeGraph(m_passes, m_resources);
            if (m_resources.Length == 0) {
                foreach (var resource in map.Values) {
                    resource.Dispose();
                }
            }
            m_passes = [];
            m_resources = [];
            m_resourceLookup = new Dictionary<string, RuntimeResource>(StringComparer.Ordinal);
            m_allocationBytes = 0;
            throw;
        }
    }
    private void ValidateExternalResources(ShaderPipelinePlan plan) {
        foreach (var planned in plan.Resources) {
            var declaration = planned.Declaration;
            if (declaration.Kind == ShaderPipelineResourceKind.Depth) {
                throw new InvalidDataException($"Depth resource '{declaration.Name}' is not supported by the pipeline executor yet.");
            }
            if (!declaration.IsExternal) {
                continue;
            }
            if (declaration.Kind == ShaderPipelineResourceKind.Image) {
                if (!m_externalImages.TryGetValue(declaration.Name, out var image)) {
                    throw new InvalidDataException($"External image '{declaration.Name}' has not been bound.");
                }
                var extent = declaration.Dimensions?.Resolve(m_width, m_height) ?? (m_width, m_height);
                if (image.Width != extent.Width || image.Height != extent.Height) {
                    throw new InvalidDataException($"External image '{declaration.Name}' is {image.Width}x{image.Height}; expected {extent.Width}x{extent.Height}.");
                }
                if (image.Format != ParseFormat(declaration.Format)) {
                    throw new InvalidDataException($"External image '{declaration.Name}' has format {image.Format}; expected {declaration.Format}.");
                }
            } else if (!m_externalBuffers.TryGetValue(declaration.Name, out var buffer)) {
                throw new InvalidDataException($"External buffer '{declaration.Name}' has not been bound.");
            } else if (buffer.SizeBytes < declaration.SizeBytes.GetValueOrDefault()) {
                throw new InvalidDataException($"External buffer '{declaration.Name}' is {buffer.SizeBytes} bytes; expected at least {declaration.SizeBytes}.");
            }
        }
    }
    private RuntimePass BuildPass(ShaderPipelinePlannedPass planned, IReadOnlyDictionary<string, RuntimeResource> map) {
        var declaration = planned.Declaration;
        var compiled = m_pipeline!.Shaders[planned.Name];
        var constants = m_passConstants.TryGetValue(planned.Name, out var writer) ? writer : null;
        if (constants is not null && ((constants.SizeBytes != 0 && (constants.SizeBytes & 3) != 0) || (constants.SizeBytes == 0 && constants.Stages != GpuShaderStage.None) || (constants.SizeBytes != 0 && constants.Stages == GpuShaderStage.None))) {
            throw new InvalidDataException($"Pass {planned.Name} has an invalid custom constants block.");
        }
        var runtime = new RuntimePass(declaration, compiled, planned.Parameters, constants, (int)m_inFlight, ResolveExtent(declaration, map));
        m_passes[planned.Index] = runtime;
        var descriptors = Descriptors(declaration, map);
        runtime.Bindings = descriptors;
        var primary = m_directX ? compiled.DxilByStage : compiled.SpirvByStage;
        if (declaration.Kind == ShaderPipelinePassKind.Compute) {
            if (!primary.TryGetValue(ShaderStage.Compute, out var bytes) || bytes.IsEmpty) {
                throw new InvalidDataException($"Pass '{declaration.Name}' has no compute bytecode.");
            }
            runtime.Primary = m_gpu.ShaderModuleFactory.Create(m_device, GpuShaderStage.Compute, bytes);
            runtime.Compute = m_gpu.ComputePipelineFactory.Create(
                m_device,
                runtime.Primary,
                new GpuComputePipelineDescription(
                    declaration.Name,
                    descriptors,
                    runtime.PushBinding));
            runtime.Pools = new IGpuComputeCommandPool[m_inFlight];
        } else {
            if (m_graphics is null ||
                !primary.TryGetValue(ShaderStage.Vertex, out var vertex) ||
                !primary.TryGetValue(ShaderStage.Fragment, out var fragment)) {
                throw new InvalidDataException($"Fullscreen pass '{declaration.Name}' needs vertex and fragment bytecode plus graphics services.");
            }
            runtime.Primary = m_gpu.ShaderModuleFactory.Create(m_device, GpuShaderStage.Vertex, vertex);
            runtime.Secondary = m_gpu.ShaderModuleFactory.Create(m_device, GpuShaderStage.Fragment, fragment);
            var vertexInput = new GpuVertexInputLayout(0, []);
            if (m_positionVertexPasses.Contains(declaration.Name)) {
                runtime.VertexBuffer = m_graphics.VertexBufferFactory.Create(m_device, FullscreenTriangle.CreateVertexData(), FullscreenTriangle.StrideBytes);
                vertexInput = new GpuVertexInputLayout(FullscreenTriangle.StrideBytes, [new GpuVertexAttribute(0, GpuVertexFormat.R32G32Float, 0)]);
            }
            runtime.Targets = new IGpuRenderTarget[m_inFlight];
            runtime.Graphics = new IGpuPipeline[m_inFlight];
            runtime.Pre = new IGpuComputeCommandPool[m_inFlight];
            runtime.Post = new IGpuComputeCommandPool[m_inFlight];
            var sampled = (uint)descriptors.Count(item => item.Kind == GpuComputeBindingKind.SampledImage);
            var extent = ResolveExtent(declaration, map);
            for (var i = 0; i < m_inFlight; i++) {
                runtime.Targets[i] = m_graphics.CreateRenderTarget(extent.Width, extent.Height);
                runtime.Graphics[i] = m_graphics.PipelineFactory.Create(
                    m_device,
                    runtime.Targets[i],
                    runtime.Primary,
                    runtime.Secondary,
                    new GpuGraphicsPipelineDescription(
                        declaration.Name,
                        vertexInput,
                        sampled,
                        false,
                        runtime.PushBinding),
                    extent.Width,
                    extent.Height);
            }
            foreach (var output in declaration.OutputReferences) {
                if (map.TryGetValue(output.Name, out var resource)) {
                    resource.Images = null;
                    resource.Targets = runtime.Targets;
                    resource.Layouts = new GpuImageLayout[m_inFlight];
                    Array.Fill(resource.Layouts, GpuImageLayout.Undefined);
                }
            }
        }
        runtime.Sets = new nint[m_inFlight];
        runtime.PoolsDescriptors = new nint[m_inFlight];
        runtime.Samplers = new nint[m_inFlight];
        return runtime;
    }
    private static List<GpuComputeBinding> Descriptors(ShaderPipelinePass pass, IReadOnlyDictionary<string, RuntimeResource> map) {
        var result = new List<GpuComputeBinding>(pass.InputReferences.Count + pass.OutputReferences.Count);
        foreach (var input in pass.InputReferences) {
            result.Add(new GpuComputeBinding(input.Binding!.Value,
                map[input.Name].Spec.Kind == ShaderPipelineResourceKind.Image ? GpuComputeBindingKind.SampledImage : GpuComputeBindingKind.StorageBufferRead));
        }
        if (pass.Kind == ShaderPipelinePassKind.Compute) {
            foreach (var output in pass.OutputReferences) {
                result.Add(new GpuComputeBinding(output.Binding!.Value,
                    map[output.Name].Spec.Kind == ShaderPipelineResourceKind.Image ? GpuComputeBindingKind.StorageImage : GpuComputeBindingKind.StorageBufferReadWrite));
            }
        }
        GpuComputeBinding.ValidateSet(result);
        return result;
    }
    private void Record(RuntimePass pass, int slot, in FrameContext context, List<nint> commands) {
        var descriptor = GetDescriptor(pass, slot);
        if (pass.Spec.Kind == ShaderPipelinePassKind.Compute) {
            var pool = pass.Pools![slot];
            if (pool is null) {
                pool = m_gpu.CommandPoolFactory.Create(m_device);
                pass.Pools[slot] = pool;
            }
            var handle = pool.CommandBufferHandle;
            var recorder = m_gpu.ComputeRecorder;
            recorder.BeginCommandBuffer(m_device.DeviceHandle, handle);
            InitializeResources(handle, recorder);
            foreach (var binding in pass.Spec.InputReferences) {
                Transition(pass, binding, slot, handle, recorder);
            }
            foreach (var binding in pass.Spec.OutputReferences) {
                Transition(pass, binding, slot, handle, recorder);
            }
            recorder.BindComputePipeline(m_device.DeviceHandle, handle, pass.Compute!.Handle);
            recorder.BindComputeDescriptorSet(m_device.DeviceHandle, handle, pass.Compute.LayoutHandle, descriptor);
            var extent = (pass.Width, pass.Height);
            PushFrameConstants(pass, context, handle, pass.Compute.LayoutHandle, extent.Width, extent.Height, recorder, null);
            recorder.Dispatch(m_device.DeviceHandle, handle, (extent.Width + pass.Spec.GroupSizeX - 1) / pass.Spec.GroupSizeX, (extent.Height + pass.Spec.GroupSizeY - 1) / pass.Spec.GroupSizeY, (1u + pass.Spec.GroupSizeZ - 1) / pass.Spec.GroupSizeZ);
            recorder.EndCommandBuffer(m_device.DeviceHandle, handle);
            commands.Add(handle);
            return;
        }

        var pre = pass.Pre![slot];
        if (pre is null) {
            pre = m_gpu.CommandPoolFactory.Create(m_device);
            pass.Pre[slot] = pre;
        }
        BarrierTarget(pass, slot, pre.CommandBufferHandle, before: true, commands);
        var target = pass.Targets![slot];
        var command = target.CommandBufferHandle;
        var recorderGraphics = m_graphics!.CommandRecorder;
        recorderGraphics.BeginCommandBuffer(m_device.DeviceHandle, command);
        recorderGraphics.BeginRenderPass(m_device.DeviceHandle, command, target.RenderPassHandle, target.FramebufferHandle, target.Width, target.Height);
        recorderGraphics.SetScissor(m_device.DeviceHandle, command, 0, 0, target.Width, target.Height);
        recorderGraphics.BindGraphicsPipeline(m_device.DeviceHandle, command, pass.Graphics![slot].Handle);
        if (pass.VertexBuffer is not null) { recorderGraphics.BindVertexBuffer(m_device.DeviceHandle, command, pass.VertexBuffer.BufferHandle); }
        PushFrameConstants(pass, context, command, pass.Graphics[slot].LayoutHandle, target.Width, target.Height, null, recorderGraphics);
        recorderGraphics.BindDescriptorSet(m_device.DeviceHandle, command, pass.Graphics[slot].LayoutHandle, descriptor);
        recorderGraphics.Draw(m_device.DeviceHandle, command, new GpuDrawParameters(3, 1));
        recorderGraphics.EndRenderPass(m_device.DeviceHandle, command);
        recorderGraphics.EndCommandBuffer(m_device.DeviceHandle, command);
        commands.Add(command);
        var post = pass.Post![slot];
        if (post is null) {
            post = m_gpu.CommandPoolFactory.Create(m_device);
            pass.Post[slot] = post;
        }
        BarrierTarget(pass, slot, post.CommandBufferHandle, before: false, commands);
    }

    private (uint Width, uint Height) ResolveExtent(ShaderPipelinePass pass, IReadOnlyDictionary<string, RuntimeResource> map) {
        foreach (var output in pass.OutputReferences) {
            if (map.TryGetValue(output.Name, out var resource) && resource.Spec.Dimensions is { } dimensions) {
                return dimensions.Resolve(m_width, m_height);
            }
        }
        foreach (var input in pass.InputReferences) {
            if (map.TryGetValue(input.Name, out var resource) && resource.Spec.Dimensions is { } dimensions) {
                return dimensions.Resolve(m_width, m_height);
            }
        }
        return (m_width, m_height);
    }

    private nint GetDescriptor(RuntimePass pass, int slot) {
        var device = m_device.DeviceHandle;
        if (pass.Sets![slot] == 0) {
            pass.PoolsDescriptors![slot] = m_gpu.DescriptorAllocator.CreatePool(device, GpuDescriptorPoolSizes.ForSets(pass.Bindings));
            var layout = pass.Spec.Kind == ShaderPipelinePassKind.Compute ? pass.Compute!.DescriptorSetLayoutHandle : pass.Graphics![slot].DescriptorSetLayoutHandle;
            pass.Sets[slot] = m_gpu.DescriptorAllocator.AllocateSet(device, pass.PoolsDescriptors[slot], layout);
            pass.Samplers![slot] = m_gpu.DescriptorAllocator.CreateSampler(device);
        }
        var descriptorIndex = 0;
        foreach (var input in pass.Spec.InputReferences) {
            var resource = m_resourceLookup[input.Name];
            var index = HistoryIndex(resource, slot, input.PreviousFrame);
            var binding = pass.Bindings[descriptorIndex++];
            if (resource.Spec.Kind == ShaderPipelineResourceKind.Image) {
                var image = ResolveImage(resource, input.Name, index);
                m_gpu.DescriptorAllocator.WriteCombinedImageSampler(device, pass.Sets![slot], binding.Binding, 0, image.ImageViewHandle, pass.Samplers![slot]);
            } else {
                var buffer = ResolveBuffer(resource, input.Name, index);
                m_gpu.DescriptorAllocator.WriteStorageBufferReadOnly(device, pass.Sets![slot], binding.Binding, buffer.BufferHandle, resource.Spec.SizeBytes ?? 0);
            }
        }
        if (pass.Spec.Kind == ShaderPipelinePassKind.Compute) {
            foreach (var output in pass.Spec.OutputReferences) {
                var resource = m_resourceLookup[output.Name];
                var binding = pass.Bindings[descriptorIndex++];
                if (resource.Spec.Kind == ShaderPipelineResourceKind.Image) {
                    var image = ResolveImage(resource, output.Name, slot);
                    m_gpu.DescriptorAllocator.WriteStorageImage(device, pass.Sets![slot], binding.Binding, 0, image.ImageViewHandle);
                } else {
                    var buffer = ResolveBuffer(resource, output.Name, slot);
                    m_gpu.DescriptorAllocator.WriteStorageBufferReadWrite(device, pass.Sets![slot], binding.Binding, buffer.BufferHandle, resource.Spec.SizeBytes ?? 0);
                }
            }
        }
        return pass.Sets[slot];
    }
    private void PushFrameConstants(RuntimePass pass, in FrameContext context, nint command, nint layout, uint width, uint height, IGpuComputeRecorder? compute, IGpuCommandRecorder? graphics) {
        if (pass.ConstantsSizeBytes == 0) {
            return;
        }
        Span<byte> bytes = stackalloc byte[(int)pass.ConstantsSizeBytes];
        if (pass.CustomConstants is not null) {
            pass.CustomConstants.Write(context, Input, width, height, m_frame, bytes);
        } else {
            var constants = new ShaderFrameConstants(
                new System.Numerics.Vector3(width, height, 1f),
                (float)Input.Seconds,
                (float)Input.DeltaSeconds,
                unchecked((int)m_frame),
                default,
                Input.Mouse,
                Input.Date,
                Input.CameraPos,
                Input.CameraFov,
                Input.CameraTarget,
                0f,
                Input.CameraUp,
                0f);
            constants.CopyTo(bytes);
            pass.Parameters.Bytes.Span[ShaderFrameConstants.SizeBytes..].CopyTo(bytes[ShaderFrameConstants.SizeBytes..]);
        }
        if (compute is not null) {
            compute.PushConstants(m_device.DeviceHandle, command, layout, pass.ConstantsStages, 0, bytes);
        } else {
            graphics!.PushConstants(m_device.DeviceHandle, command, layout, pass.ConstantsStages, 0, bytes);
        }
    }
    private void InitializeResources(nint command, IGpuComputeRecorder recorder) {
        if (!m_initializationPending) { return; }
        InitializeZeroImages(command, recorder);
        InitializeZeroBuffers(command, recorder);
        m_initializationPending = false;
    }
    private void InitializeZeroImages(nint command, IGpuComputeRecorder recorder) {
        foreach (var resource in m_resources) {
            if (resource.Spec.Initialization != ShaderPipelineInitialization.Zero || resource.Spec.IsExternal || (resource.Images is null && resource.Targets is null)) {
                continue;
            }
            if (resource.Initialized is null) {
                resource.Initialized = new bool[resource.Count];
            }
            var clearRecorder = recorder as IGpuImageInitializationRecorder;
            if (clearRecorder is null) {
                throw new InvalidOperationException("The selected GPU backend cannot clear shader pipeline history images.");
            }
            for (var index = 0; index < resource.Count; index++) {
                if (resource.Initialized[index]) {
                    continue;
                }
                var image = ResolveImage(resource, resource.Spec.Name, index);
                recorder.TransitionImageLayout(m_device.DeviceHandle, command, image.ImageHandle, GpuImageLayout.Undefined, GpuImageLayout.General, GpuComputeAccess.None, GpuComputeAccess.TransferWrite, GpuComputeStage.TopOfPipe, GpuComputeStage.Transfer);
                clearRecorder.ClearStorageImage(m_device.DeviceHandle, command, image.ImageHandle, ParseFormat(resource.Spec.Format));
                resource.Layouts![index] = GpuImageLayout.General;
                resource.Initialized[index] = true;
            }
        }
    }
    private void InitializeZeroBuffers(nint command, IGpuComputeRecorder recorder) {
        foreach (var resource in m_resources) {
            if (resource.Spec.Initialization != ShaderPipelineInitialization.Zero || resource.Buffers is null) {
                continue;
            }
            resource.Initialized ??= new bool[resource.Buffers.Length];
            if (recorder is not IGpuBufferInitializationRecorder clearRecorder) {
                throw new InvalidOperationException("The selected GPU backend cannot clear shader pipeline storage buffers.");
            }
            for (var index = 0; index < resource.Buffers.Length; index++) {
                if (resource.Initialized[index]) {
                    continue;
                }
                var buffer = resource.Buffers[index];
                recorder.TransitionBuffer(m_device.DeviceHandle, command, buffer.BufferHandle, GpuComputeAccess.None, GpuComputeAccess.TransferWrite, GpuComputeStage.TopOfPipe, GpuComputeStage.Transfer);
                clearRecorder.ClearStorageBuffer(m_device.DeviceHandle, command, buffer.BufferHandle, buffer.SizeBytes);
                resource.Initialized[index] = true;
            }
        }
    }
    private void Transition(RuntimePass pass, ResourceReference reference, int slot, nint command, IGpuComputeRecorder recorder) {
        var resource = m_resourceLookup[reference.Name];
        if (resource.Spec.Kind == ShaderPipelineResourceKind.Buffer) {
            var buffer = ResolveBuffer(resource, reference.Name, HistoryIndex(resource, slot, reference.PreviousFrame));
            var write = !reference.PreviousFrame && pass.Spec.OutputReferences.Any(output => output.Name == reference.Name);
            recorder.TransitionBuffer(m_device.DeviceHandle, command, buffer.BufferHandle, GpuComputeAccess.ShaderRead | GpuComputeAccess.ShaderWrite | GpuComputeAccess.TransferWrite, write ? GpuComputeAccess.ShaderWrite : GpuComputeAccess.ShaderRead, GpuComputeStage.ComputeShader | GpuComputeStage.Transfer, GpuComputeStage.ComputeShader);
            return;
        }
        var index = HistoryIndex(resource, slot, reference.PreviousFrame);
        var image = ResolveImage(resource, reference.Name, index);
        var desired = !reference.PreviousFrame && pass.Spec.OutputReferences.Any(output => output.Name == reference.Name)
            ? GpuImageLayout.General
            : GpuImageLayout.ShaderReadOnly;
        var old = resource.Layouts![index];
        if (old != desired) {
            recorder.TransitionImageLayout(m_device.DeviceHandle, command, image.ImageHandle, old, desired, old == GpuImageLayout.Undefined ? GpuComputeAccess.None : PriorAccess, desired == GpuImageLayout.General ? GpuComputeAccess.ShaderWrite : GpuComputeAccess.ShaderRead, old == GpuImageLayout.Undefined ? GpuComputeStage.TopOfPipe : PriorStages, GpuComputeStage.ComputeShader | GpuComputeStage.FragmentShader);
            resource.Layouts![index] = desired;
        } else if (desired == GpuImageLayout.General) {
            recorder.MemoryBarrier(m_device.DeviceHandle, command, PriorAccess, GpuComputeAccess.ShaderRead | GpuComputeAccess.ShaderWrite, PriorStages, GpuComputeStage.ComputeShader | GpuComputeStage.FragmentShader);
        }
    }

    private ShaderPipelineExternalImage ResolveImage(RuntimeResource resource, string name, int index) {
        if (resource.Spec.IsExternal && m_externalImages.TryGetValue(name, out var external)) {
            return external;
        }
        if (resource.Images is not null) {
            var image = resource.Images[index];
            return new ShaderPipelineExternalImage(image.ImageHandle, image.ImageViewHandle, image.Width, image.Height, ParseFormat(resource.Spec.Format), resource.Layouts![index]);
        }
        if (resource.Targets is not null) {
            var target = resource.Targets[index];
            return new ShaderPipelineExternalImage(target.ImageHandle, target.ImageViewHandle, target.Width, target.Height, ParseFormat(resource.Spec.Format), resource.Layouts![index]);
        }
        throw new InvalidDataException($"External image '{name}' is not bound.");
    }

    private IGpuBuffer ResolveBuffer(RuntimeResource resource, string name, int index) {
        if (resource.Buffers is not null) {
            return resource.Buffers[index];
        }
        if (m_externalBuffers.TryGetValue(name, out var buffer)) {
            return buffer;
        }
        throw new InvalidDataException($"External buffer '{name}' is not bound.");
    }

    private void BarrierTarget(RuntimePass pass, int slot, nint command, bool before, List<nint> commands) {
        var recorder = m_gpu.ComputeRecorder;
        recorder.BeginCommandBuffer(m_device.DeviceHandle, command);
        InitializeResources(command, recorder);
        foreach (var input in pass.Spec.InputReferences) {
            Transition(pass, input, slot, command, recorder);
        }
        foreach (var output in pass.Spec.OutputReferences) {
            var resource = m_resourceLookup[output.Name];
            var target = resource.Targets![slot];
            // The shared render-pass contract leaves its color attachment in ShaderReadOnly when the
            // render pass ends. Track that actual state before publishing the node's General surface.
            var old = before ? resource.Layouts![slot] : GpuImageLayout.ShaderReadOnly;
            var desired = before ? GpuImageLayout.RenderTarget : m_outputLayout;
            if (old != desired) {
                recorder.TransitionImageLayout(m_device.DeviceHandle, command, target.ImageHandle, old, desired, old == GpuImageLayout.Undefined ? GpuComputeAccess.None : PriorAccess, before ? GpuComputeAccess.ColorAttachmentWrite : GpuComputeAccess.ShaderRead, old == GpuImageLayout.Undefined ? GpuComputeStage.TopOfPipe : PriorStages, before ? GpuComputeStage.ColorAttachmentOutput : GpuComputeStage.ComputeShader | GpuComputeStage.FragmentShader);
            } else if (!before) {
                recorder.MemoryBarrier(m_device.DeviceHandle, command, GpuComputeAccess.ColorAttachmentWrite, GpuComputeAccess.ShaderRead, GpuComputeStage.ColorAttachmentOutput, GpuComputeStage.ComputeShader | GpuComputeStage.FragmentShader);
            }
            resource.Layouts![slot] = desired;
        }
        recorder.EndCommandBuffer(m_device.DeviceHandle, command);
        commands.Add(command);
    }
    private void FinalizeOutputs(int slot, List<nint> commands) {
        var pool = m_slots[slot].Final ??= m_gpu.CommandPoolFactory.Create(m_device);
        var command = pool.CommandBufferHandle;
        var recorder = m_gpu.ComputeRecorder;
        recorder.BeginCommandBuffer(m_device.DeviceHandle, command);
        var selected = m_resourceLookup[m_selectedOutput ?? m_pipeline!.Plan.OutputResourceName];
        foreach (var resource in m_resources) {
            if (resource.Spec.Kind != ShaderPipelineResourceKind.Image) { continue; }
            var external = resource.Spec.IsExternal;
            if (!external && (resource != selected || NeedsPreview(resource))) { continue; }
            var image = ResolveImage(resource, resource.Spec.Name, slot);
            var desired = external ? m_externalImages[resource.Spec.Name].Layout : m_outputLayout;
            var old = resource.Layouts![slot];
            recorder.TransitionImageLayout(m_device.DeviceHandle, command, image.ImageHandle, old, desired,
                old == GpuImageLayout.Undefined ? GpuComputeAccess.None : PriorAccess, GpuComputeAccess.ShaderRead,
                old == GpuImageLayout.Undefined ? GpuComputeStage.TopOfPipe : PriorStages, GpuComputeStage.ComputeShader | GpuComputeStage.FragmentShader);
            resource.Layouts[slot] = desired;
        }
        recorder.EndCommandBuffer(m_device.DeviceHandle, command);
        commands.Add(command);
    }
    private void PresentSelectedOutput() {
        WaitAll();
        var slot = (int)((m_frame - 1) % m_inFlight);
        var selected = m_resourceLookup[m_selectedOutput ?? m_pipeline!.Plan.OutputResourceName];
        EnsurePreview(selected);
        var commands = m_commands;
        commands.Clear();
        if (NeedsPreview(selected)) {
            m_preview!.Record(selected, ResolveImage(selected, selected.Spec.Name, slot), slot, commands);
        }
        FinalizeOutputs(slot, commands);
        m_gpu.QueueSubmitter.Submit(m_device, CollectionsMarshal.AsSpan(commands), m_slots[slot].Fence!);
        m_lastSurface = Output(slot);
        m_outputRefreshRequested = false;
    }    private void EnsurePreview(RuntimeResource selected) {
        if (!NeedsPreview(selected)) {
            if (m_preview is not null) { WaitAll(); m_device.WaitIdle(); m_preview.Dispose(); }
            m_preview = null;
            return;
        }
        var extent = selected.Spec.Dimensions?.Resolve(m_width, m_height) ?? (m_width, m_height);
        if (m_preview is null || m_preview.Width != extent.Width || m_preview.Height != extent.Height) {
            if (m_preview is not null) { WaitAll(); m_device.WaitIdle(); m_preview.Dispose(); }
            m_preview = new FloatPreviewPass(m_gpu, m_graphics ?? throw new InvalidOperationException("Float preview requires graphics services."), m_device, m_directX, extent.Width, extent.Height, m_inFlight, m_outputLayout);
        }
    }

    private static bool NeedsPreview(RuntimeResource resource) => resource.Spec.Kind == ShaderPipelineResourceKind.Image && (resource.Spec.IsExternal || IsFloatFormat(ParseFormat(resource.Spec.Format)));
    private static bool IsFloatFormat(GpuPixelFormat format) => format is GpuPixelFormat.R16G16B16A16Float or GpuPixelFormat.R32G32B32A32Float;
    private Surface Output(int slot) {
        var selectedName = m_selectedOutput ?? m_pipeline!.Plan.OutputResourceName;
        var selectedResource = m_resources.First(item => item.Spec.Name == selectedName);
        if (NeedsPreview(selectedResource)) {
            var target = m_preview?.GetTarget(slot) ?? throw new InvalidOperationException("The float preview target is not ready.");
            return Surface.SameDeviceImage(target.ImageHandle, target.ImageViewHandle, target.Width, target.Height, SurfaceFormat.R8G8B8A8Unorm);
        }
        var resource = PresentationResource(selectedResource);
        var resolved = ResolveImage(resource, resource.Spec.Name, slot);
        var imageHandle = resolved.ImageHandle;
        var imageView = resolved.ImageViewHandle;
        var width = resolved.Width;
        var height = resolved.Height;
        var format = ParseFormat(resource.Spec.Format);
        if (format == GpuPixelFormat.R8G8B8A8Unorm) {
            return Surface.SameDeviceImage(imageHandle, imageView, width, height, SurfaceFormat.R8G8B8A8Unorm);
        }
        if (format == GpuPixelFormat.B8G8R8A8Unorm) {
            return Surface.SameDeviceImage(imageHandle, imageView, width, height, SurfaceFormat.B8G8R8A8Unorm);
        }
        throw new InvalidDataException("The selected output must use an RGBA8 format.");
    }

    private RuntimeResource PresentationResource(RuntimeResource selected) {
        var selectedFormat = ParseFormat(selected.Spec.Format);
        if (selectedFormat == GpuPixelFormat.R8G8B8A8Unorm || selectedFormat == GpuPixelFormat.B8G8R8A8Unorm) {
            return selected;
        }
        throw new InvalidDataException($"Selected output '{selected.Spec.Name}' must be RGBA8 or a float image with preview conversion.");
    }

    private void CaptureIfPending(int slot) {
        m_capture.Serve("[capture] failed", path => {
            m_capturePng.ThrowIfUnavailable(path);
            if (m_lastSurface.IsEmpty || !m_lastSurface.IsSameDeviceImage) {
                throw new InvalidOperationException("A completed same-device output is required for capture.");
            }
            var format = m_lastSurface.Format switch {
                SurfaceFormat.R8G8B8A8Unorm => GpuPixelFormat.R8G8B8A8Unorm,
                SurfaceFormat.B8G8R8A8Unorm => GpuPixelFormat.B8G8R8A8Unorm,
                _ => throw new NotSupportedException($"Capture does not support surface format {m_lastSurface.Format}.")
            };
            m_readback ??= m_gpu.SurfaceTransferFactory.CreateReadback(m_device);
            var selectedName = m_selectedOutput ?? m_pipeline!.Plan.OutputResourceName;
            var selectedResource = m_resourceLookup[selectedName];
            var sourceLayout = m_outputLayout;
            var pixels = m_readback.Read(m_device, m_lastSurface.ImageHandle, format, m_lastSurface.Width, m_lastSurface.Height, 4, sourceLayout);
            if (!m_capturePng.TryWrite((int)m_lastSurface.Height, path, pixels, (int)m_lastSurface.Width)) {
                throw new NotSupportedException("PNG capture is unavailable.");
            }
        });
    }

    private void WaitAll() {
        foreach (var slot in m_slots) {
            slot.Fence?.Wait();
        }
    }

    private void Release(bool wait) {
        if (wait) {
            WaitAll();
            m_device.WaitIdle();
        }
        DisposeGraph(m_passes, m_resources);
        m_preview?.Dispose();
        m_preview = null;
        m_readback?.Dispose();
        m_readback = null;
        foreach (var slot in m_slots) {
            slot.Final?.Dispose();
            slot.Final = null;
            slot.Fence?.Dispose();
            slot.Fence = null;
        }
        m_passes = [];
        m_resources = [];
        m_resourceLookup = new Dictionary<string, RuntimeResource>(StringComparer.Ordinal);
        m_allocationBytes = 0;
        m_passLabels = [];
        m_ready = false;
    }

    private void DisposeGraph(RuntimePass[] passes, RuntimeResource[] resources) {
        foreach (var pass in passes) {
            if (pass is not null) {
                pass.Dispose(m_gpu, m_device);
            }
        }
        foreach (var resource in resources) {
            if (resource is not null) {
                resource.Dispose();
            }
        }
    }

    private static int HistoryIndex(RuntimeResource resource, int slot, bool previous) {
        if (!previous) {
            return slot;
        }
        if (!resource.Spec.History) {
            throw new InvalidDataException($"Resource '{resource.Spec.Name}' is not declared as history.");
        }
        return (slot + resource.Count - 1) % resource.Count;
    }

    private static GpuPixelFormat ParseFormat(string? format) {
        if (Enum.TryParse<GpuPixelFormat>(format, true, out var parsed)) {
            return parsed;
        }
        throw new InvalidDataException($"Unknown shader pipeline format '{format}'.");
    }

    private static void ValidatePlan(ShaderPipelinePlan plan, IFullscreenPassServices? graphics) {
        if (plan.Passes.Count == 0 || plan.Resources.Count == 0) {
            throw new InvalidDataException("A shader pipeline needs resources and passes.");
        }
        if (plan.Passes.Any(pass => pass.Declaration.Kind == ShaderPipelinePassKind.Fullscreen) && graphics is null) {
            throw new InvalidDataException("Fullscreen pipeline passes require graphics services.");
        }
        var resources = plan.Resources.ToDictionary(item => item.Name, StringComparer.Ordinal);
        foreach (var pass in plan.Passes.Where(pass => pass.Declaration.Kind == ShaderPipelinePassKind.Fullscreen)) {
            if (pass.Declaration.InputReferences.Any(input => resources[input.Name].Declaration.Kind == ShaderPipelineResourceKind.Buffer)) {
                throw new InvalidDataException($"Fullscreen pass '{pass.Name}' cannot consume a storage buffer through the current graphics binding contract.");
            }
        }
    }
    private sealed class FloatPreviewPass : IDisposable {
        private const GpuComputeAccess PriorAccess = GpuComputeAccess.ShaderRead | GpuComputeAccess.ShaderWrite | GpuComputeAccess.TransferWrite | GpuComputeAccess.ColorAttachmentWrite;
    private const GpuComputeStage PriorStages = GpuComputeStage.ComputeShader | GpuComputeStage.FragmentShader | GpuComputeStage.Transfer | GpuComputeStage.ColorAttachmentOutput;
    private readonly IGpuComputeServices m_gpu;
        private readonly IFullscreenPassServices m_graphics;
        private readonly IGpuDeviceContext m_device;
        private readonly IGpuShaderModule m_vertex;
        private readonly IGpuShaderModule m_fragment;
        private readonly IGpuRenderTarget[] m_targets;
        private readonly IGpuPipeline[] m_pipelines;
        private readonly IGpuComputeCommandPool[] m_pre;
        private readonly IGpuComputeCommandPool[] m_post;
        private readonly nint[] m_descriptorPools;
        private readonly nint[] m_descriptorSets;
        private readonly nint[] m_samplers;
        private readonly bool[] m_targetInitialized;
        private readonly GpuImageLayout m_outputLayout;

        public FloatPreviewPass(IGpuComputeServices gpu, IFullscreenPassServices graphics, IGpuDeviceContext device, bool directX, uint width, uint height, uint inFlight, GpuImageLayout outputLayout) {
            m_gpu = gpu;
            m_graphics = graphics;
            m_device = device;
            m_outputLayout = outputLayout;
            Width = width;
            Height = height;
            var extension = directX ? ".dxil" : ".spv";
            var root = Path.Combine(AppContext.BaseDirectory, "Assets", "Runtime", "pipeline-preview");

            m_targets = new IGpuRenderTarget[inFlight];
            m_pipelines = new IGpuPipeline[inFlight];
            m_pre = new IGpuComputeCommandPool[inFlight];
            m_post = new IGpuComputeCommandPool[inFlight];
            m_descriptorPools = new nint[inFlight];
            m_descriptorSets = new nint[inFlight];
            m_samplers = new nint[inFlight];
            m_targetInitialized = new bool[inFlight];
            try {
            m_vertex = gpu.ShaderModuleFactory.Create(device, GpuShaderStage.Vertex, File.ReadAllBytes(root + ".vert" + extension));
            m_fragment = gpu.ShaderModuleFactory.Create(device, GpuShaderStage.Fragment, File.ReadAllBytes(root + ".frag" + extension));
            var description = new GpuGraphicsPipelineDescription("pipeline-float-preview", new GpuVertexInputLayout(0, []), 1, false, null);
            for (var i = 0; i < inFlight; i++) {
                m_targets[i] = graphics.CreateRenderTarget(width, height);
                m_pipelines[i] = graphics.PipelineFactory.Create(device, m_targets[i], m_vertex, m_fragment, description, width, height);
            }
            } catch { Dispose(); throw; }
        }
        public uint Width { get; }
        public uint Height { get; }
        public IGpuRenderTarget GetTarget(int slot) => m_targets[slot];
        public void Record(RuntimeResource source, ShaderPipelineExternalImage image, int slot, List<nint> commands) {
            EnsureDescriptors(slot);
            var sourceImageHandle = image.ImageHandle;
            var sourceImageView = image.ImageViewHandle;
            m_gpu.DescriptorAllocator.WriteCombinedImageSampler(m_device.DeviceHandle, m_descriptorSets[slot], 0, 0, sourceImageView, m_samplers[slot]);
            var recorder = m_gpu.ComputeRecorder;
            var pre = m_pre[slot] ??= m_gpu.CommandPoolFactory.Create(m_device);
            recorder.BeginCommandBuffer(m_device.DeviceHandle, pre.CommandBufferHandle);
            var old = source.Layouts![slot];
            if (old != GpuImageLayout.ShaderReadOnly) {
                recorder.TransitionImageLayout(m_device.DeviceHandle, pre.CommandBufferHandle, sourceImageHandle, old, GpuImageLayout.ShaderReadOnly, old == GpuImageLayout.Undefined ? GpuComputeAccess.None : PriorAccess, GpuComputeAccess.ShaderRead, old == GpuImageLayout.Undefined ? GpuComputeStage.TopOfPipe : PriorStages, GpuComputeStage.FragmentShader);
                source.Layouts![slot] = GpuImageLayout.ShaderReadOnly;
            }
            var target = m_targets[slot];
            var targetOld = m_targetInitialized[slot] ? m_outputLayout : GpuImageLayout.Undefined;
            recorder.TransitionImageLayout(m_device.DeviceHandle, pre.CommandBufferHandle, target.ImageHandle, targetOld, GpuImageLayout.RenderTarget, targetOld == GpuImageLayout.Undefined ? GpuComputeAccess.None : PriorAccess, GpuComputeAccess.ColorAttachmentWrite, targetOld == GpuImageLayout.Undefined ? GpuComputeStage.TopOfPipe : PriorStages, GpuComputeStage.ColorAttachmentOutput);
            recorder.EndCommandBuffer(m_device.DeviceHandle, pre.CommandBufferHandle);
            commands.Add(pre.CommandBufferHandle);
            var draw = target.CommandBufferHandle;
            var graphics = m_graphics.CommandRecorder;
            graphics.BeginCommandBuffer(m_device.DeviceHandle, draw);
            graphics.BeginRenderPass(m_device.DeviceHandle, draw, target.RenderPassHandle, target.FramebufferHandle, Width, Height);
            graphics.SetScissor(m_device.DeviceHandle, draw, 0, 0, Width, Height);
            graphics.BindGraphicsPipeline(m_device.DeviceHandle, draw, m_pipelines[slot].Handle);
            graphics.BindDescriptorSet(m_device.DeviceHandle, draw, m_pipelines[slot].LayoutHandle, m_descriptorSets[slot]);
            graphics.Draw(m_device.DeviceHandle, draw, new GpuDrawParameters(3, 1));
            graphics.EndRenderPass(m_device.DeviceHandle, draw);
            graphics.EndCommandBuffer(m_device.DeviceHandle, draw);
            commands.Add(draw);
            var post = m_post[slot] ??= m_gpu.CommandPoolFactory.Create(m_device);
            recorder.BeginCommandBuffer(m_device.DeviceHandle, post.CommandBufferHandle);
            recorder.TransitionImageLayout(m_device.DeviceHandle, post.CommandBufferHandle, target.ImageHandle, GpuImageLayout.ShaderReadOnly, m_outputLayout, GpuComputeAccess.ColorAttachmentWrite, GpuComputeAccess.ShaderRead, GpuComputeStage.ColorAttachmentOutput, GpuComputeStage.ComputeShader | GpuComputeStage.FragmentShader);
            recorder.EndCommandBuffer(m_device.DeviceHandle, post.CommandBufferHandle);
            commands.Add(post.CommandBufferHandle);
            m_targetInitialized[slot] = true;
        }
        private void EnsureDescriptors(int slot) {
            if (m_descriptorSets[slot] != 0) { return; }
            m_descriptorPools[slot] = m_gpu.DescriptorAllocator.CreatePool(m_device.DeviceHandle, new GpuDescriptorPoolSizes(1, 1, 0, 0, 0));
            m_descriptorSets[slot] = m_gpu.DescriptorAllocator.AllocateSet(m_device.DeviceHandle, m_descriptorPools[slot], m_pipelines[slot].DescriptorSetLayoutHandle);
            m_samplers[slot] = m_gpu.DescriptorAllocator.CreateSampler(m_device.DeviceHandle);
        }
        public void Dispose() {
            foreach (var pipeline in m_pipelines) { pipeline?.Dispose(); }
            m_vertex?.Dispose();
            m_fragment?.Dispose();
            foreach (var target in m_targets) { target?.Dispose(); }
            foreach (var pool in m_pre) { pool?.Dispose(); }
            foreach (var pool in m_post) { pool?.Dispose(); }
            foreach (var sampler in m_samplers) { if (sampler != 0) { m_gpu.DescriptorAllocator.DestroySampler(m_device.DeviceHandle, sampler); } }
            foreach (var pool in m_descriptorPools) { if (pool != 0) { m_gpu.DescriptorAllocator.DestroyPool(m_device.DeviceHandle, pool); } }
        }
    }
    private sealed class FrameSlot {
        public IGpuSubmissionFence? Fence;
        public IGpuComputeCommandPool? Final;
    }

    private sealed class RuntimeResource(ShaderPipelineResource spec, int count) {
        public readonly ShaderPipelineResource Spec = spec;
        public readonly int Count = count;
        public IGpuStorageImage[]? Images;
        public IGpuBuffer[]? Buffers;
        public IGpuRenderTarget[]? Targets;
        public GpuImageLayout[]? Layouts;
        public bool[]? Initialized;

        public ulong EstimateBytes(uint frameWidth, uint frameHeight) {
            if (Spec.IsExternal) { return 0; }
            if (Spec.Kind == ShaderPipelineResourceKind.Buffer) {
                return checked((Spec.SizeBytes ?? 0) * (ulong)Count);
            }
            var extent = Spec.Dimensions?.Resolve(frameWidth, frameHeight) ?? (frameWidth, frameHeight);
            var bytesPerPixel = ParseFormat(Spec.Format) switch {
                GpuPixelFormat.R8G8B8A8Unorm or GpuPixelFormat.B8G8R8A8Unorm => 4UL,
                GpuPixelFormat.R16G16B16A16Float => 8UL,
                GpuPixelFormat.R32G32B32A32Float => 16UL,
                _ => throw new InvalidDataException($"Unsupported format '{Spec.Format}'.")
            };
            return checked((ulong)extent.Width * extent.Height * bytesPerPixel * (ulong)Count);
        }

        public ShaderPipelineResourceStatus Status(uint frameWidth, uint frameHeight) {
            var extent = Spec.Dimensions?.Resolve(frameWidth, frameHeight) ?? (frameWidth, frameHeight);
            return new ShaderPipelineResourceStatus(Spec.Name, Spec.Kind, EstimateBytes(frameWidth, frameHeight), Spec.Kind == ShaderPipelineResourceKind.Buffer ? 0 : extent.Width, Spec.Kind == ShaderPipelineResourceKind.Buffer ? 0 : extent.Height, Spec.IsExternal, Spec.History);
        }

        public void Dispose() {
            if (Images is not null) {
                foreach (var image in Images) {
                    image?.Dispose();
                }
            }
            if (Buffers is not null) {
                foreach (var buffer in Buffers) {
                    buffer?.Dispose();
                }
            }
        }
    }

    private sealed class RuntimePass(ShaderPipelinePass spec, CompiledShader compiled, ShaderPipelineParameterLayout parameterLayout, IShaderPipelinePassConstants? customConstants, int count, (uint Width, uint Height) extent) {
        public readonly ShaderPipelinePass Spec = spec;
        public readonly CompiledShader Compiled = compiled;
        public readonly int Count = count;
        public readonly uint Width = extent.Width;
        public readonly uint Height = extent.Height;
        public readonly ShaderPipelineParameterLayout ParametersLayout = parameterLayout;
        public readonly IShaderPipelinePassConstants? CustomConstants = customConstants;
        public uint ConstantsSizeBytes => CustomConstants?.SizeBytes ?? ParametersLayout.SizeBytes;
        public GpuShaderStage ConstantsStages => CustomConstants?.Stages ?? (GpuShaderStage.Compute | GpuShaderStage.Fragment);
        public GpuPushConstantBinding? PushBinding => ConstantsSizeBytes == 0 ? null : new GpuPushConstantBinding(0, ConstantsStages, new byte[ConstantsSizeBytes]);
        public ShaderPipelineParameterValues Parameters = parameterLayout.TryBind(null, out var values, out _) ? values : throw new InvalidDataException($"Invalid parameters for pass {spec.Name}.");
        public List<GpuComputeBinding> Bindings = [];

        public IGpuShaderModule? Primary;
        public IGpuShaderModule? Secondary;
        public IGpuComputePipeline? Compute;
        public IGpuVertexBuffer? VertexBuffer;
        public IGpuPipeline[]? Graphics;
        public IGpuRenderTarget[]? Targets;
        public IGpuComputeCommandPool[]? Pools;
        public IGpuComputeCommandPool[]? Pre;
        public IGpuComputeCommandPool[]? Post;
        public nint[]? PoolsDescriptors;
        public nint[]? Sets;
        public nint[]? Samplers;

        public void Dispose(IGpuComputeServices gpu, IGpuDeviceContext device) {
            Compute?.Dispose();
            VertexBuffer?.Dispose();
            if (Graphics is not null) {
                foreach (var pipeline in Graphics) {
                    pipeline?.Dispose();
                }
            }
            Primary?.Dispose();
            Secondary?.Dispose();
            if (Targets is not null) {
                foreach (var target in Targets) {
                    target?.Dispose();
                }
            }
            if (Pools is not null) {
                foreach (var pool in Pools) {
                    pool?.Dispose();
                }
            }
            if (Pre is not null) {
                foreach (var pool in Pre) {
                    pool?.Dispose();
                }
            }
            if (Post is not null) {
                foreach (var pool in Post) {
                    pool?.Dispose();
                }
            }
            if (PoolsDescriptors is not null) {
                foreach (var pool in PoolsDescriptors) {
                    if (pool != 0) {
                        gpu.DescriptorAllocator.DestroyPool(device.DeviceHandle, pool);
                    }
                }
            }
            if (Samplers is not null) {
                foreach (var sampler in Samplers) {
                    if (sampler != 0) {
                        gpu.DescriptorAllocator.DestroySampler(device.DeviceHandle, sampler);
                    }
                }
            }
        }
    }
}