using System.Buffers;
using System.Text;
using System.Text.Json;
using Puck.Abstractions.Gpu;

namespace Puck.Abstractions.Tests;

/// <summary>
/// Laws for <see cref="GpuDeviceIdentity"/>: its one-line fields and its JSON carry what the backend reported, a
/// value holding a space is quoted, a backend-specific field the backend did not report is left out of the line, and
/// the two version formats read the packed values their APIs define.
/// </summary>
public sealed class GpuDeviceIdentityLawTests {
    private static readonly GpuDeviceIdentity Vulkan = new(
        AdapterName: "Example GPU 4000",
        ApiVersion: "1.4.303",
        Backend: "vulkan",
        ConformanceVersion: "1.4.1.0",
        DeviceId: 0x2786U,
        DriverId: 4U,
        DriverName: "Example",
        DriverVersion: "566.36",
        DriverVersionRaw: 0x8D8D8000UL,
        PipelineCacheUuid: "00112233445566778899aabbccddeeff",
        VendorId: 0x10DEU
    );
    private static readonly GpuDeviceIdentity DirectX = new(
        AdapterName: "Example GPU 4000",
        ApiVersion: "12_2",
        Backend: "directx",
        DeviceId: 0x2786U,
        DriverVersion: "32.0.15.6636",
        DriverVersionRaw: 0x0020_0000_000F_19ECUL,
        VendorId: 0x10DEU
    );

    [Fact]
    public void TheLineCarriesEveryReportedField() {
        Assert.Equal(
            expected: " backend=vulkan adapter=\"Example GPU 4000\" vendor=0x10de device=0x2786 driver=566.36 driver.raw=0x8d8d8000 api=1.4.303 driver.name=Example driver.id=4 conformance=1.4.1.0 pipeline-cache.uuid=00112233445566778899aabbccddeeff",
            actual: Vulkan.AppendFields(builder: new StringBuilder()).ToString()
        );
        Assert.Equal(
            expected: " backend=directx adapter=\"Example GPU 4000\" vendor=0x10de device=0x2786 driver=32.0.15.6636 driver.raw=0x200000000f19ec api=12_2",
            actual: DirectX.AppendFields(builder: new StringBuilder()).ToString()
        );
    }
    [Fact]
    public void TheJsonCarriesEveryField() {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(bufferWriter: buffer)) {
            DirectX.WriteJson(writer: writer);
        }

        Assert.Equal(
            expected: """{"backend":"directx","adapter":"Example GPU 4000","vendor":4318,"device":10118,"driver":"32.0.15.6636","driver.raw":9007199255730668,"api":"12_2","driver.name":"","driver.id":0,"conformance":"","pipeline-cache.uuid":""}""",
            actual: Encoding.UTF8.GetString(bytes: buffer.WrittenSpan)
        );
    }
    [Fact]
    public void AQuoteInsideAValueIsEscaped() =>
        Assert.Equal(
            expected: " backend=vulkan adapter=\"A \\\"quoted\\\" GPU\" vendor=0x0001 device=0x0002 driver.raw=0x0",
            actual: new GpuDeviceIdentity(
                AdapterName: "A \"quoted\" GPU",
                ApiVersion: "",
                Backend: "vulkan",
                DeviceId: 2U,
                DriverVersion: "",
                DriverVersionRaw: 0UL,
                VendorId: 1U
            ).AppendFields(builder: new StringBuilder()).ToString()
        );
    [Fact]
    public void TheVersionFormatsReadTheirPackedLayouts() {
        // VK_MAKE_API_VERSION(0, 1, 4, 303), with a variant in the top bits the display drops.
        Assert.Equal(
            expected: "1.4.303",
            actual: GpuDeviceIdentity.FormatVulkanVersion(packed: (1U << 22) | (4U << 12) | 303U | (1U << 29))
        );
        Assert.Equal(
            expected: "32.0.15.6636",
            actual: GpuDeviceIdentity.FormatDirectXDriverVersion(version: (32UL << 48) | (0UL << 32) | (15UL << 16) | 6636UL)
        );
    }
}
