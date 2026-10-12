using Microsoft.Extensions.DependencyInjection;
using Puck.DirectX.Presentation;
using Puck.Vulkan.Presentation;
using Xunit;

namespace Puck.World.Tests;

[Collection(SceneProbeCollection.Name)]
public sealed class WorldBootCompositionLawTests {
    // world.counters reads both backends alike: each backend's presenter registration carries its own
    // presentation.skipped source, registered without resolving a device or a presenter.
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.10240")]
    public void EachBackendRegistersItsPresentationCounters() {
        var services = new ServiceCollection();

        services.AddVulkanPresenter();
        services.AddDirectXPresenter();

        var names = services
            .Where(predicate: static descriptor => ((descriptor.ServiceType == typeof(Puck.Abstractions.Counting.IWorkCounterSource)) && (descriptor.ImplementationInstance is Puck.Abstractions.Presentation.PresentationWork)))
            .Select(selector: static descriptor => ((Puck.Abstractions.Counting.IWorkCounterSource)descriptor.ImplementationInstance!).Name)
            .Order(comparer: StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            actual: names,
            expected: ["presentation.directx", "presentation.vulkan"]
        );
    }
}
