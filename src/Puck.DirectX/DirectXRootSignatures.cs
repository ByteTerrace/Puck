using System.Runtime.Versioning;
using Puck.DirectX.Interop;
using Windows.Win32;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>Creates Direct3D 12 root signatures from version 1 descriptions.</summary>
[SupportedOSPlatform("windows10.0.10240")]
public static unsafe class DirectXRootSignatures {
    /// <summary>Serializes a root-signature description and creates it on the given device.</summary>
    /// <param name="device">The device that owns the root signature.</param>
    /// <param name="description">The root-signature description to serialize.</param>
    /// <returns>The created root-signature pointer.</returns>
    public static nint Create(ID3D12Device* device, in D3D12_ROOT_SIGNATURE_DESC description) {
        ID3DBlob* sigBlob = null;
        ID3DBlob* errBlob = null;

        try {
            PInvoke.D3D12SerializeRootSignature(
                Version: D3D_ROOT_SIGNATURE_VERSION.D3D_ROOT_SIGNATURE_VERSION_1,
                pRootSignature: in description,
                ppBlob: &sigBlob,
                ppErrorBlob: &errBlob
            ).ThrowIfFailed(operation: "D3D12SerializeRootSignature");

            void* rootSig;
            var rootSigIid = ID3D12RootSignature.IID_Guid;

            device->CreateRootSignature(
                0,
                sigBlob->GetBufferPointer(),
                sigBlob->GetBufferSize(),
                in rootSigIid,
                out rootSig
            );

            return (nint)rootSig;
        } finally {
            if (sigBlob is not null) {
                _ = sigBlob->Release();
            }

            if (errBlob is not null) {
                _ = errBlob->Release();
            }
        }
    }
}
