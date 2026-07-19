using Puck.Platform.Windows.Audio;

namespace Puck.Platform.Audio;

/// <summary>Fills one render quantum with interleaved left/right s16 frames. Invoked on the device's own pump
/// thread with a span mapped DIRECTLY over the endpoint buffer (zero copies); the callback must fully overwrite it
/// (write zeros for silence) and must not capture the span. A thrown exception is caught by the pump, the quantum
/// renders silent, and the fault is counted — the "plays silent, never crashes" doctrine.</summary>
/// <param name="interleavedStereo">The quantum to fill: <c>2·frames</c> samples, frames ≤ the
/// <c>maxQuantumFrames</c> the device was opened with.</param>
public delegate void AudioRenderFill(Span<short> interleavedStereo);

/// <summary>
/// One opened stereo s16 render stream on the platform's default output endpoint. The device owns its pump thread
/// (a dedicated MTA COM thread on Windows — the capture source's template): per event wake it fills the endpoint
/// buffer's free space through the <see cref="AudioRenderFill"/> callback in bounded quanta. Any stream failure
/// (device invalidation included) parks the pump and surfaces on <see cref="Fault"/> — the device never throws
/// after construction; the OWNER watches <see cref="Fault"/> and disposes/reopens (the rebind loop lives above this
/// seam). <see cref="IDisposable.Dispose"/> stops the stream with a bounded join.
/// </summary>
public interface IAudioRenderDevice : IDisposable {
    /// <summary>Gets the stream rate in frames per second (the rate the device was opened with).</summary>
    int SampleRate { get; }

    /// <summary>Gets the endpoint buffer's total capacity in frames — the stream's latency ceiling.</summary>
    int BufferFrames { get; }

    /// <summary>Gets the total frames delivered to the endpoint since the stream opened.</summary>
    long FramesDelivered { get; }

    /// <summary>Gets the count of fill callbacks that threw (each rendered its quantum silent).</summary>
    long FillFaults { get; }

    /// <summary>Gets the failure that parked the pump, or <see langword="null"/> while the stream is healthy.</summary>
    string? Fault { get; }
}

/// <summary>
/// Opens <see cref="IAudioRenderDevice"/>s on the platform's default render endpoint — the mockable seam the world
/// speaker service (and its failure-path smoke) drives. A missing endpoint or an initialization failure returns
/// <see langword="null"/> with a decline reason, never a throw (the capture-factory pattern).
/// </summary>
public interface IAudioRenderDeviceFactory {
    /// <summary>Opens the default render endpoint as an event-driven shared-mode stereo s16 stream.</summary>
    /// <param name="sampleRate">The stream rate in frames per second.</param>
    /// <param name="maxQuantumFrames">The largest quantum the pump may request per fill callback.</param>
    /// <param name="fill">The quantum fill callback (invoked on the device's pump thread).</param>
    /// <param name="reason">On decline, why the endpoint could not be opened; empty on success.</param>
    /// <returns>The opened device, or <see langword="null"/>.</returns>
    IAudioRenderDevice? TryOpen(int sampleRate, int maxQuantumFrames, AudioRenderFill fill, out string reason);
}

/// <summary>The platform dispatch for audio rendering: Windows gets the WASAPI factory; a platform with no render
/// backend gets <see langword="null"/> (the consumer's device service simply never starts — the non-Windows posture
/// is a null factory seam, not a stub device).</summary>
public static class AudioRenderPlatform {
    /// <summary>Creates the platform's render-device factory, or <see langword="null"/> when the platform has no
    /// render backend.</summary>
    public static IAudioRenderDeviceFactory? CreateFactory() =>
        (OperatingSystem.IsWindows() ? new WasapiAudioRenderDeviceFactory() : null);
}
