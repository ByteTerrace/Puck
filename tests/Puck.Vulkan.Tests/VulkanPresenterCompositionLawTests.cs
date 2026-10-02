using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Counting;
using Puck.Vulkan.Presentation;
using Xunit;

namespace Puck.Vulkan.Tests;

/// <summary>The hosted Vulkan composition registers its presentation counters as a source of their own: a counter
/// readout enumerates every <see cref="IWorkCounterSource"/>, and reading <c>presentation.vulkan</c> resolves no
/// renderer, which would bring a GPU device up on a host that only asked what was counted.</summary>
public sealed class VulkanPresenterCompositionLawTests {
    [Fact]
    public void ACounterReadoutResolvesThePresentationCountersWithoutTheRenderer() {
        var services = new ServiceCollection().AddVulkanHostedPresentation();

        for (var index = 0; (index < services.Count); ++index) {
            if (services[index].ServiceType == typeof(VulkanRenderer)) {
                services[index] = new ServiceDescriptor(
                    factory: static _ => throw new InvalidOperationException(message: "a counter readout resolved the renderer, which brings up a GPU device"),
                    lifetime: ServiceLifetime.Singleton,
                    serviceType: typeof(VulkanRenderer)
                );
            }
        }
        using var provider = services.BuildServiceProvider();

        Assert.Contains(
            collection: provider.GetServices<IWorkCounterSource>().Select(selector: static source => source.Name),
            expected: "presentation.vulkan"
        );
    }
}
