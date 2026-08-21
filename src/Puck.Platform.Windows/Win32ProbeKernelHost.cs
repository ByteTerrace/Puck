using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Puck.Platform.Probes;

namespace Puck.Platform.Windows;

/// <summary>The Windows <see cref="IProbeKernelHost"/>: starts a <see cref="Win32D3D11ProbeKernelRunner"/> per
/// request, on its own Direct3D 11 device and thread beside the camera graph.</summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class Win32ProbeKernelHost : IProbeKernelHost {
    /// <inheritdoc/>
    public bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(major: 10, minor: 0, build: 19041);

    /// <inheritdoc/>
    public bool TryStart(in ProbeKernelRequest request, ICameraSharedStream stream, IReadOnlyList<nint> sharedTargetHandles, ProbeReadingRing ring, [NotNullWhen(true)] out IProbeKernelRun? run, out string fault) {
        if (!IsSupported) {
            run = null;
            fault = "this platform does not support sense kernels; requires Windows 10 build 19041 or later.";

            return false;
        }

        try {
            run = Win32D3D11ProbeKernelRunner.Start(request: request, stream: stream, sharedTargetHandles: sharedTargetHandles, ring: ring);
            fault = "";

            return true;
        } catch (Exception exception) {
            run = null;
            fault = exception.Message;

            return false;
        }
    }
}
