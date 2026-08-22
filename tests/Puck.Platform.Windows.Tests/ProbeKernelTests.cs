using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

/// <summary>Drives the shipped kernels through the real kernel ABI on a private device — the constant buffers, both
/// dispatches, the output ring, the staging readback, and a ring publication — against synthetic frames whose
/// answers are known. Skips on a machine with no hardware adapter.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class ProbeKernelTests {
    private const int FrameHeight = 64;
    private const int FrameWidth = 64;
    // The bright square: x in [48, 56), y in [8, 16) — top-right of the frame.
    private const int SquareLeft = 48;
    private const int SquareSize = 8;
    private const int SquareTop = 8;

    [Fact]
    public void Shipped_ir_blob_kernel_measures_a_bright_square() {
        using var bench = Bench.TryCreate();

        if (bench is null) {
            Assert.Skip(reason: "no DXGI hardware adapter is available on this machine.");
        }

        var lit = bench.CreateFrame(pixels: BuildSquare(inside: [255, 255, 255, 255], outside: [0, 0, 0, 255]));
        var ring = new ProbeReadingRing();
        var constants = new byte[16];

        BitConverter.TryWriteBytes(destination: constants.AsSpan(start: 0), value: 0.5f);
        BitConverter.TryWriteBytes(destination: constants.AsSpan(start: 4), value: 0.01f);

        var request = new ProbeKernelRequest(
            KernelSource: File.ReadAllText(path: KernelPath(name: "ir-blob")),
            AccumulateEntry: "accumulate",
            FinalizeEntry: "finalize",
            Constants: constants,
            ChannelCount: 4,
            RateHz: 240U,
            Inputs: [new ProbeKernelInput(Sensor: CameraSensor.Infrared)],
            Trigger: CameraSensor.Infrared
        );

        using var kernel = bench.CreateKernel(request: in request, ring: ring);

        Assert.True(condition: kernel.TryRun(inputViews: [lit.View], captureTimestamp: Stopwatch.GetTimestamp()));
        Assert.Equal(expected: 1L, actual: kernel.Cycles);
        Assert.True(condition: ring.TryReadLatest(reading: out var reading));
        Assert.Equal(expected: 4, actual: reading.ChannelCount);
        Assert.Equal(expected: -1, actual: reading.OutputSlot);
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
    [Fact]
    public void A_cycle_inside_the_rate_period_is_skipped() {
        using var bench = Bench.TryCreate();

        if (bench is null) {
            Assert.Skip(reason: "no DXGI hardware adapter is available on this machine.");
        }

        var lit = bench.CreateFrame(pixels: BuildSquare(inside: [255, 255, 255, 255], outside: [0, 0, 0, 255]));
        var ring = new ProbeReadingRing();
        var request = new ProbeKernelRequest(
            KernelSource: File.ReadAllText(path: KernelPath(name: "ir-blob")),
            AccumulateEntry: "accumulate",
            FinalizeEntry: "finalize",
            Constants: new byte[16],
            ChannelCount: 4,
            RateHz: 1U,
            Inputs: [new ProbeKernelInput(Sensor: CameraSensor.Infrared)],
            Trigger: CameraSensor.Infrared
        );

        using var kernel = bench.CreateKernel(request: in request, ring: ring);

        Assert.True(condition: kernel.TryRun(inputViews: [lit.View], captureTimestamp: Stopwatch.GetTimestamp()));
        Assert.False(condition: kernel.TryRun(inputViews: [lit.View], captureTimestamp: Stopwatch.GetTimestamp()));
        Assert.Equal(expected: 1L, actual: kernel.Cycles);
    }
    [Fact]
    public void Shipped_faerie_kernel_relights_into_its_output_ring() {
        using var bench = Bench.TryCreate();

        if (bench is null) {
            Assert.Skip(reason: "no DXGI hardware adapter is available on this machine.");
        }

        var color = bench.CreateFrame(pixels: BuildSquare(inside: [128, 128, 128, 255], outside: [128, 128, 128, 255]));
        var lit = bench.CreateFrame(pixels: BuildSquare(inside: [255, 255, 255, 255], outside: [0, 0, 0, 255]));
        var unlit = bench.CreateFrame(pixels: BuildSquare(inside: [0, 0, 0, 255], outside: [0, 0, 0, 255]));
        var targets = new[] { bench.CreateSharedTarget(), bench.CreateSharedTarget() };
        var slots = new LatestSlotPublication();

        slots.Configure(targetCount: targets.Length);

        var ring = new ProbeReadingRing();
        var request = new ProbeKernelRequest(
            KernelSource: File.ReadAllText(path: KernelPath(name: "faerie")),
            AccumulateEntry: "accumulate",
            FinalizeEntry: "finalize",
            Constants: FaerieDefaults(),
            ChannelCount: 4,
            RateHz: 240U,
            Inputs: [new ProbeKernelInput(Sensor: CameraSensor.Color), new ProbeKernelInput(Sensor: CameraSensor.Infrared), new ProbeKernelInput(Sensor: CameraSensor.Infrared, Previous: true)],
            Trigger: CameraSensor.Color,
            Output: new ProbeKernelOutput(
                Width: FrameWidth,
                Height: FrameHeight,
                TargetFormat: SurfaceFormat.R8G8B8A8Unorm,
                SharedTargetHandles: [targets[0].SharedHandle, targets[1].SharedHandle],
                Slots: slots
            )
        );

        using var kernel = bench.CreateKernel(request: in request, ring: ring);

        Assert.True(condition: kernel.TryRun(inputViews: [color.View, lit.View, unlit.View], captureTimestamp: Stopwatch.GetTimestamp()));
        Assert.True(condition: ring.TryReadLatest(reading: out var reading));
        Assert.Equal(expected: 4, actual: reading.ChannelCount);
        Assert.InRange(actual: reading.OutputSlot, low: 0, high: (targets.Length - 1));
        Assert.Equal(expected: reading.OutputSlot, actual: slots.LatestSlot);

        const double ExpectedCoverage = ((double)(SquareSize * SquareSize)) / (FrameWidth * FrameHeight);

        Assert.InRange(actual: ((double)reading[0]), low: -1.0, high: 1.0);
        Assert.InRange(actual: ((double)reading[1]), low: -1.0, high: 1.0);
        Assert.InRange(actual: ((double)reading[2]), low: 0.99, high: 1.0);
        Assert.InRange(actual: ((double)reading[3]), low: (ExpectedCoverage - 0.002), high: (ExpectedCoverage + 0.002));

        // The relit frame: the strobe-lit square is brighter than the unresponsive wall around it, which keeps only
        // ambient plus the light's spill.
        var pixels = bench.ReadBack(target: targets[reading.OutputSlot]);
        var inside = Luminance(pixels: pixels, x: (SquareLeft + (SquareSize / 2)), y: (SquareTop + (SquareSize / 2)));
        var outside = Luminance(pixels: pixels, x: 8, y: (FrameHeight - 8));

        Assert.True(condition: (inside > outside), userMessage: $"inside {inside} should outshine outside {outside}");
    }

    private static byte[] BuildSquare(byte[] inside, byte[] outside) {
        var pixels = new byte[FrameWidth * FrameHeight * 4];

        for (var y = 0; (y < FrameHeight); y++) {
            for (var x = 0; (x < FrameWidth); x++) {
                var offset = (((y * FrameWidth) + x) * 4);
                var source = (((x >= SquareLeft) && (x < (SquareLeft + SquareSize)) && (y >= SquareTop) && (y < (SquareTop + SquareSize))) ? inside : outside);

                source.CopyTo(array: pixels, index: offset);
            }
        }

        return pixels;
    }
    // The faerie manifest's config defaults, packed in declaration order (scalar floats pack sequentially) and padded
    // to the 16-byte constant-buffer granule.
    private static byte[] FaerieDefaults() {
        ReadOnlySpan<float> values = [0f, 0.3f, 0.35f, 0.22f, 0.9f, 1.8f, 0.55f, 0.22f, 1f, 0.86f, 0.55f, 0.1f, 0.08f, 3f, 1f, 0f, 0f, 0.035f];
        var block = new byte[80];

        for (var index = 0; (index < values.Length); index++) {
            BitConverter.TryWriteBytes(destination: block.AsSpan(start: (index * 4)), value: values[index]);
        }

        return block;
    }
    private static string KernelPath(string name, [CallerFilePath] string callerFilePath = "") {
        var repositoryRoot = Path.GetFullPath(path: Path.Combine(Path.GetDirectoryName(path: callerFilePath)!, "..", ".."));

        return Path.Combine(repositoryRoot, "src", "Puck.Shaders", "Assets", "Probes", $"{name}.hlsl");
    }
    private static int Luminance(byte[] pixels, int x, int y) {
        var offset = (((y * FrameWidth) + x) * 4);

        return (pixels[offset] + pixels[offset + 1] + pixels[offset + 2]);
    }

    // A private device on the first hardware adapter with the frames a kernel reads and the shared targets it
    // writes, created the way a camera graph's converter and a consumer render device provision them.
    private sealed class Bench : IDisposable {
        private readonly IDXGIAdapter1* m_adapter;
        private readonly ID3D11DeviceContext* m_context;
        private readonly ID3D11Device* m_device;
        private readonly ID3D11Device1* m_device1;
        private readonly List<Frame> m_frames = [];
        private readonly List<SharedTarget> m_targets = [];

        private Bench(IDXGIAdapter1* adapter, ID3D11Device* device, ID3D11Device1* device1, ID3D11DeviceContext* context) {
            m_adapter = adapter;
            m_context = context;
            m_device = device;
            m_device1 = device1;
        }

        public static Bench? TryCreate() {
            var adapter = FindHardwareAdapter();

            if (adapter is null) {
                return null;
            }

            ID3D11Device* device = null;
            ID3D11DeviceContext* context = null;

            try {
                D3D_FEATURE_LEVEL granted;
                ReadOnlySpan<D3D_FEATURE_LEVEL> levels = [D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_0];
                using var noSoftwareModule = new SafeFileHandle(ownsHandle: false, preexistingHandle: 0);

                {
                    var devicePointer = &device;
                    var contextPointer = &context;

                    ThrowIfFailed(hr: PInvoke.D3D11CreateDevice(
                        DriverType: D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN,
                        Flags: D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                        SDKVersion: PInvoke.D3D11_SDK_VERSION,
                        Software: noSoftwareModule,
                        pAdapter: ((IDXGIAdapter*)adapter),
                        pFeatureLevel: &granted,
                        pFeatureLevels: levels,
                        ppDevice: devicePointer,
                        ppImmediateContext: contextPointer
                    ), operation: "D3D11CreateDevice");
                }

                var device1Iid = ID3D11Device1.IID_Guid;

                ThrowIfFailed(hr: ((IUnknown*)device)->QueryInterface(ppvObject: out var device1Pointer, riid: in device1Iid), operation: "QueryInterface(ID3D11Device1)");

                return new Bench(adapter: adapter, context: context, device: device, device1: ((ID3D11Device1*)device1Pointer));
            } catch {
                Release(value: context);
                Release(value: device);
                Release(value: adapter);
                throw;
            }
        }
        public Frame CreateFrame(byte[] pixels) {
            ID3D11Texture2D* texture = null;
            ID3D11ShaderResourceView* view = null;

            try {
                var description = new D3D11_TEXTURE2D_DESC {
                    Width = FrameWidth,
                    Height = FrameHeight,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                    Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                    BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE,
                };

                fixed (byte* pixelData = pixels) {
                    var initialData = new D3D11_SUBRESOURCE_DATA {
                        pSysMem = pixelData,
                        SysMemPitch = (FrameWidth * 4),
                    };

                    m_device->CreateTexture2D(pDesc: &description, pInitialData: &initialData, ppTexture2D: &texture);
                }

                m_device->CreateShaderResourceView(pResource: ((ID3D11Resource*)texture), pDesc: null, ppSRView: &view);

                var frame = new Frame(texture: texture, view: view);

                m_frames.Add(item: frame);

                return frame;
            } catch {
                Release(value: view);
                Release(value: texture);
                throw;
            }
        }
        public Win32D3D11ProbeKernel CreateKernel(in ProbeKernelRequest request, ProbeReadingRing ring) => new(
            context: ((nint)m_context),
            device: ((nint)m_device),
            device1: ((nint)m_device1),
            request: in request,
            ring: ring,
            triggerHeight: FrameHeight,
            triggerWidth: FrameWidth
        );
        // An R8G8B8A8 target created with an NT shared handle exactly the way a consumer render device provisions a
        // ring slot, so the kernel opens it through the handle rather than the texture.
        public SharedTarget CreateSharedTarget() {
            ID3D11Texture2D* texture = null;

            try {
                var description = new D3D11_TEXTURE2D_DESC {
                    Width = FrameWidth,
                    Height = FrameHeight,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
                    SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                    Usage = D3D11_USAGE.D3D11_USAGE_DEFAULT,
                    BindFlags = (D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS),
                    MiscFlags = (D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED_NTHANDLE),
                };

                m_device->CreateTexture2D(pDesc: &description, pInitialData: null, ppTexture2D: &texture);

                var resourceIid = IDXGIResource1.IID_Guid;

                ThrowIfFailed(hr: ((IUnknown*)texture)->QueryInterface(ppvObject: out var resourcePointer, riid: in resourceIid), operation: "QueryInterface(IDXGIResource1)");

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

                var target = new SharedTarget(texture: texture, handle: new SafeFileHandle(ownsHandle: true, preexistingHandle: ((nint)handle.Value)));

                m_targets.Add(item: target);

                return target;
            } catch {
                Release(value: texture);
                throw;
            }
        }
        public byte[] ReadBack(SharedTarget target) {
            var description = new D3D11_TEXTURE2D_DESC {
                Width = FrameWidth,
                Height = FrameHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
                Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
                CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            };
            ID3D11Texture2D* staging = null;

            m_device->CreateTexture2D(pDesc: &description, pInitialData: null, ppTexture2D: &staging);

            try {
                m_context->CopyResource(pDstResource: ((ID3D11Resource*)staging), pSrcResource: ((ID3D11Resource*)target.Texture));

                D3D11_MAPPED_SUBRESOURCE mapped;

                m_context->Map(pResource: ((ID3D11Resource*)staging), Subresource: 0, MapType: D3D11_MAP.D3D11_MAP_READ, MapFlags: 0, pMappedResource: &mapped);

                try {
                    var pixels = new byte[FrameWidth * FrameHeight * 4];

                    for (var y = 0; (y < FrameHeight); y++) {
                        new ReadOnlySpan<byte>(((byte*)mapped.pData) + (y * mapped.RowPitch), (FrameWidth * 4)).CopyTo(destination: pixels.AsSpan(start: (y * FrameWidth * 4)));
                    }

                    return pixels;
                } finally {
                    m_context->Unmap(pResource: ((ID3D11Resource*)staging), Subresource: 0);
                }
            } finally {
                Release(value: staging);
            }
        }
        public void Dispose() {
            foreach (var frame in m_frames) {
                Release(value: frame.ViewPointer);
                Release(value: frame.Texture);
            }

            foreach (var target in m_targets) {
                target.Handle.Dispose();
                Release(value: target.Texture);
            }

            Release(value: m_device1);
            Release(value: m_context);
            Release(value: m_device);
            Release(value: m_adapter);
        }

        private static IDXGIAdapter1* FindHardwareAdapter() {
            ThrowIfFailed(hr: PInvoke.CreateDXGIFactory1(ppFactory: out var factoryPointer, riid: IDXGIFactory1.IID_Guid), operation: "CreateDXGIFactory1");

            var factory = ((IDXGIFactory1*)factoryPointer);

            try {
                for (var index = 0u; ; index++) {
                    IDXGIAdapter1* adapter;
                    var hr = factory->EnumAdapters1(Adapter: index, ppAdapter: &adapter);

                    if (HRESULT.DXGI_ERROR_NOT_FOUND == hr) {
                        return null;
                    }

                    ThrowIfFailed(hr: hr, operation: "IDXGIFactory1::EnumAdapters1");

                    var description = adapter->GetDesc1();

                    if (0 == (description.Flags & DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE)) {
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
        private static void ThrowIfFailed(HRESULT hr, string operation) {
            if (hr.Value < 0) {
                throw new COMException(message: $"{operation} failed", errorCode: hr.Value);
            }
        }
    }
    private sealed class Frame(ID3D11Texture2D* texture, ID3D11ShaderResourceView* view) {
        public ID3D11Texture2D* Texture { get; } = texture;
        public nint View => ((nint)ViewPointer);
        public ID3D11ShaderResourceView* ViewPointer { get; } = view;
    }
    private sealed class SharedTarget(ID3D11Texture2D* texture, SafeFileHandle handle) {
        public SafeFileHandle Handle { get; } = handle;
        public nint SharedHandle => Handle.DangerousGetHandle();
        public ID3D11Texture2D* Texture { get; } = texture;
    }
}
