using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.System.Com;

namespace Puck.DirectX.Apis;

/// <summary>
/// The native implementation of <see cref="IDirectXDeviceApi"/>, marshaling to <c>D3D12CreateDevice</c> and the
/// DXGI adapter entry points used to locate a target adapter.
/// </summary>
[OpensGpuDevice]
[SupportedOSPlatform("windows8.1")]
public sealed unsafe class DirectXNativeDeviceApi : IDirectXDeviceApi {
    private static DirectXDevice CreateDevice(IUnknown* adapter, DirectXFeatureLevel minimumFeatureLevel) {
        void* device;
        var result = PInvoke.D3D12CreateDevice(
            MinimumFeatureLevel: ((D3D_FEATURE_LEVEL)minimumFeatureLevel),
            pAdapter: adapter,
            ppDevice: &device,
            riid: ID3D12Device.IID_Guid
        );

        result.ThrowIfFailed(operation: "D3D12CreateDevice");
        return new DirectXDevice(
            deviceHandle: ((nint)device),
            featureLevel: minimumFeatureLevel
        );
    }
    // Returns an owned adapter pointer the caller must Release, or null if no adapter matches the LUID.
    private static IDXGIAdapter1* FindAdapter(IDXGIFactory4* factory, long adapterLuid) {
        for (var index = 0U; ; index++) {
            IDXGIAdapter1* adapter = null;
            var result = factory->EnumAdapters1(
                Adapter: index,
                ppAdapter: &adapter
            );

            if (HRESULT.DXGI_ERROR_NOT_FOUND == result) {
                return null;
            }

            result.ThrowIfFailed(operation: "IDXGIFactory4::EnumAdapters1");

            var description = adapter->GetDesc1();

            if (adapterLuid == DxgiInterop.ToLuid(luid: in description.AdapterLuid)) {
                return adapter;
            }

            _ = adapter->Release();
        }
    }

    /// <inheritdoc/>
    public DirectXDevice CreateDevice(long adapterLuid, DirectXFeatureLevel minimumFeatureLevel) {
        var factory = DxgiInterop.CreateFactory();

        try {
            var adapter = FindAdapter(
                adapterLuid: adapterLuid,
                factory: factory
            );

            if (adapter is null) {
                throw new ArgumentException(
                    message: $"No DXGI adapter was found with LUID 0x{adapterLuid:X16}.",
                    paramName: nameof(adapterLuid)
                );
            }

            try {
                return CreateDevice(
                    adapter: ((IUnknown*)adapter),
                    minimumFeatureLevel: minimumFeatureLevel
                );
            } finally {
                _ = adapter->Release();
            }
        } finally {
            _ = factory->Release();
        }
    }
    /// <inheritdoc/>
    public DirectXDevice CreateWarpDevice(DirectXFeatureLevel minimumFeatureLevel) {
        var factory = DxgiInterop.CreateFactory();

        try {
            void* adapterPointer;

            factory->EnumWarpAdapter(
                ppvAdapter: out adapterPointer,
                riid: IDXGIAdapter1.IID_Guid
            );

            var adapter = ((IDXGIAdapter1*)adapterPointer);

            try {
                return CreateDevice(
                    adapter: ((IUnknown*)adapter),
                    minimumFeatureLevel: minimumFeatureLevel
                );
            } finally {
                _ = adapter->Release();
            }
        } finally {
            _ = factory->Release();
        }
    }
    /// <inheritdoc/>
    public long GetAdapterLuid(nint deviceHandle) {
        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Direct3D 12 device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        // GetAdapterLuid reports the LUID of the adapter the device was created on; packing it the same way as a DXGI
        // adapter description yields a value directly comparable to a Vulkan physical device's reported LUID. Like the
        // descriptor-handle getters it returns a struct by value through the hidden-pointer x64 COM ABI the CsWin32
        // wrapper omits, so invoke the vtable slot directly with the return-by-pointer signature.
        var device = ((ID3D12Device*)deviceHandle);
        var vtable = *((void***)device);
        LUID luid;

        ((delegate* unmanaged[Stdcall]<ID3D12Device*, LUID*, void>)vtable[DirectXConstants.GetAdapterLuidSlot])(
            device,
            &luid
        );

        return DxgiInterop.ToLuid(luid: in luid);
    }
    /// <inheritdoc/>
    public GpuDeviceIdentity GetDeviceIdentity(nint deviceHandle) =>
        ReadAdapter(
            deviceHandle: deviceHandle,
            read: &ReadIdentity
        );
    /// <inheritdoc/>
    public GpuDeviceCapabilities GetDeviceCapabilities(nint deviceHandle) {
        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Direct3D 12 device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        return DirectXFeatureReads.Capabilities(support: new DirectXDeviceFeatureSupport(device: ((ID3D12Device*)deviceHandle)));
    }
    /// <inheritdoc/>
    public GpuMemoryProfile GetMemoryProfile(nint deviceHandle) =>
        ReadAdapter(
            deviceHandle: deviceHandle,
            read: &ReadMemoryProfile
        );

    // Opens the DXGI adapter the device was created on, found by the device's LUID, and runs read over it and the
    // device, releasing the adapter and its factory after.
    private T ReadAdapter<T>(nint deviceHandle, delegate*<IDXGIAdapter1*, ID3D12Device*, T> read) {
        var adapterLuid = GetAdapterLuid(deviceHandle: deviceHandle);
        var factory = DxgiInterop.CreateFactory();

        try {
            var adapter = FindAdapter(
                adapterLuid: adapterLuid,
                factory: factory
            );

            if (adapter is null) {
                throw new ArgumentException(
                    message: $"No DXGI adapter was found with the device's LUID 0x{adapterLuid:X16}.",
                    paramName: nameof(deviceHandle)
                );
            }

            try {
                return read(
                    adapter,
                    ((ID3D12Device*)deviceHandle)
                );
            } finally {
                _ = adapter->Release();
            }
        } finally {
            _ = factory->Release();
        }
    }
    private static GpuDeviceIdentity ReadIdentity(IDXGIAdapter1* adapter, ID3D12Device* device) {
        var description = adapter->GetDesc1();
        var driverVersion = 0UL;

        // CsWin32's friendly overload throws on a failing HRESULT; an adapter that will not report its user-mode
        // driver version reports zero rather than failing the identity.
        try {
            var deviceIid = IDXGIDevice.IID_Guid;

            adapter->CheckInterfaceSupport(
                InterfaceName: in deviceIid,
                pUMDVersion: out var umdVersion
            );
            driverVersion = unchecked((ulong)umdVersion);
        } catch (COMException) {
            driverVersion = 0UL;
        }

        return new GpuDeviceIdentity(
            AdapterName: description.Description.ToString(),
            ApiVersion: DirectXFeatureReads.MaxFeatureLevel(support: new DirectXDeviceFeatureSupport(device: device)),
            Backend: "directx",
            DeviceId: description.DeviceId,
            DriverVersion: ((driverVersion == 0UL)
                ? string.Empty
                : GpuDeviceIdentity.FormatDirectXDriverVersion(version: driverVersion)
            ),
            DriverVersionRaw: driverVersion,
            VendorId: description.VendorId
        );
    }
    private static GpuMemoryProfile ReadMemoryProfile(IDXGIAdapter1* adapter, ID3D12Device* device) {
        var description = adapter->GetDesc1();

        return DirectXFeatureReads.MemoryProfile(
            adapter: in description,
            support: new DirectXDeviceFeatureSupport(device: device)
        );
    }
}
