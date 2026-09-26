using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Windowing;
using Puck.DirectX;
using Puck.DirectX.Apis;
using Puck.DirectX.Interop;
using Puck.Memory;
using Puck.Testing;
using Puck.Vulkan;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Presentation;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// The device half of the bake sampling check. Each texture of <c>tests/Puck.SignedDistance.Tests/Fixtures/bake-sampling.json</c>
/// (a real bake's BC7 albedo, BC5 normal and BC6H emission, every mip level) is uploaded through the device's one image
/// upload (<see cref="IGpuSurfaceUpload"/>) as an image of its block-compressed format with all its levels, and the probe
/// kernel (<c>Assets/Shaders/bake-sampling.comp.hlsl</c>) samples each probe texel's center at its level through a point
/// sampler. The value read must match what the CPU decoder reads there, under the tolerance the fixture's law states:
/// BC7 within half a code of the expected code over 255, BC5 within one code (a device interpolates BC4 in float), and
/// BC6H exactly the expected half. It runs on the first Vulkan device with a graphics queue, on the first Direct3D 12
/// hardware adapter and on the software (WARP) renderer, and skips by name where the host has none.
/// </summary>
[SupportedOSPlatform("windows10.0.15063")]
public sealed class BakeSamplingDeviceLawTests {
    private const string KernelName = "bake-sampling.comp";

    [Fact]
    public void EveryProbeSampledOnAVulkanDeviceIsWhatTheDecoderReads() {
        using var device = HeadlessVulkanDevice.Create();

        Probe(
            backend: $"vulkan ({device.Name})",
            kernel: Kernel(extension: ".spv"),
            services: device.Services
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void EveryProbeSampledOnADirect3D12DeviceIsWhatTheDecoderReads(bool warp) {
        var context = new DirectXDeviceContext(
            adapterLuid: 0L,
            deviceApi: (warp
                ? new WarpDeviceApi()
                : new DirectXNativeDeviceApi()),
            minimumFeatureLevel: DirectXFeatureLevel.Level110
        );

        using (context) {
            try {
                _ = context.Device;
            } catch (GpuDeviceUnavailableException exception) {
                Assert.Skip(reason: $"no Direct3D 12 {(warp ? "WARP" : "hardware")} device on this host: {exception.Message}");
            }

            Probe(
                backend: (warp ? "directx (WARP)" : "directx"),
                kernel: Kernel(extension: ".dxil"),
                services: context.Services
            );
        }
    }

    private static byte[] Kernel(string extension) =>
        File.ReadAllBytes(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Assets", path3: "Shaders", path4: (KernelName + extension)));
    // Uploads each fixture texture with every level, samples its probes, and holds each value read to the fixture.
    private static void Probe(string backend, byte[] kernel, GpuDeviceServices services) {
        var fixture = JsonNode.Parse(json: File.ReadAllText(path: Path.Combine(path1: AppContext.BaseDirectory, path2: "Fixtures", path3: "bake-sampling.json")))!;
        var bindings = services.Bindings;
        var description = new GpuComputePipelineDescription(
            Bindings: [
                new GpuComputeBinding(Binding: 0U, Kind: GpuComputeBindingKind.SampledImage),
                new GpuComputeBinding(Binding: 1U, Kind: GpuComputeBindingKind.StorageImage),
            ],
            Name: KernelName,
            PushConstantBinding: new GpuPushConstantBinding(
                data: new byte[(4 * sizeof(uint))],
                offset: 0U,
                stageFlags: GpuShaderStage.Compute
            ),
            SamplerFilter: GpuSamplerFilter.Nearest
        );
        using var module = services.ShaderModuleFactory.Create(
            bytecode: kernel,
            stage: GpuShaderStage.Compute
        );
        using var pipeline = services.PipelineFactory.Create(
            computeShaderModule: module,
            description: description,
            name: default
        );
        using var readback = services.SurfaceTransferFactory.CreateReadback();
        var sampler = bindings.CreateSampler(filter: GpuSamplerFilter.Nearest);
        var failures = new List<string>();

        try {
            foreach (var texture in fixture["textures"]!.AsArray()) {
                SampleTexture(
                    backend: backend,
                    bindings: bindings,
                    description: description,
                    failures: failures,
                    pipeline: pipeline,
                    readback: readback,
                    sampler: sampler,
                    services: services,
                    texture: texture!
                );
            }
        } finally {
            bindings.DestroySampler(samplerHandle: sampler);
        }

        Assert.True(
            condition: (failures.Count == 0),
            userMessage: string.Join(separator: Environment.NewLine, values: failures)
        );
    }
    private static void SampleTexture(string backend, GpuDeviceServices services, IGpuBindings bindings, GpuComputePipelineDescription description, IGpuComputePipeline pipeline, IGpuSurfaceReadback readback, nint sampler, JsonNode texture, List<string> failures) {
        // The fixture names its formats in the baker's TextureFormat vocabulary, whose block-compressed members carry
        // the same names as GpuPixelFormat's until the pixel-format fold makes them one.
        var format = Enum.Parse<GpuPixelFormat>(value: texture["format"]!.GetValue<string>());
        var width = texture["width"]!.GetValue<uint>();
        var height = texture["height"]!.GetValue<uint>();
        var levels = texture["levels"]!.AsArray().Select(selector: static level => Convert.FromBase64String(s: level!.GetValue<string>())).ToArray();
        var probes = texture["probes"]!.AsArray();
        var recorder = services.Recorder;
        using var upload = services.SurfaceTransferFactory.CreateUpload();
        var view = upload.Upload(
            format: format,
            height: height,
            levels: ((uint)levels.Length),
            pixels: levels.SelectMany(selector: static level => level).ToArray(),
            width: width
        );
        using var output = services.ImageFactory.Create(
            format: GpuPixelFormat.R32G32B32A32Float,
            height: 1U,
            name: default,
            usage: GpuImageUsage.Storage,
            width: ((uint)probes.Count)
        );
        using var commands = services.CommandPoolFactory.Create(name: default);
        var pool = bindings.CreatePool(
            name: default,
            sizes: GpuDescriptorPoolSizes.ForSets(description.Bindings)
        );
        ReadOnlyMemory<byte> values;

        try {
            var set = bindings.AllocateSet(
                descriptorSetLayoutHandle: pipeline.DescriptorSetLayoutHandle,
                name: default,
                poolHandle: pool
            );
            var command = commands.CommandBufferHandle;

            bindings.WriteCombinedImageSampler(
                arrayElement: 0U,
                binding: 0U,
                descriptorSetHandle: set,
                imageViewHandle: view,
                samplerHandle: sampler
            );
            bindings.WriteStorageImage(
                arrayElement: 0U,
                binding: 1U,
                descriptorSetHandle: set,
                imageViewHandle: output.ImageViewHandle
            );
            recorder.BeginCommandBuffer(commandBufferHandle: command);
            recorder.TransitionImageLayout(
                commandBufferHandle: command,
                destinationAccessMask: GpuAccess.ShaderWrite,
                destinationStageMask: GpuStage.ComputeShader,
                imageHandle: output.ImageHandle,
                newLayout: GpuImageLayout.General,
                oldLayout: GpuImageLayout.Undefined,
                sourceAccessMask: GpuAccess.None,
                sourceStageMask: GpuStage.TopOfPipe
            );
            recorder.BindPipeline(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: command,
                pipelineHandle: pipeline.Handle
            );
            recorder.BindDescriptorSet(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: command,
                descriptorSetHandle: set,
                group: 0U,
                pipelineLayoutHandle: pipeline.LayoutHandle
            );

            for (var slot = 0; (slot < probes.Count); slot++) {
                var probe = probes[slot]!;
                ReadOnlySpan<uint> push = [probe["x"]!.GetValue<uint>(), probe["y"]!.GetValue<uint>(), probe["level"]!.GetValue<uint>(), ((uint)slot)];

                recorder.PushConstants(
                    bindPoint: GpuBindPoint.Compute,
                    commandBufferHandle: command,
                    data: MemoryMarshal.AsBytes(span: push),
                    offset: 0U,
                    pipelineLayoutHandle: pipeline.LayoutHandle,
                    stageFlags: GpuShaderStage.Compute
                );
                recorder.Dispatch(
                    commandBufferHandle: command,
                    groupCountX: 1U,
                    groupCountY: 1U,
                    groupCountZ: 1U
                );
            }

            recorder.EndCommandBuffer(commandBufferHandle: command);
            services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);
            values = readback.Read(
                bytesPerPixel: 16U,
                format: GpuPixelFormat.R32G32B32A32Float,
                height: 1U,
                sourceImageHandle: output.ImageHandle,
                sourceLayout: GpuImageLayout.General,
                width: ((uint)probes.Count)
            );
        } finally {
            bindings.DestroyPool(poolHandle: pool);
        }

        var read = MemoryMarshal.Cast<byte, float>(span: values.Span);

        for (var slot = 0; (slot < probes.Count); slot++) {
            var probe = probes[slot]!;
            var expected = probe["expected"]!.AsArray().Select(selector: static value => value!.GetValue<int>()).ToArray();
            var actual = read.Slice(length: 4, start: (slot * 4)).ToArray();

            if (!Matches(actual: actual, expected: expected, format: format)) {
                failures.Add(item: $"{backend}: {format} level {probe["level"]} texel ({probe["x"]}, {probe["y"]}) read [{string.Join(separator: ", ", values: actual)}], expected [{string.Join(separator: ", ", values: expected)}]");
            }
        }
    }
    // BC7 and BC5 expectations are unorm codes; BC6H's are half bits.
    private static bool Matches(float[] actual, int[] expected, GpuPixelFormat format) {
        for (var channel = 0; (channel < expected.Length); channel++) {
            var matches = format switch {
                GpuPixelFormat.Bc7Unorm => (Math.Abs(value: ((actual[channel] * 255.0) - expected[channel])) <= 0.5),
                GpuPixelFormat.Bc5Unorm => (Math.Abs(value: ((actual[channel] * 255.0) - expected[channel])) <= 1.0),
                GpuPixelFormat.Bc6hUfloat => (BitConverter.HalfToUInt16Bits(value: ((Half)actual[channel])) == expected[channel]),
                _ => throw new InvalidDataException(message: $"The fixture holds a {format} texture, which this law has no tolerance for."),
            };

            if (!matches) {
                return false;
            }
        }

        return true;
    }

    // A Vulkan device with no surface: the first physical device with a graphics queue family (a discrete one first),
    // its logical device created by the backend's own factory, and its neutral services created as the renderer creates
    // its own.
    private sealed class HeadlessVulkanDevice : IVulkanDeviceContext, IDisposable {
        private readonly ServiceProvider m_provider;

        private HeadlessVulkanDevice(ServiceProvider provider, VulkanInstance instance, VulkanLogicalDevice device, string name) {
            m_provider = provider;
            Instance = instance;
            LogicalDevice = device;
            Name = name;
            Services = VulkanPresenterServiceRegistration.DeviceServices(serviceProvider: provider)(this);
        }

        public VulkanInstance Instance { get; }
        public VulkanLogicalDevice LogicalDevice { get; }
        public string Name { get; }
        public VkPhysicalDevice PhysicalDevice => LogicalDevice.PhysicalDevice;
        public GpuDeviceServices Services { get; }
        public VulkanSurface Surface => throw new NotSupportedException(message: "A headless device has no surface.");

        public static HeadlessVulkanDevice Create() {
            var provider = new ServiceCollection()
                .AddPuckAllocator()
                .AddVulkanNativeApis()
                .AddVulkanFactories()
                .AddSingleton(implementationInstance: new VulkanRendererOptions { ApplicationName = nameof(BakeSamplingDeviceLawTests), EnableValidation = false })
                .AddSingleton(implementationInstance: new VulkanQueueSubmitter())
                .BuildServiceProvider();
            VulkanInstance? instance = null;

            try {
                try {
                    instance = provider.GetRequiredService<IVulkanInstanceFactory>().Create(
                        applicationName: nameof(BakeSamplingDeviceLawTests),
                        displayKind: NativeDisplayKind.Win32,
                        enableValidation: false
                    );
                } catch (GpuDeviceUnavailableException exception) {
                    Assert.Skip(reason: $"no Vulkan loader or driver: {exception.Message}");
                }

                var physicalDeviceApi = provider.GetRequiredService<IVulkanPhysicalDeviceApi>();
                var candidates = physicalDeviceApi.EnumeratePhysicalDevices(instance: instance.Commands)
                    .Select(selector: handle => (
                        Handle: handle,
                        Type: physicalDeviceApi.GetPhysicalDeviceType(instance: instance.Commands, physicalDeviceHandle: handle),
                        Graphics: physicalDeviceApi.GetQueueFamilies(instance: instance.Commands, physicalDeviceHandle: handle)
                            .FirstOrDefault(predicate: static family => ((0U != family.QueueCount) && (0 != (family.Flags & VkQueueFlags.Graphics))))
                    ))
                    .Where(predicate: static candidate => (0U != candidate.Graphics.QueueCount))
                    .OrderBy(keySelector: static candidate => ((candidate.Type == VkPhysicalDeviceType.DiscreteGpu) ? 0 : 1))
                    .ToArray();

                if (candidates.Length == 0) {
                    Assert.Skip(reason: "no Vulkan device with a graphics queue family on this host");
                }

                var chosen = candidates[0];
                var physicalDevice = new VkPhysicalDevice(
                    deviceType: chosen.Type,
                    handle: chosen.Handle,
                    queueFamilySelection: new VulkanQueueFamilySelection(
                        graphicsFamilyIndex: chosen.Graphics.Index,
                        presentFamilyIndex: chosen.Graphics.Index
                    )
                );
                VulkanLogicalDevice device;

                try {
                    device = provider.GetRequiredService<IVulkanLogicalDeviceFactory>().Create(
                        instance: instance,
                        physicalDevice: physicalDevice
                    );
                } catch (GpuDeviceUnavailableException exception) {
                    Assert.Skip(reason: $"no usable Vulkan device: {exception.Message}");

                    throw;
                }

                return new HeadlessVulkanDevice(
                    device: device,
                    instance: instance,
                    name: physicalDeviceApi.GetDeviceName(instance: instance.Commands, physicalDeviceHandle: chosen.Handle),
                    provider: provider
                );
            } catch {
                instance?.Dispose();
                provider.Dispose();

                throw;
            }
        }
        public void Dispose() {
            LogicalDevice.Dispose();
            Instance.Dispose();
            m_provider.Dispose();
        }
    }
}
