using System.Runtime.Versioning;

using Puck.Abstractions.Gpu;
using Xunit;

namespace Puck.DirectX.Tests;

/// <summary>The Direct3D 12 pipeline library's file is named from the device identity the context already read, never
/// from a second query of the adapter. When <c>IDXGIAdapter::CheckInterfaceSupport</c> will not report the user-mode
/// driver version, <see cref="Apis.DirectXNativeDeviceApi.GetDeviceIdentity"/> records the version as zero; that identity
/// still names a file on disk, and a missed creation is written to it, so the library does not fall back to memory
/// only. No device is created; the file is the one <see cref="Interop.DirectXDeviceContext"/> hands the library.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXPipelineCacheFileLawTests : IDisposable {
    private readonly string m_directory = Directory.CreateTempSubdirectory(prefix: "puck-directx-pipeline-cache-").FullName;

    public void Dispose() => Directory.Delete(
        path: m_directory,
        recursive: true
    );
    [Fact]
    public void ADriverVersionTheAdapterWillNotReportStillPersistsTheLibrary() {
        // What GetDeviceIdentity returns when CheckInterfaceSupport throws: no display version, and a raw version of zero.
        var identity = new GpuDeviceIdentity(
            AdapterName: "Example GPU 4000",
            ApiVersion: "12_2",
            Backend: "directx",
            DeviceId: 0x2786U,
            DriverVersion: string.Empty,
            DriverVersionRaw: 0UL,
            VendorId: 0x10DEU
        );
        var file = GpuPipelineCacheFile.Open(
            identity: identity,
            store: new GpuPipelineCacheStore(
                contentKey: "0123456789abcdef",
                directory: m_directory
            ),
            work: new GpuPipelineCacheWork(backend: "directx")
        );

        Assert.Equal(
            expected: $"{m_directory.Replace(newChar: '/', oldChar: '\\')}/directx/10de-2786-0000000000000000/0123456789abcdef.bin",
            actual: file.Path
        );

        file.Count(cacheHit: false);
        file.Persist(
            serialize: static library => library,
            state: new byte[] { 0x44, 0x33, 0x44, 0x31, 0x32 }
        );

        Assert.Equal(
            expected: [0x44, 0x33, 0x44, 0x31, 0x32],
            actual: File.ReadAllBytes(path: file.Path!)
        );
    }
}
