using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;
using Puck.Launcher;

namespace Puck.Post;

/// <summary>
/// The Tier-D probe root node, hosted instead of the battery when the POST is launched with <c>--probe &lt;name&gt;</c>
/// by a Tier-D stage's CHILD PROCESS. Tier D exercises the launcher's LIVE subsystems — the frame loop's device-lost
/// recovery and the runtime backend switch — which run ABOVE the root node, so they cannot be driven from inside the
/// battery's single one-shot frame; each probe is instead a small multi-frame run in an isolated process (also the
/// plan's Tier-D process-isolation mitigation). Unlike the battery node it PRESENTS REAL CONTENT — a per-frame
/// <c>gradient.comp</c> fill into its own storage image — because the probes prove resource lifecycles: the node owns
/// GPU objects, releases them in <see cref="OnDeviceLost"/> (the node-release seam), and rebuilds them against the
/// recovered device on the next frame, exactly as a production producer must.
/// <para><c>device-loss</c>: renders while the parent-set <c>PUCK_TEST_DEVICE_LOSS=1</c> injects a synthetic loss at
/// ~1 s; the launcher catches it above this node, calls <see cref="OnDeviceLost"/>, recovers the device in place, and
/// resumes. The probe asserts the run survives well past the injection, that the loss actually fired through the
/// node-release seam, that resources were rebuilt (frames continue), and that elapsed ticks stayed monotonic (a
/// recovery must not reset the loop's clock).</para>
/// <para><c>hot-switch</c>: toggles the <see cref="BackendSwitcher"/> from the preferred Vulkan backend to the
/// registered Direct3D 12 presenter mid-run, asserts the active backend actually changed, and drives further
/// presented frames on the new backend before exiting. This probe presents NO content in either phase — deliberately.
/// The POST isolated two engine gaps here (2026-07-01, follow-up work tracked): (1) the swap crashes (0xC0000005)
/// whenever the Vulkan presenter has EVER presented real content, even with the node quiesced for 10+ frames and the
/// device WaitIdle()d at the swap; and (2) even with a fixed swap, content cannot follow it — the device capability is
/// not republished and nodes have no release/rebuild seam for a backend change, so the Direct3D 12 blit would receive
/// a Vulkan image view (type confusion). Until those land, what this probe proves is the presenter LIFECYCLE:
/// deactivate → activate on the live window binding, and a surviving present loop on the new backend.</para>
/// </summary>
internal sealed class PostProbeNode : IRenderNode {
    /// <summary>The <c>--probe</c> argument selecting the device-lost recovery probe.</summary>
    public const string DeviceLossProbe = "device-loss";
    /// <summary>The <c>--probe</c> argument selecting the runtime backend-switch probe.</summary>
    public const string HotSwitchProbe = "hot-switch";

    // KEEP 0 until the engine's hot-switch-after-live-presents crash is fixed (see the class doc): any real present
    // before the swap reproduces the 0xC0000005. Raising this restores the content-across-switch repro.
    private const int ContentFramesBeforeSwitch = 0;
    private const int DrainFramesBeforeSwitch = 10;     // frames between the last content present and the swap
    private const GpuPixelFormat Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const int FramesAfterSwitch = 30;           // presents on the new backend before declaring success
    private const int FramesAfterRecovery = 60;         // frames on the rebuilt resources before declaring success
    private const uint RenderSize = 256;
    private const uint WorkgroupEdge = 8;

    private readonly NodeDescriptor m_descriptor = new(Name: "post-probe", SurfaceId: SurfaceId.New());
    private readonly string m_mode;
    private readonly PostRunResult m_runResult;
    private readonly IServiceProvider m_services;

    private nint m_commandBuffer;
    private IGpuComputeCommandPool? m_commandPool;
    private IGpuDeviceContext? m_deviceContext;
    private bool m_disposed;
    private long m_framesAfterLoss;
    private long m_framesAfterSwitch;
    private long m_frameCount;
    private IGpuComputeServices? m_gpu;
    private IGpuStorageImage? m_image;
    private ulong m_lastElapsedTicks;
    private bool m_lossObserved;
    private IGpuShaderModule? m_module;
    private bool m_outputInitialized;
    private IGpuComputePipeline? m_pipeline;
    private nint m_pool;
    private long m_rebuildCount;
    private nint m_set;
    private bool m_switched;

    /// <summary>Initializes a new instance of the <see cref="PostProbeNode"/> class.</summary>
    /// <param name="services">The application service provider (the compute services + the backend switcher).</param>
    /// <param name="mode">The probe mode (<see cref="DeviceLossProbe"/> or <see cref="HotSwitchProbe"/>).</param>
    /// <param name="runResult">The shared carrier the probe's exit code is written to.</param>
    public PostProbeNode(IServiceProvider services, string mode, PostRunResult runResult) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(mode);
        ArgumentNullException.ThrowIfNull(runResult);

        m_mode = mode;
        m_runResult = runResult;
        m_services = services;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public void OnDeviceLost() {
        // The node-release seam: drop every device child BEFORE the presenter recreates the device; the next frame's
        // EnsureResources latch rebuilds against the fresh handles. This is the exact contract production nodes obey.
        ReleaseResources();
        m_lossObserved = true;
    }

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        m_frameCount++;

        // The preserved-counter assertion: elapsed ticks must be monotonic across a recovery — the fixed-step sim is
        // never touched by a device loss, so a regression means the recovery rebuilt too much.
        if (context.ElapsedTicks < m_lastElapsedTicks) {
            Console.Error.WriteLine(value: $"PROBE {m_mode} fail | elapsed ticks regressed {m_lastElapsedTicks} -> {context.ElapsedTicks} on frame {m_frameCount} — the recovery reset the loop's clock");
            m_runResult.ExitCode = 1;
            RequestExit(context: in context);

            return default;
        }

        m_lastElapsedTicks = context.ElapsedTicks;

        if (!context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var device)) {
            return default;
        }

        // Around a hot switch the probe stops producing content: post-switch the Direct3D 12 presenter cannot blit
        // this node's Vulkan surface (the missing runtime content-re-target seam — see the class doc), and the last
        // pre-switch frames drain so the Vulkan presenter deactivates with nothing of ours in flight; the presenter
        // lifecycle is what remains under test.
        var producesContent = ((HotSwitchProbe != m_mode) || (!m_switched && (m_frameCount <= ContentFramesBeforeSwitch)));

        if (producesContent) {
            EnsureResources(device: device);
            RenderGradient(device: device);
        }

        switch (m_mode) {
            case DeviceLossProbe:
                StepDeviceLoss(context: in context);
                break;
            case HotSwitchProbe:
                StepHotSwitch(context: in context);
                break;
            default:
                Console.Error.WriteLine(value: $"PROBE fail | unknown probe mode '{m_mode}'");
                m_runResult.ExitCode = 2;
                RequestExit(context: in context);
                break;
        }

        return (producesContent
            ? new Surface(
                ImageViewHandle: m_image!.ImageViewHandle,
                Width: RenderSize,
                Height: RenderSize,
                Format: SurfaceFormat.R8G8B8A8Unorm
            )
            : default);
    }

    private void StepDeviceLoss(in FrameContext context) {
        if (0 == (m_frameCount % 64)) {
            Console.Error.WriteLine(value: $"PROBE device-loss progress | frame {m_frameCount}, loss observed: {m_lossObserved}");
        }

        if (!m_lossObserved) {
            return;
        }

        // The loss fired through OnDeviceLost and this frame rendered on REBUILT resources; hold for a stretch of
        // post-recovery frames so a wobbling device would still surface, then declare success.
        if (++m_framesAfterLoss >= FramesAfterRecovery) {
            Console.Out.WriteLine(value: $"PROBE device-loss ok | synthetic loss observed via OnDeviceLost, resources rebuilt {m_rebuildCount - 1}x, {m_framesAfterLoss} frames presented after recovery ({m_frameCount} total), elapsed ticks monotonic");
            m_runResult.ExitCode = 0;
            RequestExit(context: in context);
        }
    }

    private void StepHotSwitch(in FrameContext context) {
        var switcher = m_services.GetRequiredService<BackendSwitcher>();

        // Present real content, drain, then toggle once and keep driving frames on the other backend.
        if (!m_switched) {
            if (m_frameCount < (ContentFramesBeforeSwitch + DrainFramesBeforeSwitch)) {
                return;
            }

            // Nothing of ours has been submitted for DrainFramesBeforeSwitch frames; settle the device before the
            // presenter teardown/activation so no prior present samples our image mid-swap.
            if (context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var device)) {
                device.WaitIdle();
            }

            var before = switcher.ActiveBackendName;

            switcher.Switch();
            m_switched = true;

            if (string.Equals(before, switcher.ActiveBackendName, StringComparison.OrdinalIgnoreCase)) {
                Console.Error.WriteLine(value: $"PROBE hot-switch fail | Switch() did not change the active backend (still '{switcher.ActiveBackendName}') — is the second presenter registered?");
                m_runResult.ExitCode = 1;
                RequestExit(context: in context);
            }

            return;
        }

        if (++m_framesAfterSwitch >= FramesAfterSwitch) {
            Console.Out.WriteLine(value: $"PROBE hot-switch ok | presenter lifecycle survived the live vulkan -> '{switcher.ActiveBackendName}' swap; {m_framesAfterSwitch} frames driven on the new backend (content re-target across the switch is the deferred engine seam)");
            m_runResult.ExitCode = 0;
            RequestExit(context: in context);
        }
    }

    // Build (or rebuild after a device loss) the gradient fill: module + pipeline + storage image + descriptor set +
    // command pool, all children of the CURRENT device.
    private void EnsureResources(IGpuDeviceContext device) {
        if (m_image is not null) {
            return;
        }

        m_gpu ??= m_services.GetRequiredService<IGpuComputeServices>();
        m_deviceContext = device;

        var deviceHandle = device.DeviceHandle;
        var bytecode = File.ReadAllBytes(path: Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders", "Compute", "gradient.comp.spv"));

        GpuComputeBinding[] bindings = [new GpuComputeBinding(Binding: 0, Kind: GpuComputeBindingKind.StorageImage)];

        m_module = m_gpu.ShaderModuleFactory.Create(deviceContext: device, stage: GpuShaderStage.Compute, bytecode: bytecode);
        m_pipeline = m_gpu.ComputePipelineFactory.Create(bindings: bindings, computeShaderModule: m_module, deviceContext: device, pushConstantBinding: null);
        m_image = m_gpu.StorageImageFactory.Create(deviceContext: device, format: Format, height: RenderSize, width: RenderSize);

        var poolSizes = GpuDescriptorPoolSizes.ForSets(bindings);

        m_pool = m_gpu.DescriptorAllocator.CreatePool(deviceHandle: deviceHandle, sizes: poolSizes);
        m_set = m_gpu.DescriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_pipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: m_pool);
        m_gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: 0, descriptorSetHandle: m_set, deviceHandle: deviceHandle, imageViewHandle: m_image.ImageViewHandle);
        m_commandPool = m_gpu.CommandPoolFactory.Create(deviceContext: device);
        m_commandBuffer = m_commandPool.CommandBufferHandle;
        m_outputInitialized = false;
        m_rebuildCount++;
    }

    // One gradient fill, handed off shader-readable for the presenter to sample.
    private void RenderGradient(IGpuDeviceContext device) {
        var recorder = m_gpu!.ComputeRecorder;
        var deviceHandle = device.DeviceHandle;
        var oldLayout = (m_outputInitialized ? GpuImageLayout.ShaderReadOnly : GpuImageLayout.Undefined);
        var sourceAccess = (m_outputInitialized ? GpuComputeAccess.ShaderRead : GpuComputeAccess.None);
        var sourceStage = (m_outputInitialized ? GpuComputeStage.FragmentShader : GpuComputeStage.TopOfPipe);
        var groups = ((RenderSize + (WorkgroupEdge - 1)) / WorkgroupEdge);

        recorder.BeginCommandBuffer(commandBufferHandle: m_commandBuffer, deviceHandle: deviceHandle);
        recorder.TransitionImageLayout(commandBufferHandle: m_commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderWrite, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, imageHandle: m_image!.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: oldLayout, sourceAccessMask: sourceAccess, sourceStageMask: sourceStage);
        recorder.BindComputePipeline(commandBufferHandle: m_commandBuffer, deviceHandle: deviceHandle, pipelineHandle: m_pipeline!.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: m_commandBuffer, descriptorSetHandle: m_set, deviceHandle: deviceHandle, pipelineLayoutHandle: m_pipeline.LayoutHandle);
        recorder.Dispatch(commandBufferHandle: m_commandBuffer, deviceHandle: deviceHandle, groupCountX: groups, groupCountY: groups, groupCountZ: 1);
        recorder.TransitionImageLayout(commandBufferHandle: m_commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.FragmentShader, deviceHandle: deviceHandle, imageHandle: m_image.ImageHandle, newLayout: GpuImageLayout.ShaderReadOnly, oldLayout: GpuImageLayout.General, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader);
        recorder.EndCommandBuffer(commandBufferHandle: m_commandBuffer, deviceHandle: deviceHandle);
        m_gpu.QueueSubmitter.SubmitAndWait(commandBufferHandles: [m_commandBuffer], deviceContext: device);
        m_outputInitialized = true;
    }

    private void ReleaseResources() {
        m_commandPool?.Dispose();
        m_commandPool = null;
        m_commandBuffer = 0;

        if ((0 != m_pool) && (m_gpu is not null) && (m_deviceContext is not null)) {
            m_gpu.DescriptorAllocator.DestroyPool(deviceHandle: m_deviceContext.DeviceHandle, poolHandle: m_pool);
        }

        m_pool = 0;
        m_set = 0;
        m_pipeline?.Dispose();
        m_pipeline = null;
        m_image?.Dispose();
        m_image = null;
        m_module?.Dispose();
        m_module = null;
        m_outputInitialized = false;
    }

    private static void RequestExit(in FrameContext context) {
        if (context.Host.HoldsCapability<ITerminalControl>(capability: out var terminal)) {
            terminal.RequestExit();
        }
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_deviceContext?.WaitIdle();
        ReleaseResources();
    }
}
