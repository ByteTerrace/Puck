using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Abstractions.Recording;
using Puck.Platform.Recording;

namespace Puck.Platform.Linux;

/// <summary>
/// Registers Puck.Platform.Linux's contribution behind the contracts <c>Puck.Platform</c> declares: native windowing
/// (the null clipboard + the Wayland/Xcb window backends) and the recording graph's declining encoder/audio-source
/// factories — no Linux camera-capture or audio-render backend exists yet, so <see cref="AddLinuxCameraCapture"/>
/// registers the null services and no audio-render factory is
/// registered at all (<c>Puck.Platform.Audio.IAudioRenderDeviceFactory</c>; <c>WorldAudioRenderService</c> already
/// treats an unresolved factory as "no render backend").
/// </summary>
public static class LinuxPlatformServiceRegistration {
    /// <summary>Registers the null clipboard service and the Wayland/Xcb native-window backends.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddLinuxPlatformWindowing(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IClipboardService, NullClipboardService>();
        services.TryAddEnumerable(descriptor: ServiceDescriptor.Singleton<INativeWindowBackend, WaylandNativeWindowBackend>());
        services.TryAddEnumerable(descriptor: ServiceDescriptor.Singleton<INativeWindowBackend, XcbNativeWindowBackend>());

        return services;
    }
    /// <summary>Registers the null camera and desktop-capture services.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddLinuxCameraCapture(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ICameraCaptureService, NullCameraCaptureService>();
        services.TryAddSingleton<INativeImageCaptureService, NullNativeImageCaptureService>();

        return services;
    }
    /// <summary>Registers the declining video-encoder and audio-capture-source factories.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddLinuxRecordingPlatform(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<RecordingSessionClock>();
        services.TryAddSingleton<IVideoEncoderFactory>(implementationFactory: static _ =>
            new DecliningVideoEncoderFactory(reason: "video encoding requires Windows Media Foundation"));
        services.TryAddSingleton<IAudioCaptureSourceFactory>(implementationFactory: static _ =>
            new DecliningAudioCaptureSourceFactory(reason: "audio capture requires Windows WASAPI"));

        return services;
    }
}
