using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Abstractions.Recording;
using Puck.Platform.Audio;
using Puck.Platform.Recording;
using Puck.Platform.Windows.Audio;
using Puck.Platform.Windows.Recording;

namespace Puck.Platform.Windows;

/// <summary>
/// Registers Puck.Platform.Windows's concrete backends behind the contracts <c>Puck.Platform</c> declares: native
/// windowing (clipboard + window backend), live camera/desktop capture, the recording graph's encoder/audio-source
/// factories, and render-device audio output. Every method here constructs a Windows-only type, so the composition
/// root calls each one from inside its own <see cref="OperatingSystem.IsWindows"/> guard — that guard is what
/// satisfies the platform-compatibility analyzer for the <see cref="SupportedOSPlatformAttribute"/>-annotated types
/// underneath, and it is the one place the Windows-versus-Linux platform choice is made.
/// </summary>
public static class WindowsPlatformServiceRegistration {
    /// <summary>Registers the Win32 clipboard service and the Win32 native-window backend.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddWindowsPlatformWindowing(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IClipboardService, Win32ClipboardService>();
        services.TryAddEnumerable(descriptor: ServiceDescriptor.Singleton<INativeWindowBackend, Win32NativeWindowBackend>());

        return services;
    }
    /// <summary>Registers the Media Foundation webcam service and the Windows Graphics Capture desktop-window feed
    /// service.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddWindowsCameraCapture(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ICameraCaptureService, Win32MediaFoundationCameraService>();
        services.TryAddSingleton<INativeImageCaptureService, Win32NativeImageCaptureService>();

        return services;
    }
    /// <summary>Registers the Media Foundation hardware video-encoder ladder and the WASAPI loopback/microphone audio
    /// sources, plus the shared <see cref="RecordingSessionClock"/> both stamp against.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddWindowsRecordingPlatform(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<RecordingSessionClock>();
        services.TryAddSingleton<IVideoEncoderFactory, MediaFoundationVideoEncoderFactory>();
        services.TryAddSingleton<IAudioCaptureSourceFactory>(implementationFactory: static provider =>
            new WasapiAudioCaptureSourceFactory(clock: provider.GetRequiredService<RecordingSessionClock>()));

        return services;
    }
    /// <summary>Registers the WASAPI render-device factory the world speaker device opens its endpoint through.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    [SupportedOSPlatform("windows")]
    public static IServiceCollection AddWindowsAudioRender(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IAudioRenderDeviceFactory, WasapiAudioRenderDeviceFactory>();

        return services;
    }
    /// <summary>Registers a standalone high-resolution <see cref="IPrecisionWaiter"/> for the headless tick host, when
    /// the OS version supports a high-resolution waitable timer; a no-op otherwise (the host falls back to a coarse
    /// sleep).</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddWindowsPrecisionWaiter(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        if (Win32PrecisionWaiter.TryCreate() is { } waiter) {
            services.TryAddSingleton<IPrecisionWaiter>(instance: waiter);
        }

        return services;
    }
}
