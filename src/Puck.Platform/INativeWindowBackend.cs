namespace Puck.Platform;

/// <summary>
/// One platform package's contribution to native windowing: the display kind it serves and how to construct a
/// window for it. <c>Puck.Platform.Windows</c>/<c>Puck.Platform.Linux</c> each register the backends they carry
/// (<see cref="Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(Microsoft.Extensions.DependencyInjection.IServiceCollection, Microsoft.Extensions.DependencyInjection.ServiceDescriptor)"/>
/// against this contract); <see cref="NativeWindowFactory"/> resolves the registered set and dispatches to the one
/// matching the requested <see cref="Puck.Abstractions.Windowing.NativeDisplayKind"/> — the registration TABLE is
/// the composition-time platform choice, so the neutral core never names a concrete window type.
/// </summary>
public interface INativeWindowBackend {
    /// <summary>Gets the display kind this backend serves.</summary>
    NativeDisplayKind Kind { get; }

    /// <summary>Creates a platform window for the given options.</summary>
    /// <param name="options">The resolved window options.</param>
    /// <returns>The new platform window.</returns>
    INativeWindow Create(NativeWindowOptions options);
}
