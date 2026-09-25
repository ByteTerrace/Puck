using Puck.Abstractions.Gpu;
using Puck.Testing;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for residency. The memory profile's Vulkan and Direct3D 12 fills are pure functions of the native values they
/// read. The selector pins one policy for each of four synthetic devices — coherent unified memory, a discrete adapter
/// with a small host-visible aperture, one with none, and a profile reporting nothing — with and without a reader in
/// flight, and stages a region past its share of the aperture. One region's bytes are identical under all three
/// policies, frame by frame, read back through the device-free memory model that runs the copy kernel. A staged copy
/// states its header and runs in the staging buffer and owes only differing words; an external destination takes the
/// region at its target, and a retarget owes every word written after it.
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
                LargestDeviceLocalHeapBytes: (4UL * GiB),
                UnifiedMemory: true
            ),
            actual: CoherentUnified
        );
        Assert.Equal(
            expected: new GpuMemoryProfile(
                CoherentUnifiedMemory: false,
                DeviceLocalBytes: ((12UL * GiB) + (256UL * MiB)),
                HostVisibleDeviceLocalBytes: (256UL * MiB),
                LargestDeviceLocalHeapBytes: (12UL * GiB),
                UnifiedMemory: false
            ),
            actual: DiscreteSmallAperture
        );
        Assert.Equal(
            expected: new GpuMemoryProfile(
                CoherentUnifiedMemory: false,
                DeviceLocalBytes: (8UL * GiB),
                HostVisibleDeviceLocalBytes: 0UL,
                LargestDeviceLocalHeapBytes: (8UL * GiB),
                UnifiedMemory: false
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
                LargestDeviceLocalHeapBytes: ((512UL * MiB) + (8UL * GiB)),
                UnifiedMemory: true
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
                LargestDeviceLocalHeapBytes: (12UL * GiB),
                UnifiedMemory: false
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
                LargestDeviceLocalHeapBytes: (12UL * GiB),
                UnifiedMemory: false
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
    // The policy table: each synthetic device, with and without a reader in flight while the host writes. Only coherent
    // unified memory with no reader in flight writes in place; a reader in flight turns it into a ring, as every
    // per-frame owner's frame ring does.
    [InlineData("coherent-unified", false, GpuResidencyPolicy.InPlace)]
    [InlineData("coherent-unified", true, GpuResidencyPolicy.Ring)]
    [InlineData("discrete-small-aperture", false, GpuResidencyPolicy.Ring)]
    [InlineData("discrete-small-aperture", true, GpuResidencyPolicy.Ring)]
    [InlineData("discrete-no-aperture", false, GpuResidencyPolicy.Staged)]
    [InlineData("discrete-no-aperture", true, GpuResidencyPolicy.Staged)]
    [InlineData("default", false, GpuResidencyPolicy.Staged)]
    [InlineData("default", true, GpuResidencyPolicy.Staged)]
    [Theory]
    public void TheSelectorPinsOnePolicyPerDeviceAndReaders(string device, bool readersInFlight, GpuResidencyPolicy policy) {
        var profile = device switch {
            "coherent-unified" => CoherentUnified,
            "discrete-small-aperture" => DiscreteSmallAperture,
            "discrete-no-aperture" => DiscreteNoAperture,
            _ => default,
        };

        Assert.Equal(
            expected: policy,
            actual: GpuResidency.Select(
                byteCount: TableBytes,
                profile: profile,
                readersInFlight: readersInFlight
            )
        );
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ARegionPastItsShareOfTheApertureIsStaged(bool readersInFlight) {
        var share = ((256UL * MiB) / GpuResidency.HostVisibleShare);

        Assert.Equal(
            expected: GpuResidencyPolicy.Ring,
            actual: GpuResidency.Select(byteCount: share, profile: DiscreteSmallAperture, readersInFlight: readersInFlight)
        );
        Assert.Equal(
            expected: GpuResidencyPolicy.Staged,
            actual: GpuResidency.Select(byteCount: (share + 1UL), profile: DiscreteSmallAperture, readersInFlight: readersInFlight)
        );
        Assert.Equal(
            expected: GpuResidencyPolicy.Staged,
            actual: GpuResidency.Select(byteCount: (((4UL * GiB) / GpuResidency.HostVisibleShare) + 1UL), profile: CoherentUnified, readersInFlight: readersInFlight)
        );
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => GpuResidency.Select(byteCount: 0UL, profile: CoherentUnified, readersInFlight: readersInFlight));
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
                memory: GpuHostVisibleMemory.Host,
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
            memory: GpuHostVisibleMemory.Host,
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
            memory: GpuHostVisibleMemory.Host,
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
            memory: GpuHostVisibleMemory.Host,
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
    [Fact]
    public void AStagedCopyStatesItselfInTheStagingBufferAndOwesOnlyTheWordsThatDiffer() {
        const int RunEntryBytes = 8;
        const int HeaderBytes = (GpuRegion.CopyHeaderWords * sizeof(uint));
        var gpu = new UploadModelGpu(reportVersion: 0);
        using var copy = CopyPipeline(gpu: gpu);
        using var region = new GpuRegion(
            bindings: gpu.Services.Bindings,
            buffers: gpu.Services.BufferFactory,
            byteCount: 256,
            copyPipeline: copy,
            memory: GpuHostVisibleMemory.Host,
            policy: GpuResidencyPolicy.Staged,
            recorder: gpu.Services.Recorder,
            slotCount: 2
        );

        region.Flush(slot: 0);
        region.RecordCopy(commandBuffer: 2, slot: 0);
        gpu.ResetTallies();

        // Two words far apart are two runs: the header, a run-table entry per run and each word. The same bytes again
        // owe nothing, and a slot owing nothing writes and dispatches nothing.
        _ = region.Write(bytes: [1, 0, 0, 0], offset: 8);
        _ = region.Write(bytes: [2], offset: 200);
        Assert.False(condition: region.Write(bytes: [1, 0, 0, 0], offset: 8));
        region.Flush(slot: 1);
        Assert.True(condition: region.OwesCopy);
        region.RecordCopy(commandBuffer: 2, slot: 1);
        Assert.Equal(expected: ((long)((HeaderBytes + (2 * RunEntryBytes)) + (2 * sizeof(uint)))), actual: gpu.HostBytes());
        Assert.Equal(expected: 1, actual: gpu.UploadCopies);
        Assert.Equal(expected: region.Contents.ToArray(), actual: gpu.Memory(bufferHandle: region.Buffer(slot: 1).BufferHandle));
        Assert.False(condition: region.OwesCopy);

        gpu.ResetTallies();
        region.Flush(slot: 0);
        region.RecordCopy(commandBuffer: 2, slot: 0);
        Assert.Equal(expected: (0L, 0), actual: (gpu.HostBytes(), gpu.UploadCopies));
        Assert.Null(@object: GpuRegion.CopyPipeline.PushConstantBinding);
    }
    [Fact]
    public void AnExternalDestinationTakesTheRegionAtItsTargetAndARetargetOwesEveryWordWrittenAfterIt() {
        const int DestinationWords = 64;
        var gpu = new UploadModelGpu(reportVersion: 0);
        using var copy = CopyPipeline(gpu: gpu);
        using var destination = gpu.Services.BufferFactory.CreateDeviceLocal(
            sizeBytes: (DestinationWords * sizeof(uint)),
            usage: GpuBufferUsage.Storage
        );
        var memory = gpu.Memory(bufferHandle: destination.BufferHandle);

        memory.AsSpan().Fill(value: 0xEE);

        using var region = new GpuRegion(
            bindings: gpu.Services.Bindings,
            buffers: gpu.Services.BufferFactory,
            byteCount: 32,
            copyPipeline: copy,
            destination: destination,
            recorder: gpu.Services.Recorder,
            slotCount: 2
        );
        byte[] block = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

        // A new external region owes nothing: what the destination holds is its owner's.
        Assert.Equal(expected: GpuResidencyPolicy.Staged, actual: region.Policy);
        Assert.Same(expected: destination, actual: region.Buffer(slot: 1));
        Assert.False(condition: region.OwesCopy);

        region.Target(destinationWord: 40);
        Assert.True(condition: region.Write(bytes: block, offset: 0));
        region.Flush(slot: 0);
        region.RecordCopy(commandBuffer: 2, slot: 0);
        Assert.Equal(expected: block, actual: memory[160..172]);
        Assert.All(collection: memory[..160].Concat(second: memory[172..]), action: static value => Assert.Equal(actual: value, expected: 0xEE));

        // Other work writes the destination; the same bytes retargeted there are owed whole, and without a retarget
        // they would be owed nothing.
        memory.AsSpan(length: 12, start: 160).Clear();
        Assert.False(condition: region.Write(bytes: block, offset: 0));
        region.Target(destinationWord: 40);
        Assert.True(condition: region.Write(bytes: block, offset: 0));
        region.Flush(slot: 1);
        region.RecordCopy(commandBuffer: 2, slot: 1);
        Assert.Equal(expected: block, actual: memory[160..172]);

        // A retarget waits for the copy of what the region owes, and a write stays inside the destination.
        _ = region.Write(bytes: [9], offset: 0);
        _ = Assert.Throws<InvalidOperationException>(testCode: () => region.Target(destinationWord: 0));
        region.Flush(slot: 0);
        region.RecordCopy(commandBuffer: 2, slot: 0);
        region.Target(destinationWord: (DestinationWords - 4));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => region.Write(bytes: new byte[20], offset: 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(testCode: () => region.Target(destinationWord: DestinationWords));
    }
    [Fact]
    public void ARingLivesInTheApertureOnlyWhereTheDeviceExposesOneOntoDedicatedMemory() {
        Assert.Equal(expected: GpuHostVisibleMemory.DeviceLocal, actual: GpuResidency.RingMemory(profile: DiscreteSmallAperture));
        Assert.Equal(expected: GpuHostVisibleMemory.Host, actual: GpuResidency.RingMemory(profile: CoherentUnified));
        Assert.Equal(expected: GpuHostVisibleMemory.Host, actual: GpuResidency.RingMemory(profile: DiscreteNoAperture));
        Assert.Equal(expected: GpuHostVisibleMemory.Host, actual: GpuResidency.RingMemory(profile: default));
        Assert.Equal(expected: GpuHostVisibleMemory.Host, actual: GpuResidency.RingMemory(profile: GpuMemoryProfile.FromDirectX(
            cacheCoherentUnifiedMemory: false,
            dedicatedVideoMemory: (512UL * MiB),
            gpuUploadHeapSupported: false,
            sharedSystemMemory: (8UL * GiB),
            unifiedMemory: true
        )));

        foreach (var (profile, aperture) in ((ReadOnlySpan<(GpuMemoryProfile, int)>)[(DiscreteSmallAperture, 3), (CoherentUnified, 0)])) {
            var gpu = new UploadModelGpu(reportVersion: 0);
            using var copy = CopyPipeline(gpu: gpu);
            using var region = new GpuRegion(
                bindings: gpu.Services.Bindings,
                buffers: gpu.Services.BufferFactory,
                byteCount: 64,
                copyPipeline: copy,
                memory: GpuResidency.RingMemory(profile: profile),
                policy: GpuResidency.Select(byteCount: 64UL, profile: profile, readersInFlight: true),
                recorder: gpu.Services.Recorder,
                slotCount: 3
            );

            Assert.Equal(expected: GpuResidencyPolicy.Ring, actual: region.Policy);
            Assert.Equal(expected: aperture, actual: gpu.ApertureBuffers);
            Assert.Equal(expected: (3 - aperture), actual: gpu.HostBuffers);
        }
    }
    [Fact]
    public void AStagedCopyPastOneRowOfGroupsDispatchesMoreRowsAndStaysExact() {
        var words = (((int)GpuRegion.CopyRowThreads) + 1000);
        var gpu = new UploadModelGpu(reportVersion: 0);
        using var copy = CopyPipeline(gpu: gpu);
        using var region = new GpuRegion(
            bindings: gpu.Services.Bindings,
            buffers: gpu.Services.BufferFactory,
            byteCount: (words * sizeof(uint)),
            copyPipeline: copy,
            memory: GpuHostVisibleMemory.Host,
            policy: GpuResidencyPolicy.Staged,
            recorder: gpu.Services.Recorder,
            slotCount: 2
        );
        var bytes = new byte[(words * sizeof(uint))];

        new Random(Seed: 11).NextBytes(buffer: bytes);
        _ = region.Write(bytes: bytes, offset: 0);
        region.Flush(slot: 0);
        region.RecordCopy(commandBuffer: 2, slot: 0);
        Assert.Equal(expected: bytes, actual: gpu.Memory(bufferHandle: region.Buffer(slot: 0).BufferHandle));
        Assert.Equal(expected: (GpuRegion.CopyMaxGroupsPerDimension, 2U), actual: GpuRegion.CopyGroups(count: ((uint)words)));
        Assert.Equal(expected: (1U, 1U), actual: GpuRegion.CopyGroups(count: 1U));
        Assert.Equal(expected: (GpuRegion.CopyMaxGroupsPerDimension, 1U), actual: GpuRegion.CopyGroups(count: GpuRegion.CopyRowThreads));
    }
    [Fact]
    public void OnlyARegionWithAnExternalDestinationRetargets() {
        var gpu = new UploadModelGpu(reportVersion: 0);
        using var copy = CopyPipeline(gpu: gpu);
        using var region = new GpuRegion(
            bindings: gpu.Services.Bindings,
            buffers: gpu.Services.BufferFactory,
            byteCount: 64,
            copyPipeline: copy,
            memory: GpuHostVisibleMemory.Host,
            policy: GpuResidencyPolicy.Staged,
            recorder: gpu.Services.Recorder,
            slotCount: 2
        );

        _ = Assert.Throws<InvalidOperationException>(testCode: () => region.Target(destinationWord: 0));
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
