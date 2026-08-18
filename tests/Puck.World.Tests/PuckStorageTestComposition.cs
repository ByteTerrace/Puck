using Microsoft.Extensions.DependencyInjection;

using Puck.Storage;
using Puck.Storage.DependencyInjection;

namespace Puck.World.Tests;

/// <summary>Composes the real routed <see cref="IObjectBlobStore"/> — <see cref="IObjectBlobStoreBackend"/> is
/// internal to <c>Puck.Storage</c>, so a test that wants a real backend (as opposed to <see cref="FakeObjectBlobStore"/>'s
/// in-memory double) goes through this same DI seam production code uses, never a hand-rolled second dispatch.</summary>
internal static class PuckStorageTestComposition {
    public static IObjectBlobStore BuildStore() {
        var services = new ServiceCollection();

        PuckStorageServiceRegistration.AddCore(services: services);

        return services.BuildServiceProvider().GetRequiredService<IObjectBlobStore>();
    }
}
