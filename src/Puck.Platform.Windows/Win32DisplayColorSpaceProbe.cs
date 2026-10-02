using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;
using Windows.Win32.System.Com;

namespace Puck.Platform.Windows;

/// <summary>Reads the color space a monitor presents in, holding one DXGI factory and the output it found for the last
/// monitor asked about. A poll of the same monitor while the factory is current re-reads only that output's
/// description; a stale factory is replaced and a different monitor is found again, so a steady capture creates no
/// factory and enumerates nothing per poll. One caller at a time.</summary>
public sealed class Win32DisplayColorSpaceProbe : IDisposable {
    private readonly Func<IDisplayAdapterOutputs?> m_openOutputs;

    private IDisplayAdapterOutput? m_output;
    private nint m_outputMonitor;
    private IDisplayAdapterOutputs? m_outputs;

    /// <summary>Initializes a new instance of the <see cref="Win32DisplayColorSpaceProbe"/> class.</summary>
    /// <param name="openOutputs">Creates a factory's enumeration, or returns null when no factory can be created.</param>
    /// <exception cref="ArgumentNullException"><paramref name="openOutputs"/> is null.</exception>
    public Win32DisplayColorSpaceProbe(Func<IDisplayAdapterOutputs?> openOutputs) {
        ArgumentNullException.ThrowIfNull(argument: openOutputs);

        m_openOutputs = openOutputs;
    }

    /// <summary>Creates a probe over the system's DXGI.</summary>
    /// <returns>The probe, owned by the caller.</returns>
    [SupportedOSPlatform("windows10.0.10240")]
    public static Win32DisplayColorSpaceProbe Dxgi() => new(openOutputs: DxgiAdapterOutputs.TryOpen);
    /// <summary>Returns the color space a monitor presents in: <see cref="DisplayColorSpace.Hdr10"/> for
    /// <c>DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020</c>, which is how Windows reports HDR turned on,
    /// <see cref="DisplayColorSpace.ScRgb"/> for <c>DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709</c>, and
    /// <see cref="DisplayColorSpace.Srgb"/> for <c>DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709</c>, an SDR output.</summary>
    /// <param name="monitorHandle">The monitor.</param>
    /// <returns>The color space, or null for any other color space, a monitor no output drives, and a system DXGI cannot
    /// describe, so discovery never guesses SDR.</returns>
    public DisplayColorSpace? ColorSpaceOf(nint monitorHandle) {
        if (m_outputs is not { IsCurrent: true }) {
            ReleaseOutputs();
            m_outputs = m_openOutputs();
            if (m_outputs is null) {
                return null;
            }
        }
        if ((m_output is null) || (m_outputMonitor != monitorHandle)) {
            m_output?.Dispose();
            m_output = m_outputs.FindOutput(monitorHandle: monitorHandle);
            m_outputMonitor = monitorHandle;
            if (m_output is null) {
                return null;
            }
        }

        return m_output.ReadColorSpace();
    }
    /// <summary>Releases the factory and the output the probe holds.</summary>
    public void Dispose() => ReleaseOutputs();

    private void ReleaseOutputs() {
        m_output?.Dispose();
        m_output = null;
        m_outputs?.Dispose();
        m_outputs = null;
    }

    [SupportedOSPlatform("windows10.0.10240")]
    private sealed unsafe class DxgiAdapterOutputs : IDisplayAdapterOutputs {
        private IDXGIFactory1* m_factory;

        private DxgiAdapterOutputs(IDXGIFactory1* factory) {
            m_factory = factory;
        }

        public static IDisplayAdapterOutputs? TryOpen() {
            if (PInvoke.CreateDXGIFactory1(
                ppFactory: out var factoryPointer,
                riid: IDXGIFactory1.IID_Guid
            ).Failed) {
                return null;
            }

            return new DxgiAdapterOutputs(factory: ((IDXGIFactory1*)factoryPointer));
        }

        public bool IsCurrent => ((m_factory is not null) && m_factory->IsCurrent());

        public IDisplayAdapterOutput? FindOutput(nint monitorHandle) {
            if (m_factory is null) {
                return null;
            }

            try {
                for (var adapterIndex = 0u; ; adapterIndex++) {
                    IDXGIAdapter1* adapter;

                    if (m_factory->EnumAdapters1(
                        Adapter: adapterIndex,
                        ppAdapter: &adapter
                    ).Failed) {
                        return null;
                    }

                    try {
                        for (var outputIndex = 0u; ; outputIndex++) {
                            IDXGIOutput* output;

                            if (((IDXGIAdapter*)adapter)->EnumOutputs(
                                Output: outputIndex,
                                ppOutput: &output
                            ).Failed) {
                                break;
                            }

                            try {
                                var output6Iid = IDXGIOutput6.IID_Guid;

                                if (((IUnknown*)output)->QueryInterface(
                                    ppvObject: out var output6,
                                    riid: in output6Iid
                                ).Failed) {
                                    continue;
                                }

                                // The match keeps the reference QueryInterface took; every other output gives it back.
                                var matches = false;

                                try {
                                    matches = (((nint)((IDXGIOutput6*)output6)->GetDesc1().Monitor.Value) == monitorHandle);
                                } finally {
                                    if (!matches) {
                                        _ = ((IUnknown*)output6)->Release();
                                    }
                                }

                                if (matches) {
                                    return new DxgiAdapterOutput(output: ((IDXGIOutput6*)output6));
                                }
                            } finally {
                                _ = ((IUnknown*)output)->Release();
                            }
                        }
                    } finally {
                        _ = ((IUnknown*)adapter)->Release();
                    }
                }
            } catch (COMException) {
                return null;
            }
        }
        public void Dispose() {
            if (m_factory is not null) {
                _ = ((IUnknown*)m_factory)->Release();
                m_factory = null;
            }
        }
    }
    [SupportedOSPlatform("windows10.0.10240")]
    private sealed unsafe class DxgiAdapterOutput(IDXGIOutput6* output) : IDisplayAdapterOutput {
        private IDXGIOutput6* m_output = output;

        public DisplayColorSpace? ReadColorSpace() {
            if (m_output is null) {
                return null;
            }

            try {
                return m_output->GetDesc1().ColorSpace switch {
                    DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020 => DisplayColorSpace.Hdr10,
                    DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709 => DisplayColorSpace.ScRgb,
                    DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709 => DisplayColorSpace.Srgb,
                    _ => null,
                };
            } catch (COMException) {
                return null;
            }
        }
        public void Dispose() {
            if (m_output is not null) {
                _ = ((IUnknown*)m_output)->Release();
                m_output = null;
            }
        }
    }
}
