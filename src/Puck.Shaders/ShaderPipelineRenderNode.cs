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
public sealed class ShaderPipelineRenderNode : IRenderNode {
    private readonly IGpuComputeServices m_gpu;
    private readonly IFullscreenPassServices? m_graphics;
    private readonly IGpuDeviceContext m_device;
    private readonly bool m_directX;
    private readonly uint m_inFlight;
    private readonly FrameSlot[] m_slots;
    private readonly NodeDescriptor m_descriptor;
    private readonly Dictionary<string, ShaderPipelineExternalImage> m_externalImages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IGpuBuffer> m_externalBuffers = new(StringComparer.Ordinal);

    private CompiledShaderPipeline? m_pipeline;
    private CompiledShaderPipeline? m_pending;
    private RuntimeResource[] m_resources = [];
    private RuntimePass[] m_passes = [];
    private string? m_selectedOutput;
    private Surface m_lastSurface;
    private uint m_width;
    private uint m_height;
    private ulong m_frame;
    private int m_steps;
    private bool m_disposed;
    private bool m_ready;
    private Exception? m_lastSwapError;

    /// <summary>Creates an initially empty node. The first valid <see cref="Swap"/> installs a graph.</summary>
    public ShaderPipelineRenderNode(string name, IGpuComputeServices gpu, IGpuDeviceContext deviceContext, bool hostsOnDirectX, uint width, uint height, IFullscreenPassServices? graphics = null, uint inFlightFrames = 3) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(deviceContext);
        ArgumentOutOfRangeException.ThrowIfZero(width);
        ArgumentOutOfRangeException.ThrowIfZero(height);
        ArgumentOutOfRangeException.ThrowIfZero(inFlightFrames);
        m_descriptor = new NodeDescriptor(name, SurfaceId.New());
        m_gpu = gpu;
        m_device = deviceContext;
        m_directX = hostsOnDirectX;
        m_graphics = graphics;
        m_inFlight = inFlightFrames;
        m_slots = new FrameSlot[inFlightFrames];
        m_width = width;
        m_height = height;
    }

    /// <summary>Creates a node with an already compiled candidate.</summary>
    public ShaderPipelineRenderNode(CompiledShaderPipeline pipeline, IGpuComputeServices gpu, IGpuDeviceContext deviceContext, bool hostsOnDirectX, uint width, uint height, IFullscreenPassServices? graphics = null, uint inFlightFrames = 3)
        : this(pipeline.Plan.Definition.Name, gpu, deviceContext, hostsOnDirectX, width, height, graphics, inFlightFrames) => Swap(pipeline);

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
        m_externalImages[name] = image;
    }
    /// <summary>Binds a host-owned buffer for a named external resource. The node never disposes it.</summary>
    public void BindBuffer(string name, IGpuBuffer buffer) {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(buffer);
        m_externalBuffers[name] = buffer;
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
        if (m_ready && m_frame != 0) {
            WaitAll();
            m_lastSurface = Output((int)((m_frame - 1) % m_inFlight));
        }
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
        if (!pass.ParametersLayout.TryBind(config, out var values, out reason)) {
            return false;
        }
        pass.Parameters = values;
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
        }
        m_frame = 0;
        m_steps = 0;
        m_lastSurface = default;
    }

    /// <inheritdoc/>
    public void OnDeviceLost() => Release(wait: false);

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }
        InstallPending();
        if (m_pipeline is null) {
            return default;
        }
        if (Paused && m_steps == 0 && m_ready) {
            return m_lastSurface;
        }
        if (Paused && m_steps != 0) {
            m_steps--;
        }
        Ensure();
        var slotIndex = (int)(m_frame % m_inFlight);
        var slot = m_slots[slotIndex];
        slot.Fence!.Wait();
        var commands = new List<nint>(m_passes.Length * 3);
        foreach (var pass in m_passes) {
            Record(pass, slotIndex, context, commands);
        }
        if (commands.Count == 0) {
            return m_lastSurface;
        }
        m_gpu.QueueSubmitter.Submit(m_device, CollectionsMarshal.AsSpan(commands), slot.Fence!);
        m_lastSurface = Output(slotIndex);
        m_frame++;
        m_ready = true;
        return m_lastSurface;
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }
        m_disposed = true;
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
        var previousPasses = m_passes;
        var previousReady = m_ready;
        var hadFences = m_slots.Any(static slot => slot.Fence is not null);
        m_pipeline = next;
        m_resources = [];
        m_passes = [];
        m_ready = false;
        try {
            Ensure();
            WaitAll();
            DisposeGraph(previousPasses, previousResources);
            m_pending = null;
            m_selectedOutput = next.Plan.OutputResourceName;
        } catch (Exception error) {
            DisposeGraph(m_passes, m_resources);
            m_pipeline = previousPipeline;
            m_resources = previousResources;
            m_passes = previousPasses;
            m_ready = previousReady;
            m_pending = null;
            m_lastSwapError = error;
            if (!hadFences) {
                foreach (var slot in m_slots) { slot.Fence?.Dispose(); slot.Fence = null; }
            }
            return;
        }
    }
    private void Ensure() {
        if (m_ready) {
            return;
        }
        var plan = m_pipeline!.Plan;
        var map = new Dictionary<string, RuntimeResource>(StringComparer.Ordinal);
        foreach (var planned in plan.Resources) {
            var declaration = planned.Declaration;
            var resource = new RuntimeResource(declaration, (int)m_inFlight);
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
            map.Add(declaration.Name, resource);
        }
        m_resources = [.. map.Values];
        m_passes = new RuntimePass[plan.Passes.Count];
        for (var i = 0; i < plan.Passes.Count; i++) {
            m_passes[i] = BuildPass(plan.Passes[i], map);
        }
        for (var i = 0; i < m_inFlight; i++) {
            if (m_slots[i].Fence is null) {
                m_slots[i].Fence = m_gpu.QueueSubmitter.CreateSubmissionFence(m_device);
            }
        }
        m_ready = true;
    }
    private RuntimePass BuildPass(ShaderPipelinePlannedPass planned, IReadOnlyDictionary<string, RuntimeResource> map) {
        var declaration = planned.Declaration;
        var compiled = m_pipeline!.Shaders[planned.Name];
        var runtime = new RuntimePass(declaration, compiled, planned.Parameters, (int)m_inFlight);
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
                    new GpuPushConstantBinding(0, GpuShaderStage.Compute, new byte[planned.Parameters.SizeBytes])));
            runtime.Pools = new IGpuComputeCommandPool[m_inFlight];
        } else {
            if (m_graphics is null ||
                !primary.TryGetValue(ShaderStage.Vertex, out var vertex) ||
                !primary.TryGetValue(ShaderStage.Fragment, out var fragment)) {
                throw new InvalidDataException($"Fullscreen pass '{declaration.Name}' needs vertex and fragment bytecode plus graphics services.");
            }
            runtime.Primary = m_gpu.ShaderModuleFactory.Create(m_device, GpuShaderStage.Vertex, vertex);
            runtime.Secondary = m_gpu.ShaderModuleFactory.Create(m_device, GpuShaderStage.Fragment, fragment);
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
                        new GpuVertexInputLayout(0, []),
                        sampled,
                        false,
                        new GpuPushConstantBinding(0, GpuShaderStage.Fragment, new byte[planned.Parameters.SizeBytes])),
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
        var used = new HashSet<uint>();
        var next = 0u;
        foreach (var input in pass.InputReferences) {
            var binding = AllocateBinding(input.Binding, used, ref next);
            var resource = map[input.Name];
            var kind = resource.Spec.Kind == ShaderPipelineResourceKind.Image
                ? GpuComputeBindingKind.SampledImage
                : GpuComputeBindingKind.StorageBufferRead;
            result.Add(new GpuComputeBinding(binding, kind));
        }
        if (pass.Kind == ShaderPipelinePassKind.Compute) {
            foreach (var output in pass.OutputReferences) {
                var binding = AllocateBinding(output.Binding, used, ref next);
                var resource = map[output.Name];
                var kind = resource.Spec.Kind == ShaderPipelineResourceKind.Image
                    ? GpuComputeBindingKind.StorageImage
                    : GpuComputeBindingKind.StorageBufferReadWrite;
                result.Add(new GpuComputeBinding(binding, kind));
            }
        }
        GpuComputeBinding.ValidateSet(result);
        return result;
    }

    private static uint AllocateBinding(uint? requested, HashSet<uint> used, ref uint next) {
        if (requested is { } explicitBinding) {
            if (!used.Add(explicitBinding)) {
                throw new InvalidDataException($"Shader pipeline binding {explicitBinding} is declared more than once.");
            }
            next = Math.Max(next, explicitBinding == uint.MaxValue ? uint.MaxValue : explicitBinding + 1);
            return explicitBinding;
        }
        while (used.Contains(next)) {
            if (next == uint.MaxValue) {
                throw new InvalidDataException("Shader pipeline has no free descriptor binding.");
            }
            next++;
        }
        used.Add(next);
        return next++;
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
            InitializeZeroImages(slot, handle, recorder);
            foreach (var binding in pass.Spec.InputReferences.Concat(pass.Spec.OutputReferences)) {
                Transition(pass, binding, slot, handle, recorder);
            }
            recorder.BindComputePipeline(m_device.DeviceHandle, handle, pass.Compute!.Handle);
            recorder.BindComputeDescriptorSet(m_device.DeviceHandle, handle, pass.Compute.LayoutHandle, descriptor);
            var extent = ResolveExtent(pass.Spec, m_resources.ToDictionary(item => item.Spec.Name, StringComparer.Ordinal));
            PushFrameConstants(pass, context, handle, pass.Compute.LayoutHandle, extent.Width, extent.Height, recorder, null);
            recorder.Dispatch(m_device.DeviceHandle, handle, (extent.Width + 7) / 8, (extent.Height + 7) / 8, 1);
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
            var layout = pass.Spec.Kind == ShaderPipelinePassKind.Compute
                ? pass.Compute!.DescriptorSetLayoutHandle
                : pass.Graphics![slot].DescriptorSetLayoutHandle;
            pass.Sets[slot] = m_gpu.DescriptorAllocator.AllocateSet(device, pass.PoolsDescriptors[slot], layout);
            pass.Samplers![slot] = m_gpu.DescriptorAllocator.CreateSampler(device);
        }

        var descriptorIndex = 0;
        foreach (var input in pass.Spec.InputReferences) {
            var resource = Resource(input.Name);
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
        foreach (var output in pass.Spec.OutputReferences) {
            var resource = Resource(output.Name);
            var binding = pass.Bindings[descriptorIndex++];
            if (resource.Spec.Kind == ShaderPipelineResourceKind.Image) {
                var image = ResolveImage(resource, output.Name, slot);
                m_gpu.DescriptorAllocator.WriteStorageImage(device, pass.Sets![slot], binding.Binding, 0, image.ImageViewHandle);
            } else {
                var buffer = ResolveBuffer(resource, output.Name, slot);
                m_gpu.DescriptorAllocator.WriteStorageBufferReadWrite(device, pass.Sets![slot], binding.Binding, buffer.BufferHandle, resource.Spec.SizeBytes ?? 0);
            }
        }
        return pass.Sets![slot];

        RuntimeResource Resource(string name) {
            return m_resources.First(item => item.Spec.Name == name);
        }
    }

    private void PushFrameConstants(RuntimePass pass, in FrameContext context, nint command, nint layout, uint width, uint height, IGpuComputeRecorder? compute, IGpuCommandRecorder? graphics) {
        Span<byte> bytes = stackalloc byte[(int)pass.Parameters.Bytes.Length];
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
        pass.Parameters.Bytes.Span.CopyTo(bytes[ShaderFrameConstants.SizeBytes..]);
        if (compute is not null) {
            compute.PushConstants(m_device.DeviceHandle, command, layout, GpuShaderStage.Compute, 0, bytes);
        } else {
            graphics!.PushConstants(m_device.DeviceHandle, command, layout, GpuShaderStage.Fragment, 0, bytes);
        }
    }

    private void InitializeZeroImages(int slot, nint command, IGpuComputeRecorder recorder) {
        foreach (var resource in m_resources) {
            if (resource.Spec.Initialization != ShaderPipelineInitialization.Zero || resource.Images is null) {
                continue;
            }
            if (resource.Initialized is null) {
                resource.Initialized = new bool[resource.Images.Length];
            }
            var clearRecorder = recorder as IGpuImageInitializationRecorder;
            if (clearRecorder is null) {
                throw new InvalidOperationException("The selected GPU backend cannot clear shader pipeline history images.");
            }
            for (var index = 0; index < resource.Images.Length; index++) {
                if (resource.Initialized[index]) {
                    continue;
                }
                var image = resource.Images[index];
                recorder.TransitionImageLayout(m_device.DeviceHandle, command, image.ImageHandle, GpuImageLayout.Undefined, GpuImageLayout.General, GpuComputeAccess.None, GpuComputeAccess.ShaderWrite, GpuComputeStage.TopOfPipe, GpuComputeStage.ComputeShader);
                clearRecorder.ClearStorageImage(m_device.DeviceHandle, command, image.ImageHandle, ParseFormat(resource.Spec.Format));
                resource.Layouts![index] = GpuImageLayout.General;
                resource.Initialized[index] = true;
            }
        }
    }
    private void Transition(RuntimePass pass, ResourceReference reference, int slot, nint command, IGpuComputeRecorder recorder) {
        var resource = m_resources.First(item => item.Spec.Name == reference.Name);
        if (resource.Spec.Kind == ShaderPipelineResourceKind.Buffer) {
            var buffer = ResolveBuffer(resource, reference.Name, HistoryIndex(resource, slot, reference.PreviousFrame));
            recorder.TransitionBuffer(m_device.DeviceHandle, command, buffer.BufferHandle, GpuComputeAccess.ShaderWrite, GpuComputeAccess.ShaderRead | GpuComputeAccess.ShaderWrite, GpuComputeStage.ComputeShader, GpuComputeStage.ComputeShader);
            return;
        }
        var index = HistoryIndex(resource, slot, reference.PreviousFrame);
        var image = ResolveImage(resource, reference.Name, index);
        var desired = pass.Spec.OutputReferences.Any(output => output.Name == reference.Name)
            ? GpuImageLayout.General
            : GpuImageLayout.ShaderReadOnly;
        var old = m_externalImages.TryGetValue(reference.Name, out var external)
            ? external.Layout
            : resource.Layouts![index];
        if (old != desired) {
            recorder.TransitionImageLayout(m_device.DeviceHandle, command, image.ImageHandle, old, desired, old == GpuImageLayout.Undefined ? GpuComputeAccess.None : GpuComputeAccess.ShaderWrite, desired == GpuImageLayout.General ? GpuComputeAccess.ShaderWrite : GpuComputeAccess.ShaderRead, old == GpuImageLayout.Undefined ? GpuComputeStage.TopOfPipe : GpuComputeStage.ComputeShader, GpuComputeStage.ComputeShader);
            if (!m_externalImages.ContainsKey(reference.Name)) {
                resource.Layouts![index] = desired;
            }
        }
    }

    private ShaderPipelineExternalImage ResolveImage(RuntimeResource resource, string name, int index) {
        if (m_externalImages.TryGetValue(name, out var external)) {
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
        InitializeZeroImages(slot, command, recorder);
        foreach (var input in pass.Spec.InputReferences) {
            Transition(pass, input, slot, command, recorder);
        }
        foreach (var output in pass.Spec.OutputReferences) {
            var resource = m_resources.First(item => item.Spec.Name == output.Name);
            var target = resource.Targets![slot];
            var old = resource.Layouts![slot];
            var desired = before ? GpuImageLayout.RenderTarget : GpuImageLayout.General;
            if (old != desired) {
                recorder.TransitionImageLayout(m_device.DeviceHandle, command, target.ImageHandle, old, desired, GpuComputeAccess.ShaderWrite, GpuComputeAccess.ShaderRead, old == GpuImageLayout.Undefined ? GpuComputeStage.TopOfPipe : GpuComputeStage.FragmentShader, GpuComputeStage.FragmentShader);
                resource.Layouts![slot] = desired;
            }
        }
        recorder.EndCommandBuffer(m_device.DeviceHandle, command);
        commands.Add(command);
    }

    private Surface Output(int slot) {
        var selectedName = m_selectedOutput ?? m_pipeline!.Plan.OutputResourceName;
        var resource = PresentationResource(m_resources.First(item => item.Spec.Name == selectedName));
        nint imageHandle;
        nint imageView;
        uint width;
        uint height;
        if (resource.Images is not null) {
            var image = resource.Images[slot];
            imageHandle = image.ImageHandle;
            imageView = image.ImageViewHandle;
            width = image.Width;
            height = image.Height;
        } else {
            var target = resource.Targets![slot];
            imageHandle = target.ImageHandle;
            imageView = target.ImageViewHandle;
            width = target.Width;
            height = target.Height;
        }
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
        // A float image cannot be handed to Surface, whose contract is four-byte RGBA8 presentation.
        // Use the authored visualization/conversion pass when one is present; it performs the conversion on-GPU.
        foreach (var pass in m_pipeline!.Plan.Passes) {
            if (!pass.Declaration.InputReferences.Any(input => input.Name == selected.Spec.Name)) {
                continue;
            }
            foreach (var output in pass.Declaration.OutputReferences) {
                var candidate = m_resources.FirstOrDefault(item => item.Spec.Name == output.Name);
                if (candidate is null) {
                    continue;
                }
                var format = ParseFormat(candidate.Spec.Format);
                if (format == GpuPixelFormat.R8G8B8A8Unorm || format == GpuPixelFormat.B8G8R8A8Unorm) {
                    return candidate;
                }
            }
        }
        throw new InvalidDataException($"Selected float output '{selected.Spec.Name}' has no RGBA8 conversion pass.");
    }
    private void WaitAll() {
        foreach (var slot in m_slots) {
            slot.Fence?.Wait();
        }
    }

    private void Release(bool wait) {
        if (wait) {
            WaitAll();
        }
        DisposeGraph(m_passes, m_resources);
        foreach (var slot in m_slots) {
            slot.Fence?.Dispose();
            slot.Fence = null;
        }
        m_passes = [];
        m_resources = [];
        m_ready = false;
    }

    private void DisposeGraph(RuntimePass[] passes, RuntimeResource[] resources) {
        foreach (var pass in passes) {
            pass.Dispose(m_gpu, m_device);
        }
        foreach (var resource in resources) {
            resource.Dispose();
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
    private sealed class FrameSlot {
        public IGpuSubmissionFence? Fence;
    }

    private sealed class RuntimeResource(ShaderPipelineResource spec, int count) {
        public readonly ShaderPipelineResource Spec = spec;
        public readonly int Count = count;
        public IGpuStorageImage[]? Images;
        public IGpuBuffer[]? Buffers;
        public IGpuRenderTarget[]? Targets;
        public GpuImageLayout[]? Layouts;
        public bool[]? Initialized;

        public void Dispose() {
            if (Images is not null) {
                foreach (var image in Images) {
                    image.Dispose();
                }
            }
            if (Buffers is not null) {
                foreach (var buffer in Buffers) {
                    buffer.Dispose();
                }
            }
        }
    }

    private sealed class RuntimePass(ShaderPipelinePass spec, CompiledShader compiled, ShaderPipelineParameterLayout parameterLayout, int count) {
        public readonly ShaderPipelinePass Spec = spec;
        public readonly CompiledShader Compiled = compiled;
        public readonly int Count = count;
        public readonly ShaderPipelineParameterLayout ParametersLayout = parameterLayout;
        public ShaderPipelineParameterValues Parameters = parameterLayout.TryBind(null, out var values, out _) ? values : throw new InvalidDataException($"Invalid parameters for pass {spec.Name}.");
        public List<GpuComputeBinding> Bindings = [];
        public IGpuShaderModule? Primary;
        public IGpuShaderModule? Secondary;
        public IGpuComputePipeline? Compute;
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
            if (Graphics is not null) {
                foreach (var pipeline in Graphics) {
                    pipeline.Dispose();
                }
            }
            Primary?.Dispose();
            Secondary?.Dispose();
            if (Targets is not null) {
                foreach (var target in Targets) {
                    target.Dispose();
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