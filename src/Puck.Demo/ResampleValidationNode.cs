using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions;
using Puck.Hosting;
using Puck.Vulkan.Interfaces;

namespace Puck.Demo;

/// <summary>
/// A one-shot root render node installed under <c>--validate-resample</c>. It proves the SAMPLED-IMAGE COMPUTE
/// binding — a read-only texture filtered through a sampler inside a compute kernel (the new
/// <see cref="GpuComputeBindingKind.SampledImage"/>: a Vulkan combined-image-sampler / a Direct3D 12 SRV read
/// through a static root-signature sampler) — works on BOTH backends. Every prior compute kernel reads storage
/// images by integer index; <c>resample.comp</c> is the first to <em>sample</em> one, which is what lets a source of
/// any resolution be scaled/filtered into a differently sized destination.
/// <para>
/// For each backend it renders the neutral <c>sdf-child.comp</c> test pattern into a source image, then runs
/// <c>resample.comp</c> over it two ways:
/// <list type="number">
/// <item>an IDENTITY resample (same extent, <c>srcSize=(1,1)</c>, NEAREST) — which samples each destination pixel
/// center exactly onto its source texel, so the output must be BIT-IDENTICAL to the source. Direct rendering is its
/// own oracle: a broken SRV/sampler/root-signature would diverge.</item>
/// <item>a 2x LINEAR upscale — kept so the two backends' filtered outputs can be compared to each other (≤ ±1 LSB),
/// proving the filtering path agrees cross-backend, not just that each runs.</item>
/// </list>
/// 0 = pass, 2 = infra-fail. It never presents.
/// </para>
/// </summary>
internal sealed class ResampleValidationNode : IRenderNode {
    private const uint Format = GpuPixelFormat.R8G8B8A8Unorm;
    private const uint SourceSize = 64;
    private const uint UpscaleSize = 128;
    private const uint WorkgroupEdge = 8;
    private const int ChildPushByteLength = 16;   // ChildParams    { uint2 extent; float time; uint pad; }
    private const int ResamplePushByteLength = 32; // ResampleParams { uint2 outExtent; float2 srcOrigin; float2 srcSize; uint cellSize; uint quantizeLevels; }

    private readonly NodeDescriptor m_descriptor = new(
        Name: "resample-validation",
        SurfaceId: SurfaceId.New()
    );
    private readonly ParityResult m_result;
    private readonly IServiceProvider m_serviceProvider;
    private bool m_done;

    /// <summary>Initializes a new instance of the <see cref="ResampleValidationNode"/> class.</summary>
    /// <param name="serviceProvider">The application service provider (the live Vulkan device + compute services, and the LUID source for the bespoke Direct3D 12 device).</param>
    /// <param name="result">The shared result the exit code is written to.</param>
    public ResampleValidationNode(IServiceProvider serviceProvider, ParityResult result) {
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
                Console.Out.WriteLine(value: "RESAMPLE skip | Direct3D 12 requires Windows 10.0.10240+");
                m_result.ExitCode = 0;
            } else {
                Validate();
                m_result.ExitCode = 0;
            }
        } catch (Exception exception) {
            Console.Error.WriteLine(value: $"RESAMPLE infra-fail | {exception}");
            m_result.ExitCode = 2;
        }

        if (context.Host.HoldsCapability<ITerminalControl>(capability: out var terminal)) {
            terminal.RequestExit();
        }

        return default;
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private void Validate() {
        var childBase = Path.Combine(path1: CrossBackendShowcase.ShaderDirectory, path2: "sdf-child.comp");
        var resampleBase = Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders", path4: "Resample");
        var resampleFile = Path.Combine(path1: resampleBase, path2: "resample.comp");

        // Vulkan: the live host device + its neutral compute services (SPIR-V kernels).
        var vulkanDevice = (IGpuDeviceContext)Resolve<IVulkanDeviceContext>();
        var vulkan = RunBackend(
            childBytecode: File.ReadAllBytes(path: (childBase + ".spv")),
            deviceContext: vulkanDevice,
            gpu: Resolve<IGpuComputeServices>(),
            resampleBytecode: File.ReadAllBytes(path: (resampleFile + ".spv"))
        );

        // Direct3D 12: a bespoke device LUID-matched to the Vulkan host (DXIL kernels), exactly as the indirect/reverse gates.
        using var directX = new DirectXComputeWorldDevice(hostProvider: m_serviceProvider);
        var directx = RunBackend(
            childBytecode: File.ReadAllBytes(path: (childBase + ".dxil")),
            deviceContext: directX.DeviceContext,
            gpu: (IGpuComputeServices)directX.Services.GetService(serviceType: typeof(IGpuComputeServices))!,
            resampleBytecode: File.ReadAllBytes(path: (resampleFile + ".dxil"))
        );

        if (
            !vulkan.IdentityMatches ||
            !directx.IdentityMatches
        ) {
            throw new InvalidOperationException(message: $"A nearest identity resample did not reproduce its source (vulkan match={vulkan.IdentityMatches}, directX match={directx.IdentityMatches}) — the sampled-image binding is reading the wrong texels.");
        }

        var maxDelta = MaxAbsDelta(a: vulkan.Upscale, b: directx.Upscale);

        if (maxDelta > 1) {
            throw new InvalidOperationException(message: $"The 2x linear upscale diverged across backends by {maxDelta} LSB (> 1) — the compute sampler filters differently on Vulkan vs Direct3D 12.");
        }

        Console.Out.WriteLine(value: $"RESAMPLE pass | {SourceSize}x{SourceSize} sdf-child sampled in compute on Vulkan (combined-image-sampler) AND Direct3D 12 (SRV + static sampler): nearest identity == source bit-for-bit, 2x linear upscale matches cross-backend (≤ {maxDelta} LSB)");
    }
    private T Resolve<T>() => (T)m_serviceProvider.GetService(serviceType: typeof(T))!;

    // Runs both checks on one backend: (a) source vs nearest identity resample (must be bit-identical), and (b) the
    // bytes of a 2x linear upscale (returned for the cross-backend comparison). Drives only the neutral compute seam,
    // so the identical code runs on whichever backend gpu wraps.
    private static (bool IdentityMatches, byte[] Upscale) RunBackend(IGpuComputeServices gpu, IGpuDeviceContext deviceContext, byte[] childBytecode, byte[] resampleBytecode) {
        var identity = Resample(childBytecode: childBytecode, deviceContext: deviceContext, filter: GpuSamplerFilter.Nearest, gpu: gpu, outSize: SourceSize, resampleBytecode: resampleBytecode);
        var upscale = Resample(childBytecode: childBytecode, deviceContext: deviceContext, filter: GpuSamplerFilter.Linear, gpu: gpu, outSize: UpscaleSize, resampleBytecode: resampleBytecode);

        return (identity.Source.AsSpan().SequenceEqual(other: identity.Output.AsSpan()), upscale.Output);
    }

    // Renders sdf-child into a SourceSize source image, then resamples it into an outSize output through the compute
    // sampler at the given filter, reading BOTH images back. One command buffer: fill source → make it shader-readable
    // → resample (sample source, write output) → make output readable → submit-and-wait → read both.
    private static (byte[] Source, byte[] Output) Resample(IGpuComputeServices gpu, IGpuDeviceContext deviceContext, byte[] childBytecode, byte[] resampleBytecode, uint outSize, uint filter) {
        var deviceHandle = deviceContext.DeviceHandle;

        var childPush = new byte[ChildPushByteLength];
        var childWords = MemoryMarshal.Cast<byte, uint>(span: childPush.AsSpan());

        childWords[0] = SourceSize;
        childWords[1] = SourceSize; // ChildParams.extent; time (word 2) and pad (word 3) stay 0.

        var resamplePush = new byte[ResamplePushByteLength];
        var resampleWords = MemoryMarshal.Cast<byte, uint>(span: resamplePush.AsSpan());
        var resampleFloats = MemoryMarshal.Cast<byte, float>(span: resamplePush.AsSpan());

        resampleWords[0] = outSize;
        resampleWords[1] = outSize; // outExtent
        resampleFloats[2] = 0f;
        resampleFloats[3] = 0f;     // srcOrigin
        resampleFloats[4] = 1f;
        resampleFloats[5] = 1f;     // srcSize (whole source)
        resampleWords[6] = 1;       // cellSize: 1 = no pixelation
        resampleWords[7] = 0;       // quantizeLevels: 0 = no quantization

        GpuComputeBinding[] childBindings = [new GpuComputeBinding(Binding: 0, Kind: GpuComputeBindingKind.StorageImage)];
        GpuComputeBinding[] resampleBindings = [
            new GpuComputeBinding(Binding: 0, Kind: GpuComputeBindingKind.StorageImage),
            new GpuComputeBinding(Binding: 1, Kind: GpuComputeBindingKind.SampledImage),
        ];

        using var childModule = gpu.ShaderModuleFactory.Create(deviceContext: deviceContext, stage: GpuShaderStage.Compute, bytecode: childBytecode);
        using var resampleModule = gpu.ShaderModuleFactory.Create(deviceContext: deviceContext, stage: GpuShaderStage.Compute, bytecode: resampleBytecode);
        using var childPipeline = gpu.ComputePipelineFactory.Create(
            bindings: childBindings,
            computeShaderModule: childModule,
            deviceContext: deviceContext,
            pushConstantBinding: new GpuPushConstantBinding(data: childPush, offset: 0, stageFlags: GpuShaderStage.Compute)
        );
        using var resamplePipeline = gpu.ComputePipelineFactory.Create(
            bindings: resampleBindings,
            computeShaderModule: resampleModule,
            deviceContext: deviceContext,
            pushConstantBinding: new GpuPushConstantBinding(data: resamplePush, offset: 0, stageFlags: GpuShaderStage.Compute),
            samplerFilter: filter
        );
        using var sourceImage = gpu.StorageImageFactory.Create(deviceContext: deviceContext, format: Format, height: SourceSize, width: SourceSize);
        using var outputImage = gpu.StorageImageFactory.Create(deviceContext: deviceContext, format: Format, height: outSize, width: outSize);
        using var commandPool = gpu.CommandPoolFactory.Create(deviceContext: deviceContext);

        var sampler = gpu.DescriptorAllocator.CreateSampler(deviceHandle: deviceHandle, filter: filter);
        var poolSizes = GpuDescriptorPoolSizes.ForSets(childBindings, resampleBindings);
        var pool = gpu.DescriptorAllocator.CreatePool(
            combinedImageSamplerCount: poolSizes.CombinedImageSamplerCount,
            deviceHandle: deviceHandle,
            maxSets: poolSizes.MaxSets,
            storageBufferCount: poolSizes.StorageBufferCount,
            storageImageCount: poolSizes.StorageImageCount
        );

        try {
            var childSet = gpu.DescriptorAllocator.AllocateSet(descriptorSetLayoutHandle: childPipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: pool);
            var resampleSet = gpu.DescriptorAllocator.AllocateSet(descriptorSetLayoutHandle: resamplePipeline.DescriptorSetLayoutHandle, deviceHandle: deviceHandle, poolHandle: pool);

            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: 0, descriptorSetHandle: childSet, deviceHandle: deviceHandle, imageViewHandle: sourceImage.ImageViewHandle);
            gpu.DescriptorAllocator.WriteStorageImage(arrayElement: 0, binding: 0, descriptorSetHandle: resampleSet, deviceHandle: deviceHandle, imageViewHandle: outputImage.ImageViewHandle);
            gpu.DescriptorAllocator.WriteCombinedImageSampler(arrayElement: 0, binding: 1, descriptorSetHandle: resampleSet, deviceHandle: deviceHandle, imageViewHandle: sourceImage.ImageViewHandle, samplerHandle: sampler);

            var recorder = gpu.ComputeRecorder;
            var commandBuffer = commandPool.CommandBufferHandle;
            var childGroups = ((SourceSize + (WorkgroupEdge - 1)) / WorkgroupEdge);
            var outputGroups = ((outSize + (WorkgroupEdge - 1)) / WorkgroupEdge);

            recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);

            // Fill the source with sdf-child (writes it in the General/UAV working layout).
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderWrite, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, imageHandle: sourceImage.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuComputeAccess.None, sourceStageMask: GpuComputeStage.TopOfPipe);
            recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, pipelineHandle: childPipeline.Handle);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: childSet, deviceHandle: deviceHandle, pipelineLayoutHandle: childPipeline.LayoutHandle);
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: childPush, deviceHandle: deviceHandle, offset: 0, pipelineLayoutHandle: childPipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recorder.Dispatch(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, groupCountX: childGroups, groupCountY: childGroups, groupCountZ: 1);

            // Make the source shader-readable so the resample kernel can SAMPLE it; this barrier also orders the fill's
            // writes before the sample's reads (compute → compute).
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, imageHandle: sourceImage.ImageHandle, newLayout: GpuImageLayout.ShaderReadOnly, oldLayout: GpuImageLayout.General, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader);

            // Bring the output into the General/UAV working layout for the resample to write.
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderWrite, destinationStageMask: GpuComputeStage.ComputeShader, deviceHandle: deviceHandle, imageHandle: outputImage.ImageHandle, newLayout: GpuImageLayout.General, oldLayout: GpuImageLayout.Undefined, sourceAccessMask: GpuComputeAccess.None, sourceStageMask: GpuComputeStage.TopOfPipe);
            recorder.BindComputePipeline(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, pipelineHandle: resamplePipeline.Handle);
            recorder.BindComputeDescriptorSet(commandBufferHandle: commandBuffer, descriptorSetHandle: resampleSet, deviceHandle: deviceHandle, pipelineLayoutHandle: resamplePipeline.LayoutHandle);
            recorder.PushConstants(commandBufferHandle: commandBuffer, data: resamplePush, deviceHandle: deviceHandle, offset: 0, pipelineLayoutHandle: resamplePipeline.LayoutHandle, stageFlags: GpuShaderStage.Compute);
            recorder.Dispatch(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle, groupCountX: outputGroups, groupCountY: outputGroups, groupCountZ: 1);

            // Make both images readback-readable.
            recorder.TransitionImageLayout(commandBufferHandle: commandBuffer, destinationAccessMask: GpuComputeAccess.ShaderRead, destinationStageMask: GpuComputeStage.FragmentShader, deviceHandle: deviceHandle, imageHandle: outputImage.ImageHandle, newLayout: GpuImageLayout.ShaderReadOnly, oldLayout: GpuImageLayout.General, sourceAccessMask: GpuComputeAccess.ShaderWrite, sourceStageMask: GpuComputeStage.ComputeShader);
            recorder.EndCommandBuffer(commandBufferHandle: commandBuffer, deviceHandle: deviceHandle);

            gpu.QueueSubmitter.SubmitAndWait(commandBufferHandles: [commandBuffer], deviceContext: deviceContext);

            using var readback = gpu.SurfaceTransferFactory.CreateReadback(deviceContext: deviceContext);

            var source = readback.Read(bytesPerPixel: 4, deviceContext: deviceContext, format: GpuPixelFormat.R8G8B8A8Unorm, height: SourceSize, sourceImageHandle: sourceImage.ImageHandle, width: SourceSize).ToArray();
            var output = readback.Read(bytesPerPixel: 4, deviceContext: deviceContext, format: GpuPixelFormat.R8G8B8A8Unorm, height: outSize, sourceImageHandle: outputImage.ImageHandle, width: outSize).ToArray();

            return (source, output);
        } finally {
            gpu.DescriptorAllocator.DestroySampler(deviceHandle: deviceHandle, samplerHandle: sampler);
            gpu.DescriptorAllocator.DestroyPool(deviceHandle: deviceHandle, poolHandle: pool);
        }
    }
    private static int MaxAbsDelta(byte[] a, byte[] b) {
        if (a.Length != b.Length) {
            return int.MaxValue;
        }

        var max = 0;

        for (var index = 0; (index < a.Length); index++) {
            max = Math.Max(max, Math.Abs(a[index] - b[index]));
        }

        return max;
    }
}
