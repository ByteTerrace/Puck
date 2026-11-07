using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Puck.Storage.DependencyInjection;

/// <summary>Registers the routed <see cref="IObjectBlobStore"/>. A storage provider needs no registration here: each
/// target names its own backend.</summary>
public static class PuckStorageServiceRegistration {
    /// <summary>Adds the routed store as a singleton when none is registered.</summary>
    /// <param name="services">The service collection.</param>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static void AddCore(IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IObjectBlobStore, ObjectBlobStore>();
    }
}
