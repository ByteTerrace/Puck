namespace Puck.Abstractions.Recording;

/// <summary>
/// A live PCM audio source (a microphone, the system loopback mix) delivering interleaved 32-bit float
/// samples with session-clock timestamps — the platform half of a recording-document audio row. The device
/// captures on its own thread into an internal ring; the recording session drains via <see cref="Read"/>.
/// </summary>
/// <remarks><see cref="Read"/> is called from the session's one audio thread. Overflow drops OLDEST samples
/// and counts them (<see cref="DroppedSampleCount"/>) — capture never blocks a device thread.</remarks>
public interface IAudioCaptureSource : IDisposable {
    /// <summary>The sample rate in hertz of the delivered PCM.</summary>
    int SampleRate { get; }

    /// <summary>The interleaved channel count of the delivered PCM.</summary>
    int Channels { get; }

    /// <summary>Samples dropped to ring overflow since <see cref="Start"/> (honest accounting for status echoes).</summary>
    long DroppedSampleCount { get; }

    /// <summary>Starts device capture.</summary>
    void Start();

    /// <summary>Stops device capture; buffered samples remain readable.</summary>
    void Stop();

    /// <summary>Drains up to <paramref name="interleaved"/>.Length samples into the buffer.</summary>
    /// <param name="interleaved">The destination for interleaved float samples.</param>
    /// <param name="firstSampleTimestampNanoseconds">The session-clock timestamp of the first returned sample; 0 when none.</param>
    /// <returns>The number of samples written (a multiple of <see cref="Channels"/>).</returns>
    int Read(Span<float> interleaved, out long firstSampleTimestampNanoseconds);
}

/// <summary>
/// Creates the platform's audio sources for the recording document's audio rows. Factories decline
/// (<see langword="null"/> + reason) rather than throw — a missing microphone or a denied OS privacy setting
/// is a loud status line, never a crash.
/// </summary>
public interface IAudioCaptureSourceFactory {
    /// <summary>Creates the system-output loopback source (what the machine is playing), or declines.</summary>
    /// <param name="reason">Why creation declined, or empty.</param>
    IAudioCaptureSource? CreateLoopback(out string reason);

    /// <summary>Creates the default (or named) capture-device source — the microphone — or declines.</summary>
    /// <param name="deviceId">The device to open, or <see langword="null"/> for the system default.</param>
    /// <param name="reason">Why creation declined, or empty.</param>
    IAudioCaptureSource? CreateMicrophone(string? deviceId, out string reason);
}
