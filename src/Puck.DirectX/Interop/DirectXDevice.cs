using System.Runtime.Versioning;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX.Interop;

/// <summary>
/// An <see cref="IDisposable"/> wrapper that owns a native <c>ID3D12Device</c> COM object and releases it
/// exactly once on disposal.
/// </summary>
[SupportedOSPlatform("windows8.1")]
public sealed unsafe class DirectXDevice : IDisposable {
    private nint m_deviceHandle;

    /// <summary>Initializes a new instance of the <see cref="DirectXDevice"/> class taking ownership of a device.</summary>
    /// <param name="deviceHandle">The native <c>ID3D12Device</c> pointer to own.</param>
    /// <param name="featureLevel">The feature level the device was created with.</param>
    /// <exception cref="ArgumentException"><paramref name="deviceHandle"/> is zero.</exception>
    public DirectXDevice(nint deviceHandle, DirectXFeatureLevel featureLevel) {
        if (0 == deviceHandle) {
            throw new ArgumentException(
                message: "Direct3D 12 device handle must be non-zero.",
                paramName: nameof(deviceHandle)
            );
        }

        FeatureLevel = featureLevel;
        m_deviceHandle = deviceHandle;
    }

    /// <summary>Gets the feature level the device was created with.</summary>
    public DirectXFeatureLevel FeatureLevel { get; }
    /// <summary>Gets the native <c>ID3D12Device</c> pointer, or zero once disposed.</summary>
    public nint Handle => m_deviceHandle;

    /// <summary>Releases the owned <c>ID3D12Device</c>. Safe to call more than once.</summary>
    public void Dispose() {
        var handle = Interlocked.Exchange(
            location1: ref m_deviceHandle,
            value: 0
        );

        if (0 != handle) {
            _ = ((ID3D12Device*)handle)->Release();
        }
    }
}
