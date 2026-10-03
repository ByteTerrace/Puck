using System.Buffers.Binary;
using System.Runtime.Versioning;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the shipped sky pass (<c>sdf-sky-runs.comp</c>) evaluates the sky once for a pixel the lit image
/// leaves uncovered and never for one it covers, with the one-pixel dilation that keeps every texel beside an edge
/// evaluated: a pixel is evaluated when it or one of its eight neighbours has coverage below one. It counts each pixel it
/// evaluates as one <c>gpu.sky.evaluations</c>, and writes every pixel's texel, an unevaluated one's as a zero base the
/// composite filters out, counting each as one texel written. On a 16x8 lit image, every pixel covered evaluates nothing,
/// every pixel uncovered evaluates 128, and the left half covered evaluates the right half and the covered column beside
/// it, 72; each writes 128 texels. The lit image is read as written for every pixel, and every binding the pass does not
/// read holds a filler of its kind.
/// </summary>
[SupportedOSPlatform("windows10.0.15063")]
[Trait("Category", "Gpu")]
public sealed class SdfSkyEvaluationDeviceLawTests {
    private const uint Width = 16;
    private const uint Height = 8;

    [Fact]
    public void VulkanEvaluatesTheSkyOnlyWhereTheLitImageLeavesItUncovered() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfSkyEvaluationDeviceLawTests));

        Verify(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXEvaluatesTheSkyOnlyWhereTheLitImageLeavesItUncovered() {
        using var device = DirectXTestDevices.Hardware();

        Verify(services: device.Services, extension: ".dxil");
    }

    private static void Verify(GpuDeviceServices services, string extension) {
        Assert.Equal(expected: (0L, 128L), actual: Run(covered: static _ => true, extension: extension, services: services));
        Assert.Equal(expected: (128L, 128L), actual: Run(covered: static _ => false, extension: extension, services: services));
        // Columns 0 to 7 covered: column 7 sees column 8 beside it, so columns 7 to 15 evaluate, nine of eight pixels.
        Assert.Equal(expected: (72L, 128L), actual: Run(covered: static x => (x < 8), extension: extension, services: services));
    }
    // Runs the sky pass once over a lit image whose column x is covered when covered(x), and returns its counted sky
    // evaluations and texels written.
    private static (long Evaluations, long Texels) Run(GpuDeviceServices services, string extension, Func<uint, bool> covered) {
        var parameters = SdfWorldInterfaces.SkyParameters;
        var block = new byte[parameters.SizeBytes];

        parameters.WriteExtent(block: block, height: Height, width: Width);
        void Word(string member, uint value, int lane = 0) => BinaryPrimitives.WriteUInt32LittleEndian(
            destination: block.AsSpan(start: (((int)parameters.BlockOffsetOf(member: member)) + (lane * 4))), value: value);
        Word(member: SdfWorldPackage.ImageExtent, value: Width);
        Word(lane: 1, member: SdfWorldPackage.ImageExtent, value: Height);
        Word(member: SdfWorldPackage.ResolvedSurface, value: 1u);
        var lit = new byte[((Width * Height) * 4)];

        for (var y = 0u; (y < Height); y++) {
            for (var x = 0u; (x < Width); x++) {
                lit[((((y * Width) + x) * 4) + 3)] = (covered(arg: x) ? ((byte)255) : ((byte)0));
            }
        }
        var layout = parameters.Layout.PipelineLayout(stages: GpuShaderStage.Compute);
        var description = new GpuComputePipelineDescription(Bindings: [], Layout: layout, Name: "sdf-sky-runs-proof", PushConstantBinding: null);
        using var module = services.ShaderModuleFactory.Create(stage: GpuShaderStage.Compute,
            bytecode: File.ReadAllBytes(path: Path.Combine(path1: SdfKernelSet.DefaultDirectory, path2: ("sdf-sky-runs.comp" + extension))));
        using var pipeline = services.PipelineFactory.Create(computeShaderModule: module, description: description, name: default);
        var padded = new byte[((((((ulong)block.Length) + IGpuBindings.ConstantBufferAlignment) - 1UL) / IGpuBindings.ConstantBufferAlignment) * IGpuBindings.ConstantBufferAlignment)];

        block.CopyTo(array: padded, index: 0);
        using var constants = services.BufferFactory.CreateHostVisible(data: padded, name: default, usage: GpuBufferUsage.Uniform);
        using var fillerBlock = services.BufferFactory.CreateHostVisible(data: new byte[padded.Length], name: default, usage: GpuBufferUsage.Uniform);
        using var fillerBuffer = services.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: 4096, usage: GpuBufferUsage.Storage);
        using var fillerStorage = services.ImageFactory.Create(format: GpuPixelFormat.R16G16B16A16Float, height: 1, name: default, usage: GpuImageUsage.Storage, width: 1);
        using var counters = services.BufferFactory.CreateDeviceLocal(name: default, sizeBytes: ((ulong)GpuKernelCounters.RowBytes), usage: GpuBufferUsage.Storage);
        using var counted = services.BufferFactory.CreateReadback(name: default, sizeBytes: ((ulong)GpuKernelCounters.RowBytes));
        // The runs the pass writes, at the output extent, so every write lands in an image.
        IGpuImage RunImage() => services.ImageFactory.Create(format: GpuPixelFormat.R16G16B16A16Float, height: Height, name: default, usage: GpuImageUsage.Storage, width: Width);
        using var skyBase = RunImage();
        using var skyScale = RunImage();
        using var skyOffset = RunImage();
        using var upload = services.SurfaceTransferFactory.CreateUpload();
        var litView = upload.Upload(pixels: lit, format: GpuPixelFormat.R8G8B8A8Unorm, width: Width, height: Height);
        using var fillerUpload = services.SurfaceTransferFactory.CreateUpload();
        var fillerSampled = fillerUpload.Upload(pixels: new byte[4], format: GpuPixelFormat.R8G8B8A8Unorm, width: 1, height: 1);
        var sampler = services.Bindings.CreateSampler();
        using var commands = services.CommandPoolFactory.Create(name: default);
        var pool = services.Bindings.CreatePool(name: default, sizes: GpuDescriptorPoolSizes.ForGroups(groups: layout.Groups));

        try {
            uint Binding(string member) => SdfKernelInterfaces.BindingOf(layout: parameters.Layout, member: member);
            var images = new Dictionary<uint, nint> {
                [Binding(member: SdfWorldPackage.LitImage)] = litView,
                [Binding(member: SdfWorldPackage.SkyBaseWritten)] = skyBase.ImageViewHandle,
                [Binding(member: SdfWorldPackage.SkyScaleWritten)] = skyScale.ImageViewHandle,
                [Binding(member: SdfWorldPackage.SkyOffsetWritten)] = skyOffset.ImageViewHandle,
            };
            var counterBinding = Binding(member: ShaderWorkCounters.Buffer);
            var sets = new List<(uint Group, nint Set)>();

            foreach (var group in parameters.Layout.Groups) {
                var set = services.Bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[((int)group.Set)], name: default, poolHandle: pool);
                var pass = (group.Group == ShaderInterfaceGroup.Pass);

                sets.Add(item: (group.Set, set));
                foreach (var binding in group.Bindings.Where(predicate: static binding => !binding.Pushed)) {
                    for (var element = 0u; (element < binding.Count); element++) {
                        switch (binding.Kind) {
                            case GpuBindingKind.ConstantBuffer:
                                var buffer = (pass ? constants : fillerBlock);

                                services.Bindings.WriteConstantBuffer(descriptorSetHandle: set, binding: binding.Binding, arrayElement: element, bufferHandle: buffer.BufferHandle, bufferSize: buffer.SizeBytes);
                                break;
                            case GpuBindingKind.SampledImage:
                                services.Bindings.WriteSampledImage(descriptorSetHandle: set, binding: binding.Binding, arrayElement: element,
                                    imageViewHandle: ((pass && images.TryGetValue(key: binding.Binding, value: out var view)) ? view : fillerSampled));
                                break;
                            case GpuBindingKind.StorageImage:
                                services.Bindings.WriteStorageImage(descriptorSetHandle: set, binding: binding.Binding, arrayElement: element,
                                    imageViewHandle: ((pass && images.TryGetValue(key: binding.Binding, value: out var image)) ? image : fillerStorage.ImageViewHandle));
                                break;
                            case GpuBindingKind.Sampler:
                                services.Bindings.WriteSampler(descriptorSetHandle: set, binding: binding.Binding, arrayElement: element, samplerHandle: sampler);
                                break;
                            default:
                                var storage = ((pass && (binding.Binding == counterBinding)) ? counters : fillerBuffer);

                                services.Bindings.WriteBuffer(descriptorSetHandle: set, binding: binding.Binding, bufferHandle: storage.BufferHandle,
                                    bufferSize: storage.SizeBytes, kind: binding.Kind, elementStride: binding.ElementStride);
                                break;
                        }
                    }
                }
            }
            var recorder = services.Recorder;
            var command = commands.CommandBufferHandle;

            recorder.BeginCommandBuffer(commandBufferHandle: command);
            foreach (var image in new[] { skyBase, skyScale, skyOffset, fillerStorage }) {
                recorder.TransitionImageLayout(commandBufferHandle: command, imageHandle: image.ImageHandle,
                    sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe, oldLayout: GpuImageLayout.Undefined,
                    destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader, newLayout: GpuImageLayout.General);
            }
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: counters.BufferHandle,
                sourceAccessMask: GpuAccess.None, sourceStageMask: GpuStage.TopOfPipe, destinationAccessMask: GpuAccess.TransferWrite, destinationStageMask: GpuStage.Transfer);
            recorder.ClearStorageBuffer(commandBufferHandle: command, bufferHandle: counters.BufferHandle, sizeBytes: counters.SizeBytes);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: counters.BufferHandle,
                sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer, destinationAccessMask: GpuAccess.ShaderWrite, destinationStageMask: GpuStage.ComputeShader);
            recorder.BindPipeline(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, pipelineHandle: pipeline.Handle);
            foreach (var (group, set) in sets) {
                recorder.BindDescriptorSet(bindPoint: GpuBindPoint.Compute, commandBufferHandle: command, descriptorSetHandle: set, group: group, pipelineLayoutHandle: pipeline.LayoutHandle);
            }
            recorder.Dispatch(commandBufferHandle: command, groupCountX: ((Width + 7) / 8), groupCountY: ((Height + 7) / 8), groupCountZ: 1);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: counters.BufferHandle,
                sourceAccessMask: GpuAccess.ShaderWrite, sourceStageMask: GpuStage.ComputeShader, destinationAccessMask: GpuAccess.TransferRead, destinationStageMask: GpuStage.Transfer);
            recorder.CopyBuffer(commandBufferHandle: command, destinationBufferHandle: counted.BufferHandle, sizeBytes: counters.SizeBytes, sourceBufferHandle: counters.BufferHandle);
            recorder.TransitionBuffer(commandBufferHandle: command, bufferHandle: counted.BufferHandle,
                sourceAccessMask: GpuAccess.TransferWrite, sourceStageMask: GpuStage.Transfer, destinationAccessMask: GpuAccess.HostRead, destinationStageMask: GpuStage.Host);
            recorder.EndCommandBuffer(commandBufferHandle: command);
            services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);
            var words = new byte[GpuKernelCounters.RowBytes];

            counted.Read(destination: words);
            // The row holds each kernel kind as a 64-bit count, in GpuWork.KernelKinds order: steps, texels, then sky.
            long Count(int kind) => BinaryPrimitives.ReadInt64LittleEndian(source: words.AsSpan(start: ((kind * GpuKernelCounters.CountWords) * sizeof(uint))));

            return (Evaluations: Count(kind: 2), Texels: Count(kind: 1));
        } finally {
            services.Bindings.DestroyPool(poolHandle: pool);
            services.Bindings.DestroySampler(samplerHandle: sampler);
        }
    }
}
