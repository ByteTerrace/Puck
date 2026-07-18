using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Abstractions.Recording;
using Puck.Platform.Recording;
using Puck.Platform.Windows.Recording;

namespace Puck.Platform;

/// <summary>
/// Registers the recording graph's platform backends (parallel to <see cref="CameraCaptureServiceRegistration.AddCameraCapture"/>):
/// the Media Foundation hardware video-encoder ladder and the WASAPI loopback/microphone audio sources on Windows, and
/// declining factories everywhere else so the recording graph resolves its seams from DI and reports an honest reason
/// rather than failing to construct. The shared <see cref="RecordingSessionClock"/> is registered so both audio sources
/// (and the video lane, via the recording session) stamp one timeline; the recording session re-anchors its epoch at
/// capture start.
/// </summary>
public static class RecordingPlatformRegistration {
    /// <summary>Registers the platform <see cref="IVideoEncoderFactory"/> and <see cref="IAudioCaptureSourceFactory"/>.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddRecordingPlatform(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<RecordingSessionClock>();

        services.TryAddSingleton<IVideoEncoderFactory>(implementationFactory: static _ =>
            (OperatingSystem.IsWindows()
                ? new MediaFoundationVideoEncoderFactory()
                : new DecliningVideoEncoderFactory(reason: "video encoding requires Windows Media Foundation")));

        services.TryAddSingleton<IAudioCaptureSourceFactory>(implementationFactory: static provider =>
            (OperatingSystem.IsWindows()
                ? new WasapiAudioCaptureSourceFactory(clock: provider.GetRequiredService<RecordingSessionClock>())
                : new DecliningAudioCaptureSourceFactory(reason: "audio capture requires Windows WASAPI")));

        return services;
    }
}
