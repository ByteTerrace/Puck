using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Puck.Platform.Probes;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace Puck.Platform.Windows.Tests;

/// <summary>A private Direct3D 11 device on the first hardware adapter, with the frames a kernel reads and the
/// shared targets it writes, provisioned the way a camera graph's converter and a consumer render device provision
/// them — including opening a target back through its own NT shared handle, so a kernel test exercises the same
/// cross-handle path production code does.</summary>
[SupportedOSPlatform("windows10.0.10240")]
internal sealed unsafe class KernelBench : IDisposable {
    public const int FrameHeight = 64;
    public const int FrameWidth = 64;

    private readonly IDXGIAdapter1* m_adapter;
    private readonly ID3D11DeviceContext* m_context;
    private readonly ID3D11Device* m_device;
    private readonly ID3D11Device1* m_device1;
    private readonly List<Frame> m_frames = [];
    private readonly List<nint> m_openedTextures = [];
    private readonly List<nint> m_openedViews = [];
    private readonly List<SharedTarget> m_targets = [];

    private KernelBench(IDXGIAdapter1* adapter, ID3D11Device* device, ID3D11Device1* device1, ID3D11DeviceContext* context) {
        m_adapter = adapter;
        m_context = context;
        m_device = device;
        m_device1 = device1;

        var description = adapter->GetDesc1();

        AdapterLuid = (((long)description.AdapterLuid.HighPart) << 32) | description.AdapterLuid.LowPart;
    }

    // Packed the same way Win32D3D11.FindAdapterByLuid matches it: (HighPart << 32) | LowPart.
    public long AdapterLuid { get; }

    public static KernelBench? TryCreate() {
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

            return new KernelBench(adapter: adapter, context: context, device: device, device1: ((ID3D11Device1*)device1Pointer));
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
                    SysMemPitch = (FrameWidth * 4),
                    pSysMem = pixelData,
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
    // ring slot, so a kernel test opens it through the handle rather than the texture.
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
                BindFlags = D3D11_BIND_FLAG.D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_FLAG.D3D11_BIND_UNORDERED_ACCESS,
                MiscFlags = D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_FLAG.D3D11_RESOURCE_MISC_SHARED_NTHANDLE,
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
    /// <summary>Creates a shared ring of <paramref name="slots"/> targets, its handles (in slot order), and a
    /// <see cref="LatestSlotPublication"/> configured for it — for a <see cref="ProbeKernelInput.Ring"/> test. Read
    /// a published slot back with <see cref="ReadBack"/> on the matching <see cref="SharedRing.Targets"/> entry.</summary>
    public SharedRing CreateSharedRing(int slots) {
        var targets = new SharedTarget[slots];
        var handles = new nint[slots];

        for (var index = 0; (index < slots); index++) {
            targets[index] = CreateSharedTarget();
            handles[index] = targets[index].SharedHandle;
        }

        var publication = new LatestSlotPublication();

        publication.Configure(targetCount: slots);

        return new SharedRing(handles: handles, slots: publication, targets: targets);
    }
    /// <summary>Writes CPU pixel data into an already-created target texture (Default usage accepts
    /// <c>UpdateSubresource</c> without a staging round trip).</summary>
    public void UploadPixels(SharedTarget target, byte[] pixels) {
        fixed (byte* pixelData = pixels) {
            m_context->UpdateSubresource(
                pDstResource: ((ID3D11Resource*)target.Texture),
                DstSubresource: 0,
                pDstBox: null,
                pSrcData: pixelData,
                SrcRowPitch: ((uint)(FrameWidth * 4)),
                SrcDepthPitch: 0
            );
        }
    }
    /// <summary>Opens a shared target back through its NT handle (as a consumer device would) and creates a
    /// shader-resource view over the opened texture; the view lives until this bench disposes.</summary>
    public nint OpenSharedView(nint sharedHandle) {
        using var handle = new SafeFileHandle(ownsHandle: false, preexistingHandle: sharedHandle);

        m_device1->OpenSharedResource1(hResource: handle, ppResource: out var opened, returnedInterface: ID3D11Texture2D.IID_Guid);

        var texture = ((ID3D11Texture2D*)opened);
        ID3D11ShaderResourceView* view = null;

        m_device->CreateShaderResourceView(pResource: ((ID3D11Resource*)texture), pDesc: null, ppSRView: &view);

        m_openedTextures.Add(item: ((nint)texture));
        m_openedViews.Add(item: ((nint)view));

        return ((nint)view);
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
                var pixels = new byte[((FrameWidth * FrameHeight) * 4)];

                for (var y = 0; (y < FrameHeight); y++) {
                    new ReadOnlySpan<byte>((((byte*)mapped.pData) + (y * mapped.RowPitch)), (FrameWidth * 4)).CopyTo(destination: pixels.AsSpan(start: ((y * FrameWidth) * 4)));
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
        foreach (var view in m_openedViews) {
            Release(value: ((ID3D11ShaderResourceView*)view));
        }

        foreach (var texture in m_openedTextures) {
            Release(value: ((ID3D11Texture2D*)texture));
        }

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
            throw new COMException(errorCode: hr.Value, message: $"{operation} failed");
        }
    }

    public sealed class Frame(ID3D11Texture2D* texture, ID3D11ShaderResourceView* view) {
        public ID3D11Texture2D* Texture { get; } = texture;

        public nint View => ((nint)ViewPointer);

        public ID3D11ShaderResourceView* ViewPointer { get; } = view;
    }
    public sealed class SharedTarget(ID3D11Texture2D* texture, SafeFileHandle handle) {
        public SafeFileHandle Handle { get; } = handle;

        public nint SharedHandle => Handle.DangerousGetHandle();

        public ID3D11Texture2D* Texture { get; } = texture;
    }
    /// <summary>A shared ring created by <see cref="CreateSharedRing"/>: the targets in slot order (for
    /// <see cref="ReadBack"/>), their shared handles in the same order (for a
    /// <see cref="ProbeKernelInput.Ring"/>'s <c>SharedTargetHandles</c>), and the publication a test drives with
    /// <see cref="LatestSlotPublication.TryReserveWriteSlot"/>/<see cref="LatestSlotPublication.Publish"/>.</summary>
    public sealed class SharedRing(SharedTarget[] targets, nint[] handles, LatestSlotPublication slots) {
        public nint[] Handles { get; } = handles;
        public LatestSlotPublication Slots { get; } = slots;
        public SharedTarget[] Targets { get; } = targets;
    }
}
