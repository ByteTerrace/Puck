using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Puck.Abstractions.Presentation;
using Puck.Platform.Probes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;
using Xunit;

namespace Puck.Platform.Windows.Tests;

/// <summary>Drives the shipped <c>ir-blob</c> kernel through the real runner ABI — a private device, a shared NT
/// handle opened read-only, the constant buffer, both dispatches, the staging readback, and a ring publication —
/// against a synthetic frame whose answer is known. Skips on a machine with no hardware adapter.</summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed unsafe class ProbeKernelRunnerTests {
    private const int FrameHeight = 64;
    private const int FrameWidth = 64;
    // The bright square: x in [48, 56), y in [8, 16) — top-right of the frame.
    private const int SquareLeft = 48;
    private const int SquareSize = 8;
    private const int SquareTop = 8;

    private static readonly string IrBlobKernelPath = ResolveIrBlobKernelPath();

    [Fact]
    public void Shipped_ir_blob_kernel_measures_a_bright_square_through_the_runner() {
        using var frame = SharedFrame.TryCreate(pixels: BuildPixels());

        if (frame is null) {
            Assert.Skip(reason: "no DXGI hardware adapter is available on this machine.");
        }

        var stream = new SyntheticSharedStream();
        var ring = new ProbeReadingRing();
        var constants = new byte[ProbeKernelRequestConstantsBytes];

        BitConverter.TryWriteBytes(destination: constants.AsSpan(start: 0), value: 0.5f);
        BitConverter.TryWriteBytes(destination: constants.AsSpan(start: 4), value: 0.01f);

        var request = new ProbeKernelRequest(
            AccumulateEntry: "accumulate",
            AdapterLuid: frame.AdapterLuid,
            ChannelCount: 4,
            Constants: constants,
            FinalizeEntry: "finalize",
            Height: FrameHeight,
            KernelSource: File.ReadAllText(path: IrBlobKernelPath),
            RateHz: 120U,
            TargetFormat: SurfaceFormat.R8G8B8A8Unorm,
            Width: FrameWidth
        );

        using var run = Win32D3D11ProbeKernelRunner.Start(request: request, stream: stream, sharedTargetHandles: [frame.SharedHandle], ring: ring);
        var deadline = (Stopwatch.GetTimestamp() + (3L * Stopwatch.Frequency));

        while ((run.Cycles < 2L) && !run.IsEnded && (Stopwatch.GetTimestamp() < deadline)) {
            Thread.Sleep(millisecondsTimeout: 5);
        }

        Assert.Null(@object: run.Fault);
        Assert.False(condition: run.IsEnded);
        Assert.True(condition: (run.Cycles >= 2L));
        Assert.True(condition: ring.TryReadLatest(reading: out var reading));
        Assert.Equal(expected: 4, actual: reading.ChannelCount);
        Assert.True(condition: (reading.CompletionTimestamp >= reading.CaptureTimestamp));

        // Centroid of the square: u = (48 + 4) / 64, v = (8 + 4) / 64 → x = 2u - 1, y = 1 - 2v (y-up).
        const double ExpectedX = (((SquareLeft + (SquareSize / 2.0)) / FrameWidth) * 2.0) - 1.0;
        const double ExpectedY = 1.0 - (((SquareTop + (SquareSize / 2.0)) / FrameHeight) * 2.0);
        const double ExpectedCoverage = ((double)(SquareSize * SquareSize)) / (FrameWidth * FrameHeight);

        Assert.InRange(actual: ((double)reading[0]), low: (ExpectedX - 0.01), high: (ExpectedX + 0.01));
        Assert.InRange(actual: ((double)reading[1]), low: (ExpectedY - 0.01), high: (ExpectedY + 0.01));
        Assert.InRange(actual: ((double)reading[2]), low: (ExpectedCoverage - 0.001), high: (ExpectedCoverage + 0.001));
        Assert.InRange(actual: ((double)reading[3]), low: 0.99, high: 1.0);
        Assert.InRange(actual: ((double)reading.Confidence), low: 0.99, high: 1.0);
    }

    // A ProbeKernelRequest's constants block: two floats padded to the 16-byte granule a constant buffer needs.
    private const int ProbeKernelRequestConstantsBytes = 16;

    private static byte[] BuildPixels() {
        var pixels = new byte[FrameWidth * FrameHeight * 4];

        for (var y = SquareTop; (y < (SquareTop + SquareSize)); y++) {
            for (var x = SquareLeft; (x < (SquareLeft + SquareSize)); x++) {
                var offset = (((y * FrameWidth) + x) * 4);

                pixels[offset] = 255;
                pixels[offset + 1] = 255;
                pixels[offset + 2] = 255;
                pixels[offset + 3] = 255;
            }
        }

        return pixels;
    }
    private static string ResolveIrBlobKernelPath([CallerFilePath] string callerFilePath = "") {
        var repositoryRoot = Path.GetFullPath(path: Path.Combine(Path.GetDirectoryName(path: callerFilePath)!, "..", ".."));

        return Path.Combine(repositoryRoot, "src", "Puck.Shaders", "Assets", "Probes", "ir-blob.hlsl");
    }

    // One R8G8B8A8 texture on the first hardware adapter, created with an NT shared handle exactly the way a
    // consumer render device provisions a camera target, so the runner opens it on its own device.
    private sealed class SharedFrame : IDisposable {
        private readonly IDXGIAdapter1* m_adapter;
        private readonly ID3D11DeviceContext* m_context;
        private readonly ID3D11Device* m_device;
        private readonly SafeFileHandle m_handle;
        private readonly ID3D11Texture2D* m_texture;

        private SharedFrame(IDXGIAdapter1* adapter, long adapterLuid, ID3D11Device* device, ID3D11DeviceContext* context, ID3D11Texture2D* texture, SafeFileHandle handle) {
            m_adapter = adapter;
            m_context = context;
            m_device = device;
            m_handle = handle;
            m_texture = texture;
            AdapterLuid = adapterLuid;
        }

        public long AdapterLuid { get; }
        public nint SharedHandle => m_handle.DangerousGetHandle();

        public static SharedFrame? TryCreate(byte[] pixels) {
            var adapter = FindHardwareAdapter(adapterLuid: out var adapterLuid);

            if (adapter is null) {
                return null;
            }

            ID3D11Device* device = null;
            ID3D11DeviceContext* context = null;
            ID3D11Texture2D* texture = null;

            try {
                Win32D3D11.CreateMultithreadedDevice(
                    adapter: ((IDXGIAdapter*)adapter),
                    context: out context,
                    device: out device,
                    driverType: D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN,
                    flags: D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT
                );

                var description = new D3D11_TEXTURE2D_DESC {
                    Width = FrameWidth,
                    Height = FrameHeight,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                    Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                    BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                    MiscFlags = (D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED_NTHANDLE),
                };

                fixed (byte* pixelData = pixels) {
                    var initialData = new D3D11_SUBRESOURCE_DATA {
                        pSysMem = pixelData,
                        SysMemPitch = (FrameWidth * 4),
                    };

                    device->CreateTexture2D(pDesc: &description, pInitialData: &initialData, ppTexture2D: &texture);
                }

                // No keyed mutex guards the shared texture, so the initial upload must have retired on this device
                // before another device opens and samples it — the same drain a camera producer performs per slot.
                var queryDescription = new D3D11_QUERY_DESC { Query = D3D11_QUERY.D3D11_QUERY_EVENT };
                ID3D11Query* query = null;

                device->CreateQuery(pQueryDesc: &queryDescription, ppQuery: &query);

                try {
                    Win32D3D11.WaitForCompletion(context: context, query: query);
                } finally {
                    Release(value: query);
                }

                var resourceIid = IDXGIResource1.IID_Guid;

                Win32D3D11.ThrowIfFailed(hr: ((IUnknown*)texture)->QueryInterface(ppvObject: out var resourcePointer, riid: in resourceIid), operation: "QueryInterface(IDXGIResource1)");

                var resource = ((IDXGIResource1*)resourcePointer);
                HANDLE handle;

                try {
                    resource->CreateSharedHandle(
                        pAttributes: null,
                        dwAccess: ((uint)(DXGI_SHARED_RESOURCE_RW.DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_RW.DXGI_SHARED_RESOURCE_WRITE)),
                        lpName: default,
                        pHandle: &handle
                    );
                } finally {
                    _ = ((IUnknown*)resource)->Release();
                }

                return new SharedFrame(
                    adapter: adapter,
                    adapterLuid: adapterLuid,
                    context: context,
                    device: device,
                    handle: new SafeFileHandle(ownsHandle: true, preexistingHandle: ((nint)handle.Value)),
                    texture: texture
                );
            } catch {
                Release(value: texture);
                Release(value: context);
                Release(value: device);
                Release(value: adapter);
                throw;
            }
        }
        public void Dispose() {
            m_handle.Dispose();
            Release(value: m_texture);
            Release(value: m_context);
            Release(value: m_device);
            Release(value: m_adapter);
        }

        private static IDXGIAdapter1* FindHardwareAdapter(out long adapterLuid) {
            Win32D3D11.ThrowIfFailed(hr: PInvoke.CreateDXGIFactory1(ppFactory: out var factoryPointer, riid: IDXGIFactory1.IID_Guid), operation: "CreateDXGIFactory1");

            var factory = ((IDXGIFactory1*)factoryPointer);

            try {
                for (var index = 0u; ; index++) {
                    IDXGIAdapter1* adapter;
                    var hr = factory->EnumAdapters1(Adapter: index, ppAdapter: &adapter);

                    if (HRESULT.DXGI_ERROR_NOT_FOUND == hr) {
                        adapterLuid = 0L;

                        return null;
                    }

                    Win32D3D11.ThrowIfFailed(hr: hr, operation: "IDXGIFactory1::EnumAdapters1");

                    var description = adapter->GetDesc1();

                    if (0 == (description.Flags & DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE)) {
                        adapterLuid = ((((long)description.AdapterLuid.HighPart) << 32) | description.AdapterLuid.LowPart);

                        return adapter;
                    }

                    _ = adapter->Release();
                }
            } finally {
                _ = factory->Release();
            }
        }
        private static void Release<T>(T* value) where T : unmanaged {
            if (value is not null) {
                _ = ((IUnknown*)value)->Release();
            }
        }
    }
    // A one-slot shared stream whose frame "arrives" anew every time the runner releases the slot, so consecutive
    // cycles each see a fresh FrameVersion against the same texture.
    private sealed class SyntheticSharedStream : ICameraSharedStream {
        private long m_frameVersion = 1L;
        private long m_lastFrameTimestamp = Stopwatch.GetTimestamp();

        public long FrameVersion => Interlocked.Read(location: ref m_frameVersion);
        public int Height => FrameHeight;
        public long LastFrameTimestamp => Interlocked.Read(location: ref m_lastFrameTimestamp);
        public int LatestSlot => 0;
        public CameraCaptureFormat NativeFormat => new(Subtype: "RGB32", RateHz: 30.0);
        public CameraSensor Sensor => CameraSensor.Infrared;
        public SurfaceFormat TargetFormat => SurfaceFormat.R8G8B8A8Unorm;
        public int Width => FrameWidth;

        public void Release(int slot) {
            _ = Interlocked.Exchange(location1: ref m_lastFrameTimestamp, value: Stopwatch.GetTimestamp());
            _ = Interlocked.Increment(location: ref m_frameVersion);
        }
        public void Start(IReadOnlyList<nint> sharedTargetHandles) {
        }
        public bool TryAcquireLatest(out int slot) {
            slot = 0;

            return true;
        }
    }
}
