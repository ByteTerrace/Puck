using System.Diagnostics.CodeAnalysis;

namespace Puck.Platform.Probes;

/// <summary>One KERNEL probe's compiled-shader request: what to compile, the packed constant-buffer bytes bound
/// from the kind's config, and the extent/format of the shared camera target it reads.</summary>
/// <param name="KernelSource">The kernel's HLSL source text.</param>
/// <param name="AccumulateEntry">The per-pixel accumulation entry point.</param>
/// <param name="FinalizeEntry">The single-dispatch entry point that writes the reading's channels.</param>
/// <param name="Constants">The packed constant-buffer bytes bound from the probe's config, in the kind
/// manifest's declared field order.</param>
/// <param name="ChannelCount">The number of channels the kind declares.</param>
/// <param name="RateHz">The probe's declared rate ceiling.</param>
/// <param name="AdapterLuid">The consumer render device's adapter LUID; the kernel host's own device must share
/// the adapter for the camera's shared handles to be openable.</param>
/// <param name="Width">The shared camera target's width in pixels.</param>
/// <param name="Height">The shared camera target's height in pixels.</param>
/// <param name="TargetFormat">The shared camera target's pixel format.</param>
public readonly record struct ProbeKernelRequest(string KernelSource, string AccumulateEntry, string FinalizeEntry, ReadOnlyMemory<byte> Constants, int ChannelCount, uint RateHz, long AdapterLuid, int Width, int Height, SurfaceFormat TargetFormat);

/// <summary>Runs KERNEL-class probe kinds: handwritten GPU compute against a camera's shared-tier frame, on its
/// own device and thread, publishing into a <see cref="ProbeReadingRing"/>.</summary>
public interface IProbeKernelHost {
    /// <summary>Gets a value indicating whether this platform can run sense kernels at all.</summary>
    bool IsSupported { get; }

    /// <summary>Tries to start a kernel run against a live shared camera stream.</summary>
    /// <param name="request">The compiled-shader request.</param>
    /// <param name="stream">The shared camera stream the kernel reads.</param>
    /// <param name="sharedTargetHandles">The consumer-provisioned shared target handles <paramref name="stream"/>
    /// publishes into (opaque NT handles on Windows).</param>
    /// <param name="ring">The ring the run publishes readings into.</param>
    /// <param name="run">When this returns <see langword="true"/>, the started run.</param>
    /// <param name="fault">A human-readable refusal reason when this returns <see langword="false"/>; otherwise empty.</param>
    /// <returns><see langword="true"/> if the run started.</returns>
    bool TryStart(in ProbeKernelRequest request, ICameraSharedStream stream, IReadOnlyList<nint> sharedTargetHandles, ProbeReadingRing ring, [NotNullWhen(true)] out IProbeKernelRun? run, out string fault);
}
/// <summary>One live kernel run, started by <see cref="IProbeKernelHost.TryStart"/>. Disposing stops it.</summary>
public interface IProbeKernelRun : IDisposable {
    /// <summary>Gets a value indicating whether the run has permanently stopped (a fault, or the stream ending) —
    /// the consumer's signal to dispose it and, if the attachment is still live, start a new run.</summary>
    bool IsEnded { get; }
    /// <summary>Gets the fault that ended the run, or <see langword="null"/> while it is still live.</summary>
    string? Fault { get; }
    /// <summary>Gets the number of completed accumulate/finalize dispatches.</summary>
    long Cycles { get; }
    /// <summary>Gets the number of cycles skipped because no fresh camera frame arrived within the run's rate
    /// ceiling.</summary>
    long Drops { get; }
}
