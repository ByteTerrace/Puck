using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Puck.DirectX.Messages;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dxgi;

namespace Puck.DirectX.Apis;

/// <summary>
/// The native implementation of <see cref="IDirectXAdapterApi"/>, marshaling to the DXGI factory and adapter
/// enumeration entry points.
/// </summary>
[SupportedOSPlatform("windows8.1")]
public sealed unsafe class DirectXNativeAdapterApi : IDirectXAdapterApi {
    private static DirectXAdapterDescription Describe(in DXGI_ADAPTER_DESC1 description) {
        return new DirectXAdapterDescription(
            AdapterLuid: DxgiInterop.ToLuid(luid: in description.AdapterLuid),
            DedicatedSystemMemory: (ulong)description.DedicatedSystemMemory,
            DedicatedVideoMemory: (ulong)description.DedicatedVideoMemory,
            Description: description.Description.ToString(),
            DeviceId: description.DeviceId,
            IsSoftware: (DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE == (description.Flags & DXGI_ADAPTER_FLAG.DXGI_ADAPTER_FLAG_SOFTWARE)),
            Revision: description.Revision,
            SharedSystemMemory: (ulong)description.SharedSystemMemory,
            SubSystemId: description.SubSysId,
            VendorId: description.VendorId
        );
    }

    /// <inheritdoc/>
    public IReadOnlyList<DirectXAdapterDescription> EnumerateAdapters() {
        var factory = DxgiInterop.CreateFactory();

        try {
            var adapters = new List<DirectXAdapterDescription>();

            for (var index = 0U; ; index++) {
                IDXGIAdapter1* adapter = null;
                var result = factory->EnumAdapters1(
                    index,
                    &adapter
                );

                if (HRESULT.DXGI_ERROR_NOT_FOUND == result) {
                    break;
                }

                result.ThrowIfFailed(operation: "IDXGIFactory4::EnumAdapters1");

                try {
                    var description = adapter->GetDesc1();

                    adapters.Add(item: Describe(description: in description));
                } finally {
                    _ = adapter->Release();
                }
            }

            return adapters;
        } finally {
            _ = factory->Release();
        }
    }
}
