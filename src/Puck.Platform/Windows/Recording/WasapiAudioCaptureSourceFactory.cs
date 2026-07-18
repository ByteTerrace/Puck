using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Puck.Abstractions.Recording;
using Puck.Platform.Recording;

namespace Puck.Platform.Windows.Recording;

/// <summary>
/// The Windows <see cref="IAudioCaptureSourceFactory"/>: opens WASAPI loopback (the render endpoint's mix) and
/// microphone (a capture endpoint) sources sharing one <see cref="RecordingSessionClock"/>, so both stamp against the
/// same timeline. A missing device or an OS microphone-privacy denial surfaces as a decline reason (never a throw): the
/// underlying <c>IAudioClient</c> initialize returns <c>E_ACCESSDENIED</c> when the privacy setting blocks capture.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WasapiAudioCaptureSourceFactory : IAudioCaptureSourceFactory {
    private readonly RecordingSessionClock m_clock;

    /// <summary>Creates the factory bound to the recording session's shared clock.</summary>
    /// <param name="clock">The clock whose epoch both sources stamp against.</param>
    /// <exception cref="ArgumentNullException"><paramref name="clock"/> is <see langword="null"/>.</exception>
    public WasapiAudioCaptureSourceFactory(RecordingSessionClock clock) {
        ArgumentNullException.ThrowIfNull(clock);
        m_clock = clock;
    }

    /// <inheritdoc/>
    public IAudioCaptureSource? CreateLoopback(out string reason) => TryCreate(loopback: true, deviceId: null, label: "system loopback", reason: out reason);

    /// <inheritdoc/>
    public IAudioCaptureSource? CreateMicrophone(string? deviceId, out string reason) => TryCreate(loopback: false, deviceId: deviceId, label: "microphone", reason: out reason);

    private IAudioCaptureSource? TryCreate(bool loopback, string? deviceId, string label, out string reason) {
        if (!OperatingSystem.IsWindows()) {
            reason = $"{label} capture requires Windows WASAPI";

            return null;
        }

        try {
            reason = "";

            return new WasapiAudioCaptureSource(clock: m_clock, loopback: loopback, deviceId: deviceId);
        } catch (COMException exception) {
            reason = ((exception.HResult == Wasapi.EAccessDenied)
                ? $"{label} access was denied by OS privacy settings (0x{exception.HResult:X8})"
                : $"{label} could not be opened: 0x{exception.HResult:X8} {exception.Message}");

            return null;
        } catch (Exception exception) {
            reason = $"{label} could not be opened: {exception.Message}";

            return null;
        }
    }
}
