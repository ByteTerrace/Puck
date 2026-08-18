using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Puck.Storage.DependencyInjection;

public static class PuckStorageServiceRegistration {
    public static void AddCore(IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(descriptor: ServiceDescriptor.Singleton<IObjectBlobStoreBackend, AzureBlobObjectBlobStoreBackend>());
        services.TryAddEnumerable(descriptor: ServiceDescriptor.Singleton<IObjectBlobStoreBackend, DirectoryObjectBlobStoreBackend>());
        services.TryAddSingleton<IObjectBlobStore, ObjectBlobStore>();
    }
}
