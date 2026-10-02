using System.Runtime.InteropServices;
using Puck.Abstractions.Gpu;
using Puck.SdfVm;
using Puck.Shaders;
using Puck.Testing;
using Xunit;

namespace Puck.World.Tests;

public sealed partial class SdfResolveDeviceLawTests {
    private const uint TemporalIdentity = (1u << 30) | 13u;

    private sealed record TemporalCase(uint Frames = 7, float Reactivity = 0, uint Identity = 0, float Distance = 1000,
        float HistoryValue = 0.75f, float PreviousLensX = 0, bool SplitIdentity = false, float JitterX = 0, uint Debug = 0, bool PremultipliedCoverage = false);

    [Fact]
    public void VulkanTemporalResolveRejectsAndRectifiesHistory() {
        using var device = HeadlessVulkanDevice.Create(applicationName: nameof(SdfResolveDeviceLawTests));

        VerifyTemporal(services: device.Services, extension: ".spv");
    }
    [Fact]
    public void DirectXTemporalResolveRejectsAndRectifiesHistory() {
        using var device = DirectXTestDevices.Hardware();

        VerifyTemporal(services: device.Services, extension: ".dxil");
    }

    private static void VerifyTemporal(GpuDeviceServices services, string extension) {
        // At output x30 the source position is7.125: current=1/8, history=3/4, stable weight=7/8.
        Sample(new(), 0.671875f);
        Sample(new(PremultipliedCoverage: true), 0.671875f);
        Sample(new(Frames: 1), 0.4375f);
        Sample(new(Frames: 0), 0.125f);
        Sample(new(Debug: 1), 0.125f);
        Sample(new(Reactivity: 1), 0.125f);
        Sample(new(Reactivity: 0.5f), 0.3984375f);
        Sample(new(Identity: TemporalIdentity), 0.125f);
        Sample(new(HistoryValue: 2), 0.890625f); // Neighborhood rectifies history to its current maximum1.
        Sample(new(HistoryValue: -1), 0.015625f); // And to its current minimum0.
        Sample(new(JitterX: 0.25f), 0.65625f); // Subtract jitter: position6.875, current0; history stays in output coordinates.
        Sample(new(PreviousLensX: (-1f / OutputWidth), SplitIdentity: true), 0.3984375f);
        Sample(new(Identity: TemporalIdentity, Distance: 6), 0.671875f, y: 34);
        Sample(new(Identity: TemporalIdentity, Distance: 60), 0.125f, y: 34);
        Sample(new(Identity: TemporalIdentity, Distance: float.NaN), 0.125f, y: 34);
        // Reset frames must agree bit-for-bit with the spatial kernel over the whole output, at both kinds of visibility.
        var spatial = Run(services: services, extension: extension, sharpness: 0, width: OutputWidth, height: OutputHeight);
        var reset = Run(services: services, extension: extension, sharpness: 0, width: OutputWidth, height: OutputHeight, temporal: new(Frames: 0));

        Assert.Equal(actual: reset.Color, expected: spatial.Color);

        void Sample(TemporalCase sample, float expected, uint y = 20) {
            var result = Run(extension: extension, height: OutputHeight, services: services, sharpness: 0, temporal: sample, width: OutputWidth);

            Check(color: result.Color, value: expected, width: OutputWidth, x: 30, y: y, alpha: (sample.PremultipliedCoverage ? expected : 1f));
        }
    }

    private sealed class TemporalInputs : IDisposable {
        private readonly GpuDeviceServices m_services;
        private readonly IGpuBuffer m_surface;
        private readonly IGpuBuffer m_world;
        private readonly IGpuSurfaceUpload m_history;
        private readonly IGpuSurfaceUpload m_reactivity;
        private readonly nint m_historyView;
        private readonly nint m_reactivityView;
        private readonly nint m_sampler;

        public TemporalInputs(GpuDeviceServices services, TemporalCase sample, uint width, uint height) {
            m_services = services;
            var surfaces = new uint[((width * height) * 2)];
            var colors = new Half[((width * height) * 4)];

            for (uint y = 0; (y < height); y++) {
                for (uint x = 0; (x < width); x++) {
                    var pixel = ((y * width) + x);

                    surfaces[(pixel * 2)] = BitConverter.SingleToUInt32Bits(value: sample.Distance);
                    surfaces[((pixel * 2) + 1)] = ((sample.SplitIdentity && (x >= 31)) ? TemporalIdentity : sample.Identity);
                    colors[(pixel * 4)] = colors[((pixel * 4) + 1)] = colors[((pixel * 4) + 2)] = ((Half)sample.HistoryValue);
                    colors[((pixel * 4) + 3)] = ((Half)(sample.PremultipliedCoverage ? sample.HistoryValue : 1f));
                }
            }
            m_surface = services.BufferFactory.CreateHostVisible(data: MemoryMarshal.AsBytes(span: surfaces.AsSpan()), usage: GpuBufferUsage.Storage, name: default);
            m_world = services.BufferFactory.CreateHostVisible(data: new byte[4096], name: default, usage: GpuBufferUsage.Storage);
            m_history = services.SurfaceTransferFactory.CreateUpload();
            m_reactivity = services.SurfaceTransferFactory.CreateUpload();
            m_historyView = m_history.Upload(pixels: MemoryMarshal.AsBytes(span: colors.AsSpan()).ToArray(), format: GpuPixelFormat.R16G16B16A16Float, width: width, height: height);
            var reactive = new float[(RenderWidth * RenderHeight)];

            Array.Fill(array: reactive, value: sample.Reactivity);
            m_reactivityView = m_reactivity.Upload(pixels: MemoryMarshal.AsBytes(span: reactive.AsSpan()).ToArray(), format: GpuPixelFormat.R32Float, width: RenderWidth, height: RenderHeight);
            m_sampler = services.Bindings.CreateSampler();
        }

        public nint Bind(ShaderPipelineParameterLayout parameters, IGpuComputePipeline pipeline, nint pool, nint passSet) {
            var bindings = m_services.Bindings;

            uint Binding(string name) => SdfWorldInterfaces.BindingOf(layout: parameters.Layout, member: name);
            bindings.WriteSampledImage(descriptorSetHandle: passSet, binding: Binding(name: SdfWorldPackage.HistoryColor), arrayElement: 0, imageViewHandle: m_historyView);
            bindings.WriteSampledImage(descriptorSetHandle: passSet, binding: Binding(name: SdfWorldPackage.Reactivity), arrayElement: 0, imageViewHandle: m_reactivityView);
            bindings.WriteBuffer(descriptorSetHandle: passSet, binding: Binding(name: SdfWorldPackage.HistorySurface), bufferHandle: m_surface.BufferHandle,
                bufferSize: m_surface.SizeBytes, kind: GpuBindingKind.ReadOnlyBuffer, elementStride: 8);
            var worldSet = bindings.AllocateSet(descriptorSetLayoutHandle: pipeline.GroupLayoutHandles[1], poolHandle: pool, name: default);

            foreach (var resource in parameters.Layout.Groups.Single(predicate: static group => (group.Group == ShaderInterfaceGroup.World)).Resources) {
                switch (resource.Kind) {
                    case GpuBindingKind.ReadOnlyBuffer:
                        bindings.WriteBuffer(descriptorSetHandle: worldSet, binding: resource.Binding, bufferHandle: m_world.BufferHandle,
                            bufferSize: m_world.SizeBytes, kind: resource.Kind, elementStride: resource.Member.Type!.Value.SizeBytes());
                        break;
                    case GpuBindingKind.SampledImage:
                        bindings.WriteSampledImage(descriptorSetHandle: worldSet, binding: resource.Binding, arrayElement: 0, imageViewHandle: m_historyView);
                        break;
                    case GpuBindingKind.Sampler:
                        for (uint index = 0; (index < resource.Member.DescriptorCount); index++) {
                            bindings.WriteSampler(descriptorSetHandle: worldSet, binding: resource.Binding, arrayElement: index, samplerHandle: m_sampler);
                        }
                        break;
                }
            }
            return worldSet;
        }
        public void Dispose() {
            m_services.Bindings.DestroySampler(samplerHandle: m_sampler);
            m_reactivity.Dispose(); m_history.Dispose(); m_world.Dispose(); m_surface.Dispose();
        }
    }
}
