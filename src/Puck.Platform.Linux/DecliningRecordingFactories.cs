using Puck.Abstractions.Recording;

namespace Puck.Platform.Linux;

/// <summary>An <see cref="IVideoEncoderFactory"/> that always declines — no Linux video-encoder backend exists yet.</summary>
public sealed class DecliningVideoEncoderFactory : IVideoEncoderFactory {
    private readonly string m_reason;

    /// <summary>Creates a factory that declines with the given reason.</summary>
    /// <param name="reason">Why video encoding is unavailable on this platform.</param>
    public DecliningVideoEncoderFactory(string reason) => m_reason = reason;

    /// <inheritdoc/>
    public IVideoEncoder? Create(IReadOnlyList<string> codecLadder, int width, int height, int frameRate, int bitrateKilobitsPerSecond, out string reason) {
        reason = m_reason;

        return null;
    }
}
/// <summary>An <see cref="IAudioCaptureSourceFactory"/> that always declines — no Linux audio-capture backend exists
/// yet.</summary>
public sealed class DecliningAudioCaptureSourceFactory : IAudioCaptureSourceFactory {
    private readonly string m_reason;

    /// <summary>Creates a factory that declines with the given reason.</summary>
    /// <param name="reason">Why audio capture is unavailable on this platform.</param>
    public DecliningAudioCaptureSourceFactory(string reason) => m_reason = reason;

    /// <inheritdoc/>
    public IAudioCaptureSource? CreateLoopback(out string reason) {
        reason = m_reason;

        return null;
    }
    /// <inheritdoc/>
    public IAudioCaptureSource? CreateMicrophone(string? deviceId, out string reason) {
        reason = m_reason;

        return null;
    }
}
