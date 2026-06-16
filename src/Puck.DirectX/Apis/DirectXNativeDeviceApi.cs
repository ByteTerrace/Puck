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
[SupportedOSPlatform("windows8.1")]
public sealed unsafe class DirectXNativeDeviceApi : IDirectXDeviceApi {
    // Probed highest-first; the first level that accepts device creation is the adapter's maximum.
    private static readonly DirectXFeatureLevel[] FeatureLevelsHighToLow = [
        DirectXFeatureLevel.Level122,
        DirectXFeatureLevel.Level121,
        DirectXFeatureLevel.Level120,
        DirectXFeatureLevel.Level111,
        DirectXFeatureLevel.Level110,
    ];

    private static DirectXDevice CreateDevice(IUnknown* adapter, DirectXFeatureLevel minimumFeatureLevel) {
        void* device;
        var result = PInvoke.D3D12CreateDevice(
            pAdapter: adapter,
            MinimumFeatureLevel: (D3D_FEATURE_LEVEL)minimumFeatureLevel,
            riid: ID3D12Device.IID_Guid,
            ppDevice: &device
        );

        result.ThrowIfFailed(operation: "D3D12CreateDevice");
        return new DirectXDevice(
            deviceHandle: (nint)device,
            featureLevel: minimumFeatureLevel
        );
    }
    // Returns an owned adapter pointer the caller must Release, or null if no adapter matches the LUID.
    private static IDXGIAdapter1* FindAdapter(IDXGIFactory4* factory, long adapterLuid) {
        for (var index = 0U; ; index++) {
            IDXGIAdapter1* adapter = null;
            var result = factory->EnumAdapters1(
                index,
                &adapter
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
                    adapter: (IUnknown*)adapter,
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
                riid: IDXGIAdapter1.IID_Guid,
                ppvAdapter: out adapterPointer
            );

            var adapter = (IDXGIAdapter1*)adapterPointer;

            try {
                return CreateDevice(
                    adapter: (IUnknown*)adapter,
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
    public DirectXFeatureLevel? ProbeMaxFeatureLevel(long adapterLuid) {
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
                // A null device pointer asks D3D12CreateDevice to test creation without realizing a device,
                // returning success (S_FALSE) when the adapter meets the requested minimum feature level.
                foreach (var level in FeatureLevelsHighToLow) {
                    var result = PInvoke.D3D12CreateDevice(
                        pAdapter: (IUnknown*)adapter,
                        MinimumFeatureLevel: (D3D_FEATURE_LEVEL)level,
                        riid: ID3D12Device.IID_Guid,
                        ppDevice: null
                    );

                    if (result.Succeeded) {
                        return level;
                    }
                }

                return null;
            } finally {
                _ = adapter->Release();
            }
        } finally {
            _ = factory->Release();
        }
    }
}
