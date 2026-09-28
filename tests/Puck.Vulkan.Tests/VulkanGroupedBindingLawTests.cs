using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Puck.Abstractions.Gpu;
using Puck.Abstractions.Memory;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Holds <see cref="VulkanGpuBindings"/> and <see cref="VulkanGpuRecorder"/> to a group's set with no driver: a
/// descriptor API that records each write, and a command table whose <c>vkCmdBindDescriptorSets</c> records what it is
/// handed. A group's constant buffer is a uniform buffer write, its separate image a sampled image write in
/// shader-read-only layout, and its sampler a sampler write, each at its binding and element. A set allocated against a
/// group's layout belongs to that group: a bind records it at the group's set number on both bind points, a bind at any
/// other group is refused by name, and destroying its pool forgets it. A set of any other layout belongs to group 0.</summary>
public sealed unsafe class VulkanGroupedBindingLawTests {
    private const nint CommandBuffer = 0x0C00;
    private const nint DeviceHandle = 0x0D00;
    private const nint GroupSet = 0x5003;
    private const nint GroupSetLayout = 0x1003;
    private const nint PipelineLayout = 0x7000;
    private const nint Pool = 0x3000;

    [ThreadStatic]
    private static List<(uint BindPoint, uint FirstSet, uint Count, nint Set)>? Binds;

    [Fact]
    public void A_groups_writes_are_its_descriptor_types_at_their_bindings() {
        var rig = new Rig();

        rig.Bindings.WriteConstantBuffer(
            arrayElement: 1,
            binding: 0,
            bufferHandle: 0x61,
            bufferSize: IGpuBindings.ConstantBufferAlignment,
            descriptorSetHandle: GroupSet
        );
        rig.Bindings.WriteSampledImage(
            arrayElement: 0,
            binding: 1,
            descriptorSetHandle: GroupSet,
            imageViewHandle: 0x62
        );
        rig.Bindings.WriteSampler(
            arrayElement: 2,
            binding: 2,
            descriptorSetHandle: GroupSet,
            samplerHandle: 0x63
        );

        Assert.Equal(
            actual: rig.Descriptors.Buffers.Select(selector: static write => (write.DescriptorType, write.Binding, write.ArrayElement, write.BufferHandle, write.BufferOffset, write.BufferRange)),
            expected: [(VulkanDescriptorType.UniformBuffer, 0U, 1U, ((nint)0x61), 0UL, IGpuBindings.ConstantBufferAlignment)]
        );
        Assert.Equal(
            actual: rig.Descriptors.Images.Select(selector: static write => (write.DescriptorType, write.Binding, write.ArrayElement, write.ImageViewHandle, write.ImageLayout, write.SamplerHandle)),
            expected: [
                (VulkanDescriptorType.SampledImage, 1U, 0U, ((nint)0x62), VulkanImageLayout.ShaderReadOnlyOptimal, ((nint)0)),
                (VulkanDescriptorType.Sampler, 2U, 2U, ((nint)0), 0U, ((nint)0x63)),
            ]
        );
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => rig.Bindings.WriteConstantBuffer(
            arrayElement: 0,
            binding: 0,
            bufferHandle: 0x61,
            bufferSize: 100UL,
            descriptorSetHandle: GroupSet
        ));
    }
    [Fact]
    public void A_groups_set_binds_at_its_set_number_and_only_there() {
        var rig = new Rig();

        rig.Device.SetGroups.AddLayouts(setLayoutHandles: [0x1000, 0, 0, GroupSetLayout]);

        var set = rig.Bindings.AllocateSet(
            descriptorSetLayoutHandle: GroupSetLayout,
            name: default,
            poolHandle: Pool
        );
        var flat = rig.Bindings.AllocateSet(
            descriptorSetLayoutHandle: 0x2000,
            name: default,
            poolHandle: Pool
        );

        Binds = [];

        try {
            foreach (var bindPoint in ((ReadOnlySpan<GpuBindPoint>)[GpuBindPoint.Graphics, GpuBindPoint.Compute])) {
                rig.Recorder.BindDescriptorSet(
                    bindPoint: bindPoint,
                    commandBufferHandle: CommandBuffer,
                    descriptorSetHandle: set,
                    group: 3,
                    pipelineLayoutHandle: PipelineLayout
                );
            }

            rig.Recorder.BindDescriptorSet(
                bindPoint: GpuBindPoint.Compute,
                commandBufferHandle: CommandBuffer,
                descriptorSetHandle: flat,
                group: 0,
                pipelineLayoutHandle: PipelineLayout
            );
            Assert.Equal(
                actual: Assert.Throws<InvalidOperationException>(testCode: () => rig.Recorder.BindDescriptorSet(
                    bindPoint: GpuBindPoint.Graphics,
                    commandBufferHandle: CommandBuffer,
                    descriptorSetHandle: set,
                    group: 2,
                    pipelineLayoutHandle: PipelineLayout
                )).Message,
                expected: "A set of group 3 is bound at group 2; a set binds only at its own group."
            );
            Assert.Equal(
                actual: Assert.Throws<InvalidOperationException>(testCode: () => rig.Recorder.BindDescriptorSet(
                    bindPoint: GpuBindPoint.Compute,
                    commandBufferHandle: CommandBuffer,
                    descriptorSetHandle: flat,
                    group: 3,
                    pipelineLayoutHandle: PipelineLayout
                )).Message,
                expected: "A set of group 0 is bound at group 3; a set binds only at its own group."
            );
            Assert.Equal(
                actual: Binds,
                expected: [(0U, 3U, 1U, set), (1U, 3U, 1U, set), (1U, 0U, 1U, flat)]
            );
        } finally {
            Binds = null;
        }

        Assert.Equal(
            actual: rig.Device.SetGroups.LiveSets,
            expected: 1
        );
        rig.Bindings.DestroyPool(poolHandle: Pool);
        Assert.Equal(
            actual: (rig.Device.SetGroups.LiveSets, rig.Device.SetGroups.GroupOf(setHandle: set)),
            expected: (0, 0U)
        );
    }

    // A vkGetDeviceProcAddr stand-in: vkCmdBindDescriptorSets records, and every other entry point resolves to a trap
    // nothing here reaches.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Resolve(nint handle, byte* name) =>
        Encoding.UTF8.GetString(bytes: MemoryMarshal.CreateReadOnlySpanFromNullTerminated(value: name)) switch {
            "vkCmdBindDescriptorSets" => ((nint)((delegate* unmanaged[Cdecl]<nint, uint, nint, uint, uint, nint*, uint, nint, void>)&BindDescriptorSets)),
            _ => ((nint)((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, nint>)&Trap)),
        };
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void BindDescriptorSets(nint commandBuffer, uint bindPoint, nint layout, uint firstSet, uint count, nint* sets, uint dynamicCount, nint dynamicOffsets) =>
        Binds!.Add(item: (bindPoint, firstSet, count, sets[0]));
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Trap(nint first, nint second, nint third, nint fourth) => 0;

    private sealed class Rig {
        public Rig() {
            var commands = new VulkanDeviceCommands(
                deviceHandle: DeviceHandle,
                memory: null,
                procedures: new VulkanProcResolver(
                    getDeviceProcAddr: &Resolve,
                    getInstanceProcAddr: &Resolve
                )
            );
            var context = new DeviceContext(device: new VulkanLogicalDevice(
                device: commands,
                graphicsQueue: default,
                logicalDeviceApi: new NoLogicalDeviceApi(),
                physicalDevice: default,
                presentQueue: default
            ));

            Device = context.LogicalDevice;
            Bindings = new VulkanGpuBindings(
                allocator: new VulkanDescriptorAllocator(descriptorApi: Descriptors),
                deviceContext: context,
                naming: GpuObjectNaming.Off
            );
            Recorder = new VulkanGpuRecorder(
                deviceContext: context,
                recordingApi: new VulkanNativeCommandBufferRecordingApi(allocator: new HeapAllocator())
            );
        }

        public VulkanGpuBindings Bindings { get; }
        public RecordingDescriptorApi Descriptors { get; } = new();
        public VulkanLogicalDevice Device { get; }
        public VulkanGpuRecorder Recorder { get; }
    }
    private sealed class DeviceContext(VulkanLogicalDevice device) : IVulkanDeviceContext {
        public VulkanInstance Instance => throw new NotSupportedException();
        public VulkanLogicalDevice LogicalDevice => device;
        public VkPhysicalDevice PhysicalDevice => throw new NotSupportedException();
        public VulkanSurface Surface => throw new NotSupportedException();
    }
    private sealed class HeapAllocator : IAllocator {
        public void* Allocate(nuint size, nuint alignment = 0) => NativeMemory.Alloc(byteCount: Math.Max(
            val1: size,
            val2: 1
        ));
        public void Free(void* ptr) => NativeMemory.Free(ptr: ptr);
        public void* Reallocate(void* ptr, nuint newSize, nuint alignment = 0) => NativeMemory.Realloc(
            byteCount: Math.Max(
                val1: newSize,
                val2: 1
            ),
            ptr: ptr
        );
    }
    private sealed class NoLogicalDeviceApi : IVulkanLogicalDeviceApi {
        public VkResult CreateLogicalDevice(VulkanLogicalDeviceCreateRequest request, out VulkanDeviceCommands? device) => throw new NotSupportedException();
        public void DestroyDevice(VulkanDeviceCommands device) { }
        public nint GetDeviceQueue(VulkanDeviceCommands device, uint queueFamilyIndex, uint queueIndex) => throw new NotSupportedException();
        public VkResult WaitIdle(VulkanDeviceCommands device) => VkResult.Success;
    }
    private sealed class RecordingDescriptorApi : IVulkanDescriptorApi {
        private nint m_nextSet = GroupSet;

        public List<VulkanDescriptorBufferWriteRequest> Buffers { get; } = [];
        public List<VulkanDescriptorImageWriteRequest> Images { get; } = [];

        public nint AllocateSet(VulkanDescriptorSetAllocateRequest request) => m_nextSet++;
        public nint CreatePool(VulkanDescriptorPoolCreateRequest request) => Pool;
        public nint CreateSampler(VulkanSamplerCreateRequest request) => throw new NotSupportedException();
        public void DestroyPool(VulkanDeviceCommands device, nint poolHandle) { }
        public void DestroySampler(VulkanDeviceCommands device, nint samplerHandle) { }
        public void WriteBuffer(VulkanDescriptorBufferWriteRequest request) => Buffers.Add(item: request);
        public void WriteImage(VulkanDescriptorImageWriteRequest request) => Images.Add(item: request);
    }
}
