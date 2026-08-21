using System.Diagnostics.CodeAnalysis;

namespace Puck.Platform.Probes;

/// <summary>The "no kernel host" fallback: declines every request. Registered on platforms without a sense-kernel
/// backend, so a KERNEL probe faults cleanly by name instead of never running.</summary>
public sealed class NullProbeKernelHost : IProbeKernelHost {
    /// <inheritdoc/>
    public bool IsSupported => false;

    /// <inheritdoc/>
    public bool TryStart(in ProbeKernelRequest request, ICameraSharedStream stream, IReadOnlyList<nint> sharedTargetHandles, ProbeReadingRing ring, [NotNullWhen(true)] out IProbeKernelRun? run, out string fault) {
        run = null;
        fault = "no sense kernel host is registered on this platform";

        return false;
    }
}
