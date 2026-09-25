using Puck.Abstractions.Gpu;
using Puck.Testing;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for residency. The memory profile's Vulkan and Direct3D 12 fills are pure functions of the native values they
/// read. The selector pins one policy for each of four synthetic devices — coherent unified memory, a discrete adapter
/// with a small host-visible aperture, one with none, and a profile reporting nothing — and stages a region past its
/// share of the aperture. One region's bytes are identical under all three policies, frame by frame, read back through
/// the device-free memory model that runs the copy kernel.
/// </summary>
public sealed class GpuResidencyLawTests {
    private const uint DeviceLocal = 0x1U;
    private const uint DiscreteDevice = 2U;
    private const ulong GiB = (1UL << 30);
    private const ulong HeapDeviceLocal = 0x1UL;
    private const uint HostCached = 0x8U;
    private const uint HostCoherent = 0x4U;
    private const uint HostVisible = 0x2U;
    private const uint IntegratedDevice = 1U;
    private const ulong MiB = (1UL << 20);
    // A region the size of a small host table.
    private const ulong TableBytes = (64UL * 1024UL);

    // An integrated device whose device-local heap the host reaches coherently, beside host memory: a handheld's
    // unified memory. Types and heaps are in the native pair layout.
    private static GpuMemoryProfile CoherentUnified => GpuMemoryProfile.FromVulkan(
        deviceType: IntegratedDevice,
        memoryHeaps: [(4UL * GiB), HeapDeviceLocal, (8UL * GiB), 0UL],
        memoryTypes: [
            DeviceLocal, 0U,
            (HostVisible | HostCoherent), 1U,
            (DeviceLocal | HostVisible | HostCoherent), 0U,
            (HostVisible | HostCoherent | HostCached), 1U,
        ]
    );
    // A discrete adapter that exposes a 256 MiB aperture onto its device memory.
    private static GpuMemoryProfile DiscreteSmallAperture => GpuMemoryProfile.FromVulkan(
        deviceType: DiscreteDevice,
        memoryHeaps: [(12UL * GiB), HeapDeviceLocal, (32UL * GiB), 0UL, (256UL * MiB), HeapDeviceLocal],
        memoryTypes: [
            DeviceLocal, 0U,
            (HostVisible | HostCoherent), 1U,
            (HostVisible | HostCoherent | HostCached), 1U,
            (DeviceLocal | HostVisible | HostCoherent), 2U,
        ]
    );
    // A discrete adapter the host reaches only through host memory.
    private static GpuMemoryProfile DiscreteNoAperture => GpuMemoryProfile.FromVulkan(
        deviceType: DiscreteDevice,
        memoryHeaps: [(8UL * GiB), HeapDeviceLocal, (16UL * GiB), 0UL],
        memoryTypes: [
            DeviceLocal, 0U,
            (HostVisible | HostCoherent), 1U,
            (HostVisible | HostCoherent | HostCached), 1U,
        ]
    );

    [Fact]
    public void TheVulkanFillReadsTheNativeTypesAndHeaps() {
        Assert.Equal(
            expected: new GpuMemoryProfile(
                CoherentUnifiedMemory: true,
                DeviceLocalBytes: (4UL * GiB),
                HostVisibleDeviceLocalBytes: (4UL * GiB),
                LargestDeviceLocalHeapBytes: (4UL * GiB)
            ),
            actual: CoherentUnified
        );
        Assert.Equal(
            expected: new GpuMemoryProfile(
                CoherentUnifiedMemory: false,
                DeviceLocalBytes: ((12UL * GiB) + (256UL * MiB)),
                HostVisibleDeviceLocalBytes: (256UL * MiB),
                LargestDeviceLocalHeapBytes: (12UL * GiB)
            ),
            actual: DiscreteSmallAperture
        );
        Assert.Equal(
            expected: new GpuMemoryProfile(
                CoherentUnifiedMemory: false,
                DeviceLocalBytes: (8UL * GiB),
                HostVisibleDeviceLocalBytes: 0UL,
                LargestDeviceLocalHeapBytes: (8UL * GiB)
            ),
            actual: DiscreteNoAperture
        );
        Assert.True(condition: DiscreteSmallAperture.IsDeviceLocalHostVisible);
        Assert.False(condition: DiscreteNoAperture.IsDeviceLocalHostVisible);
        // An integrated device the host cannot write device-local memory on coherently is not coherent unified memory.
        Assert.False(condition: GpuMemoryProfile.FromVulkan(
            deviceType: IntegratedDevice,
            memoryHeaps: [(2UL * GiB), HeapDeviceLocal],
            memoryTypes: [DeviceLocal, 0U, (DeviceLocal | HostVisible | HostCached), 0U]
        ).CoherentUnifiedMemory);
    }
    [Fact]
    public void TheVulkanFillRefusesWhatIsNotTheNativeLayout() {
        _ = Assert.Throws<ArgumentException>(testCode: () => GpuMemoryProfile.FromVulkan(
            deviceType: DiscreteDevice,
            memoryHeaps: [GiB, HeapDeviceLocal],
            memoryTypes: [DeviceLocal]
        ));
        _ = Assert.Throws<ArgumentException>(testCode: () => GpuMemoryProfile.FromVulkan(
            deviceType: DiscreteDevice,
            memoryHeaps: [GiB, HeapDeviceLocal],
            memoryTypes: [DeviceLocal, 1U]
        ));
    }
    [Fact]
    public void TheDirectXFillReadsTheArchitectureAndTheAdapter() {
        Assert.Equal(
            expected: new GpuMemoryProfile(
                CoherentUnifiedMemory: true,
                DeviceLocalBytes: ((512UL * MiB) + (8UL * GiB)),
                HostVisibleDeviceLocalBytes: ((512UL * MiB) + (8UL * GiB)),
                LargestDeviceLocalHeapBytes: ((512UL * MiB) + (8UL * GiB))
            ),
            actual: GpuMemoryProfile.FromDirectX(
                cacheCoherentUnifiedMemory: true,
                dedicatedVideoMemory: (512UL * MiB),
                gpuUploadHeapSupported: false,
                sharedSystemMemory: (8UL * GiB),
                unifiedMemory: true
            )
        );
        Assert.False(condition: GpuMemoryProfile.FromDirectX(
            cacheCoherentUnifiedMemory: false,
            dedicatedVideoMemory: (512UL * MiB),
            gpuUploadHeapSupported: false,
            sharedSystemMemory: (8UL * GiB),
            unifiedMemory: true
        ).CoherentUnifiedMemory);
        Assert.Equal(
            expected: new GpuMemoryProfile(
                CoherentUnifiedMemory: false,
                DeviceLocalBytes: (12UL * GiB),
                HostVisibleDeviceLocalBytes: (12UL * GiB),
                LargestDeviceLocalHeapBytes: (12UL * GiB)
            ),
            actual: GpuMemoryProfile.FromDirectX(
                cacheCoherentUnifiedMemory: false,
                dedicatedVideoMemory: (12UL * GiB),
                gpuUploadHeapSupported: true,
                sharedSystemMemory: (16UL * GiB),
                unifiedMemory: false
            )
        );
        Assert.Equal(
            expected: new GpuMemoryProfile(
                CoherentUnifiedMemory: false,
                DeviceLocalBytes: (12UL * GiB),
                HostVisibleDeviceLocalBytes: 0UL,
                LargestDeviceLocalHeapBytes: (12UL * GiB)
            ),
            actual: GpuMemoryProfile.FromDirectX(
                cacheCoherentUnifiedMemory: false,
                dedicatedVideoMemory: (12UL * GiB),
                gpuUploadHeapSupported: false,
                sharedSystemMemory: (16UL * GiB),
                unifiedMemory: false
            )
        );
    }
    [Fact]
    public void TheSelectorPinsOnePolicyPerDevice() {
        Assert.Equal(
            expected: GpuResidencyPolicy.InPlace,
            actual: GpuResidency.Select(byteCount: TableBytes, profile: CoherentUnified)
        );
        Assert.Equal(
            expected: GpuResidencyPolicy.Ring,
            actual: GpuResidency.Select(byteCount: TableBytes, profile: DiscreteSmallAperture)
        );
        Assert.Equal(
            expected: GpuResidencyPolicy.Staged,
            actual: GpuResidency.Select(byteCount: TableBytes, profile: DiscreteNoAperture)
        );
        Assert.Equal(
            expected: GpuResidencyPolicy.Staged,
            actual: GpuResidency.Select(byteCount: TableBytes, profile: default)
        );
    }
    [Fact]
    public void ARegionPastItsShareOfTheApertureIsStaged() {
        var share = ((256UL * MiB) / GpuResidency.HostVisibleShare);

        Assert.Equal(
            expected: GpuResidencyPolicy.Ring,
            actual: GpuResidency.Select(byteCount: share, profile: DiscreteSmallAperture)
        );
        Assert.Equal(
            expected: GpuResidencyPolicy.Staged,
            actual: GpuResidency.Select(byteCount: (share + 1UL), profile: DiscreteSmallAperture)
        );
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => GpuResidency.Select(byteCount: 0UL, profile: CoherentUnified));
        Assert.Equal(
            expected: new[] { "staged", "ring", "in-place" },
            actual: Enum.GetValues<GpuResidencyPolicy>().Select(selector: static policy => GpuResidency.Name(policy: policy))
        );
    }
    [Fact]
    public void OneRegionReadsTheSameBytesUnderEveryPolicy() {
        const int ByteCount = 8192;
        const int Slots = 3;
        var readings = new Dictionary<GpuResidencyPolicy, List<byte[]>>();

        foreach (var policy in Enum.GetValues<GpuResidencyPolicy>()) {
            var gpu = new UploadModelGpu(reportVersion: 0);
            using var copy = CopyPipeline(gpu: gpu);
            using var region = new GpuRegion(
                bindings: gpu.Services.Bindings,
                buffers: gpu.Services.BufferFactory,
                byteCount: ByteCount,
                copyPipeline: copy,
                policy: policy,
                recorder: gpu.Services.Recorder,
                slotCount: Slots
            );
            var expected = new byte[ByteCount];
            var frames = new List<byte[]>();
            var random = new Random(Seed: 7);

            for (var frame = 0; (frame < 24); frame++) {
                var slot = (frame % Slots);

                foreach (var (offset, bytes) in Writes(frame: frame, random: random)) {
                    _ = region.Write(
                        bytes: bytes,
                        offset: offset
                    );
                    bytes.CopyTo(array: expected, index: offset);
                }

                region.Flush(slot: slot);
                region.RecordCopy(
                    commandBuffer: 2,
                    slot: slot
                );

                var read = gpu.Memory(bufferHandle: region.Buffer(slot: slot).BufferHandle)[..ByteCount];

                Assert.Equal(
                    actual: read,
                    expected: expected
                );
                Assert.Equal(
                    expected: expected,
                    actual: region.Contents.ToArray()
                );
                frames.Add(item: read);
            }

            Assert.Equal(
                expected: (policy == GpuResidencyPolicy.Staged),
                actual: (gpu.UploadCopies > 0)
            );
            readings[policy] = frames;
        }

        Assert.Equal(
            expected: readings[GpuResidencyPolicy.Staged],
            actual: readings[GpuResidencyPolicy.Ring]
        );
        Assert.Equal(
            expected: readings[GpuResidencyPolicy.Staged],
            actual: readings[GpuResidencyPolicy.InPlace]
        );
    }
    [Fact]
    public void AStagedCopyOfASlotNotFlushedSinceTheLastWriteIsRefused() {
        var gpu = new UploadModelGpu(reportVersion: 0);
        using var copy = CopyPipeline(gpu: gpu);
        using var region = new GpuRegion(
            bindings: gpu.Services.Bindings,
            buffers: gpu.Services.BufferFactory,
            byteCount: 64,
            copyPipeline: copy,
            policy: GpuResidencyPolicy.Staged,
            recorder: gpu.Services.Recorder,
            slotCount: 2
        );

        region.Flush(slot: 0);
        _ = Assert.Throws<InvalidOperationException>(testCode: () => region.RecordCopy(commandBuffer: 2, slot: 1));
        _ = region.Write(bytes: [1, 2, 3], offset: 5);
        _ = Assert.Throws<InvalidOperationException>(testCode: () => region.RecordCopy(commandBuffer: 2, slot: 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => new GpuRegion(
            bindings: gpu.Services.Bindings,
            buffers: gpu.Services.BufferFactory,
            byteCount: 6,
            copyPipeline: copy,
            policy: GpuResidencyPolicy.Ring,
            recorder: gpu.Services.Recorder,
            slotCount: 2
        ));
    }
    [InlineData(GpuResidencyPolicy.InPlace)]
    [InlineData(GpuResidencyPolicy.Ring)]
    [InlineData(GpuResidencyPolicy.Staged)]
    [Theory]
    public void TheCopyPoolARegionStatesIsThePoolItCreates(GpuResidencyPolicy policy) {
        var gpu = new UploadModelGpu(reportVersion: 0);
        using var copy = CopyPipeline(gpu: gpu);
        using var region = new GpuRegion(
            bindings: gpu.Services.Bindings,
            buffers: gpu.Services.BufferFactory,
            byteCount: 64,
            copyPipeline: copy,
            policy: policy,
            recorder: gpu.Services.Recorder,
            slotCount: 3
        );

        Assert.Equal(
            actual: gpu.PoolsCreated,
            expected: ((policy == GpuResidencyPolicy.Staged)
                ? [GpuRegion.CopyPoolSizes(slotCount: 3)]
                : [])
        );
    }

    private static IGpuComputePipeline CopyPipeline(UploadModelGpu gpu) {
        using var module = gpu.Services.ShaderModuleFactory.Create(
            bytecode: new byte[] { UploadModelGpu.RegionCopyBytecode },
            stage: GpuShaderStage.Compute
        );

        return gpu.Services.PipelineFactory.Create(
            computeShaderModule: module,
            description: GpuRegion.CopyPipeline
        );
    }
    // A frame's writes: none on the first frame, one straddling words, more separate ranges than a host buffer keeps
    // runs for, a write of what is already there, more separate ranges than one copy carries, then scattered runs.
    private static IEnumerable<(int Offset, byte[] Bytes)> Writes(int frame, Random random) {
        switch (frame) {
            case 0:
                yield break;
            case 1:
                yield return (6, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);

                break;
            case 2:
                for (var index = 0; (index < 20); index++) {
                    yield return ((index * 100), [((byte)(index + 1))]);
                }

                break;
            case 3:
                yield return (6, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);

                break;
            case 4:
                for (var index = 0; (index < 300); index++) {
                    yield return ((index * 20), [((byte)(0x80 | index)), 0x5A]);
                }

                break;
            default:
                for (var index = random.Next(maxValue: 12); (index > 0); index--) {
                    var length = random.Next(maxValue: 40, minValue: 1);
                    var bytes = new byte[length];

                    random.NextBytes(buffer: bytes);

                    yield return (random.Next(maxValue: (8192 - length)), bytes);
                }

                break;
        }
    }
}
