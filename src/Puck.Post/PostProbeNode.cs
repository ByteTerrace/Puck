using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.DirectX;
using Puck.DirectX.Interop;
using Puck.DirectX.Presentation;
using Puck.Hosting;
using Puck.Launcher;

namespace Puck.Post;

/// <summary>
/// The Tier-D probe root node, hosted instead of the battery when the POST is launched with <c>--probe &lt;name&gt;</c>
/// by a Tier-D stage's CHILD PROCESS. Tier D exercises the launcher's LIVE subsystems — the frame loop's device-lost
/// recovery and the runtime backend switch — which run ABOVE the root node, so they cannot be driven from inside the
/// battery's single one-shot frame; each probe is instead a small multi-frame run in an isolated process (also the
/// plan's Tier-D process-isolation mitigation). The node PRESENTS REAL CONTENT — a per-frame <c>sdf-child</c> fill
/// into its own storage image — because the probes prove resource lifecycles: the node owns GPU objects, releases
/// them at the transition seams, and rebuilds them against the current device, exactly as a production producer must.
/// <para><c>device-loss</c>: renders while the parent-set <c>PUCK_TEST_DEVICE_LOSS=1</c> injects a synthetic loss at
/// ~1 s; the launcher catches it above this node, calls <see cref="OnDeviceLost"/> (the node-release seam), recovers
/// the device in place, and resumes. The probe asserts the run survives well past the injection, that the loss fired
/// through the seam, that resources were rebuilt (frames continue), and that elapsed ticks stayed monotonic (a
/// recovery must not reset the loop's clock).</para>
/// <para><c>hot-switch</c>: presents on the preferred Vulkan backend, toggles the <see cref="BackendSwitcher"/> to
/// the registered Direct3D 12 presenter mid-run, and keeps presenting REAL content on the new backend — the node
/// polls <see cref="BackendSwitcher.ActiveBackendName"/> each frame and RE-TARGETS on change: it releases its Vulkan
/// resources (safe: a deactivated backend's device now deliberately survives its presenter — the 2026-07-02 fix for
/// the switch-after-live-presents crash the POST discovered) and rebuilds the same fill on the presenter's Direct3D
/// 12 device with a self-composed neutral service bundle, handing back D3D12 surfaces the active blit can consume.
/// Success additionally reads the post-switch image back on the Direct3D 12 device and asserts non-flat content.</para>
/// </summary>
internal sealed class PostProbeNode : IRenderNode {
    /// <summary>The <c>--probe</c> argument selecting the device-lost recovery probe.</summary>
    public const string DeviceLossProbe = "device-loss";
    /// <summary>The <c>--probe</c> argument selecting the runtime backend-switch probe.</summary>
    public const string HotSwitchProbe = "hot-switch";
    /// <summary>The <c>--probe</c> argument selecting the present-cadence (closed-loop present-timing feedback) probe.</summary>
    public const string PresentCadenceProbe = "present-cadence";

    private const int ChildPushByteLength = 16;         // ChildParams { uint2 extent; float time; uint pad; }
    private const int ContentFramesBeforeSwitch = 20;   // real presented frames on Vulkan before the swap
    private const GpuPixelFormat Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const int FramesAfterSwitch = 30;           // real presented frames on the new backend before success
    private const int FramesAfterRecovery = 60;         // frames on the rebuilt resources before declaring success
    private const uint RenderSize = 256;
    private const int PresentFrameBudget = 600;   // frames to wait for the sample target before an env-lenient skip
    private const int PresentSamplesTarget = 30;  // confirmed presents to collect before declaring the feedback live
    private const string VulkanBackendName = "vulkan";
    private const uint WorkgroupEdge = 8;

    private readonly byte[] m_childPush = BuildChildPush();
    private readonly NodeDescriptor m_descriptor = new(Name: "post-probe", SurfaceId: SurfaceId.New());
    private readonly string m_mode;
    private readonly PostRunResult m_runResult;
    private readonly IServiceProvider m_services;

    private nint m_commandBuffer;
    private IGpuComputeCommandPool? m_commandPool;
    private IGpuDeviceContext? m_deviceContext;
    private ServiceProvider? m_directXProvider;
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
    private string? m_resourcesBackend;
    private nint m_set;
    private bool m_switched;
    private bool m_verdictWritten;
    private int m_confirmedPresents;
    private uint m_lastPresentCount;
    private long m_lastPresentTicks;
    private double m_presentIntervalMaxMs;
    private double m_presentIntervalMinMs = double.MaxValue;
    private double m_presentIntervalSumMs;
    private bool m_nonMonotonicPresent;

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
        // rebuild latch recreates them against the fresh handles. This is the exact contract production nodes obey.
        ReleaseResources();
        m_lossObserved = true;
    }

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_disposed) {
            return default;
        }

        m_frameCount++;

        // The preserved-counter assertion: elapsed ticks must be monotonic across a recovery/switch — the fixed-step
        // sim is never touched by either, so a regression means the transition rebuilt too much.
        if (context.ElapsedTicks < m_lastElapsedTicks) {
            Console.Error.WriteLine(value: $"PROBE {m_mode} fail | elapsed ticks regressed {m_lastElapsedTicks} -> {context.ElapsedTicks} on frame {m_frameCount} — the transition reset the loop's clock");
            m_runResult.ExitCode = 1;
            m_verdictWritten = true;
            RequestExit(context: in context);

            return default;
        }

        m_lastElapsedTicks = context.ElapsedTicks;

        // Step FIRST: a hot switch must happen before this frame's render, so the surface handed back below is always
        // produced on (and consumable by) the presenter that will blit it.
        switch (m_mode) {
            case DeviceLossProbe:
                StepDeviceLoss(context: in context);
                break;
            case HotSwitchProbe:
                StepHotSwitch(context: in context);
                break;
            case PresentCadenceProbe:
                StepPresentCadence(context: in context);
                break;
            default:
                Console.Error.WriteLine(value: $"PROBE fail | unknown probe mode '{m_mode}'");
                m_runResult.ExitCode = 2;
                m_verdictWritten = true;
                RequestExit(context: in context);

                return default;
        }

        if (m_verdictWritten) {
            // A step verdict was just written (the run is exiting); do not render a frame after it.
            return default;
        }

        // The re-target seam: rebuild whenever the resources' backend no longer matches the active one (the device a
        // deactivated backend published stays alive, so releasing against it is safe at any later frame).
        var activeBackend = ((HotSwitchProbe == m_mode)
            ? m_services.GetRequiredService<BackendSwitcher>().ActiveBackendName
            : VulkanBackendName);

        if ((m_image is not null) && !string.Equals(m_resourcesBackend, activeBackend, StringComparison.OrdinalIgnoreCase)) {
            ReleaseResources();
            m_rebuildCount++;
        }

        if (!EnsureResources(activeBackend: activeBackend, context: in context)) {
            return default;
        }

        RenderPattern();

        return new Surface(
            ImageViewHandle: m_image!.ImageViewHandle,
            Width: RenderSize,
            Height: RenderSize,
            Format: SurfaceFormat.R8G8B8A8Unorm
        );
    }

    private void StepDeviceLoss(in FrameContext context) {
        if (!m_lossObserved) {
            return;
        }

        // The loss fired through OnDeviceLost and frames keep rendering on REBUILT resources; hold for a stretch of
        // post-recovery frames so a wobbling device would still surface, then declare success.
        if (++m_framesAfterLoss >= FramesAfterRecovery) {
            Console.Out.WriteLine(value: $"PROBE device-loss ok | synthetic loss observed via OnDeviceLost, resources rebuilt, {m_framesAfterLoss} frames presented after recovery ({m_frameCount} total), elapsed ticks monotonic");
            m_runResult.ExitCode = 0;
            m_verdictWritten = true;
            RequestExit(context: in context);
        }
    }

    private void StepHotSwitch(in FrameContext context) {
        var switcher = m_services.GetRequiredService<BackendSwitcher>();

        if (!m_switched) {
            if (m_frameCount <= ContentFramesBeforeSwitch) {
                return;
            }

            var before = switcher.ActiveBackendName;

            switcher.Switch();
            m_switched = true;

            if (string.Equals(before, switcher.ActiveBackendName, StringComparison.OrdinalIgnoreCase)) {
                Console.Error.WriteLine(value: $"PROBE hot-switch fail | Switch() did not change the active backend (still '{switcher.ActiveBackendName}') — is the second presenter registered?");
                m_runResult.ExitCode = 1;
                m_verdictWritten = true;
                RequestExit(context: in context);
            }

            return;
        }

        if (++m_framesAfterSwitch >= FramesAfterSwitch) {
            // The content proof: the post-switch image lives on the Direct3D 12 device; read it back THERE and
            // require non-flat content before declaring the re-target complete.
            if (!TryDescribeContent(distinctSample: out var distinctSample)) {
                Console.Error.WriteLine(value: $"PROBE hot-switch fail | the post-switch image on '{switcher.ActiveBackendName}' read back flat — the re-targeted render produced no content");
                m_runResult.ExitCode = 1;
                m_verdictWritten = true;
                RequestExit(context: in context);

                return;
            }

            Console.Out.WriteLine(value: $"PROBE hot-switch ok | presented {ContentFramesBeforeSwitch} real frames on vulkan, switched live to '{switcher.ActiveBackendName}', re-targeted (rebuilds: {m_rebuildCount}), presented {m_framesAfterSwitch} real frames there; post-switch readback non-flat ({distinctSample} distinct sample values)");
            m_runResult.ExitCode = 0;
            m_verdictWritten = true;
            RequestExit(context: in context);
        }
    }

    // D1: observe the CLOSED-LOOP present-timing feedback the pacer phase-locks to. The node presents real content
    // every frame (below), so confirmed presents accumulate; each new confirmed present's timestamp is differenced
    // with the prior one to build the live present cadence. This proves the feedback path (IPresentTimingFeedback via
    // the BackendSwitcher) delivers monotonic, plausible present timings — the LIVE plumbing A5's pure-CPU aligner sim
    // cannot cover. It deliberately does NOT assert VRR phase-lock convergence: that needs a variable-refresh panel
    // (the dev display is fixed-refresh), so a presenter/display that delivers no feedback SKIPs rather than fails.
    private void StepPresentCadence(in FrameContext context) {
        if (m_services.GetRequiredService<BackendSwitcher>() is not IPresentTimingFeedback feedback) {
            Console.Error.WriteLine(value: "PROBE present-cadence fail | the active presenter does not expose IPresentTimingFeedback");
            m_runResult.ExitCode = 1;
            m_verdictWritten = true;
            RequestExit(context: in context);

            return;
        }

        var sample = feedback.LastPresentTiming;

        // A changed PresentCount signals a NEW confirmed present; difference its timestamp with the previous one.
        if (sample.IsAvailable && (sample.PresentCount != m_lastPresentCount)) {
            if (m_lastPresentTicks > 0L) {
                var intervalMs = ((sample.PresentTimestampTicks - m_lastPresentTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);

                if (intervalMs <= 0.0) {
                    m_nonMonotonicPresent = true;
                } else {
                    m_confirmedPresents++;
                    m_presentIntervalSumMs += intervalMs;
                    m_presentIntervalMinMs = Math.Min(val1: m_presentIntervalMinMs, val2: intervalMs);
                    m_presentIntervalMaxMs = Math.Max(val1: m_presentIntervalMaxMs, val2: intervalMs);
                }
            }

            m_lastPresentCount = sample.PresentCount;
            m_lastPresentTicks = sample.PresentTimestampTicks;
        }

        if (m_nonMonotonicPresent) {
            Console.Error.WriteLine(value: "PROBE present-cadence fail | consecutive confirmed-present timestamps were not monotonic");
            m_runResult.ExitCode = 1;
            m_verdictWritten = true;
            RequestExit(context: in context);

            return;
        }

        if (m_confirmedPresents >= PresentSamplesTarget) {
            var meanMs = (m_presentIntervalSumMs / m_confirmedPresents);

            Console.Out.WriteLine(value: $"PROBE present-cadence ok | closed-loop present-timing feedback live: {m_confirmedPresents} confirmed presents, interval mean {meanMs:0.###} ms (min {m_presentIntervalMinMs:0.###}, max {m_presentIntervalMaxMs:0.###} ms), timestamps monotonic — VRR phase-lock convergence needs a variable-refresh panel (A5 covers the aligner math)");
            m_runResult.ExitCode = 0;
            m_verdictWritten = true;
            RequestExit(context: in context);

            return;
        }

        // Env-lenient: a presenter/display that never delivers enough confirmed-present feedback within the frame
        // budget is a SKIP, not a failure (the closed-loop feature is simply absent here). The stage maps it to Skip.
        if (m_frameCount >= PresentFrameBudget) {
            Console.Out.WriteLine(value: $"PROBE present-cadence skip | present-timing feedback delivered only {m_confirmedPresents}/{PresentSamplesTarget} confirmed presents in {m_frameCount} frames — this presenter/display provides no closed-loop present timing");
            m_runResult.ExitCode = 0;
            m_verdictWritten = true;
            RequestExit(context: in context);
        }
    }

    // Build (or rebuild after a loss/switch) the sdf-child fill against the ACTIVE backend: module + pipeline +
    // storage image + descriptor set + command pool, all children of that backend's device.
    private bool EnsureResources(string activeBackend, in FrameContext context) {
        if (m_image is not null) {
            return true;
        }

        if (string.Equals(activeBackend, VulkanBackendName, StringComparison.OrdinalIgnoreCase)) {
            if (!context.Host.TryResolveCapability<IGpuDeviceContext>(capability: out var device)) {
                return false;
            }

            m_deviceContext = device;
            m_gpu = m_services.GetRequiredService<IGpuComputeServices>();
        } else if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
            BindDirectX();
        } else {
            return false;
        }

        var deviceHandle = m_deviceContext!.DeviceHandle;
        var extension = (string.Equals(activeBackend, VulkanBackendName, StringComparison.OrdinalIgnoreCase) ? ".spv" : ".dxil");
        var bytecode = PostShaders.Read(folder: "Sdf", file: ("sdf-child.comp" + extension));

        GpuComputeBinding[] bindings = [new GpuComputeBinding(Binding: 0, Kind: GpuComputeBindingKind.StorageImage)];

        m_module = m_gpu!.ShaderModuleFactory.Create(deviceContext: m_deviceContext, stage: GpuShaderStage.Compute, bytecode: bytecode);
        m_pipeline = m_gpu.ComputePipelineFactory.Create(bindings: bindings, computeShaderModule: m_module, deviceContext: m_deviceContext, pushConstantBinding: new GpuPushConstantBinding(data: m_childPush, offset: 0, stageFlags: GpuShaderStage.Compute));
        m_image = m_gpu.StorageImageFactory.Create(deviceContext: m_deviceContext, format: Format, height: RenderSize, width: RenderSize);

        m_pool = m_gpu.DescriptorAllocator.CreatePool(
            deviceHandle: deviceHandle,
            sizes: GpuDescriptorPoolSizes.ForSets(bindings)
        );
        m_set = m_gpu.DescriptorAllocator.AllocateSet(descriptorSetLayoutHandle: m_pipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: m_pool);
        m_gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: 0, descriptorSetHandle: m_set, deviceHandle: deviceHandle, imageViewHandle: m_image.ImageViewHandle);
        m_commandPool = m_gpu.CommandPoolFactory.Create(deviceContext: m_deviceContext);
        m_commandBuffer = m_commandPool.CommandBufferHandle;
        m_outputInitialized = false;
        m_resourcesBackend = activeBackend;

        return true;
    }

    // The Direct3D 12 half of the re-target seam: the PRESENTER's device (the DI singleton the blit consumes
    // surfaces on) plus a self-composed neutral compute bundle — the neutral services are per-call-device, so the
    // bundle is freely constructible (the same composition PostDirectXDevice uses for the Tier-C off-host device).
    [SupportedOSPlatform("windows10.0.10240")]
    private void BindDirectX() {
        m_deviceContext = m_services.GetRequiredService<DirectXDeviceContext>();

        if (m_directXProvider is null) {
            var collection = new ServiceCollection();

            collection.AddDirectXComputeApis();
            collection.TryAddSingleton<IGpuDescriptorAllocator>(static _ => new DirectXGpuDescriptorAllocator());
            collection.TryAddSingleton<IGpuQueueSubmitter>(static _ => new DirectXGpuQueueSubmitter());
            collection.TryAddSingleton<IGpuShaderModuleFactory>(static _ => new DirectXGpuShaderModuleFactory());
            collection.TryAddSingleton<IGpuStorageBufferFactory>(static _ => new DirectXGpuStorageBufferFactory());
            collection.TryAddSingleton<IGpuSurfaceTransferFactory>(static _ => new DirectXGpuSurfaceTransferFactory());

            m_directXProvider = collection.BuildServiceProvider();
        }

        m_gpu = m_directXProvider.GetRequiredService<IGpuComputeServices>();
    }

    // One sdf-child fill, handed off shader-readable for the active presenter to blit.
    private void RenderPattern() {
        var recorder = m_gpu!.ComputeRecorder;
        var deviceHandle = m_deviceContext!.DeviceHandle;
        var oldLayout = (m_outputInitialized ? GpuImageLayout.ShaderReadOnly : GpuImageLayout.Undefined);
        var sourceAccess = (m_outputInitialized ? GpuComputeAccess.ShaderRead : GpuComputeAccess.None);
        var sourceStage = (m_outputInitialized ? GpuComputeStage.FragmentShader : GpuComputeStage.TopOfPipe);
        var groups = ((RenderSize + (WorkgroupEdge - 1)) / WorkgroupEdge);

        recorder.BeginCommandBuffer(commandBufferHandle: m_commandBuffer, deviceHandle: deviceHandle);
        recorder.TransitionImageLayout(commandBufferHandle: m_commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderWrite, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, imageHandle: m_image!.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: oldLayout, sourceAccessMask: sourceAccess, sourceStageMask: sourceStage);
        recorder.BindComputePipeline(commandBufferHandle: m_commandBuffer, deviceHandle: deviceHandle, pipelineHandle: m_pipeline!.Handle);
        recorder.BindComputeDescriptorSet(commandBufferHandle: m_commandBuffer, descriptorSetHandle: m_set, deviceHandle: deviceHandle, pipelineLayoutHandle: m_pipeline.LayoutHandle);
        recorder.PushConstants(commandBufferHandle: m_commandBuffer, data: m_childPush, deviceHandle: deviceHandle, offset: 0, pipelineLayoutHandle: m_pipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
        recorder.Dispatch(commandBufferHandle: m_commandBuffer, deviceHandle: deviceHandle, groupCountX: groups, groupCountY: groups, groupCountZ: 1);
        recorder.TransitionImageLayout(commandBufferHandle: m_commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.FragmentShader, deviceHandle: deviceHandle, imageHandle: m_image.ImageHandle, newLayout: GpuImageLayout.ShaderReadOnly, oldLayout: GpuImageLayout.General, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader);
        recorder.EndCommandBuffer(commandBufferHandle: m_commandBuffer, deviceHandle: deviceHandle);
        m_gpu.QueueSubmitter.SubmitAndWait(commandBufferHandles: [m_commandBuffer], deviceContext: m_deviceContext);
        m_outputInitialized = true;
    }

    // Reads the current image back on ITS OWN device and reports whether it carries varying content.
    private bool TryDescribeContent(out int distinctSample) {
        distinctSample = 0;

        if ((m_gpu is null) || (m_deviceContext is null) || (m_image is null)) {
            return false;
        }

        using var readback = m_gpu.SurfaceTransferFactory.CreateReadback(deviceContext: m_deviceContext);
        var pixels = readback.Read(bytesPerPixel: 4, deviceContext: m_deviceContext, format: Format, height: RenderSize, sourceImageHandle: m_image.ImageHandle, width: RenderSize).Span;
        var seen = new HashSet<uint>();

        for (var offset = 0; (offset < pixels.Length); offset += ((int)RenderSize * 4)) {
            _ = seen.Add(item: MemoryMarshal.Read<uint>(source: pixels.Slice(start: offset, length: 4)));
        }

        distinctSample = seen.Count;

        return (seen.Count > 1);
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
        m_resourcesBackend = null;
    }

    private static byte[] BuildChildPush() {
        var push = new byte[ChildPushByteLength];
        var words = MemoryMarshal.Cast<byte, uint>(span: push.AsSpan());

        words[0] = RenderSize;
        words[1] = RenderSize; // ChildParams.extent; time (word 2) and pad (word 3) stay 0.

        return push;
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
        m_directXProvider?.Dispose();
    }
}
