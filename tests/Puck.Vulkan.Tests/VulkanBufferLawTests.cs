using System.Runtime.InteropServices;

using Puck.Abstractions.Gpu;
using Puck.Vulkan.Bindings;
using Puck.Vulkan.Interfaces;
using Puck.Vulkan.Interop;
using Puck.Vulkan.Messages;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>Pins, without a device, the usage and memory every buffer kind is created with through the one
/// <see cref="IVulkanBufferApi"/>, and the owner contract of <see cref="VulkanBuffer"/>. The numbers are the raw
/// <c>VkBufferUsageFlagBits</c> and <c>VkMemoryPropertyFlagBits</c> values the per-kind
/// buffer APIs used before they became one, so a change to any of them is a change to what reaches the driver.</summary>
public sealed class VulkanBufferLawTests {
    private const uint DeviceLocalProperty = 0x00000001;
    private const uint HostVisibleCoherentProperties = 0x00000006;
    private const uint IndirectStorageUsage = 0x00000123;
    private const uint StorageUsage = 0x00000023;
    private const uint UniformUsage = 0x00000010;

    public static TheoryData<bool, GpuBufferUsage, uint, VulkanBufferMemory> StorageKinds() => new() {
        { true, GpuBufferUsage.Storage, StorageUsage, VulkanBufferMemory.HostCoherent },
        { false, GpuBufferUsage.Storage, StorageUsage, VulkanBufferMemory.DeviceLocal },
        { true, GpuBufferUsage.Storage | GpuBufferUsage.Indirect, IndirectStorageUsage, VulkanBufferMemory.HostCoherent },
        { false, GpuBufferUsage.Storage | GpuBufferUsage.Indirect, IndirectStorageUsage, VulkanBufferMemory.DeviceLocal },
        { true, GpuBufferUsage.Uniform, UniformUsage, VulkanBufferMemory.HostCoherent },
    };
    [MemberData(nameof(StorageKinds))]
    [Theory]
    public void EveryPlacementIsCreatedWithItsUsageAndMemoryOnTheBoundDevice(bool hostVisible, GpuBufferUsage usage, uint vulkanUsage, VulkanBufferMemory memory) {
        var bufferApi = new RecordingBufferApi();
        var device = new UntouchableDeviceContext();
        var factory = new VulkanGpuBufferFactory(
            bufferApi: bufferApi,
            deviceContext: device
        );
        using var buffer = (hostVisible
            ? factory.CreateHostVisible(
                sizeBytes: 64,
                usage: usage
            )
            : factory.CreateDeviceLocal(
                sizeBytes: 64,
                usage: usage
            ));

        Assert.Equal(
            actual: bufferApi.Created,
            expected: [(vulkanUsage, memory, 64UL)]
        );
        Assert.Same(
            actual: bufferApi.Devices.Single(),
            expected: device
        );
    }
    [InlineData(GpuBufferUsage.Vertex, 0x00000080u)]
    [InlineData(GpuBufferUsage.Index, 0x00000040u)]
    [InlineData(GpuBufferUsage.Vertex | GpuBufferUsage.Index, 0x000000C0u)]
    [Theory]
    public void AGeometryBufferIsHostCoherentWithItsDeclaredUsagesAndFilledWithItsData(GpuBufferUsage usage, uint vulkanUsage) {
        var bufferApi = new RecordingBufferApi();
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8];

        using var buffer = ((VulkanBuffer)new VulkanGpuBufferFactory(
            bufferApi: bufferApi,
            deviceContext: new UntouchableDeviceContext()
        ).CreateHostVisible(
            data: data,
            usage: usage
        ));

        Assert.Equal(
            actual: bufferApi.Created,
            expected: [(vulkanUsage, VulkanBufferMemory.HostCoherent, 8UL)]
        );
        Assert.Equal(
            actual: buffer.Read(),
            expected: data
        );
    }
    [Fact]
    public void ABufferWithoutAUsageOrBytesIsRefusedBeforeItIsCreated() {
        var bufferApi = new RecordingBufferApi();
        var factory = new VulkanGpuBufferFactory(
            bufferApi: bufferApi,
            deviceContext: new UntouchableDeviceContext()
        );

        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => factory.CreateHostVisible(
            data: [1, 2, 3, 4],
            usage: GpuBufferUsage.None
        ));
        _ = Assert.Throws<ArgumentException>(testCode: () => factory.CreateHostVisible(
            data: [],
            usage: GpuBufferUsage.Vertex
        ));
        _ = Assert.Throws<ArgumentException>(testCode: () => factory.CreateDeviceLocal(
            sizeBytes: 0,
            usage: GpuBufferUsage.Storage
        ));
        Assert.Empty(collection: bufferApi.Created);
    }
    [Fact]
    public void TheUsageEveryCallSiteNamesIsTheSpecificationValue() {
        // The staging upload and readback call sites create through these constants on paths that also record device commands, so the constants are what this law can reach there.
        Assert.Equal(
            actual: VulkanBufferUsageFlags.Storage,
            expected: StorageUsage
        );
        Assert.Equal(
            actual: VulkanBufferUsageFlags.TransferDestination,
            expected: 0x00000002u
        );
    }
    [Fact]
    public void EachMemoryKindSelectsItsPropertiesAndWhetherTheyAreRequired() {
        Assert.Equal(
            actual: VulkanNativeBufferApi.MemoryProperties(memory: VulkanBufferMemory.HostCoherent),
            expected: (HostVisibleCoherentProperties, true, GpuMemoryRole.HostVisible)
        );
        Assert.Equal(
            actual: VulkanNativeBufferApi.MemoryProperties(memory: VulkanBufferMemory.HostCoherentDeviceLocal),
            expected: (HostVisibleCoherentProperties | DeviceLocalProperty, true, GpuMemoryRole.HostVisibleDeviceLocal)
        );
        Assert.Equal(
            actual: VulkanNativeBufferApi.MemoryProperties(memory: VulkanBufferMemory.DeviceLocal),
            expected: (DeviceLocalProperty, true, GpuMemoryRole.DeviceLocal)
        );
        Assert.Equal(
            actual: VulkanNativeBufferApi.MemoryProperties(memory: VulkanBufferMemory.PreferDeviceLocal),
            expected: (DeviceLocalProperty, false, GpuMemoryRole.DeviceLocal)
        );
    }
    [Fact]
    public void AHostCoherentBufferMapsOnceAndUnmapsBeforeItsOneDestroy() {
        var bufferApi = new RecordingBufferApi();
        var buffer = VulkanBuffer.Create(
            bufferApi: bufferApi,
            device: new UntouchableDeviceContext(),
            memory: VulkanBufferMemory.HostCoherent,
            sizeBytes: 16,
            usage: VulkanBufferUsageFlags.Storage
        );

        buffer.Write<uint>(
            data: [7u, 11u],
            destinationOffsetBytes: 8
        );
        buffer.Dispose();
        buffer.Dispose();

        Assert.Equal(
            actual: bufferApi.Calls,
            expected: ["create", "map", "unmap", "destroy"]
        );
        Assert.Equal(
            actual: buffer.BufferHandle,
            expected: 0
        );
    }
    [Fact]
    public void ADeviceLocalBufferIsNeverMappedAndRefusesHostAccess() {
        var bufferApi = new RecordingBufferApi();

        using var buffer = VulkanBuffer.Create(
            bufferApi: bufferApi,
            device: new UntouchableDeviceContext(),
            memory: VulkanBufferMemory.DeviceLocal,
            sizeBytes: 16,
            usage: VulkanBufferUsageFlags.Storage
        );

        _ = Assert.Throws<InvalidOperationException>(testCode: () => buffer.Write<uint>(data: [1u]));
        _ = Assert.Throws<InvalidOperationException>(testCode: () => buffer.Read());
        Assert.Equal(
            actual: bufferApi.Calls,
            expected: ["create"]
        );
    }

    /// <summary>Records every call and hands out distinct non-zero handles; a mapping is real memory so writes and reads
    /// through it can be checked.</summary>
    private sealed unsafe class RecordingBufferApi : IVulkanBufferApi {
        private readonly Dictionary<nint, nint> m_mappings = [];

        private nint m_nextHandle = 0x1000;

        public List<string> Calls { get; } = [];
        public List<(uint Usage, VulkanBufferMemory Memory, ulong SizeBytes)> Created { get; } = [];
        public List<IVulkanDeviceContext> Devices { get; } = [];

        public VulkanBufferHandles Create(IVulkanDeviceContext device, uint usage, VulkanBufferMemory memory, ulong sizeBytes) {
            Calls.Add(item: "create");
            Created.Add(item: (usage, memory, sizeBytes));
            Devices.Add(item: device);
            m_nextHandle += 2;

            return new VulkanBufferHandles(
                Buffer: m_nextHandle,
                Device: null!,
                Memory: (m_nextHandle + 1)
            );
        }
        public void Destroy(VulkanBufferHandles handles) => Calls.Add(item: "destroy");
        public nint Map(VulkanBufferHandles handles, ulong sizeBytes) {
            var block = ((nint)NativeMemory.AllocZeroed(byteCount: ((nuint)sizeBytes)));

            Calls.Add(item: "map");
            m_mappings.Add(
                key: handles.Memory,
                value: block
            );
            return block;
        }
        public void Unmap(VulkanBufferHandles handles) {
            Calls.Add(item: "unmap");
            Assert.True(condition: m_mappings.Remove(
                key: handles.Memory,
                value: out var block
            ));
            NativeMemory.Free(ptr: ((void*)block));
        }
    }
    /// <summary>A device context whose every member throws: a factory that reaches the device itself instead of handing the
    /// context to its buffer API fails the law.</summary>
    private sealed class UntouchableDeviceContext : IVulkanDeviceContext, IGpuDeviceContext {
        public long AdapterLuid => throw new NotSupportedException();
        public GpuDeviceCapabilities? Capabilities => throw new NotSupportedException();
        public GpuDeviceIdentity? Identity => throw new NotSupportedException();
        public VulkanInstance Instance => throw new NotSupportedException();
        public VulkanLogicalDevice LogicalDevice => throw new NotSupportedException();
        public GpuMemoryProfile MemoryProfile => throw new NotSupportedException();
        public VkPhysicalDevice PhysicalDevice => throw new NotSupportedException();
        public GpuDeviceServices Services => throw new NotSupportedException();
        public VulkanSurface Surface => throw new NotSupportedException();

        public void WaitIdle() => throw new NotSupportedException();
    }
}
