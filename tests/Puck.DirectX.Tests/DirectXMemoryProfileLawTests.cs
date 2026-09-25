using System.Runtime.Versioning;

using Puck.Abstractions.Gpu;
using Puck.DirectX.Apis;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>The Direct3D 12 memory profile is a pure function of the structures the runtime and DXGI report
/// (<see cref="DirectXNativeDeviceApi.MemoryProfile"/>): fixture structures for a cache-coherent unified adapter, a
/// unified one without cache coherence, and discrete adapters with and without GPU upload heaps fill the profile each
/// should and select the policy each should. No device is created.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXMemoryProfileLawTests {
    private const ulong GiB = (1UL << 30);
    private const ulong MiB = (1UL << 20);
    // A region the size of a small host table.
    private const ulong TableBytes = (64UL * 1024UL);

    [Fact]
    public void ACacheCoherentUnifiedAdapterWritesInPlace() {
        var profile = Fill(
            cacheCoherent: true,
            dedicated: (512UL * MiB),
            shared: (8UL * GiB),
            unified: true,
            uploadHeaps: false
        );

        Assert.Equal(
            expected: new GpuMemoryProfile(
                CoherentUnifiedMemory: true,
                DeviceLocalBytes: ((512UL * MiB) + (8UL * GiB)),
                HostVisibleDeviceLocalBytes: ((512UL * MiB) + (8UL * GiB)),
                LargestDeviceLocalHeapBytes: ((512UL * MiB) + (8UL * GiB))
            ),
            actual: profile
        );
        Assert.Equal(
            expected: GpuResidencyPolicy.InPlace,
            actual: GpuResidency.Select(byteCount: TableBytes, profile: profile, readersInFlight: false)
        );
    }
    [Fact]
    public void AUnifiedAdapterWithoutCacheCoherenceWritesThroughARing() {
        var profile = Fill(
            cacheCoherent: false,
            dedicated: (512UL * MiB),
            shared: (8UL * GiB),
            unified: true,
            uploadHeaps: false
        );

        Assert.False(condition: profile.CoherentUnifiedMemory);
        Assert.Equal(
            expected: GpuResidencyPolicy.Ring,
            actual: GpuResidency.Select(byteCount: TableBytes, profile: profile, readersInFlight: false)
        );
    }
    [Fact]
    public void ADiscreteAdapterWritesThroughARingOnlyWithGpuUploadHeaps() {
        var withUploadHeaps = Fill(
            cacheCoherent: false,
            dedicated: (12UL * GiB),
            shared: (16UL * GiB),
            unified: false,
            uploadHeaps: true
        );
        var without = Fill(
            cacheCoherent: false,
            dedicated: (12UL * GiB),
            shared: (16UL * GiB),
            unified: false,
            uploadHeaps: false
        );

        Assert.Equal(
            expected: new GpuMemoryProfile(
                CoherentUnifiedMemory: false,
                DeviceLocalBytes: (12UL * GiB),
                HostVisibleDeviceLocalBytes: (12UL * GiB),
                LargestDeviceLocalHeapBytes: (12UL * GiB)
            ),
            actual: withUploadHeaps
        );
        Assert.Equal(
            expected: (withUploadHeaps with { HostVisibleDeviceLocalBytes = 0UL }),
            actual: without
        );
        Assert.Equal(
            expected: GpuResidencyPolicy.Ring,
            actual: GpuResidency.Select(byteCount: TableBytes, profile: withUploadHeaps, readersInFlight: false)
        );
        Assert.Equal(
            expected: GpuResidencyPolicy.Staged,
            actual: GpuResidency.Select(byteCount: TableBytes, profile: without, readersInFlight: false)
        );
    }

    private static GpuMemoryProfile Fill(bool unified, bool cacheCoherent, ulong dedicated, ulong shared, bool uploadHeaps) {
        var architecture = new D3D12_FEATURE_DATA_ARCHITECTURE {
            CacheCoherentUMA = cacheCoherent,
            UMA = unified,
        };
        var adapter = new DXGI_ADAPTER_DESC1 {
            DedicatedVideoMemory = ((nuint)dedicated),
            SharedSystemMemory = ((nuint)shared),
        };
        var options16 = new D3D12_FEATURE_DATA_D3D12_OPTIONS16 {
            GPUUploadHeapSupported = uploadHeaps,
        };

        return DirectXNativeDeviceApi.MemoryProfile(
            adapter: in adapter,
            architecture: in architecture,
            options16: in options16
        );
    }
}
