using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Puck.Abstractions.Gpu;
using Puck.DirectX.Interop;
using Puck.Testing;
using Windows.Win32.Graphics.Direct3D12;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Creates the gate spike's plan-built layouts on the default adapter with the Direct3D 12 debug layer on: the
/// film grain, pixelate and arrays root signatures (<see cref="DirectXRootSignatures.CreateLayout"/>), and film grain's
/// frame and pass-group sets, written with a constant buffer, a separate image and a sampler in the device's sampler heap
/// and bound at their groups on a submitted command list, with no <c>[d3d12-debug]</c> line. It creates no pipeline
/// state, so it says nothing of a root signature's agreement with a shader. It runs alone, beside the
/// liveness tests, because turning the debug layer on removes every device the process already holds; it skips when the
/// host has no device or no debug layer.</summary>
[Collection(name: nameof(ConsoleErrorCollection))]
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGroupedLayoutDebugLayerTests {
    [Fact]
    public void The_spike_layouts_create_on_the_default_adapter_with_no_debug_layer_message() {
        var output = new StringWriter();
        var context = DirectXTestDevices.Debug(output: output);
        var filmGrain = GpuGroupLayoutTables.FilmGrain(pushesIndex: true);

        try {
            var bindings = context.Services.Bindings;

            foreach (var description in ((GpuPipelineLayoutDescription[])[filmGrain, GpuGroupLayoutTables.Pixelate(pushesIndex: true), GpuGroupLayoutTables.Arrays()])) {
                using var layout = DirectXRootSignatures.CreateLayout(
                    description: description,
                    device: ((ID3D12Device*)context.Device.Handle)
                );

                Assert.NotEqual(
                    actual: layout.RootSignatureHandle,
                    expected: 0
                );

                if (ReferenceEquals(objA: description, objB: filmGrain)) {
                    WriteAndBindThePassGroup(
                        context: context,
                        layout: layout
                    );
                }
            }

            context.DrainDebugMessages();
        } finally {
            context.Dispose();
        }

        Assert.DoesNotContain(
            actualString: output.ToString(),
            expectedSubstring: "[d3d12-debug]"
        );
    }

    // Fills film grain's frame and pass groups' sets (a constant buffer, a separate image and a sampler) and binds both
    // at their groups on a recorded, submitted command list.
    private static void WriteAndBindThePassGroup(DirectXDeviceContext context, DirectXPipelineLayout layout) {
        var services = context.Services;
        var bindings = services.Bindings;
        var layoutHandle = GCHandle.Alloc(value: layout);
        using var constants = services.BufferFactory.CreateHostVisible(
            name: default,
            sizeBytes: IGpuBindings.ConstantBufferAlignment,
            usage: GpuBufferUsage.Uniform
        );
        using var image = services.ImageFactory.Create(
            format: GpuPixelFormat.R8G8B8A8Unorm,
            height: 4,
            name: default,
            usage: GpuImageUsage.Sampled,
            width: 4
        );
        var pool = bindings.CreatePool(sizes: GpuDescriptorPoolSizes.ForGroups(groups: GpuGroupLayoutTables.FilmGrain(pushesIndex: true).Groups), name: default);

        try {
            var frame = bindings.AllocateSet(
                descriptorSetLayoutHandle: layout.GroupHandles[0],
                poolHandle: pool,
                name: default
            );
            var pass = bindings.AllocateSet(
                descriptorSetLayoutHandle: layout.GroupHandles[3],
                poolHandle: pool,
                name: default
            );

            foreach (var set in ((ReadOnlySpan<nint>)[frame, pass])) {
                bindings.WriteConstantBuffer(
                    arrayElement: 0,
                    binding: 0,
                    bufferHandle: constants.BufferHandle,
                    bufferSize: IGpuBindings.ConstantBufferAlignment,
                    descriptorSetHandle: set
                );
            }

            bindings.WriteSampledImage(
                arrayElement: 0,
                binding: 1,
                descriptorSetHandle: pass,
                imageViewHandle: image.ImageViewHandle
            );
            bindings.WriteSampler(
                arrayElement: 0,
                binding: 2,
                descriptorSetHandle: pass,
                samplerHandle: bindings.CreateSampler()
            );

            using var commands = services.CommandPoolFactory.Create(name: default);
            var command = commands.CommandBufferHandle;

            services.Recorder.BeginCommandBuffer(commandBufferHandle: command);
            ((ID3D12GraphicsCommandList*)DirectXCommandBufferState.Decode(commandBufferHandle: command).CommandList)->SetGraphicsRootSignature(pRootSignature: ((ID3D12RootSignature*)layout.RootSignatureHandle));

            foreach (var (set, group) in ((ReadOnlySpan<(nint, uint)>)[(frame, 0U), (pass, 3U)])) {
                services.Recorder.BindDescriptorSet(
                    bindPoint: GpuBindPoint.Graphics,
                    commandBufferHandle: command,
                    descriptorSetHandle: set,
                    group: group,
                    pipelineLayoutHandle: GCHandle.ToIntPtr(value: layoutHandle)
                );
            }

            services.Recorder.EndCommandBuffer(commandBufferHandle: command);
            services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);
        } finally {
            bindings.DestroyPool(poolHandle: pool);
            layoutHandle.Free();
        }
    }
}
