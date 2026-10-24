using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Puck.Platform.Probes;

namespace Puck.Platform.Linux;

/// <summary>
/// Registers Puck.Platform.Linux's contribution behind the contracts <c>Puck.Platform</c> declares: native windowing
/// (the null clipboard + the Wayland/Xcb window backends). No Linux camera-capture or audio-render backend exists
/// yet, so <see cref="AddLinuxCameraCapture"/>
/// registers the null services and no audio-render factory is
/// registered at all (<c>Puck.Platform.Audio.IAudioRenderDeviceFactory</c>; <c>WorldAudioRenderService</c> already
/// treats an unresolved factory as "no render backend").
/// </summary>
public static class LinuxPlatformServiceRegistration {
    /// <summary>Registers the null camera, desktop-capture and probe kernel host services.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddLinuxCameraCapture(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ICameraCaptureService, NullCameraCaptureService>();
        services.TryAddSingleton<INativeImageCaptureService, NullNativeImageCaptureService>();
        services.TryAddSingleton<IProbeKernelHostService, NullProbeKernelHostService>();

        return services;
    }
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
}
