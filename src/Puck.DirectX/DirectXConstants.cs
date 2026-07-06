using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>
/// Direct3D 12 specification constants shared across the backend's interop types. Centralized here so the fixed
/// ABI values — descriptor-heap vtable slots, the SRV swizzle mask, subresource and access flags, and the
/// copy row-pitch alignment — are declared exactly once rather than copied into each consumer.
/// </summary>
public static unsafe class DirectXConstants {
    /// <summary><c>D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES</c>: transitions every subresource of a resource at once.</summary>
    public const uint AllSubresources = 0xFFFFFFFFu;
    /// <summary><c>D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING</c>: the identity RGBA component swizzle for SRVs.</summary>
    public const uint DefaultShader4ComponentMapping = 0x00001688u;
    /// <summary><c>GENERIC_ALL</c>: the shared-resource access flag passed when opening a cross-API shared handle.</summary>
    public const uint GenericAll = 0x10000000u;
    /// <summary>
    /// The vtable slot of <c>ID3D12DescriptorHeap::GetCPUDescriptorHandleForHeapStart</c> (after the three
    /// <c>IUnknown</c> methods, <c>ID3D12Object</c>'s five, and <c>ID3D12DeviceChild</c>'s one). The method
    /// returns a struct by value, which the x64 COM ABI returns through a hidden pointer parameter the CsWin32
    /// wrapper omits — so callers invoke the slot directly with the return-by-pointer signature.
    /// </summary>
    public const int GetCpuDescriptorHandleSlot = 9;
    /// <summary>The vtable slot of <c>ID3D12DescriptorHeap::GetGPUDescriptorHandleForHeapStart</c> (the slot after
    /// <see cref="GetCpuDescriptorHandleSlot"/>); invoked directly for the same return-by-pointer ABI reason.</summary>
    public const int GetGpuDescriptorHandleSlot = 10;
    /// <summary>The vtable slot of <c>ID3D12Device::GetAdapterLuid</c> (the three <c>IUnknown</c> methods,
    /// <c>ID3D12Object</c>'s four, then the thirty-six <c>ID3D12Device</c> methods preceding it). Like the descriptor
    /// handle getters it returns a struct by value through the hidden-pointer x64 COM ABI the CsWin32 wrapper omits, so
    /// callers invoke the slot directly with the return-by-pointer signature.</summary>
    public const int GetAdapterLuidSlot = 43;
    /// <summary>The vtable slot of <c>ID3D12Device::GetDeviceRemovedReason</c> (the last <c>ID3D12Device</c> method
    /// before <c>GetCopyableFootprints</c>; six slots precede <c>GetAdapterLuid</c> at 43). The CsWin32 wrapper folds
    /// its returned HRESULT into a throwing, <c>void</c>-returning friendly overload, so callers wanting the raw
    /// removal-reason HRESULT invoke the slot directly.</summary>
    public const int GetDeviceRemovedReasonSlot = 37;
    /// <summary><c>D3D12_TEXTURE_DATA_PITCH_ALIGNMENT</c>: the required row-pitch alignment for buffer-texture copies.</summary>
    public const uint TextureRowPitchAlignment = 256;

    /// <summary>Gets a descriptor heap's CPU handle for its first descriptor, via the direct vtable-slot call the
    /// hidden-pointer x64 COM ABI requires (see <see cref="GetCpuDescriptorHandleSlot"/>).</summary>
    /// <param name="heap">The descriptor heap.</param>
    /// <returns>The CPU descriptor handle at the heap's start.</returns>
    [SupportedOSPlatform("windows10.0.10240")]
    public static D3D12_CPU_DESCRIPTOR_HANDLE GetCpuHeapStart(ID3D12DescriptorHeap* heap) {
        D3D12_CPU_DESCRIPTOR_HANDLE handle;
        var vtable = *(void***)heap;

        ((delegate* unmanaged[Stdcall]<ID3D12DescriptorHeap*, D3D12_CPU_DESCRIPTOR_HANDLE*, void>)vtable[GetCpuDescriptorHandleSlot])(
            heap,
            &handle
        );

        return handle;
    }
    /// <summary>Gets a descriptor heap's GPU handle for its first descriptor, via the direct vtable-slot call the
    /// hidden-pointer x64 COM ABI requires (see <see cref="GetGpuDescriptorHandleSlot"/>).</summary>
    /// <param name="heap">The descriptor heap.</param>
    /// <returns>The GPU descriptor handle at the heap's start.</returns>
    [SupportedOSPlatform("windows10.0.10240")]
    public static D3D12_GPU_DESCRIPTOR_HANDLE GetGpuHeapStart(ID3D12DescriptorHeap* heap) {
        D3D12_GPU_DESCRIPTOR_HANDLE handle;
        var vtable = *(void***)heap;

        ((delegate* unmanaged[Stdcall]<ID3D12DescriptorHeap*, D3D12_GPU_DESCRIPTOR_HANDLE*, void>)vtable[GetGpuDescriptorHandleSlot])(
            heap,
            &handle
        );

        return handle;
    }
}
