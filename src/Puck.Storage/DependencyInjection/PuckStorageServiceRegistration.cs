using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Puck.Storage.DependencyInjection;

public static class PuckStorageServiceRegistration {
    public static void AddCore(IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(descriptor: ServiceDescriptor.Singleton<IObjectBlobStoreBackend, LocalFileObjectBlobStoreBackend>());
        services.TryAddEnumerable(descriptor: ServiceDescriptor.Singleton<IObjectBlobStoreBackend, AzureBlobObjectBlobStoreBackend>());
        services.TryAddSingleton<IObjectBlobStore, ObjectBlobStore>();
        services.TryAddSingleton<IJsonObjectBlobStore, JsonObjectBlobStore>();
    }
}
