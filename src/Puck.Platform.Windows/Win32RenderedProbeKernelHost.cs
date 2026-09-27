using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Puck.Platform.Probes;

namespace Puck.Platform.Windows;

/// <summary>
/// The render adapter's own probe kernel host: a Direct3D 11 device on the adapter the render device runs on, and a
/// worker that runs the kernels whose trigger socket reads a rendered source (a view export or another probe's output).
/// <see cref="Signal"/> wakes the worker, which runs every attached kernel whose trigger ring has published since it last
/// ran, so a kernel cycles once per new frame of its trigger and never on a clock. Such a kernel binds no camera socket.
/// Disposing the host ends every run and releases the device after the worker stops.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class Win32RenderedProbeKernelHost : IRenderedProbeKernelHost {
    private readonly Win32ProbeKernelBench m_bench = new();

    private readonly Win32D3D11VideoDevice m_device;

    private readonly AutoResetEvent m_wake = new(initialState: false);

    private readonly Thread m_worker;

    private volatile bool m_closing;
    private bool m_disposed;

    /// <summary>Initializes a new instance of the <see cref="Win32RenderedProbeKernelHost"/> class, creating its device
    /// on the adapter and starting its worker.</summary>
    /// <param name="adapterLuid">The render adapter's LUID (packed <c>(HighPart &lt;&lt; 32) | LowPart</c>).</param>
    /// <exception cref="InvalidOperationException">No adapter matches, or the device could not be created.</exception>
    public Win32RenderedProbeKernelHost(long adapterLuid) {
        m_device = new Win32D3D11VideoDevice(adapterLuid: adapterLuid);
        m_worker = new Thread(start: Work) {
            IsBackground = true,
            Name = "probe-kernel-host",
        };
        m_worker.Start();
    }

    /// <inheritdoc/>
    public void Signal() {
        if (!m_disposed) {
            _ = m_wake.Set();
        }
    }
    /// <inheritdoc/>
    /// <remarks>A kernel is refused when a socket reads a camera sensor, or when its trigger socket reads no
    /// ring.</remarks>
    public bool TryAttachKernel(in ProbeKernelRequest request, ProbeReadingRing ring, [NotNullWhen(true)] out IProbeKernelRun? run, out string fault) {
        ArgumentNullException.ThrowIfNull(ring);
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );

        foreach (var input in request.Inputs) {
            if (input is ProbeKernelInput.Sensor or ProbeKernelInput.StrobePair) {
                run = null;
                fault = "a kernel on the render adapter binds no camera socket";

                return false;
            }
        }

        if (request.TriggerRing is null) {
            run = null;
            fault = $"the trigger socket {request.Trigger} reads no rendered source";

            return false;
        }

        run = m_bench.Attach(
            request: in request,
            ring: ring
        );
        fault = "";

        return true;
    }
    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_closing = true;
        _ = m_wake.Set();
        m_worker.Join();
        m_wake.Dispose();
        m_device.Dispose();
    }

    private void Work() {
        try {
            while (true) {
                _ = m_wake.WaitOne();

                if (m_closing) {
                    break;
                }

                m_bench.OnPublications(device: m_device);
            }
        } finally {
            m_bench.Close();
        }
    }
}
/// <summary>Opens the render adapter's probe kernel host (<see cref="Win32RenderedProbeKernelHost"/>).</summary>
public sealed class Win32ProbeKernelHostService : IProbeKernelHostService {
    /// <inheritdoc/>
    public bool TryOpen(long adapterLuid, [NotNullWhen(true)] out IRenderedProbeKernelHost? host, out string fault) {
        if (!OperatingSystem.IsWindowsVersionAtLeast(
            major: 10,
            minor: 0,
            build: 10240
        )) {
            host = null;
            fault = "probe kernels on the render adapter need Windows 10";

            return false;
        }

        try {
            host = new Win32RenderedProbeKernelHost(adapterLuid: adapterLuid);
            fault = "";

            return true;
        } catch (Exception exception) when ((exception is InvalidOperationException or System.Runtime.InteropServices.COMException)) {
            host = null;
            fault = $"the render adapter's probe kernel device did not open: {exception.Message}";

            return false;
        }
    }
}
