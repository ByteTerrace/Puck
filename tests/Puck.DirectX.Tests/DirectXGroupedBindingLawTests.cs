using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Puck.Abstractions.Gpu;
using Puck.Testing;
using Windows.Win32.Graphics.Direct3D12;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>Holds a group's set on a software (WARP) device to what it holds and where it binds. The gate spike's film
/// grain layout's pass group takes its constant buffer, its separate image and its sampler through
/// <see cref="IGpuBindings"/>, and a write of a kind the group does not declare at that binding, or a constant buffer
/// view of an unaligned size, is refused by name. A recorded bind sets a group's view table and sampler table at that
/// group and refuses a set bound at a group other than its own, or at a group the pipeline does not have. A pool's sets
/// release with it: a thousand cycles of creating a pool, allocating a set of each kind and destroying the pool leave
/// the bindings holding the handles they began with.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGroupedBindingLawTests {
    [Fact]
    public void A_groups_set_takes_its_constant_buffer_image_and_sampler_and_binds_only_at_its_own_group() {
        using var context = DirectXTestDevices.Warp();
        var services = context.Services;
        var bindings = services.Bindings;
        var filmGrain = GpuGroupLayoutTables.FilmGrain(pushesIndex: true);
        using var layout = DirectXRootSignatures.CreateLayout(
            description: filmGrain,
            device: ((ID3D12Device*)context.Device.Handle)
        );
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
        var pool = bindings.CreatePool(sizes: GpuDescriptorPoolSizes.ForGroups(groups: filmGrain.Groups), name: default);

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

            bindings.WriteConstantBuffer(
                arrayElement: 0,
                binding: 0,
                bufferHandle: constants.BufferHandle,
                bufferSize: IGpuBindings.ConstantBufferAlignment,
                descriptorSetHandle: frame
            );
            bindings.WriteConstantBuffer(
                arrayElement: 0,
                binding: 0,
                bufferHandle: constants.BufferHandle,
                bufferSize: IGpuBindings.ConstantBufferAlignment,
                descriptorSetHandle: pass
            );
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
                samplerHandle: bindings.CreateSampler(filter: GpuSamplerFilter.Nearest)
            );

            // A sampler lives in the sampler table and a view in the view table, so a write of a kind the group does
            // not declare at a binding is refused rather than landing in the other table.
            Assert.Equal(
                actual: Assert.Throws<InvalidOperationException>(testCode: () => bindings.WriteSampler(
                    arrayElement: 0,
                    binding: 1,
                    descriptorSetHandle: pass,
                    samplerHandle: bindings.CreateSampler()
                )).Message,
                expected: "Group 3 declares no Sampler at binding 1."
            );
            Assert.Equal(
                actual: Assert.Throws<InvalidOperationException>(testCode: () => bindings.WriteSampledImage(
                    arrayElement: 0,
                    binding: 0,
                    descriptorSetHandle: frame,
                    imageViewHandle: image.ImageViewHandle
                )).Message,
                expected: "Group 0 declares no SampledImage at binding 0."
            );
            _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => bindings.WriteConstantBuffer(
                arrayElement: 0,
                binding: 0,
                bufferHandle: constants.BufferHandle,
                bufferSize: 128UL,
                descriptorSetHandle: pass
            ));

            using var commands = services.CommandPoolFactory.Create(name: default);
            var command = commands.CommandBufferHandle;
            var recorder = services.Recorder;

            recorder.BeginCommandBuffer(commandBufferHandle: command);
            ((ID3D12GraphicsCommandList*)DirectXCommandBufferState.Decode(commandBufferHandle: command).CommandList)->SetGraphicsRootSignature(pRootSignature: ((ID3D12RootSignature*)layout.RootSignatureHandle));
            recorder.BindDescriptorSet(
                bindPoint: GpuBindPoint.Graphics,
                commandBufferHandle: command,
                descriptorSetHandle: frame,
                group: 0,
                pipelineLayoutHandle: GCHandle.ToIntPtr(value: layoutHandle)
            );
            recorder.BindDescriptorSet(
                bindPoint: GpuBindPoint.Graphics,
                commandBufferHandle: command,
                descriptorSetHandle: pass,
                group: 3,
                pipelineLayoutHandle: GCHandle.ToIntPtr(value: layoutHandle)
            );
            Assert.Equal(
                actual: Assert.Throws<InvalidOperationException>(testCode: () => recorder.BindDescriptorSet(
                    bindPoint: GpuBindPoint.Graphics,
                    commandBufferHandle: command,
                    descriptorSetHandle: pass,
                    group: 0,
                    pipelineLayoutHandle: GCHandle.ToIntPtr(value: layoutHandle)
                )).Message,
                expected: "A set of group 3 is bound at group 0; a set binds only at its own group."
            );
            recorder.EndCommandBuffer(commandBufferHandle: command);
            services.QueueSubmitter.SubmitAndWait(commandBufferHandles: [command]);
        } finally {
            bindings.DestroyPool(poolHandle: pool);
            layoutHandle.Free();
        }
    }
    [Fact]
    public void A_pools_sets_release_with_it_across_a_thousand_allocate_and_destroy_cycles() {
        using var context = DirectXTestDevices.Warp();
        var bindings = context.Services.Bindings;
        var filmGrain = GpuGroupLayoutTables.FilmGrain(pushesIndex: true);
        using var layout = DirectXRootSignatures.CreateLayout(
            description: filmGrain,
            device: ((ID3D12Device*)context.Device.Handle)
        );
        // A set of a pipeline created without a layout description: one table of one storage buffer.
        var flatLayout = GCHandle.Alloc(value: new DirectXPipelineLayout {
            DescriptorSlotCount = 1,
            SlotByBinding = [0],
        });
        var sizes = GpuDescriptorPoolSizes.ForGroups(groups: filmGrain.Groups) with {
            MaxSets = 3,
            StorageBufferCount = 1,
        };
        var handles = context.DescriptorBindings.LiveHandles;
        var views = context.DescriptorHeaps.Budget.FreeViewDescriptors;

        try {
            for (var cycle = 0; (cycle < 1000); cycle++) {
                var pool = bindings.CreatePool(name: default, sizes: sizes);

                _ = bindings.AllocateSet(
                    descriptorSetLayoutHandle: layout.GroupHandles[0],
                    poolHandle: pool,
                    name: default
                );
                _ = bindings.AllocateSet(
                    descriptorSetLayoutHandle: layout.GroupHandles[3],
                    poolHandle: pool,
                    name: default
                );
                _ = bindings.AllocateSet(
                    descriptorSetLayoutHandle: GCHandle.ToIntPtr(value: flatLayout),
                    poolHandle: pool,
                    name: default
                );
                Assert.Equal(
                    actual: context.DescriptorBindings.LiveHandles,
                    expected: (handles + 4L)
                );
                bindings.DestroyPool(poolHandle: pool);
            }
        } finally {
            flatLayout.Free();
        }

        Assert.Equal(
            actual: (context.DescriptorBindings.LiveHandles, context.DescriptorHeaps.Budget.FreeViewDescriptors),
            expected: (handles, views)
        );
    }
}
