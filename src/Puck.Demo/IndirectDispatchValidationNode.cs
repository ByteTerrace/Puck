using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions;
using Puck.Hosting;
using Puck.Vulkan.Interfaces;

namespace Puck.Demo;

/// <summary>
/// A one-shot root render node installed under <c>--validate-indirect</c>. It proves INDIRECT compute dispatch —
/// group counts the GPU reads from a buffer (Vulkan <c>vkCmdDispatchIndirect</c> / Direct3D 12 <c>ExecuteIndirect</c>
/// with a DISPATCH command signature) — behaves IDENTICALLY to a direct dispatch on BOTH backends. For each backend it
/// renders the neutral <c>sdf-child.comp</c> test pattern into a storage image twice — once via
/// <see cref="IGpuComputeRecorder.Dispatch"/>, once via <see cref="IGpuComputeRecorder.DispatchIndirect"/> reading the
/// same group counts from an args buffer — and asserts the two readbacks are BIT-IDENTICAL (a broken signature,
/// stride, offset, or args buffer would change the launched grid and diverge). Direct dispatch is its own oracle, so
/// no hand-computed expected pixels are needed. 0 = pass, 2 = infra-fail. It never presents.
/// </summary>
internal sealed class IndirectDispatchValidationNode : IRenderNode {
    private const uint Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const uint RenderSize = 64;
    private const uint WorkgroupEdge = 8;
    private const uint Groups = ((RenderSize + (WorkgroupEdge - 1)) / WorkgroupEdge); // 8 — covers the 64x64 image
    private const int PushByteLength = 16; // ChildParams { uint2 extent; float time; uint pad; }

    private readonly NodeDescriptor m_descriptor = new(
        Name: "indirect-dispatch-validation",
        SurfaceId: SurfaceId.New()
    );
    private readonly ParityResult m_result;
    private readonly IServiceProvider m_serviceProvider;
    private bool m_done;

    /// <summary>Initializes a new instance of the <see cref="IndirectDispatchValidationNode"/> class.</summary>
    /// <param name="serviceProvider">The application service provider (the live Vulkan device + compute services, and the LUID source for the bespoke Direct3D 12 device).</param>
    /// <param name="result">The shared result the exit code is written to.</param>
    public IndirectDispatchValidationNode(IServiceProvider serviceProvider, ParityResult result) {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(result);

        m_result = result;
        m_serviceProvider = serviceProvider;
    }

    /// <inheritdoc/>
    public NodeDescriptor Descriptor => m_descriptor;

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public Surface ProduceFrame(in FrameContext context) {
        if (m_done) {
            return default;
        }

        m_done = true;

        try {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240)) {
                Console.Out.WriteLine(value: "INDIRECT skip | Direct3D 12 requires Windows 10.0.10240+");
                m_result.ExitCode = 0;
            } else {
                Validate();
                m_result.ExitCode = 0;
            }
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"INDIRECT infra-fail | {exception}");
            m_result.ExitCode = 2;
        }

        if (context.Host.HoldsCapability<ITerminalControl>(capability: out var terminal)) {
            terminal.RequestExit();
        }

        return default;
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private void Validate() {
        var shaderBase = Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-child.comp");

        // Vulkan: the live host device + its neutral compute services (SPIR-V kernel).
        var vulkanDevice = (IGpuDeviceContext)Resolve<IVulkanDeviceContext>();
        var vulkanMatch = DirectMatchesIndirect(
            bytecode: File.ReadAllBytes(path: (shaderBase + ".spv")),
            deviceContext: vulkanDevice,
            gpu: Resolve<IGpuComputeServices>()
        );

        // Direct3D 12: a bespoke device LUID-matched to the Vulkan host (DXIL kernel), exactly as the reverse-share gate.
        using var directX = new DirectXComputeWorldDevice(hostProvider: m_serviceProvider);
        var directXMatch = DirectMatchesIndirect(
            bytecode: File.ReadAllBytes(path: (shaderBase + ".dxil")),
            deviceContext: directX.DeviceContext,
            gpu: (IGpuComputeServices)directX.Services.GetService(serviceType: typeof(IGpuComputeServices))!
        );

        if (
            !vulkanMatch ||
            !directXMatch
        ) {
            throw new InvalidOperationException(message: $"DispatchIndirect did not reproduce the direct dispatch (vulkan match={vulkanMatch}, directX match={directXMatch}).");
        }

        Console.Out.WriteLine(value: $"INDIRECT pass | {RenderSize}x{RenderSize} sdf-child | Dispatch == DispatchIndirect ({Groups}x{Groups}x1) bit-for-bit on Vulkan (vkCmdDispatchIndirect) AND Direct3D 12 (ExecuteIndirect)");
    }
    private T Resolve<T>() => (T)m_serviceProvider.GetService(serviceType: typeof(T))!;

    // Renders sdf-child once directly and once indirectly on one backend and reports whether the two readbacks match
    // exactly (same device, same kernel, same grid — so a correct indirect dispatch is bit-identical, no FP tolerance).
    private static bool DirectMatchesIndirect(IGpuComputeServices gpu, IGpuDeviceContext deviceContext, byte[] bytecode) {
        var direct = Render(bytecode: bytecode, deviceContext: deviceContext, gpu: gpu, indirect: false);
        var indirect = Render(bytecode: bytecode, deviceContext: deviceContext, gpu: gpu, indirect: true);

        return direct.AsSpan().SequenceEqual(other: indirect.AsSpan());
    }

    // One render of sdf-child into a fresh 64x64 storage image via either a direct or an indirect dispatch, read back
    // to host pixels. Drives only the neutral compute seam, so the identical code runs on whichever backend gpu wraps.
    private static byte[] Render(IGpuComputeServices gpu, IGpuDeviceContext deviceContext, byte[] bytecode, bool indirect) {
        var deviceHandle = deviceContext.DeviceHandle;
        var push = new byte[PushByteLength];
        var pushWords = MemoryMarshal.Cast<byte, uint>(span: push.AsSpan());

        // ChildParams: extent = (64, 64); time = 0 (bit pattern 0); pad = 0.
        pushWords[0] = RenderSize;
        pushWords[1] = RenderSize;

        GpuComputeBinding[] bindings = [new GpuComputeBinding(Binding: 0, Kind: GpuComputeBindingKind.StorageImage)];

        using var shaderModule = gpu.ShaderModuleFactory.Create(deviceContext: deviceContext, stage: GpuShaderStage.Compute, bytecode: bytecode);
        using var pipeline = gpu.ComputePipelineFactory.Create(
            bindings: bindings,
            computeShaderModule: shaderModule,
            deviceContext: deviceContext,
            pushConstantBinding: new GpuPushConstantBinding(data: push, offset: 0, stageFlags: GpuShaderStage.Compute)
        );
        using var image = gpu.StorageImageFactory.Create(deviceContext: deviceContext, format: Format, height: RenderSize, width: RenderSize);
        using var commandPool = gpu.CommandPoolFactory.Create(deviceContext: deviceContext);
        var argumentBuffer = (indirect ? gpu.StorageBufferFactory.CreateIndirectArgs(deviceContext: deviceContext, sizeBytes: 12) : null);

        var poolSizes = GpuDescriptorPoolSizes.ForSets(bindings);
        var pool = gpu.DescriptorAllocator.CreatePool(deviceHandle: deviceHandle, sizes: poolSizes);

        try {
            argumentBuffer?.Write<uint>(data: [Groups, Groups, 1]);

            var set = gpu.DescriptorAllocator.AllocateSet(descriptorSetLayoutHandle: pipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: pool);

            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: 0, descriptorSetHandle: set, deviceHandle: deviceHandle, imageViewHandle: image.ImageViewHandle);

            var recorder = gpu.ComputeRecorder;
            var commandBuffer = commandPool.CommandBufferHandle;

            recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);
            recorder.TransitionImageLayout(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.ShaderWrite,
                destinationStageMask: GpuComputeStage.ComputeShader,
                deviceHandle: deviceHandle,
                imageHandle: image.ImageHandle,
                newLayout: GpuImageLayout.General,
                oldLayout: GpuImageLayout.Undefined,
                sourceAccessMask: GpuComputeAccess.None,
                sourceStageMask: GpuComputeStage.TopOfPipe
            );
            recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, pipelineHandle: pipeline.Handle);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: set, deviceHandle: deviceHandle, pipelineLayoutHandle: pipeline.LayoutHandle);
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: push, deviceHandle: deviceHandle, offset: 0, pipelineLayoutHandle: pipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);

            if (indirect) {
                recorder.DispatchIndirect(argumentBufferHandle: argumentBuffer!.BufferHandle, argumentBufferOffset: 0, commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);
            } else {
                recorder.Dispatch(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, groupCountX: Groups, groupCountY: Groups, groupCountZ: 1);
            }

            recorder.TransitionImageLayout(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuComputeAccess.ShaderRead,
                destinationStageMask: GpuComputeStage.FragmentShader,
                deviceHandle: deviceHandle,
                imageHandle: image.ImageHandle,
                newLayout: GpuImageLayout.ShaderReadOnly,
                oldLayout: GpuImageLayout.General,
                sourceAccessMask: GpuComputeAccess.ShaderWrite,
                sourceStageMask: GpuComputeStage.ComputeShader
            );
            recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);

            gpu.QueueSubmitter.SubmitAndWait(commandBufferHandles: [commandBuffer], deviceContext: deviceContext);

            using var readback = gpu.SurfaceTransferFactory.CreateReadback(deviceContext: deviceContext);

            // Retain a copy: the readback buffer is reused across calls (direct vs indirect read the same backend).
            return readback.Read(
                bytesPerPixel: 4,
                deviceContext: deviceContext,
                format: GpuPixelFormat.R8G8B8A8Unorm,
                height: RenderSize,
                sourceImageHandle: image.ImageHandle,
                width: RenderSize
            ).ToArray();
        } finally {
            gpu.DescriptorAllocator.DestroyPool(deviceHandle: deviceHandle, poolHandle: pool);
            argumentBuffer?.Dispose();
        }
    }
}
