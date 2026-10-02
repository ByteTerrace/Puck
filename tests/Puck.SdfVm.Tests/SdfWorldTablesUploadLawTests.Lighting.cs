using Puck.Abstractions.Gpu;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldTablesUploadLawTests {
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void NativeLightingUploadsOnlyChangedWordsAndRetainsEveryTableAcrossRingSlots(bool ring) {
        const ulong GiB = (1UL << 30);
        var profile = (ring ? new GpuMemoryProfile(CoherentUnifiedMemory: false, DeviceLocalBytes: (12 * GiB),
            HostVisibleDeviceLocalBytes: (12 * GiB), LargestDeviceLocalHeapBytes: (12 * GiB), UnifiedMemory: false) : default);
        using var rig = new Rig(slots: 1, profile: profile);

        rig.Warm();
        var lighting = SdfLighting.Default();
        var light = lighting.GetLight(index: 0);

        lighting.SetLight(index: 0, light: light with { Color = light.Color with { X = 0.375f } });
        lighting.StarSeed = 0x80000001u;
        var expected = new SdfLightingUpload();

        expected.Pack(environment: lighting);

        (string Part, byte[] Bytes)[] tables = [
            ("light-frame", expected.LightFrameBytes.ToArray()), ("lights", expected.LightBytes.ToArray()),
            ("sky-frame", expected.SkyFrameBytes.ToArray()), ("sky-stops", expected.StopBytes.ToArray()),
            ("sky-softboxes", expected.SoftboxBytes.ToArray()),
        ];
        var bufferCount = 0;
        var hostBytes = 0L;
        var deviceBytes = 0L;

        foreach (var (part, bytes) in tables) {
            var buffers = rig.Gpu.BuffersOf(part: part);

            bufferCount += buffers.Length;
            Assert.Equal((ring ? 2 : 3), buffers.Length);
            foreach (var buffer in buffers) {
                if (buffer.HostVisible) {
                    Assert.Equal((bytes.Length + (ring ? 0 : StagingReserveBytes)), buffer.Bytes.Length);
                    Assert.Equal(actual: buffer.Aperture, expected: ring);
                    hostBytes += buffer.Bytes.Length;
                } else {
                    Assert.False(condition: ring);
                    Assert.Equal(bytes.Length, buffer.Bytes.Length);
                    deviceBytes += buffer.Bytes.Length;
                }
            }
        }
        Assert.Equal(actual: (bufferCount, hostBytes, deviceBytes), expected: (ring ? (10, 1728L, 0L) : (15, 22368L, 864L)));
        var memory = rig.Engine.LightingMemory;

        Assert.Equal((ring ? new GpuMemoryBytes(DeviceLocal: 1728, HostVisible: 0) : new GpuMemoryBytes(DeviceLocal: 864, HostVisible: 22368)), memory.Gpu);
        Assert.Equal(864UL, memory.CpuShadowBytes);
        Assert.Equal((ring ? 864UL : 11184UL), memory.CpuScratchBytes);
        Assert.Equal(864UL, expected.CpuScratchBytes);
        TestContext.Current.TestOutputHelper!.WriteLine(message: $"native lighting buffers: policy={(ring ? "ring" : "staged")}; allocations={bufferCount}; host-visible-bytes={hostBytes}; separate-device-local-bytes={deviceBytes}; cpu-shadow-bytes={memory.CpuShadowBytes}; cpu-scratch-bytes={memory.CpuScratchBytes}");

        void AssertTables(bool everySlot) {
            foreach (var (part, bytes) in tables) {
                var actual = rig.Gpu.BuffersOf(part: part).Where(predicate: buffer => (ring || !buffer.HostVisible)).ToArray();

                if (everySlot) { Assert.All(actual, buffer => Assert.Equal(actual: buffer.Bytes, expected: bytes)); } else { Assert.Contains(collection: actual, filter: buffer => bytes.AsSpan().SequenceEqual(other: buffer.Bytes)); }
            }
        }

        rig.Render(time: 0f, lighting: lighting);
        Assert.Equal((ring ? 0 : 2), rig.Gpu.UploadCopies);
        // The light color also supplies the sky's cloud illumination, so the sky record owes that word and the seed.
        Assert.Equal((ring ? 12L : ((2L * HeaderBytes) + (3L * (RunEntryBytes + sizeof(uint))))), rig.Gpu.HostBytes());
        AssertTables(everySlot: !ring);
        for (var frame = 0; (frame <= SdfWorldTables.FrameRingSize); frame++) {
            rig.Render(time: (frame + 1f), lighting: lighting);
            Assert.Equal(0, rig.Gpu.UploadCopies);
            Assert.Equal(((ring && (frame == 0)) ? 12L : 0L), rig.Gpu.HostBytes());
            AssertTables(everySlot: true);
        }
    }
}
